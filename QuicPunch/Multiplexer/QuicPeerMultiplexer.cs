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
        private readonly Func<PeerInfo, Guid, CancellationToken, Task<bool>>? _sessionAccessCheck;
        private readonly Func<Guid, QuicConnection, Stream, Task<bool>>? _sessionRegisteredCallback;
        private readonly Func<Guid, QuicConnection, Task>? _sessionUnregisteredCallback;
        private readonly Func<PeerInfo, Guid, Guid, Guid, bool, CancellationToken, Task<bool>>? _recoveryAuthorization;
        private readonly Action<PeerInfo, Guid, Guid, Guid, bool>? _recoveryFinished;
        private readonly Action<PeerInfo, Guid, Guid, Guid>? _recoveryHandlerFinished;
        private readonly Action<SessionStateChange>? _sessionStateChanged;
        private readonly PeerTransportOptions _transportOptions;
        private Task? _heartbeatTask;

        private readonly ConcurrentDictionary<ushort, MultiplexedQuicConnectionTransport> _activeSessions = new();
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<bool>> _pendingSessionResponses = new();
        private readonly ConcurrentDictionary<ushort, CancellationTokenSource> _pendingIncomingDecisions = new();

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
        private int _authorizationGeneration;
        private CancellationTokenRegistration _lifetimeRegistration;
        private readonly object _heartbeatGate = new();
        private Guid _pendingPing;
        private TaskCompletionSource<bool>? _pingReply;
        private long _pingStarted;
        private readonly System.Collections.Generic.HashSet<ushort> _usedSessionIds = new();
        private readonly System.Collections.Generic.HashSet<ushort> _sessionReservations = new();
        internal const int MaxVirtualSessions = 128;

        internal int ReservedSessionCount
        {
            get { lock (_usedSessionIds) return _sessionReservations.Count; }
        }

        private void ReleaseSessionReservation(ushort id)
        {
            lock (_usedSessionIds) _sessionReservations.Remove(id);
        }

        private bool TryReserveRemoteSession(ControlMessage msg)
        {
            if (msg.SessionShortId == 0 || (msg.SessionShortId % 2 == 0) == _isServer || msg.SessionGuid == Guid.Empty)
                throw new InvalidDataException("Invalid remote session identity.");
            lock (_usedSessionIds)
            {
                if (!_usedSessionIds.Add(msg.SessionShortId)) throw new InvalidDataException("Reused remote session ID.");
                if (IsDisposed || !_accessCheck(_peer) || _sessionReservations.Count >= MaxVirtualSessions) return false;
                return _sessionReservations.Add(msg.SessionShortId);
            }
        }

        private Task SendSessionDeniedAsync(ControlMessage msg, CancellationToken ct) =>
            SendControlMessageAsync(new ControlMessage { Type = ControlMessageType.SessionResponse,
                SessionShortId = msg.SessionShortId, Flags = 0, Reason = "Unauthorized or session limit reached" }, ct);


        public QuicPeerMultiplexer(
            QuicConnection physicalConnection,
            PeerInfo peer,
            bool isServer,
            Func<Guid, QuicPunch.IProtocolHandler?> protocolLookup,
            Func<PeerInfo, bool> accessCheck,
            Func<PeerInfo, Guid, CancellationToken, Task<bool>>? sessionAccessCheck = null,
            Stream? initialControlStream = null,
            Func<Guid, QuicConnection, Stream, Task<bool>>? sessionRegisteredCallback = null,
            Func<Guid, QuicConnection, Task>? sessionUnregisteredCallback = null,
            Func<PeerInfo, Guid, Guid, Guid, bool, CancellationToken, Task<bool>>? recoveryAuthorization = null,
            Action<PeerInfo, Guid, Guid, Guid, bool>? recoveryFinished = null,
            Action<SessionStateChange>? sessionStateChanged = null,
            PeerTransportOptions? transportOptions = null,
            Action<PeerInfo, Guid, Guid, Guid>? recoveryHandlerFinished = null)
        {
            _physicalConnection = physicalConnection ?? throw new ArgumentNullException(nameof(physicalConnection));
            _peer = peer ?? throw new ArgumentNullException(nameof(peer));
            _isServer = isServer;
            _protocolLookup = protocolLookup ?? throw new ArgumentNullException(nameof(protocolLookup));
            _accessCheck = accessCheck ?? throw new ArgumentNullException(nameof(accessCheck));
            _sessionAccessCheck = sessionAccessCheck;
            _sessionRegisteredCallback = sessionRegisteredCallback;
            _sessionUnregisteredCallback = sessionUnregisteredCallback;
            _recoveryAuthorization = recoveryAuthorization;
            _recoveryFinished = recoveryFinished;
            _recoveryHandlerFinished = recoveryHandlerFinished;
            _sessionStateChanged = sessionStateChanged;
            _transportOptions = transportOptions ?? new();
            _transportOptions.Validate();
            if (physicalConnection.IsTor)
                _transportOptions = _transportOptions with
                {
                    HeartbeatInterval = TimeSpan.FromSeconds(Math.Max(5, _transportOptions.HeartbeatInterval.TotalSeconds)),
                    HeartbeatTimeout = TimeSpan.FromSeconds(Math.Max(20, _transportOptions.HeartbeatTimeout.TotalSeconds))
                };

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
        private sealed class ProtocolAvailability
        {
            public Guid Request;
            public long RequestedAt;
            public long AnsweredAt;
            public bool Available;
        }
        private readonly ConcurrentDictionary<Guid, ProtocolAvailability> _protocolAvailability = new();

        /// <summary>Current remote readiness and policy; unknown until checked or when the result expires.</summary>
        public bool? GetRemoteProtocolAvailability(Guid protocolId)
        {
            if (IsDisposed || !_accessCheck(_peer) || !_protocolAvailability.TryGetValue(protocolId, out var state)) return null;
            lock (state) return state.AnsweredAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(state.AnsweredAt) < TimeSpan.FromSeconds(15)
                ? state.Available : null;
        }

        /// <summary>Checks operational readiness and policy without selecting a protocol version or opening a session.</summary>
        public async Task RefreshRemoteProtocolAvailabilityAsync(Guid protocolId)
        {
            if (IsDisposed || !_accessCheck(_peer) || _protocolLookup(protocolId) == null) return;
            var state = _protocolAvailability.GetOrAdd(protocolId, _ => new());
            Guid nonce;
            lock (state)
            {
                if (state.RequestedAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(state.RequestedAt) < TimeSpan.FromSeconds(5)) return;
                state.RequestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                nonce = state.Request = Guid.NewGuid();
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await SendControlMessageAsync(new ControlMessage { Type = ControlMessageType.ProtocolAvailabilityRequest,
                    ProtocolId = protocolId, SessionGuid = nonce }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) when (IsDisposed || _cts.IsCancellationRequested) { }
            catch (Exception ex) { QuicPunchLog.Info($"[MULTIPLEXER] Capability probe failed: {ex.Message}"); }
        }
        private long _lastApplicationActivityTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        private int _incomingSessionDecisions;
        private int _idleCloseRequested;
        public long LastApplicationActivityTimestamp => Interlocked.Read(ref _lastApplicationActivityTimestamp);
        public bool HasApplicationSessions => ReservedSessionCount != 0 || !_activeSessions.IsEmpty || Volatile.Read(ref _incomingSessionDecisions) != 0;
        internal bool IdleCloseRequested => Volatile.Read(ref _idleCloseRequested) != 0;
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
            ThrowIfDisposed();
            _lifetimeRegistration = cancellationToken.Register(() => { _ = DisposeAsync(); });
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
                try { _controlStream.SetQuicPriority(Helpers.QuicStreamPriority.Highest); } catch { }
                _controlStreamReaderTask = Task.Run(() => ControlStreamLoopAsync(_controlStream, _cts.Token));
                _ = _controlStreamReaderTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            else if (!_isServer)
            {
                QuicPunchLog.Info($"[MULTIPLEXER] Opening Control Stream (Stream 0) with {_peer.Name}...");
                _controlStream = await _physicalConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
                try { _controlStream.SetQuicPriority(Helpers.QuicStreamPriority.Highest); } catch { }
                _controlStreamReadyTcs.TrySetResult(_controlStream);
                _controlStreamReaderTask = Task.Run(() => ControlStreamLoopAsync(_controlStream, _cts.Token));
                _ = _controlStreamReaderTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }

            _acceptLoopTask = Task.Run(() => AcceptStreamsLoopAsync(_cts.Token));
            _ = _acceptLoopTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _heartbeatTask = HeartbeatLoopAsync();
        }

        private async Task HeartbeatLoopAsync()
        {
            using var timer = new PeriodicTimer(_transportOptions.HeartbeatInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false)) TrySendTransportPing();
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (Exception ex) { QuicPunchLog.Info($"[MULTIPLEXER] Heartbeat failed: {ex.Message}"); await DisposeAsync().ConfigureAwait(false); }
        }

        private void OnPhysicalDatagramReceived(byte[] datagram)
        {
            if (datagram.Length < 2 || !_accessCheck(_peer)) return;
            ushort sessionId = BinaryPrimitives.ReadUInt16LittleEndian(datagram.AsSpan(0, 2));
            if (!_pendingSessionResponses.ContainsKey(sessionId) && _activeSessions.TryGetValue(sessionId, out var sessionTransport))
            {
                byte[] payload = datagram.AsSpan(2).ToArray();
                sessionTransport.DispatchIncomingDatagram(payload);
            }
        }

        internal bool SendDatagram(ushort sessionId, ReadOnlySpan<byte> datagram)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_accessCheck(_peer)) return false;
            if (datagram.Length > MaxDatagramPayloadLength) return false;
            Span<byte> buffer = datagram.Length <= 2048 ? stackalloc byte[2 + datagram.Length] : new byte[2 + datagram.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[..2], sessionId);
            datagram.CopyTo(buffer.Slice(2));
            return _physicalConnection.SendDatagram(buffer);
        }

        internal int MaxDatagramPayloadLength => Math.Max(0, (_physicalConnection.DatagramChannel?.MaxSendDatagramLength ?? 0) - 2);

        private ushort AllocateSessionId()
        {
            lock (_usedSessionIds)
            {
                if (IsDisposed) throw new ObjectDisposedException(nameof(QuicPeerMultiplexer));
                if (_sessionReservations.Count >= MaxVirtualSessions) throw new InvalidOperationException("Too many active or pending virtual sessions.");
                // Never reuse IDs on a physical connection: late datagrams must not reach a new session.
                while (_nextSessionIdCounter <= ushort.MaxValue)
                {
                    ushort id = (ushort)_nextSessionIdCounter;
                    _nextSessionIdCounter += 2;
                    if (id != 0 && _usedSessionIds.Add(id)) { _sessionReservations.Add(id); return id; }
                }
                throw new InvalidOperationException("Session ID space exhausted; establish a new physical connection.");
            }
        }

        /// <summary>
        /// Requests opening a new virtual QuicConnection for the given protocol over the shared physical QUIC connection.
        /// </summary>
        public Task<QuicConnection> OpenVirtualSessionAsync(
            Guid protocolId,
            CancellationToken cancellationToken = default) =>
            OpenVirtualSessionAsync(protocolId, Guid.NewGuid(), false, cancellationToken);

        /// <summary>Opens or resumes one logical session. Resume never requests fresh user consent.</summary>
        public async Task<QuicConnection> OpenVirtualSessionAsync(
            Guid protocolId, Guid sessionGuid, bool resume, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (sessionGuid == Guid.Empty) throw new ArgumentException("Session ID must not be empty.", nameof(sessionGuid));
            if (!_accessCheck(_peer)) throw new InvalidOperationException("Peer must be trusted before opening application protocols.");
            Interlocked.Exchange(ref _lastApplicationActivityTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            // Give human consent time to finish; recovery needs no fresh consent.
            linked.CancelAfter(resume ? TimeSpan.FromSeconds(35) : QuicPunch.SessionConsentTimeout + TimeSpan.FromSeconds(5));
            ushort sessionId = AllocateSessionId();
            MultiplexedQuicConnectionTransport? transport = null;

            try
            {
                transport = new MultiplexedQuicConnectionTransport(this, sessionGuid, sessionId, protocolId);
                transport.Handler = _protocolLookup(protocolId);
                // Standalone multiplexers may host ordinary sessions without a node's
                // cross-connection authorization store. Only coordinated sessions resume.
                transport.Recoverable = _recoveryAuthorization != null && transport.Handler?.SupportsSessionRecovery == true;
                transport.IsRecoveredSession = resume;
                if (resume && !transport.Recoverable) throw new InvalidOperationException("This handler does not support session recovery.");
                transport.Peer = _peer;
                _activeSessions[sessionId] = transport;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = tcs.Task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _pendingSessionResponses[sessionId] = tcs;
                // Send binary SessionRequest on Control Stream
                await SendControlMessageAsync(new ControlMessage
                {
                    Type = ControlMessageType.SessionRequest,
                    SessionShortId = sessionId,
                    SessionGuid = sessionGuid,
                    ProtocolId = protocolId,
                    Flags = (byte)((transport.Recoverable ? 4 : 0) | (resume ? 8 : 0))
                }, linked.Token).ConfigureAwait(false);

                bool accepted = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                if (!accepted || !_accessCheck(_peer))
                {
                    _activeSessions.TryRemove(sessionId, out _);
                    string? protocolName = transport.Handler?.ProtocolName;
                    if (string.IsNullOrWhiteSpace(protocolName)) protocolName = "Application";
                    throw new SessionRejectedException($"Remote peer {_peer.Name} rejected session for protocol {protocolName}.");
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
                ReleaseSessionReservation(sessionId);
                if (transport != null) try { await transport.DisposeAsync().ConfigureAwait(false); } catch { }
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
                        try { _controlStream.SetQuicPriority(Helpers.QuicStreamPriority.Highest); } catch { }
                        _controlStreamReadyTcs.TrySetResult(stream);
                        QuicPunchLog.Info($"[MULTIPLEXER] Server accepted Control Stream (Stream 0) from {_peer.Name}.");
                        _controlStreamReaderTask = Task.Run(() => ControlStreamLoopAsync(_controlStream, _cts.Token));
                        _ = _controlStreamReaderTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        continue;
                    }

                    // Otherwise, read the 6-byte stream preface with slowloris timeout and bounded concurrency
                    try { await _prefaceSemaphore.WaitAsync(ct).ConfigureAwait(false); }
                    catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                            byte[] header = new byte[MultiplexedStreamTransport.StreamHeaderSize];
                            await stream.ReadExactlyAsync(header, linkedCts.Token).ConfigureAwait(false);

                            ushort sessionId = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
                            QuicPunchLog.Info($"[MULTIPLEXER ACCEPT LOOP] Read preface for stream {stream.Id}: SessionId={sessionId}");

                            if (_accessCheck(_peer) && _activeSessions.TryGetValue(sessionId, out var sessionTransport))
                            {
                                var multiplexedStream = new MultiplexedStreamTransport(stream.Transport, sessionId, openedLocally: false);
                                if (!sessionTransport.EnqueueInboundStream(multiplexedStream))
                                    await multiplexedStream.DisposeAsync().ConfigureAwait(false);
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
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[MULTIPLEXER] AcceptStreamsLoop ended with {_peer.Name} ({RemoteEndPoint}): {DescribeTransportError(ex)}");
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
                    using var frameDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    frameDeadline.CancelAfter(_transportOptions.HeartbeatTimeout);
                    await controlStream.ReadExactlyAsync(payload, frameDeadline.Token).ConfigureAwait(false);

                    var msg = ControlMessage.Decode(payload);
                    await ProcessControlMessageAsync(msg, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                QuicPunchLog.Info($"[MULTIPLEXER] Incomplete control frame from {_peer.Name} ({RemoteEndPoint}): payload deadline {_transportOptions.HeartbeatTimeout.TotalSeconds:F1}s expired.");
            }
            catch (EndOfStreamException) { }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[MULTIPLEXER] ControlStreamLoop terminated with {_peer.Name} ({RemoteEndPoint}): {DescribeTransportError(ex)}");
            }
            finally
            {
                _completionTcs.TrySetResult();
                _ = DisposeAsync();
            }
        }

        internal static string DescribeTransportError(Exception error) => error is System.Net.Quic.QuicException quic
            ? $"{quic.Message} [QuicError={quic.QuicError}, TransportErrorCode={quic.TransportErrorCode?.ToString() ?? "none"}, ApplicationErrorCode={quic.ApplicationErrorCode?.ToString() ?? "none"}]"
            : $"{error.GetType().Name}: {error.Message}";

        public sealed record UserPresence(bool IsAfk, bool AcceptsNotifications);
        private UserPresence? _remoteUserPresence;
        private long _presenceReceivedAt;
        public UserPresence? RemoteUserPresence => !IsDisposed && _accessCheck(_peer) &&
            System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _presenceReceivedAt)) < TimeSpan.FromSeconds(90)
            ? Volatile.Read(ref _remoteUserPresence) : null;

        // These control frames never open application sessions or keep an idle
        // application transport alive. Older multiplexers ignore unknown types.
        public Task SendUserPresenceAsync(bool afk, bool acceptsNotifications, CancellationToken ct = default) =>
            IsDisposed || !_accessCheck(_peer) ? Task.CompletedTask : SendControlMessageAsync(new ControlMessage {
                Type = ControlMessageType.UserPresence, ErrorCode = (afk ? 1 : 0) | (acceptsNotifications ? 0 : 2)
            }, ct);

        private async Task ProcessControlMessageAsync(ControlMessage msg, CancellationToken ct)
        {
            switch (msg.Type)
            {
                case ControlMessageType.UserPresence:
                    if (_accessCheck(_peer) && msg.ErrorCode is >= 0 and <= 3 && msg.Flags == 0 && msg.SessionShortId == 0 &&
                        msg.SessionGuid == Guid.Empty && msg.ProtocolId == Guid.Empty)
                    {
                        Volatile.Write(ref _remoteUserPresence, new UserPresence((msg.ErrorCode & 1) != 0, (msg.ErrorCode & 2) == 0));
                        Volatile.Write(ref _presenceReceivedAt, System.Diagnostics.Stopwatch.GetTimestamp());
                    }
                    break;
                case ControlMessageType.ProtocolAvailabilityRequest:
                    if (!_accessCheck(_peer)) break;
                    await SendControlMessageAsync(new ControlMessage { Type = ControlMessageType.ProtocolAvailabilityResponse,
                        ProtocolId = msg.ProtocolId, SessionGuid = msg.SessionGuid,
                        Flags = (byte)(_protocolLookup(msg.ProtocolId)?.CanStartSession == true ? 1 : 0) }, ct).ConfigureAwait(false);
                    break;
                case ControlMessageType.ProtocolAvailabilityResponse:
                    if (!_accessCheck(_peer) || !_protocolAvailability.TryGetValue(msg.ProtocolId, out var availability)) break;
                    lock (availability)
                    {
                        if (msg.SessionGuid != availability.Request || availability.Request == Guid.Empty ||
                            System.Diagnostics.Stopwatch.GetElapsedTime(availability.RequestedAt) > TimeSpan.FromSeconds(3)) break;
                        availability.Available = msg.Accepted;
                        availability.AnsweredAt = System.Diagnostics.Stopwatch.GetTimestamp();
                        availability.Request = Guid.Empty;
                    }
                    break;
                case ControlMessageType.Ping:
                    await SendControlMessageAsync(new ControlMessage { Type = ControlMessageType.Pong, SessionGuid = msg.SessionGuid }, ct).ConfigureAwait(false);
                    break;
                case ControlMessageType.Pong:
                    lock (_heartbeatGate)
                    {
                        if (_pendingPing != Guid.Empty && msg.SessionGuid == _pendingPing)
                        {
                            _pendingPing = Guid.Empty;
                            _pingReply?.TrySetResult(true);
                            _peer.MarkSeen();
                            _peer.LastPingResponseUtc = DateTime.UtcNow;
                        }
                    }
                    break;
                case ControlMessageType.TransportIdle:
                    if (!HasApplicationSessions)
                    {
                        Interlocked.Exchange(ref _idleCloseRequested, 1);
                        _ = DisposeAsync();
                    }
                    break;
                case ControlMessageType.SessionRequest:
                    // Reserve before awaiting a decision. Reject excess requests on the reader
                    // itself so a peer cannot create an unbounded queue of rejection workers.
                    if (!TryReserveRemoteSession(msg)) await SendSessionDeniedAsync(msg, ct).ConfigureAwait(false);
                    else _ = RunReservedIncomingSessionAsync(msg, ct);
                    break;

                case ControlMessageType.SessionResponse:
                    if (_pendingSessionResponses.TryGetValue(msg.SessionShortId, out var tcs))
                    {
                        tcs.TrySetResult(msg.Accepted);
                    }
                    break;

                case ControlMessageType.SessionClose:
                    if (_pendingIncomingDecisions.TryGetValue(msg.SessionShortId, out var pendingDecision))
                        try { pendingDecision.Cancel(); } catch (ObjectDisposedException) { }
                    if (_activeSessions.TryGetValue(msg.SessionShortId, out var closingTransport))
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await closingTransport.RemoteCloseAsync(msg.ErrorCode, ct).ConfigureAwait(false); } catch { }
                            try { await closingTransport.DisposeAsync().ConfigureAwait(false); } catch { }
                            _activeSessions.TryRemove(msg.SessionShortId, out _);
                        });
                    }
                    break;
            }
        }

        private async Task HandleIncomingSessionRequestAsync(ControlMessage msg, CancellationToken ct)
        {
            if (!TryReserveRemoteSession(msg)) { await SendSessionDeniedAsync(msg, ct).ConfigureAwait(false); return; }
            await RunReservedIncomingSessionAsync(msg, ct).ConfigureAwait(false);
        }

        private async Task RunReservedIncomingSessionAsync(ControlMessage msg, CancellationToken ct)
        {
            bool handedToHandler = false;
            MultiplexedQuicConnectionTransport? pendingTransport = null;
            int authorizationGeneration = Volatile.Read(ref _authorizationGeneration);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            lifetime.CancelAfter((msg.Flags & 8) != 0 ? TimeSpan.FromSeconds(35) : QuicPunch.SessionConsentTimeout + TimeSpan.FromSeconds(5));
            _pendingIncomingDecisions[msg.SessionShortId] = lifetime;
            ct = lifetime.Token;
            try
            {
                bool authorized = _accessCheck(_peer);
                var handler = _protocolLookup(msg.ProtocolId);
                bool recoverable = (msg.Flags & 4) != 0;
                bool resume = (msg.Flags & 8) != 0;
                var transport = pendingTransport = new MultiplexedQuicConnectionTransport(this, msg.SessionGuid, msg.SessionShortId, msg.ProtocolId)
                { Handler = handler, Peer = _peer, Recoverable = recoverable, IsRecoveredSession = resume };
                authorized = authorized && (!resume || recoverable) &&
                    (!recoverable || (handler?.SupportsSessionRecovery == true && _recoveryAuthorization != null));
                if (authorized && handler != null && (recoverable || _sessionAccessCheck != null))
                {
                    Interlocked.Increment(ref _incomingSessionDecisions);
                    try
                    {
                        authorized = await (recoverable
                            ? _recoveryAuthorization!(_peer, msg.ProtocolId, msg.SessionGuid, transport.RecoveryOwner, resume, ct)
                            : _sessionAccessCheck!(_peer, msg.ProtocolId, ct)).WaitAsync(ct).ConfigureAwait(false);
                    }
                    finally { Interlocked.Decrement(ref _incomingSessionDecisions); }
                }
                authorized = authorized && !ct.IsCancellationRequested && authorizationGeneration == Volatile.Read(ref _authorizationGeneration) && _accessCheck(_peer);

                if (!authorized || handler == null)
                {
                    FinishRecovery(transport);
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

                Interlocked.Exchange(ref _lastApplicationActivityTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
                _activeSessions[msg.SessionShortId] = transport;

                await SendControlMessageAsync(new ControlMessage
                {
                    Type = ControlMessageType.SessionResponse,
                    SessionShortId = msg.SessionShortId,
                    Flags = 1 // Accepted = true
                }, ct).ConfigureAwait(false);

                QuicPunchLog.Info($"[MULTIPLEXER] Accepted incoming session {msg.SessionShortId} for protocol {msg.ProtocolId} from {_peer.Name}. Spawning handler...");

                handedToHandler = true;
                _ = RunApplicationHandlerAsync();
                async Task RunApplicationHandlerAsync()
                {
                    using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, transport.SessionToken);
                    CancellationToken ct = sessionLifetime.Token;
                    QuicConnection? virtualConn = null;
                    try
                    {
                        virtualConn = QuicConnection.FromTransport(transport);
                        transport.Connection = virtualConn;
                        QuicPunchLog.Info($"[MULTIPLEXER] Session {msg.SessionShortId}: Waiting to accept initial inbound application stream...");
                        // Accept the initial application stream
                        using var initialTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        initialTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                        var initialStream = await virtualConn.AcceptInboundStreamAsync(initialTimeout.Token).ConfigureAwait(false);
                        try { initialStream.Priority16 = handler.StreamPriority; } catch { }
                        QuicPunchLog.Info($"[MULTIPLEXER] Session {msg.SessionShortId}: Accepted initial inbound application stream {initialStream.Id} (Priority={handler.StreamPriority})!");

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
                            ct.ThrowIfCancellationRequested();
                            if (!_accessCheck(_peer)) throw new UnauthorizedAccessException("Peer trust was revoked before the application handler started.");
                            RaiseSessionState(transport, resume ? SessionState.Recovered : SessionState.Connected);
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
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        QuicPunchLog.Error($"[MULTIPLEXER] Protocol handler exception for {msg.ProtocolId}", ex);
                    }
                    finally
                    {
                        _activeSessions.TryRemove(msg.SessionShortId, out _);
                        ReleaseSessionReservation(msg.SessionShortId);
                        try { await transport.DisposeAsync().ConfigureAwait(false); } catch { }
                        if (virtualConn != null)
                        {
                            try { await virtualConn.DisposeAsync().ConfigureAwait(false); } catch { }
                        }
                        _recoveryHandlerFinished?.Invoke(_peer, msg.ProtocolId, msg.SessionGuid, transport.RecoveryOwner);
                        if (!transport.Recoverable) RaiseSessionState(transport, SessionState.Closed);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { QuicPunchLog.Error("[MULTIPLEXER] Incoming session failed", ex); }
            finally
            {
                _pendingIncomingDecisions.TryRemove(msg.SessionShortId, out _);
                if (!handedToHandler)
                {
                    if (pendingTransport != null) FinishRecovery(pendingTransport);
                    if (pendingTransport != null) _recoveryHandlerFinished?.Invoke(_peer, msg.ProtocolId, msg.SessionGuid, pendingTransport.RecoveryOwner);
                    ReleaseSessionReservation(msg.SessionShortId);
                    if (_activeSessions.TryRemove(msg.SessionShortId, out var failed))
                        try { await failed.DisposeAsync().ConfigureAwait(false); } catch { }
                    else if (pendingTransport != null)
                        try { await pendingTransport.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        }

        private async Task SendControlMessageAsync(ControlMessage msg, CancellationToken ct)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            ct = lifetime.Token;
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
            catch
            {
                // A cancelled partial frame cannot be followed by another frame on this stream.
                _ = DisposeAsync();
                throw;
            }
            finally
            {
                _controlSendLock.Release();
            }
        }

        internal async ValueTask NotifySessionClosedAsync(ushort sessionId, long errorCode, CancellationToken ct)
        {
            _activeSessions.TryRemove(sessionId, out _);
            ReleaseSessionReservation(sessionId);
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

        internal async Task RevokeApplicationSessionsAsync()
        {
            Interlocked.Increment(ref _authorizationGeneration);
            foreach (var pending in _pendingSessionResponses.Values) pending.TrySetResult(false);
            foreach (var session in _activeSessions.Values)
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        internal void FinishRecovery(MultiplexedQuicConnectionTransport session) =>
            _recoveryFinished?.Invoke(_peer, session.ProtocolId, session.SessionGuid, session.RecoveryOwner, session.TransportLost && session.Recoverable);

        private void RaiseSessionState(MultiplexedQuicConnectionTransport session, SessionState state)
        {
            try { _sessionStateChanged?.Invoke(new(_peer.Id, session.ProtocolId, session.SessionGuid, state)); }
            catch (Exception ex) { QuicPunchLog.Info($"[MULTIPLEXER] Session observer failed: {ex.Message}"); }
        }

        internal async Task CloseIdleTransportAsync(CancellationToken ct)
        {
            if (HasApplicationSessions || IsDisposed) return;
            Interlocked.Exchange(ref _idleCloseRequested, 1);
            try { await SendControlMessageAsync(new ControlMessage { Type = ControlMessageType.TransportIdle }, ct).ConfigureAwait(false); }
            catch { }
            await DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Reads smoothed RTT, loss rate, MTU, and bandwidth directly from MsQuic connection statistics.
        /// </summary>
        public bool TryGetTelemetry(out QuicConnectionTelemetry telemetry) =>
            _physicalConnection.TryGetTelemetry(out telemetry);

        /// <summary>
        /// Transmits an immediate native QUIC transport PING frame to refresh RTT measurement.
        /// </summary>
        public bool TrySendTransportPing()
        {
            if (IsDisposed) return false;
            Guid nonce;
            lock (_heartbeatGate)
            {
                if (_pendingPing != Guid.Empty)
                {
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(_pingStarted) >= _transportOptions.HeartbeatTimeout)
                    {
                        QuicPunchLog.Info($"[MULTIPLEXER] Closing transport: correlated heartbeat unanswered for {_transportOptions.HeartbeatTimeout.TotalSeconds:F1}s.");
                        _ = DisposeAsync();
                    }
                    return true;
                }
                nonce = _pendingPing = Guid.NewGuid();
                _pingReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _pingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            _ = SendHeartbeatAsync(nonce);
            return true;
        }

        /// <summary>Verifies a correlated reply before preserving an apparently active tunnel.</summary>
        public async Task<bool> ProbeLivenessAsync(CancellationToken ct = default, TimeSpan? timeout = null)
        {
            if (timeout.HasValue && timeout.Value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            if (!TrySendTransportPing()) return false;
            Task<bool> reply;
            lock (_heartbeatGate) reply = _pingReply!.Task;
            TimeSpan deadline = timeout.HasValue && timeout.Value < _transportOptions.HeartbeatTimeout
                ? timeout.Value : _transportOptions.HeartbeatTimeout;
            try { return await reply.WaitAsync(deadline, ct).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                QuicPunchLog.Info("[MULTIPLEXER] Closing transport: liveness probe timed out.");
                await DisposeAsync().ConfigureAwait(false); return false;
            }
        }

        private async Task SendHeartbeatAsync(Guid nonce)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(_transportOptions.HeartbeatTimeout);
                await SendControlMessageAsync(new ControlMessage { Type = ControlMessageType.Ping, SessionGuid = nonce }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested) QuicPunchLog.Info($"[MULTIPLEXER] Closing transport: heartbeat send failed: {ex.Message}");
                await DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _completionTcs.TrySetResult();
            foreach (var session in _activeSessions.Values)
            {
                session.TransportLost = !IdleCloseRequested;
                FinishRecovery(session);
                if (session.Recoverable && session.TransportLost) RaiseSessionState(session, SessionState.Reconnecting);
            }
            try { _cts.Cancel(); } catch (AggregateException ex) { QuicPunchLog.Info($"[MULTIPLEXER] Cancellation observer failed: {ex.Message}"); }
            lock (_heartbeatGate) _pingReply?.TrySetResult(false);
            _lifetimeRegistration.Unregister();
            _controlStreamReadyTcs.TrySetCanceled();
            foreach (var pending in _pendingSessionResponses.Values) pending.TrySetCanceled();
            _pendingSessionResponses.Clear();

            // Close the physical transport first. Aborting individual streams first
            // would look like a normal application close to the remote session.
            try { await _physicalConnection.CloseAsync(0x51505243, CancellationToken.None).ConfigureAwait(false); } catch { }

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
            // Workers may still release their managed gates while cancellation unwinds.
            // Do not dispose these gates underneath those continuations.
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
            Pong = 5,
            TransportIdle = 6,
            ProtocolAvailabilityRequest = 7,
            ProtocolAvailabilityResponse = 8,
            UserPresence = 9
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
