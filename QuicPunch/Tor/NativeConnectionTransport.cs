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
        // Apply connection defaults once, before exposing the transport to callers.
        Helpers.MsQuicTuner.TryApplyOptimalTuning(_connection);
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

        var channel = Interlocked.Exchange(ref _datagramChannel, null);
        if (channel != null)
        {
            channel.OnDatagramReceived -= OnRawDatagramReceived;
            channel.Dispose();
        }
        _datagrams.Clear();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private Helpers.IQuicDatagramChannel? _datagramChannel;
    private readonly Datagrams.CategorizedDatagramProtocol _datagrams = new();

    public Helpers.IQuicDatagramChannel? DatagramChannel =>
        Volatile.Read(ref _disposed) == 0 ? _datagramChannel : null;

    private void OnRawDatagramReceived(byte[] data)
    {
        // The multiplexer consumes the raw channel. Only decode application envelopes
        // here when the direct native datagram API actually has a receiver.
        if (!_datagrams.HasReceivers) return;
        var peer = new PeerInfo(_connection.RemoteCertificate is X509Certificate2 certificate ? certificate : null, Array.Empty<byte>())
        {
            ActiveEndPoint = _connection.RemoteEndPoint as IPEndPoint
        };
        _datagrams.Receive(data, peer);
    }

    public bool SendDatagram(ReadOnlySpan<byte> datagram) => DatagramChannel?.Send(datagram) ?? false;

    public bool SendCategorizedDatagram(byte category, ReadOnlySpan<byte> payload,
        Datagrams.QuicDatagramFlags flags = Datagrams.QuicDatagramFlags.None, uint sequenceNumber = 0) =>
        _datagrams.SendDatagram(DatagramChannel, category, payload, flags, sequenceNumber);

    public bool SendFrame(byte category, ReadOnlySpan<byte> frameData, bool isKeyFrame = false, int maxFragmentSize = 1200) =>
        _datagrams.SendFrame(DatagramChannel, category, frameData, isKeyFrame, maxFragmentSize);

    public void RegisterDatagramCategory(byte category, Action<PeerInfo, Datagrams.QuicDatagramMessage> handler) =>
        _datagrams.RegisterCategory(category, handler);

    public event Action<PeerInfo, Datagrams.QuicDatagramMessage>? OnCategorizedDatagramReceived
    {
        add => _datagrams.OnDatagramReceived += value;
        remove => _datagrams.OnDatagramReceived -= value;
    }

    public event Action<PeerInfo, byte, ReadOnlyMemory<byte>, bool>? OnFrameReceived
    {
        add => _datagrams.OnFrameReceived += value;
        remove => _datagrams.OnFrameReceived -= value;
    }

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
