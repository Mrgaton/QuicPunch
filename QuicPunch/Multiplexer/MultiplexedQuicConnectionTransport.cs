#nullable enable

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using QuicPunch.Datagrams;
using QuicPunch.Helpers;

namespace QuicPunch.Multiplexer
{
    /// <summary>
    /// Implements <see cref="IQuicConnectionTransport"/> for a single virtual application session
    /// multiplexed over a shared physical QUIC connection.
    /// Supports transparent RFC 9221 datagram demultiplexing, native telemetry, and QoS delegation.
    /// </summary>
    public sealed class MultiplexedQuicConnectionTransport : IQuicConnectionTransport
    {
        private readonly QuicPeerMultiplexer _multiplexer;
        private readonly ushort _sessionShortId;
        private readonly Guid _sessionGuid;
        private readonly Guid _protocolId;
        private readonly Channel<IQuicStreamTransport> _inboundStreams;
        private readonly ConcurrentDictionary<long, IQuicStreamTransport> _activeStreams = new();
        private readonly VirtualDatagramChannel _datagramChannel;
        private readonly CategorizedDatagramProtocol _datagrams = new();
        private PeerInfo? _peer;
        private QuicPunch.IProtocolHandler? _handler;
        private QuicConnection? _connection;
        private int _disposed;
        private int _closed;
        private int _remoteClosing;
        private readonly CancellationTokenSource _sessionLifetime = new();
        internal CancellationToken SessionToken => _sessionLifetime.Token;

        public MultiplexedQuicConnectionTransport(
            QuicPeerMultiplexer multiplexer,
            Guid sessionGuid,
            ushort sessionShortId,
            Guid protocolId)
        {
            _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
            _sessionGuid = sessionGuid;
            _sessionShortId = sessionShortId;
            _protocolId = protocolId;

            var channelOptions = new BoundedChannelOptions(128)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            };
            _inboundStreams = Channel.CreateBounded<IQuicStreamTransport>(channelOptions);
            _datagramChannel = new VirtualDatagramChannel(this);
        }

        public Guid SessionGuid => _sessionGuid;
        public ushort SessionShortId => _sessionShortId;
        public Guid ProtocolId => _protocolId;
        internal Guid RecoveryOwner { get; } = Guid.NewGuid();
        internal bool Recoverable { get; set; }
        public bool IsRecoveredSession { get; internal set; }
        public bool TransportLost { get; internal set; }
        public QuicPeerMultiplexer Multiplexer => _multiplexer;

        public PeerInfo? Peer { get => _peer; set => _peer = value; }
        public QuicPunch.IProtocolHandler? Handler { get => _handler; set => _handler = value; }
        public QuicConnection? Connection { get => _connection; set => _connection = value; }

        public event Action<PeerInfo, QuicDatagramMessage>? OnCategorizedDatagramReceived
        {
            add => _datagrams.OnDatagramReceived += value;
            remove => _datagrams.OnDatagramReceived -= value;
        }

        public event Action<PeerInfo, byte, ReadOnlyMemory<byte>, bool>? OnFrameReceived
        {
            add => _datagrams.OnFrameReceived += value;
            remove => _datagrams.OnFrameReceived -= value;
        }

        public QuicPunch.TransportType TransportType => _multiplexer.TransportType;

        public EndPoint? LocalEndPoint => _multiplexer.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _multiplexer.RemoteEndPoint;
        public string TargetHostName => _multiplexer.TargetHostName;
        public SslApplicationProtocol NegotiatedApplicationProtocol => _multiplexer.NegotiatedApplicationProtocol;
        public SslProtocols SslProtocol => _multiplexer.SslProtocol;
        public TlsCipherSuite NegotiatedCipherSuite => _multiplexer.NegotiatedCipherSuite;
        public X509Certificate? RemoteCertificate => _multiplexer.RemoteCertificate;

        // Virtual datagram channel exposing RFC 9221 for Voice and real-time streaming
        public IQuicDatagramChannel? DatagramChannel => _datagramChannel;

        public bool SendDatagram(ReadOnlySpan<byte> datagram) => Volatile.Read(ref _closed) == 0 &&
            Volatile.Read(ref _remoteClosing) == 0 && Volatile.Read(ref _disposed) == 0 &&
            _multiplexer.SendDatagram(_sessionShortId, datagram);

