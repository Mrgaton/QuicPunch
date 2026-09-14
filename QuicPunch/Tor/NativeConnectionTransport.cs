using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace QuicPunch;

internal sealed class NativeConnectionTransport :
    IQuicConnectionTransport
{
    private readonly System.Net.Quic.QuicConnection _connection;
    private int _disposed;

    internal NativeConnectionTransport(
        System.Net.Quic.QuicConnection connection)
    {
        _connection = connection ??
                      throw new ArgumentNullException(nameof(connection));
        if (Helpers.MsQuicDatagramChannel.IsSupported)
        {
            try
            {
                _datagramChannel = Helpers.MsQuicDatagramChannel.Attach(_connection);
                _datagramChannel.OnDatagramReceived += OnRawDatagramReceived;
            }
            catch { }
        }
    }

    internal System.Net.Quic.QuicConnection InnerConnection =>
        _connection;

    public EndPoint? LocalEndPoint =>
        _connection.LocalEndPoint;

    public EndPoint? RemoteEndPoint =>
        _connection.RemoteEndPoint;

    public string TargetHostName =>
        _connection.TargetHostName;

    public SslApplicationProtocol NegotiatedApplicationProtocol =>
        _connection.NegotiatedApplicationProtocol;

    public SslProtocols SslProtocol =>
        _connection.SslProtocol;

    public TlsCipherSuite NegotiatedCipherSuite =>
        _connection.NegotiatedCipherSuite;

    public X509Certificate? RemoteCertificate =>
        _connection.RemoteCertificate;

    public async ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(
        QuicStreamType type,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        System.Net.Quic.QuicStream stream =
            await _connection
                .OpenOutboundStreamAsync(
                    Map(type),
                    cancellationToken)
                .ConfigureAwait(false);

        return new NativeStreamTransport(stream);
    }

    public async ValueTask<IQuicStreamTransport> AcceptInboundStreamAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        System.Net.Quic.QuicStream stream =
            await _connection
                .AcceptInboundStreamAsync(cancellationToken)
                .ConfigureAwait(false);

        return new NativeStreamTransport(stream);
    }

    public ValueTask CloseAsync(
        long errorCode,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _connection.CloseAsync(
            errorCode,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private Helpers.IQuicDatagramChannel? _datagramChannel;
    private readonly Datagrams.DatagramFrameReassembler _reassembler = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<byte, Action<PeerInfo, Datagrams.QuicDatagramMessage>> _categoryHandlers = new();
    private uint _nextSequenceNumber;
    private int _nextFrameId;

    public Helpers.IQuicDatagramChannel? DatagramChannel
    {
        get
        {
            if (_datagramChannel == null && Helpers.MsQuicDatagramChannel.IsSupported)
            {
                try
                {
                    _datagramChannel = Helpers.MsQuicDatagramChannel.Attach(_connection);
                    _datagramChannel.OnDatagramReceived += OnRawDatagramReceived;
                }
                catch { }
            }
            return _datagramChannel;
        }
    }

    private void OnRawDatagramReceived(byte[] data)
    {
        if (Datagrams.QuicDatagramEnvelope.TryParse(data, out var env))
        {
            var peer = new PeerInfo(_connection.RemoteCertificate is X509Certificate2 c2 ? c2 : null, Array.Empty<byte>())
            {
                ActiveEndPoint = _connection.RemoteEndPoint as IPEndPoint
            };

            if (env.IsFragmented)
            {
                if (_reassembler.TryProcessFragment(env, out byte cat, out var fullFrame, out bool isKey))
                {
                    OnFrameReceived?.Invoke(peer, cat, fullFrame, isKey);
                }
            }
            else
            {
                var msg = new Datagrams.QuicDatagramMessage(
                    peer,
                    env.Category,
                    env.Flags,
                    env.SequenceNumber,
                    data.AsMemory(env.IsFramed ? Datagrams.QuicDatagramEnvelope.UnfragmentedHeaderSize : 0),
                    isFramed: env.IsFramed);

                if (_categoryHandlers.TryGetValue(env.Category, out var handler))
                {
                    handler(peer, msg);
                }
                OnCategorizedDatagramReceived?.Invoke(peer, msg);
            }
        }
    }

    public bool SendDatagram(ReadOnlySpan<byte> datagram) => DatagramChannel?.Send(datagram) ?? false;

    public bool SendCategorizedDatagram(byte category, ReadOnlySpan<byte> payload, Datagrams.QuicDatagramFlags flags = Datagrams.QuicDatagramFlags.None, uint sequenceNumber = 0)
    {
        if (sequenceNumber == 0) sequenceNumber = (uint)Interlocked.Increment(ref _nextSequenceNumber);
        int required = Datagrams.QuicDatagramEnvelope.UnfragmentedHeaderSize + payload.Length;
        Span<byte> buf = stackalloc byte[required];
        Datagrams.QuicDatagramEnvelope.WriteEnvelope(buf, category, flags, sequenceNumber, payload);
        return SendDatagram(buf);
    }

    public bool SendFrame(byte category, ReadOnlySpan<byte> frameData, bool isKeyFrame = false, int maxFragmentSize = 1200)
    {
        if (maxFragmentSize <= 0) maxFragmentSize = 1200;
        if (frameData.Length <= maxFragmentSize)
        {
            var flags = isKeyFrame ? Datagrams.QuicDatagramFlags.IsKeyFrame : Datagrams.QuicDatagramFlags.None;
            return SendCategorizedDatagram(category, frameData, flags);
        }

        ushort frameId = (ushort)(Interlocked.Increment(ref _nextFrameId) & 0x7FFF);
        int totalFrags = (frameData.Length + maxFragmentSize - 1) / maxFragmentSize;
        if (totalFrags > 1024) throw new ArgumentException("Frame exceeds max 1024 fragments", nameof(frameData));

        bool allSent = true;
        Span<byte> buf = stackalloc byte[Datagrams.QuicDatagramEnvelope.FragmentedHeaderSize + maxFragmentSize];

        for (int i = 0; i < totalFrags; i++)
        {
            int offset = i * maxFragmentSize;
            int len = Math.Min(maxFragmentSize, frameData.Length - offset);
            var chunk = frameData.Slice(offset, len);
            uint seq = (uint)Interlocked.Increment(ref _nextSequenceNumber);
            var flags = Datagrams.QuicDatagramFlags.IsFragmented;
            if (isKeyFrame) flags |= Datagrams.QuicDatagramFlags.IsKeyFrame;
            if (i == totalFrags - 1) flags |= Datagrams.QuicDatagramFlags.IsLastFragment;

            int written = Datagrams.QuicDatagramEnvelope.WriteFragmentEnvelope(buf, category, flags, seq, frameId, (ushort)i, (ushort)totalFrags, chunk);
            if (!SendDatagram(buf.Slice(0, written))) allSent = false;
        }
        return allSent;
    }

    public void RegisterDatagramCategory(byte category, Action<PeerInfo, Datagrams.QuicDatagramMessage> handler) =>
        _categoryHandlers[category] = handler;

    public event Action<PeerInfo, Datagrams.QuicDatagramMessage>? OnCategorizedDatagramReceived;
    public event Action<PeerInfo, byte, ReadOnlyMemory<byte>, bool>? OnFrameReceived;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private static System.Net.Quic.QuicStreamType Map(
        QuicStreamType type) =>
        type switch
        {
            QuicStreamType.Unidirectional =>
                System.Net.Quic.QuicStreamType.Unidirectional,

            QuicStreamType.Bidirectional =>
                System.Net.Quic.QuicStreamType.Bidirectional,

            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
}