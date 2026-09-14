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
        private readonly DatagramFrameReassembler _reassembler = new();
        private readonly ConcurrentDictionary<byte, Action<PeerInfo, QuicDatagramMessage>> _categoryHandlers = new();
        private PeerInfo? _peer;
        private QuicPunch.IProtocolHandler? _handler;
        private QuicConnection? _connection;
        private uint _nextSequenceNumber;
        private int _nextFrameId;
        private int _disposed;
        private int _closed;

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

            var channelOptions = new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true
            };
            _inboundStreams = Channel.CreateUnbounded<IQuicStreamTransport>(channelOptions);
            _datagramChannel = new VirtualDatagramChannel(this);
        }

        public Guid SessionGuid => _sessionGuid;
        public ushort SessionShortId => _sessionShortId;
        public Guid ProtocolId => _protocolId;
        public QuicPeerMultiplexer Multiplexer => _multiplexer;

        public PeerInfo? Peer { get => _peer; set => _peer = value; }
        public QuicPunch.IProtocolHandler? Handler { get => _handler; set => _handler = value; }
        public QuicConnection? Connection { get => _connection; set => _connection = value; }

        public event Action<PeerInfo, QuicDatagramMessage>? OnCategorizedDatagramReceived;
        public event Action<PeerInfo, byte, ReadOnlyMemory<byte>, bool>? OnFrameReceived;

        public QuicPunch.TransportType TransportType => QuicPunch.TransportType.Wan;

        public EndPoint? LocalEndPoint => _multiplexer.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _multiplexer.RemoteEndPoint;
        public string TargetHostName => _multiplexer.TargetHostName;
        public SslApplicationProtocol NegotiatedApplicationProtocol => _multiplexer.NegotiatedApplicationProtocol;
        public SslProtocols SslProtocol => _multiplexer.SslProtocol;
        public TlsCipherSuite NegotiatedCipherSuite => _multiplexer.NegotiatedCipherSuite;
        public X509Certificate? RemoteCertificate => _multiplexer.RemoteCertificate;

        // Virtual datagram channel exposing RFC 9221 for Voice and real-time streaming
        public IQuicDatagramChannel? DatagramChannel => _datagramChannel;

        public bool SendDatagram(ReadOnlySpan<byte> datagram) =>
            _multiplexer.SendDatagram(_sessionShortId, datagram);

        public bool SendCategorizedDatagram(
            byte category,
            ReadOnlySpan<byte> payload,
            QuicDatagramFlags flags = QuicDatagramFlags.None,
            uint sequenceNumber = 0)
        {
            if (sequenceNumber == 0)
            {
                sequenceNumber = (uint)Interlocked.Increment(ref _nextSequenceNumber);
            }

            int required = QuicDatagramEnvelope.UnfragmentedHeaderSize + payload.Length;
            Span<byte> buffer = stackalloc byte[required];
            QuicDatagramEnvelope.WriteEnvelope(buffer, category, flags, sequenceNumber, payload);
            return SendDatagram(buffer);
        }

        public bool SendFrame(
            byte category,
            ReadOnlySpan<byte> frameData,
            bool isKeyFrame = false,
            int maxFragmentSize = 1200)
        {
            if (maxFragmentSize <= 0) maxFragmentSize = 1200;

            if (frameData.Length <= maxFragmentSize)
            {
                var flags = isKeyFrame ? QuicDatagramFlags.IsKeyFrame : QuicDatagramFlags.None;
                return SendCategorizedDatagram(category, frameData, flags);
            }

            ushort frameId = (ushort)(Interlocked.Increment(ref _nextFrameId) & 0x7FFF);
            int totalFrags = (frameData.Length + maxFragmentSize - 1) / maxFragmentSize;
            if (totalFrags > 1024)
            {
                throw new ArgumentException($"Frame too large ({frameData.Length} bytes), exceeds max 1024 fragments", nameof(frameData));
            }

            bool allSent = true;
            Span<byte> buf = stackalloc byte[QuicDatagramEnvelope.FragmentedHeaderSize + maxFragmentSize];

            for (int i = 0; i < totalFrags; i++)
            {
                int offset = i * maxFragmentSize;
                int len = Math.Min(maxFragmentSize, frameData.Length - offset);
                var chunk = frameData.Slice(offset, len);

                uint seq = (uint)Interlocked.Increment(ref _nextSequenceNumber);
                var flags = QuicDatagramFlags.IsFragmented;
                if (isKeyFrame) flags |= QuicDatagramFlags.IsKeyFrame;
                if (i == totalFrags - 1) flags |= QuicDatagramFlags.IsLastFragment;

                int written = QuicDatagramEnvelope.WriteFragmentEnvelope(buf, category, flags, seq, frameId, (ushort)i, (ushort)totalFrags, chunk);

                if (!SendDatagram(buf.Slice(0, written)))
                {
                    allSent = false;
                }
            }

            return allSent;
        }

        public void RegisterDatagramCategory(byte category, Action<PeerInfo, QuicDatagramMessage> handler)
        {
            _categoryHandlers[category] = handler;
        }

        internal void DispatchIncomingDatagram(byte[] payload)
        {
            _datagramChannel.Dispatch(payload);

            if (QuicDatagramEnvelope.TryParse(payload, out var env))
            {
                var peer = _peer ?? _multiplexer.Peer;

                if (env.IsFragmented)
                {
                    if (_reassembler.TryProcessFragment(env, out byte cat, out var fullFrame, out bool isKey))
                    {
                        OnFrameReceived?.Invoke(peer, cat, fullFrame, isKey);
                        if (_handler != null && _connection != null)
                        {
                            _handler.OnFrameReceived(_connection, peer, cat, fullFrame, isKey);
                        }
                    }
                }
                else
                {
                    var msg = new QuicDatagramMessage(
                        peer,
                        env.Category,
                        env.Flags,
                        env.SequenceNumber,
                        payload.AsMemory(env.IsFramed ? QuicDatagramEnvelope.UnfragmentedHeaderSize : 0),
                        isFramed: env.IsFramed);

                    if (_categoryHandlers.TryGetValue(env.Category, out var catHandler))
                    {
                        catHandler(peer, msg);
                    }

                    OnCategorizedDatagramReceived?.Invoke(peer, msg);

                    if (_handler != null && _connection != null)
                    {
                        _handler.OnDatagramReceived(_connection, peer, msg);
                    }
                }
            }
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
            // Write the 6-byte preface immediately
            await multiplexedStream.EnsureHeaderWrittenAsync(cancellationToken).ConfigureAwait(false);

            _activeStreams[multiplexedStream.Id] = multiplexedStream;
            return multiplexedStream;
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

            _activeStreams[stream.Id] = stream;
            return stream;
        }

        public bool EnqueueInboundStream(IQuicStreamTransport stream)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            bool wasFirst = Interlocked.Exchange(ref _hasInboundStream, 1) == 0;
            if (Volatile.Read(ref _closed) != 0 && !wasFirst)
                return false;

            return _inboundStreams.Writer.TryWrite(stream);
        }

        public async ValueTask CloseAsync(
            long errorCode,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            _datagramChannel.Dispose();

            // If the initial inbound stream has not yet been accepted, wait briefly for any in-flight header arrival
            if (Volatile.Read(ref _hasInboundStream) == 0)
            {
                for (int i = 0; i < 20 && Volatile.Read(ref _hasInboundStream) == 0; i++)
                {
                    try { await Task.Delay(25, cancellationToken).ConfigureAwait(false); } catch { break; }
                }
            }

            _inboundStreams.Writer.TryComplete();

            foreach (var stream in _activeStreams.Values)
            {
                try { stream.Abort(QuicAbortDirection.Both, errorCode); } catch { }
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            _activeStreams.Clear();

            await _multiplexer.NotifySessionClosedAsync(_sessionShortId, errorCode, cancellationToken).ConfigureAwait(false);
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

            public ushort MaxSendDatagramLength =>
                _owner.Multiplexer.PhysicalConnection.DatagramChannel?.MaxSendDatagramLength ?? 1420;

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