        public bool SendCategorizedDatagram(byte category, ReadOnlySpan<byte> payload,
            QuicDatagramFlags flags = QuicDatagramFlags.None, uint sequenceNumber = 0) =>
            _datagrams.SendDatagram(DatagramChannel, category, payload, flags, sequenceNumber);

        public bool SendFrame(byte category, ReadOnlySpan<byte> frameData, bool isKeyFrame = false, int maxFragmentSize = 1200) =>
            _datagrams.SendFrame(DatagramChannel, category, frameData, isKeyFrame, maxFragmentSize);

        public void RegisterDatagramCategory(byte category, Action<PeerInfo, QuicDatagramMessage> handler) =>
            _datagrams.RegisterCategory(category, handler);

        internal void DispatchIncomingDatagram(byte[] payload)
        {
            _datagramChannel.Dispatch(payload);
            _datagrams.Receive(payload, _peer ?? _multiplexer.Peer);
        }

        // Hardware QoS & Telemetry delegation to the physical connection
        public bool TryGetTelemetry(out QuicConnectionTelemetry telemetry) =>
            _multiplexer.TryGetTelemetry(out telemetry);

        public bool TrySetDscp(QuicDscpPriority priority) =>
            _multiplexer.PhysicalConnection.TrySetDscp(priority);

        public bool TrySetCongestionControl(QuicCongestionAlgorithm algorithm) =>
            _multiplexer.PhysicalConnection.TrySetCongestionControl(algorithm);

        public bool TrySendTransportPing() =>
            _multiplexer.TrySendTransportPing();

        public async ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(
            QuicStreamType type,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            IQuicStreamTransport rawStream = await _multiplexer
                .OpenOutboundStreamAsync(_sessionShortId, type, cancellationToken)
                .ConfigureAwait(false);

            var multiplexedStream = new MultiplexedStreamTransport(rawStream, _sessionShortId, openedLocally: true);
            if (_handler != null)
            {
                try { multiplexedStream.Priority16 = _handler.StreamPriority; } catch { }
            }

            // Write the 6-byte preface immediately
            try { await multiplexedStream.EnsureHeaderWrittenAsync(cancellationToken).ConfigureAwait(false); }
            catch { await multiplexedStream.DisposeAsync().ConfigureAwait(false); throw; }

            TrackStream(multiplexedStream);
            return multiplexedStream;
        }

        private void TrackStream(IQuicStreamTransport stream)
        {
            _activeStreams[stream.Id] = stream;
            if (stream is MultiplexedStreamTransport multiplexed)
            {
                multiplexed.Closed = () => _activeStreams.TryRemove(new System.Collections.Generic.KeyValuePair<long, IQuicStreamTransport>(stream.Id, stream));
                multiplexed.Faulted = ex =>
                {
                    if (_multiplexer.PhysicalConnection.IsTransportClosed ||
                        (ex is System.Net.Quic.QuicException quic && quic.QuicError != System.Net.Quic.QuicError.StreamAborted &&
                        quic.QuicError != System.Net.Quic.QuicError.OperationAborted))
                        _ = _multiplexer.DisposeAsync();
                };
                multiplexed.EndOfStream = async ct =>
                {
                    // Android and TCP lanes may report EOF before their physical
                    // connection callback. A verified control reply distinguishes
                    // normal application EOF from a failed transport.
                    if (!Recoverable || Volatile.Read(ref _remoteClosing) != 0 || Volatile.Read(ref _closed) != 0) return;
                    if (_multiplexer.PhysicalConnection.IsTransportClosed || !await _multiplexer.ProbeLivenessAsync(ct).ConfigureAwait(false))
                        await _multiplexer.DisposeAsync().ConfigureAwait(false);
                };
            }
        }

        private int _hasInboundStream;
        public bool HasInboundStream => Volatile.Read(ref _hasInboundStream) != 0;

        public async ValueTask<IQuicStreamTransport> AcceptInboundStreamAsync(
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            IQuicStreamTransport stream = await _inboundStreams.Reader
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);

            if (_handler != null)
            {
                try { stream.Priority16 = _handler.StreamPriority; } catch { }
            }

