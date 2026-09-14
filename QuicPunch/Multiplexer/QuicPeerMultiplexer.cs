#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch.Helpers;

namespace QuicPunch.Multiplexer
{
    /// <summary>
    /// Manages the single primary physical QUIC connection with a peer, multiplexing
    /// all application protocols (virtual QuicConnections), datagrams, and control signaling.
    /// Uses high-performance zero-allocation binary framing for Stream 0 control messages.
    /// </summary>
    public sealed class QuicPeerMultiplexer : IAsyncDisposable
    {
        public const ushort ControlSessionId = 0;

        private readonly QuicConnection _physicalConnection;
        private readonly PeerInfo _peer;
        private readonly bool _isServer;
        private readonly Func<Guid, QuicPunch.IProtocolHandler?> _protocolLookup;
        private readonly Func<PeerInfo, bool> _accessCheck;
        private readonly Func<Guid, QuicConnection, Stream, Task<bool>>? _sessionRegisteredCallback;
        private readonly Func<Guid, QuicConnection, Task>? _sessionUnregisteredCallback;

        private readonly ConcurrentDictionary<ushort, MultiplexedQuicConnectionTransport> _activeSessions = new();
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<bool>> _pendingSessionResponses = new();

        private Stream? _controlStream;
        private readonly TaskCompletionSource<Stream> _controlStreamReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task? _acceptLoopTask;
        private Task? _controlStreamReaderTask;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Slowloris defense: bound concurrent pending preface reads to 64
        private readonly SemaphoreSlim _prefaceSemaphore = new(64, 64);
        private readonly SemaphoreSlim _controlSendLock = new(1, 1);

        private int _nextSessionIdCounter;
        private int _disposed;

        public QuicPeerMultiplexer(
            QuicConnection physicalConnection,
            PeerInfo peer,
            bool isServer,
            Func<Guid, QuicPunch.IProtocolHandler?> protocolLookup,
            Func<PeerInfo, bool> accessCheck,
            Stream? initialControlStream = null,
            Func<Guid, QuicConnection, Stream, Task<bool>>? sessionRegisteredCallback = null,
            Func<Guid, QuicConnection, Task>? sessionUnregisteredCallback = null)
        {
            _physicalConnection = physicalConnection ?? throw new ArgumentNullException(nameof(physicalConnection));
            _peer = peer ?? throw new ArgumentNullException(nameof(peer));
            _isServer = isServer;
            _protocolLookup = protocolLookup ?? throw new ArgumentNullException(nameof(protocolLookup));
            _accessCheck = accessCheck ?? throw new ArgumentNullException(nameof(accessCheck));
            _sessionRegisteredCallback = sessionRegisteredCallback;
            _sessionUnregisteredCallback = sessionUnregisteredCallback;

            if (initialControlStream != null)
            {
                _controlStream = initialControlStream;
                _controlStreamReadyTcs.TrySetResult(initialControlStream);
            }

            // Client uses odd numbers, Server uses even numbers for Session IDs to prevent collision
            _nextSessionIdCounter = isServer ? 2 : 1;
        }

        public QuicConnection PhysicalConnection => _physicalConnection;
        public PeerInfo Peer => _peer;
        public bool IsServer => _isServer;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public Task Completion => _completionTcs.Task;
        public QuicPunch.TransportType TransportType => _physicalConnection.TransportType;

        public EndPoint? LocalEndPoint => _physicalConnection.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _physicalConnection.RemoteEndPoint;
        public string TargetHostName => _physicalConnection.TargetHostName;
        public SslApplicationProtocol NegotiatedApplicationProtocol => _physicalConnection.NegotiatedApplicationProtocol;
        public SslProtocols SslProtocol => _physicalConnection.SslProtocol;
        public TlsCipherSuite NegotiatedCipherSuite => _physicalConnection.NegotiatedCipherSuite;
        public X509Certificate? RemoteCertificate => _physicalConnection.RemoteCertificate;

        /// <summary>
        /// Starts the multiplexer: opens/accepts Stream 0 (Control Stream), attaches physical datagram receiver,
        /// and begins the stream accept loop.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            var ct = linkedCts.Token;

            // Wire physical datagram channel for RFC 9221 demultiplexing
            if (_physicalConnection.DatagramChannel != null)
            {
                _physicalConnection.DatagramChannel.OnDatagramReceived += OnPhysicalDatagramReceived;
                QuicPunchLog.Info($"[MULTIPLEXER START] DatagramChannel: SendEnabled={_physicalConnection.DatagramChannel.IsSendEnabled}, RecvEnabled={_physicalConnection.DatagramChannel.IsReceiveEnabled}");
            }
            else
            {
                QuicPunchLog.Info("[MULTIPLEXER START] DatagramChannel is NULL!");
            }

            // Client initiates Stream 0; Server awaits and accepts it in AcceptStreamsLoopAsync
            if (_controlStream != null)
            {
                _controlStreamReaderTask = Task.Run(() => ControlStreamLoopAsync(_controlStream, _cts.Token));
            }
            else if (!_isServer)
            {
                QuicPunchLog.Info($"[MULTIPLEXER] Opening Control Stream (Stream 0) with {_peer.Name}...");
                _controlStream = await _physicalConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
                _controlStreamReadyTcs.TrySetResult(_controlStream);
                _controlStreamReaderTask = Task.Run(() => ControlStreamLoopAsync(_controlStream, _cts.Token));
            }

            _acceptLoopTask = Task.Run(() => AcceptStreamsLoopAsync(_cts.Token));
        }

        private void OnPhysicalDatagramReceived(byte[] datagram)
        {
            if (datagram.Length < 2) return;
            ushort sessionId = BinaryPrimitives.ReadUInt16LittleEndian(datagram.AsSpan(0, 2));
            if (_activeSessions.TryGetValue(sessionId, out var sessionTransport))
            {
                byte[] payload = datagram.AsSpan(2).ToArray();
                sessionTransport.DispatchIncomingDatagram(payload);
            }
        }