            TrackStream(stream);
            return stream;
        }

        public bool EnqueueInboundStream(IQuicStreamTransport stream)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _remoteClosing) != 0)
                return false;

            bool wasFirst = Interlocked.Exchange(ref _hasInboundStream, 1) == 0;
            if (Volatile.Read(ref _closed) != 0 && !wasFirst)
                return false;

            if (_handler != null)
            {
                try { stream.Priority16 = _handler.StreamPriority; } catch { }
            }

            return _inboundStreams.Writer.TryWrite(stream);
        }

        public async ValueTask CloseAsync(
            long errorCode,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            try { _sessionLifetime.Cancel(); } catch (AggregateException ex) { QuicPunchLog.Info($"[SESSION] Cancellation observer failed: {ex.Message}"); }
            _multiplexer.FinishRecovery(this);

            _datagramChannel.Dispose();

            _inboundStreams.Writer.TryComplete();
            while (_inboundStreams.Reader.TryRead(out var queued))
            {
                try { await queued.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            _datagrams.Clear();

            using var drainDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (var stream in _activeStreams.Values)
            {
                if (!TransportLost && errorCode == 0)
                {
                    try
                    {
                        if (stream.CanWrite) { stream.CompleteWrites(); await stream.WritesClosed.WaitAsync(drainDeadline.Token).ConfigureAwait(false); }
                    }
                    catch { }
                    try { stream.Abort(QuicAbortDirection.Read, errorCode); } catch { }
                }
                else try { stream.Abort(QuicAbortDirection.Both, errorCode); } catch { }
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            _activeStreams.Clear();

            await _multiplexer.NotifySessionClosedAsync(_sessionShortId, errorCode, cancellationToken).ConfigureAwait(false);
        }

        internal async Task RemoteCloseAsync(long errorCode, CancellationToken ct)
        {
            Interlocked.Exchange(ref _remoteClosing, 1);
            if (errorCode == 0)
            {
                // Stream bytes and control frames have independent ordering. Let the
                // application consume the peer's FIN and any buffered response first.
                try { await Task.WhenAll(_activeStreams.Values.Select(x => x.ReadsClosed)).WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch { }
            }
            await CloseAsync(errorCode, ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await CloseAsync(0, CancellationToken.None).ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _remoteClosing) != 0) throw new InvalidOperationException("The remote session has closed.");
        }

        /// <summary>
        /// Virtual datagram channel providing RFC 9221 datagram capabilities tagged with SessionId.
        /// </summary>
        public sealed class VirtualDatagramChannel : IQuicDatagramChannel
        {
            private readonly MultiplexedQuicConnectionTransport _owner;
            private readonly Channel<byte[]> _incomingDatagrams;
            private int _disposed;

            public VirtualDatagramChannel(MultiplexedQuicConnectionTransport owner)
            {
                _owner = owner;
                _incomingDatagrams = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = false,
                    SingleWriter = false
                });
            }

            public bool IsSendEnabled =>
                _owner.Multiplexer.PhysicalConnection.DatagramChannel?.IsSendEnabled ?? false;

            public bool IsReceiveEnabled =>
                _owner.Multiplexer.PhysicalConnection.DatagramChannel?.IsReceiveEnabled ?? false;

            public ushort MaxSendDatagramLength
            {
                get
                {
                    ushort physicalMax = _owner.Multiplexer.PhysicalConnection.DatagramChannel?.MaxSendDatagramLength ?? 1420;
                    return physicalMax > sizeof(ushort)
                        ? (ushort)(physicalMax - sizeof(ushort))
                        : (ushort)0;
                }
            }

            public Channel<byte[]> IncomingDatagrams => _incomingDatagrams;

            public event Action<byte[]>? OnDatagramReceived;
            public event Action<IPEndPoint>? OnPeerAddressChanged
            {
                add
                {
                    if (_owner.Multiplexer.PhysicalConnection.DatagramChannel != null)
                        _owner.Multiplexer.PhysicalConnection.DatagramChannel.OnPeerAddressChanged += value;
                }
                remove
                {
                    if (_owner.Multiplexer.PhysicalConnection.DatagramChannel != null)
                        _owner.Multiplexer.PhysicalConnection.DatagramChannel.OnPeerAddressChanged -= value;
                }
            }

            public bool Send(ReadOnlySpan<byte> datagram) => _owner.SendDatagram(datagram);

            internal void Dispatch(byte[] datagram)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                _incomingDatagrams.Writer.TryWrite(datagram);
                OnDatagramReceived?.Invoke(datagram);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _incomingDatagrams.Writer.TryComplete();
            }
        }
    }
}