        internal bool SendDatagram(ushort sessionId, ReadOnlySpan<byte> datagram)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            Span<byte> buffer = stackalloc byte[2 + datagram.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[..2], sessionId);
            datagram.CopyTo(buffer.Slice(2));
            return _physicalConnection.SendDatagram(buffer);
        }

        private ushort AllocateSessionId()
        {
            int next = Interlocked.Add(ref _nextSessionIdCounter, 2);
            return (ushort)(next & 0x7FFF); // Keep within positive ushort range
        }

        /// <summary>
        /// Requests opening a new virtual QuicConnection for the given protocol over the shared physical QUIC connection.
        /// </summary>
        public async Task<QuicConnection> OpenVirtualSessionAsync(
            Guid protocolId,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            ushort sessionId = AllocateSessionId();
            var sessionGuid = Guid.NewGuid();

            var transport = new MultiplexedQuicConnectionTransport(this, sessionGuid, sessionId, protocolId);
            var handler = _protocolLookup(protocolId);
            transport.Handler = handler;
            transport.Peer = _peer;
            _activeSessions[sessionId] = transport;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingSessionResponses[sessionId] = tcs;

            try
            {
                // Send binary SessionRequest on Control Stream
                await SendControlMessageAsync(new ControlMessage
                {
                    Type = ControlMessageType.SessionRequest,
                    SessionShortId = sessionId,
                    SessionGuid = sessionGuid,
                    ProtocolId = protocolId
                }, cancellationToken).ConfigureAwait(false);

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                bool accepted = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                if (!accepted)
                {
                    _activeSessions.TryRemove(sessionId, out _);
                    throw new InvalidOperationException($"Remote peer {_peer.Name} rejected session for protocol {protocolId}.");
                }

                QuicPunchLog.Info($"[MULTIPLEXER] Virtual session {sessionId} for protocol {protocolId} established with {_peer.Name}.");
                var virtualConn = QuicConnection.FromTransport(transport);
                transport.Connection = virtualConn;
                return virtualConn;
            }
            catch
            {
                _activeSessions.TryRemove(sessionId, out _);
                _pendingSessionResponses.TryRemove(sessionId, out _);
                try { await transport.DisposeAsync().ConfigureAwait(false); } catch { }
                throw;
            }
            finally
            {
                _pendingSessionResponses.TryRemove(sessionId, out _);
            }
        }

        internal async ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(
            ushort sessionId,
            QuicStreamType type,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            // Open stream on the underlying physical connection
            var nativeStream = await _physicalConnection.OpenOutboundStreamAsync(type, cancellationToken).ConfigureAwait(false);
            return nativeStream.Transport;
        }

        private async Task AcceptStreamsLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var stream = await _physicalConnection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
                    QuicPunchLog.Info($"[MULTIPLEXER ACCEPT LOOP] Accepted physical stream {stream.Id} from {_peer.Name} (IsServer={_isServer}, ControlNull={_controlStream == null})");

                    // If server and control stream not yet received, the very first inbound bidirectional stream is Stream 0!
                    if (_isServer && _controlStream == null)
                    {
                        _controlStream = stream;
                        _controlStreamReadyTcs.TrySetResult(stream);
                        QuicPunchLog.Info($"[MULTIPLEXER] Server accepted Control Stream (Stream 0) from {_peer.Name}.");
                        _controlStreamReaderTask = Task.Run(() => ControlStreamLoopAsync(_controlStream, _cts.Token));
                        continue;
                    }

                    // Otherwise, read the 6-byte stream preface with slowloris timeout and bounded concurrency
                    _ = Task.Run(async () =>
                    {
                        await _prefaceSemaphore.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                            byte[] header = new byte[MultiplexedStreamTransport.StreamHeaderSize];
                            await stream.ReadExactlyAsync(header, linkedCts.Token).ConfigureAwait(false);

                            ushort sessionId = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
                            QuicPunchLog.Info($"[MULTIPLEXER ACCEPT LOOP] Read preface for stream {stream.Id}: SessionId={sessionId}");

                            if (_activeSessions.TryGetValue(sessionId, out var sessionTransport))
                            {
                                var multiplexedStream = new MultiplexedStreamTransport(stream.Transport, sessionId, openedLocally: false);
                                sessionTransport.EnqueueInboundStream(multiplexedStream);
                                QuicPunchLog.Info($"[MULTIPLEXER ACCEPT LOOP] Enqueued inbound stream {stream.Id} to session {sessionId}");
                            }
                            else
                            {
                                QuicPunchLog.Info($"[MULTIPLEXER] Inbound stream for unknown or closed session {sessionId} from {_peer.Name}; aborting.");
                                stream.Abort(QuicAbortDirection.Both, 0);
                                stream.Dispose();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            QuicPunchLog.Info($"[MULTIPLEXER ACCEPT LOOP] Timeout reading preface on stream {stream.Id}");
                            // Timeout reading 6-byte preface -> abort slowloris stream!
                            try { stream.Abort(QuicAbortDirection.Both, 0x01); } catch { }
                            try { stream.Dispose(); } catch { }
                        }
                        catch (Exception ex)
                        {
                            QuicPunchLog.Error($"[MULTIPLEXER ACCEPT LOOP] Error reading preface on stream {stream.Id}", ex);
                            try { stream.Dispose(); } catch { }
                        }
                        finally
                        {
                            _prefaceSemaphore.Release();
                        }
                    }, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[MULTIPLEXER] AcceptStreamsLoop ended: {ex.Message}");
            }
            finally
            {
                _completionTcs.TrySetResult();
                _ = DisposeAsync();
            }
        }

        private async Task ControlStreamLoopAsync(Stream controlStream, CancellationToken ct)
        {
            byte[] lenBuffer = new byte[4];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await controlStream.ReadExactlyAsync(lenBuffer, ct).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(lenBuffer);
                    if (length <= 0 || length > 4096)
                        throw new InvalidDataException($"Invalid control frame length: {length}");

                    byte[] payload = new byte[length];
                    await controlStream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

                    var msg = ControlMessage.Decode(payload);
                    await ProcessControlMessageAsync(msg, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (EndOfStreamException) { }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[MULTIPLEXER] ControlStreamLoop terminated: {ex.Message}");
            }
            finally
            {
                _completionTcs.TrySetResult();
                _ = DisposeAsync();
            }
        }

        private async Task ProcessControlMessageAsync(ControlMessage msg, CancellationToken ct)
        {
            switch (msg.Type)
            {
                case ControlMessageType.SessionRequest:
                    await HandleIncomingSessionRequestAsync(msg, ct).ConfigureAwait(false);
                    break;

                case ControlMessageType.SessionResponse:
                    if (_pendingSessionResponses.TryGetValue(msg.SessionShortId, out var tcs))
                    {
                        tcs.TrySetResult(msg.Accepted);
                    }
                    break;

                case ControlMessageType.SessionClose:
                    if (_activeSessions.TryGetValue(msg.SessionShortId, out var closingTransport))
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await closingTransport.CloseAsync(msg.ErrorCode, ct).ConfigureAwait(false); } catch { }
                            try { await closingTransport.DisposeAsync().ConfigureAwait(false); } catch { }
                            _activeSessions.TryRemove(msg.SessionShortId, out _);
                        });
                    }
                    break;
            }
        }

        private async Task HandleIncomingSessionRequestAsync(ControlMessage msg, CancellationToken ct)
        {
            bool authorized = _accessCheck(_peer);
            var handler = _protocolLookup(msg.ProtocolId);

            if (!authorized || handler == null)
            {
                QuicPunchLog.Info($"[MULTIPLEXER] Rejected incoming session {msg.SessionShortId} for protocol {msg.ProtocolId} (Authorized: {authorized}, HandlerFound: {handler != null})");
                await SendControlMessageAsync(new ControlMessage
                {
                    Type = ControlMessageType.SessionResponse,
                    SessionShortId = msg.SessionShortId,
                    Flags = 0, // Accepted = false
                    Reason = !authorized ? "Unauthorized" : "Protocol handler not registered"
                }, ct).ConfigureAwait(false);
                return;
            }

            var transport = new MultiplexedQuicConnectionTransport(this, msg.SessionGuid, msg.SessionShortId, msg.ProtocolId);
            transport.Handler = handler;
            transport.Peer = _peer;
            _activeSessions[msg.SessionShortId] = transport;

            await SendControlMessageAsync(new ControlMessage
            {
                Type = ControlMessageType.SessionResponse,
                SessionShortId = msg.SessionShortId,
                Flags = 1 // Accepted = true
            }, ct).ConfigureAwait(false);

            QuicPunchLog.Info($"[MULTIPLEXER] Accepted incoming session {msg.SessionShortId} for protocol {msg.ProtocolId} from {_peer.Name}. Spawning handler...");

            _ = Task.Run(async () =>
            {
                QuicConnection? virtualConn = null;
                try
                {
                    virtualConn = QuicConnection.FromTransport(transport);
                    transport.Connection = virtualConn;
                    QuicPunchLog.Info($"[MULTIPLEXER] Session {msg.SessionShortId}: Waiting to accept initial inbound application stream...");
                    // Accept the initial application stream
                    var initialStream = await virtualConn.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
                    QuicPunchLog.Info($"[MULTIPLEXER] Session {msg.SessionShortId}: Accepted initial inbound application stream {initialStream.Id}!");

                    if (_sessionRegisteredCallback != null)
                    {
                        bool registered = await _sessionRegisteredCallback(msg.ProtocolId, virtualConn, initialStream).ConfigureAwait(false);
                        QuicPunchLog.Info($"[MULTIPLEXER] Session {msg.SessionShortId}: _sessionRegisteredCallback returned {registered}");
                        if (!registered)
                        {
                            return;
                        }
                    }

                    try
                    {
                        QuicPunchLog.Info($"[MULTIPLEXER] Session {msg.SessionShortId}: Invoking handler.HandleAsync...");
                        await handler.HandleAsync(virtualConn, initialStream, _peer, ct).ConfigureAwait(false);
                        QuicPunchLog.Info($"[MULTIPLEXER] Session {msg.SessionShortId}: handler.HandleAsync completed.");
                    }
                    finally
                    {
                        if (_sessionUnregisteredCallback != null)
                        {
                            await _sessionUnregisteredCallback(msg.ProtocolId, virtualConn).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    QuicPunchLog.Error($"[MULTIPLEXER] Protocol handler exception for {msg.ProtocolId}", ex);
                }
                finally
                {
                    _activeSessions.TryRemove(msg.SessionShortId, out _);
                    try { await transport.DisposeAsync().ConfigureAwait(false); } catch { }
                    if (virtualConn != null)
                    {
                        try { await virtualConn.DisposeAsync().ConfigureAwait(false); } catch { }
                    }
                }
            }, ct);
        }

        private async Task SendControlMessageAsync(ControlMessage msg, CancellationToken ct)
        {
            // Await control stream readiness (prevents race condition on startup)
            var stream = await _controlStreamReadyTcs.Task.WaitAsync(ct).ConfigureAwait(false);

            await _controlSendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                byte[] frame = new byte[512];
                msg.Encode(frame.AsSpan(4), out int payloadLen);
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payloadLen);

                await stream.WriteAsync(frame.AsMemory(0, 4 + payloadLen), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _controlSendLock.Release();
            }
        }

        internal async ValueTask NotifySessionClosedAsync(ushort sessionId, long errorCode, CancellationToken ct)
        {
            _activeSessions.TryRemove(sessionId, out _);
            try
            {
                if (_controlStream != null && !_cts.IsCancellationRequested)
                {
                    await SendControlMessageAsync(new ControlMessage
                    {
                        Type = ControlMessageType.SessionClose,
                        SessionShortId = sessionId,
                        ErrorCode = errorCode
                    }, ct).ConfigureAwait(false);
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads smoothed RTT, loss rate, MTU, and bandwidth directly from MsQuic connection statistics.
        /// </summary>
        public bool TryGetTelemetry(out QuicConnectionTelemetry telemetry) =>
            _physicalConnection.TryGetTelemetry(out telemetry);

        /// <summary>
        /// Transmits an immediate native QUIC transport PING frame to refresh RTT measurement.
        /// </summary>
        public bool TrySendTransportPing() =>
            _physicalConnection.TrySendTransportPing();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _completionTcs.TrySetResult();
            _cts.Cancel();

            if (_physicalConnection.DatagramChannel != null)
            {
                _physicalConnection.DatagramChannel.OnDatagramReceived -= OnPhysicalDatagramReceived;
            }

            foreach (var session in _activeSessions.Values)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            _activeSessions.Clear();

            if (_controlStream != null)
            {
                try { await _controlStream.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            try { await _physicalConnection.DisposeAsync().ConfigureAwait(false); } catch { }
            _cts.Dispose();
            _controlSendLock.Dispose();
            _prefaceSemaphore.Dispose();
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        }

        public enum ControlMessageType : byte
        {
            SessionRequest = 1,
            SessionResponse = 2,
            SessionClose = 3,
            Ping = 4,
            Pong = 5
        }

        /// <summary>
        /// Compact 44-byte binary control frame with zero heap allocations for fast session signaling.
        /// </summary>
        public readonly struct ControlMessage
        {
            public const int FixedHeaderSize = 44; // 1 + 2 + 1 + 16 + 16 + 8

            public ControlMessageType Type { get; init; }
            public ushort SessionShortId { get; init; }
            public byte Flags { get; init; }
            public Guid SessionGuid { get; init; }
            public Guid ProtocolId { get; init; }
            public long ErrorCode { get; init; }
            public string? Reason { get; init; }

            public bool Accepted => (Flags & 1) != 0;
            public bool HasReason => !string.IsNullOrEmpty(Reason);

            public void Encode(Span<byte> destination, out int bytesWritten)
            {
                byte flags = Flags;
                byte[]? reasonBytes = null;
                if (!string.IsNullOrEmpty(Reason))
                {
                    flags |= 2;
                    reasonBytes = System.Text.Encoding.UTF8.GetBytes(Reason);
                }

                destination[0] = (byte)Type;
                BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(1, 2), SessionShortId);
                destination[3] = flags;
                SessionGuid.TryWriteBytes(destination.Slice(4, 16));
                ProtocolId.TryWriteBytes(destination.Slice(20, 16));
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(36, 8), ErrorCode);

                int written = FixedHeaderSize;
                if (reasonBytes != null && reasonBytes.Length > 0)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(written, 2), (ushort)reasonBytes.Length);
                    written += 2;
                    reasonBytes.CopyTo(destination.Slice(written));
                    written += reasonBytes.Length;
                }

                bytesWritten = written;
            }

            public static ControlMessage Decode(ReadOnlySpan<byte> source)
            {
                if (source.Length < FixedHeaderSize)
                    throw new InvalidDataException($"Control frame too small: {source.Length} < {FixedHeaderSize}");

                var type = (ControlMessageType)source[0];
                ushort sessionShortId = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(1, 2));
                byte flags = source[3];
                var sessionGuid = new Guid(source.Slice(4, 16));
                var protocolId = new Guid(source.Slice(20, 16));
                long errorCode = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(36, 8));

                string? reason = null;
                if ((flags & 2) != 0 && source.Length >= FixedHeaderSize + 2)
                {
                    ushort reasonLen = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FixedHeaderSize, 2));
                    if (source.Length >= FixedHeaderSize + 2 + reasonLen)
                    {
                        reason = System.Text.Encoding.UTF8.GetString(source.Slice(FixedHeaderSize + 2, reasonLen));
                    }
                }

                return new ControlMessage
                {
                    Type = type,
                    SessionShortId = sessionShortId,
                    Flags = flags,
                    SessionGuid = sessionGuid,
                    ProtocolId = protocolId,
                    ErrorCode = errorCode,
                    Reason = reason
                };
            }
        }
    }
}
