#nullable enable

global using QuicConnection = QuicPunch.QuicConnection;
global using QuicStream = QuicPunch.QuicStream;
global using QuicStreamType = QuicPunch.QuicStreamType;
global using QuicAbortDirection = QuicPunch.QuicAbortDirection;

using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NativeQuicClientConnectionOptions = global::System.Net.Quic.QuicClientConnectionOptions;
using NativeQuicConnection = global::System.Net.Quic.QuicConnection;
using NativeQuicStreamType = global::System.Net.Quic.QuicStreamType;

namespace QuicPunch;

public sealed class QuicConnection : IAsyncDisposable
{
    private static IQuicConnectionFactory s_transportFactory =
        NativeQuicConnectionFactory.Instance;

    private readonly IQuicConnectionTransport _transport;
    private int _disposed;

    private QuicConnection(IQuicConnectionTransport transport)
    {
        _transport = transport ??
            throw new ArgumentNullException(nameof(transport));
    }

    public static IQuicConnectionFactory TransportFactory
    {
        get => Volatile.Read(ref s_transportFactory);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref s_transportFactory, value);
        }
    }

    public static bool IsSupported => TransportFactory.IsSupported;

    public EndPoint? LocalEndPoint => _transport.LocalEndPoint;

    public EndPoint? RemoteEndPoint => _transport.RemoteEndPoint;

    public string TargetHostName => _transport.TargetHostName;

    public SslApplicationProtocol NegotiatedApplicationProtocol =>
        _transport.NegotiatedApplicationProtocol;

    public SslProtocols SslProtocol => _transport.SslProtocol;

    public TlsCipherSuite NegotiatedCipherSuite =>
        _transport.NegotiatedCipherSuite;

    public X509Certificate? RemoteCertificate =>
        _transport.RemoteCertificate;

    public QuicPunch.TransportType TransportType =>
        _transport.TransportType;

    public bool IsTor =>
        TransportType == QuicPunch.TransportType.Tor;

    public bool IsNativeQuic =>
        TransportType == QuicPunch.TransportType.Wan;

    public NativeQuicConnection? NativeConnection =>
        (_transport as NativeConnectionTransport)?.InnerConnection;

    private Helpers.IQuicDatagramChannel? _datagramChannel;

    /// <summary>
    /// Gets the RFC 9221 unreliable datagram channel attached to this connection (native or virtual).
    /// </summary>
    public Helpers.IQuicDatagramChannel? DatagramChannel
    {
        get
        {
            if (_transport.DatagramChannel != null)
                return _transport.DatagramChannel;

            if (_datagramChannel == null && NativeConnection != null && Helpers.MsQuicDatagramChannel.IsSupported)
            {
                try { _datagramChannel = Helpers.MsQuicDatagramChannel.Attach(NativeConnection); } catch { }
            }
            return _datagramChannel;
        }
    }

    /// <summary>
    /// Transmits an unreliable QUIC datagram over this connection without retransmissions or head-of-line blocking.
    /// </summary>
    public bool SendDatagram(ReadOnlySpan<byte> datagram)
    {
        if (_transport.SendDatagram(datagram))
            return true;
        return DatagramChannel?.Send(datagram) ?? false;
    }

    /// <summary>
    /// Transmits a categorized RFC 9221 datagram with automatic framing, category tag, sequence number, and flags.
    /// </summary>
    public bool SendCategorizedDatagram(byte category, ReadOnlySpan<byte> payload, Datagrams.QuicDatagramFlags flags = Datagrams.QuicDatagramFlags.None, uint sequenceNumber = 0) =>
        _transport.SendCategorizedDatagram(category, payload, flags, sequenceNumber);

    /// <summary>
    /// Transmits a multi-fragment frame (e.g. video, camera, or screen slice) sliced into MTU-sized datagrams,
    /// which will be automatically reassembled on the receiver without stalling or head-of-line blocking.
    /// </summary>
    public bool SendFrame(byte category, ReadOnlySpan<byte> frameData, bool isKeyFrame = false, int maxFragmentSize = 1200) =>
        _transport.SendFrame(category, frameData, isKeyFrame, maxFragmentSize);

    /// <summary>
    /// Registers a handler for a specific datagram category (e.g. VideoKeyFrame, Audio, CursorInput).
    /// </summary>
    public void RegisterDatagramCategory(byte category, Action<PeerInfo, Datagrams.QuicDatagramMessage> handler) =>
        _transport.RegisterDatagramCategory(category, handler);

    /// <summary>
    /// Fired whenever a categorized RFC 9221 datagram arrives on this connection.
    /// </summary>
    public event Action<PeerInfo, Datagrams.QuicDatagramMessage>? OnCategorizedDatagramReceived
    {
        add => _transport.OnCategorizedDatagramReceived += value;
        remove => _transport.OnCategorizedDatagramReceived -= value;
    }

    /// <summary>
    /// Fired whenever a complete multi-datagram frame (video, camera, screen share) is reassembled.
    /// </summary>
    public event Action<PeerInfo, byte, ReadOnlyMemory<byte>, bool>? OnFrameReceived
    {
        add => _transport.OnFrameReceived += value;
        remove => _transport.OnFrameReceived -= value;
    }

    /// <summary>
    /// Queries real-time performance telemetry and protocol metadata from this connection.
    /// </summary>
    /// <param name="telemetry">When this method returns, contains the populated <see cref="Helpers.QuicConnectionTelemetry"/>, or <c>null</c> if the query failed.</param>
    /// <returns><c>true</c> if telemetry was successfully retrieved; otherwise, <c>false</c>.</returns>
    public bool TryGetTelemetry(out Helpers.QuicConnectionTelemetry telemetry)
    {
        if (_transport.TryGetTelemetry(out telemetry))
            return true;

        telemetry = null!;
        if (NativeConnection != null && Helpers.MsQuicTuner.TryGetTelemetry(NativeConnection, out telemetry))
        {
            string? alpnStr = null;
            try
            {
                if (!NegotiatedApplicationProtocol.Protocol.IsEmpty)
                {
                    alpnStr = System.Text.Encoding.UTF8.GetString(NegotiatedApplicationProtocol.Protocol.Span);
                }
            }
            catch { }

            telemetry = telemetry with
            {
                CipherSuite = NegotiatedCipherSuite != 0 ? NegotiatedCipherSuite.ToString() : telemetry.CipherSuite,
                Alpn = alpnStr ?? telemetry.Alpn,
                SslProtocol = SslProtocol != SslProtocols.None ? SslProtocol.ToString() : telemetry.SslProtocol,
                TargetHostName = !string.IsNullOrEmpty(TargetHostName) ? TargetHostName : telemetry.TargetHostName,
                NativeLocalEndPoint = telemetry.NativeLocalEndPoint ?? (LocalEndPoint as System.Net.IPEndPoint),
                NativeRemoteEndPoint = telemetry.NativeRemoteEndPoint ?? (RemoteEndPoint as System.Net.IPEndPoint)
            };
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sets the DSCP QoS prioritization byte on the underlying socket (Voice = EF/46, Video = AF41/34).
    /// </summary>
    public bool TrySetDscp(Helpers.QuicDscpPriority priority)
    {
        if (_transport.TrySetDscp(priority))
            return true;
        return NativeConnection != null && Helpers.MsQuicTuner.TrySetDscp(NativeConnection, priority);
    }

    /// <summary>
    /// Dynamically switches congestion control algorithm (e.g. to BBR).
    /// </summary>
    public bool TrySetCongestionControl(Helpers.QuicCongestionAlgorithm algorithm)
    {
        if (_transport.TrySetCongestionControl(algorithm))
            return true;
        return NativeConnection != null && Helpers.MsQuicTuner.TrySetCongestionControl(NativeConnection, algorithm);
    }

    /// <summary>
    /// Queries the active congestion control algorithm of this connection.
    /// </summary>
    public bool TryGetCongestionControl(out Helpers.QuicCongestionAlgorithm algorithm) =>
        Helpers.MsQuicTuner.TryGetCongestionControl(this, out algorithm);

    /// <summary>
    /// Sets the stream scheduling scheme on this connection (FIFO or RoundRobin).
    /// </summary>
    public bool TrySetStreamSchedulingScheme(Helpers.QuicStreamSchedulingScheme scheme) =>
        Helpers.MsQuicTuner.TrySetStreamSchedulingScheme(this, scheme);

    /// <summary>
    /// Queries the active stream scheduling scheme of this connection.
    /// </summary>
    public bool TryGetStreamSchedulingScheme(out Helpers.QuicStreamSchedulingScheme scheme) =>
        Helpers.MsQuicTuner.TryGetStreamSchedulingScheme(this, out scheme);

    /// <summary>
    /// Configures Path MTU Discovery bounds and timing parameters on this connection.
    /// </summary>
    public bool TrySetPathMtuDiscovery(ushort minMtu = 1280, ushort maxMtu = 1500, ulong timeoutUs = 600_000_000UL, byte missingProbeCount = 3) =>
        Helpers.MsQuicTuner.TrySetPathMtuDiscovery(this, minMtu, maxMtu, timeoutUs, missingProbeCount);

    /// <summary>
    /// Gets the current dynamically discovered Path MTU in bytes (e.g. 1280 to 1500, or up to 9000 for Jumbo Frames).
    /// </summary>
    public ushort PathMtu => Helpers.MsQuicTuner.TryGetPathMtu(this, out var mtu) ? mtu : (ushort)1500;

    /// <summary>
    /// Attempts to retrieve the dynamically discovered Path MTU in bytes for this connection.
    /// </summary>
    public bool TryGetPathMtu(out ushort pathMtu) => Helpers.MsQuicTuner.TryGetPathMtu(this, out pathMtu);

    /// <summary>
    /// Cached 0-RTT session resumption ticket for fast reconnects.
    /// </summary>
    public byte[]? ResumptionTicket { get; set; }

    /// <summary>
    /// Dynamically tunes the flow control window sizes on this connection.
    /// </summary>
    public bool TrySetFlowControlWindows(uint connWindow = 16 * 1024 * 1024, uint streamWindow = 4 * 1024 * 1024) =>
        Helpers.MsQuicTuner.TrySetFlowControlWindows(this, connWindow, streamWindow);

    /// <summary>
    /// Dynamically sets the native transport keepalive interval.
    /// </summary>
    public bool TrySetKeepAlive(TimeSpan interval) =>
        Helpers.MsQuicTuner.TrySetKeepAliveInterval(this, (uint)Math.Clamp(interval.TotalMilliseconds, 0, uint.MaxValue));

    /// <summary>
    /// Sends an immediate native transport PING / keepalive signal.
    /// </summary>
    public bool TrySendTransportPing() =>
        _transport.TrySendTransportPing() || Helpers.MsQuicTuner.TrySendTransportPing(this);

    /// <summary>
    /// Attempts to retrieve the TLS 1.3 0-RTT session resumption ticket from this connection.
    /// </summary>
    public bool TryGetResumptionTicket(out byte[] ticket)
    {
        if (Helpers.MsQuicTuner.TryGetResumptionTicket(this, out ticket))
        {
            ResumptionTicket = ticket;
            return true;
        }
        ticket = ResumptionTicket ?? Array.Empty<byte>();
        return ResumptionTicket != null;
    }

    /// <summary>
    /// Sets a TLS 1.3 0-RTT session resumption ticket on this connection prior to connecting.
    /// </summary>
    public bool TrySetResumptionTicket(ReadOnlySpan<byte> ticket)
    {
        ResumptionTicket = ticket.ToArray();
        return Helpers.MsQuicTuner.TrySetResumptionTicket(this, ticket);
    }

    /// <summary>
    /// Atomically applies optimal native MsQuic tunings (BBR, Pacing, HyStart, PMTUD, 16MB/4MB flow control, and 5s keepalive) to this connection.
    /// </summary>
    public bool ApplyOptimalTuning() =>
        NativeConnection != null && Helpers.MsQuicTuner.TryApplyOptimalTuning(NativeConnection);

    public static async ValueTask<QuicConnection> ConnectAsync(
        NativeQuicClientConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        Helpers.MsQuicTuner.EnsureOptimalConfiguration();

        if (Helpers.MsQuicDatagramChannel.IsSupported)
        {
            Helpers.MsQuicDatagramChannel.EnableDatagramsOnConfigurationCache();
        }

        IQuicConnectionTransport transport =
            await TransportFactory
                .ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);

        var conn = new QuicConnection(transport);
        if (conn.NativeConnection != null)
        {
            conn.ApplyOptimalTuning();
        }
        return conn;
    }

    public static async ValueTask<QuicConnection> ConnectAsync(
        NativeQuicClientConnectionOptions options,
        IQuicConnectionFactory factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);

        IQuicConnectionTransport transport =
            await factory
                .ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);

        var conn = new QuicConnection(transport);
        if (conn.NativeConnection != null)
        {
            conn.ApplyOptimalTuning();
        }
        return conn;
    }

    public static QuicConnection CreateDummy(
        IDummyQuicLaneProvider provider,
        QuicConnectionRole role,
        Guid? connectionId = null,
        bool leaveProviderOpen = false)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return new QuicConnection(
            new DummyQuicConnectionTransport(
                provider,
                role,
                connectionId,
                leaveProviderOpen));
    }

    public static QuicConnection CreateDummy(
        Stream connectedStream,
        QuicConnectionRole role,
        bool streamWasOpenedByRemote = false,
        QuicStreamType streamType = QuicStreamType.Bidirectional,
        Action? completeWrites = null,
        Action<QuicAbortDirection, long>? abort = null)
    {
        ArgumentNullException.ThrowIfNull(connectedStream);

        var lane = new StreamDummyQuicLane(
            connectedStream,
            streamType,
            streamWasOpenedByRemote,
            completeWrites,
            abort);

        var provider = new SingleStreamDummyQuicLaneProvider(
            lane,
            role,
            streamWasOpenedByRemote,
            streamType);

        return CreateDummy(provider, role);
    }

    public static QuicConnection FromTransport(
        IQuicConnectionTransport transport) =>
        new(transport);

    public static QuicConnection Wrap(
        NativeQuicConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Helpers.MsQuicTuner.EnsureOptimalConfiguration();
        Helpers.MsQuicTuner.TryApplyOptimalTuning(connection);
        var conn = new QuicConnection(
            new NativeConnectionTransport(connection));
        return conn;
    }

    public static implicit operator QuicConnection(
        NativeQuicConnection connection) =>
        Wrap(connection);

    public async ValueTask<QuicStream> OpenOutboundStreamAsync(
        QuicStreamType type,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        IQuicStreamTransport stream =
            await _transport
                .OpenOutboundStreamAsync(type, cancellationToken)
                .ConfigureAwait(false);

        ValidateStreamTransport(stream, openedLocally: true);
        return new QuicStream(stream);
    }

    public ValueTask<QuicStream> OpenOutboundStreamAsync(
        NativeQuicStreamType type,
        CancellationToken cancellationToken = default) =>
        OpenOutboundStreamAsync(Map(type), cancellationToken);

    public async ValueTask<QuicStream> AcceptInboundStreamAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        IQuicStreamTransport stream =
            await _transport
                .AcceptInboundStreamAsync(cancellationToken)
                .ConfigureAwait(false);

        ValidateStreamTransport(stream, openedLocally: false);
        return new QuicStream(stream);
    }

    public ValueTask<QuicStream> AcceptInboundStreamAsync(
        NativeQuicStreamType type,
        CancellationToken cancellationToken = default) =>
        AcceptInboundStreamAsync(cancellationToken);

    public ValueTask CloseAsync(
        long errorCode,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _transport.CloseAsync(errorCode, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _datagramChannel?.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    public static void UseNativeTransport() =>
        TransportFactory = NativeQuicConnectionFactory.Instance;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private static void ValidateStreamTransport(
        IQuicStreamTransport stream,
        bool openedLocally)
    {
        if (stream is null)
            throw new InvalidOperationException(
                "The QUIC transport returned a null stream.");

        if (stream.Stream is null)
            throw new InvalidOperationException(
                "The QUIC transport returned a stream with a null underlying Stream.");

        if (stream.CanRead && !stream.Stream.CanRead)
        {
            throw new InvalidOperationException(
                "The transport says the stream is readable but its underlying Stream is not.");
        }

        if (stream.CanWrite && !stream.Stream.CanWrite)
        {
            throw new InvalidOperationException(
                "The transport says the stream is writable but its underlying Stream is not.");
        }

        if (stream.Type == QuicStreamType.Bidirectional)
        {
            if (!stream.CanRead || !stream.CanWrite)
            {
                throw new InvalidOperationException(
                    "A bidirectional QUIC-like stream must support both reading and writing.");
            }

            return;
        }

        // QUIC semantics for unidirectional streams:
        // opener = write-only; accepter = read-only.
        if (openedLocally)
        {
            if (stream.CanRead || !stream.CanWrite)
            {
                throw new InvalidOperationException(
                    "A locally opened unidirectional stream must be write-only.");
            }
        }
        else
        {
            if (!stream.CanRead || stream.CanWrite)
            {
                throw new InvalidOperationException(
                    "A remotely opened unidirectional stream must be read-only.");
            }
        }
    }

    private static QuicStreamType Map(
        NativeQuicStreamType type) =>
        type switch
        {
            NativeQuicStreamType.Unidirectional =>
                QuicStreamType.Unidirectional,

            NativeQuicStreamType.Bidirectional =>
                QuicStreamType.Bidirectional,

            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
}

public enum QuicConnectionRole
{
    Client = 0,
    Server = 1
}

public enum QuicStreamType
{
    Unidirectional = 0,
    Bidirectional = 1
}

[Flags]
public enum QuicAbortDirection
{
    Read = 1,
    Write = 2,
    Both = Read | Write
}

public interface IQuicStreamTransport : IDisposable, IAsyncDisposable
{
    long Id { get; }
    QuicStreamType Type { get; }

    bool CanRead { get; }
    bool CanWrite { get; }

    Stream Stream { get; }

    Task ReadsClosed { get; }
    Task WritesClosed { get; }

    byte Priority { get; set; }

    ushort Priority16
    {
        get => (ushort)((Priority << 8) | Priority);
        set => Priority = (byte)(value >> 8);
    }

    void CompleteWrites();

    void Abort(
        QuicAbortDirection abortDirection,
        long errorCode);
}

public interface IQuicConnectionTransport : IAsyncDisposable
{
    QuicPunch.TransportType TransportType => QuicPunch.TransportType.Wan;

    EndPoint? LocalEndPoint { get; }
    EndPoint? RemoteEndPoint { get; }

    string TargetHostName => string.Empty;
    SslApplicationProtocol NegotiatedApplicationProtocol => default;
    SslProtocols SslProtocol => SslProtocols.None;
    TlsCipherSuite NegotiatedCipherSuite => default;
    X509Certificate? RemoteCertificate => null;

    ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(
        QuicStreamType type,
        CancellationToken cancellationToken = default);

    ValueTask<IQuicStreamTransport> AcceptInboundStreamAsync(
        CancellationToken cancellationToken = default);

    ValueTask CloseAsync(
        long errorCode,
        CancellationToken cancellationToken = default);

    Helpers.IQuicDatagramChannel? DatagramChannel => null;
    bool SendDatagram(ReadOnlySpan<byte> datagram) => false;
    bool SendCategorizedDatagram(byte category, ReadOnlySpan<byte> payload, Datagrams.QuicDatagramFlags flags = Datagrams.QuicDatagramFlags.None, uint sequenceNumber = 0) => false;
    bool SendFrame(byte category, ReadOnlySpan<byte> frameData, bool isKeyFrame = false, int maxFragmentSize = 1200) => false;
    void RegisterDatagramCategory(byte category, Action<PeerInfo, Datagrams.QuicDatagramMessage> handler) { }
    event Action<PeerInfo, Datagrams.QuicDatagramMessage>? OnCategorizedDatagramReceived { add { } remove { } }
    event Action<PeerInfo, byte, ReadOnlyMemory<byte>, bool>? OnFrameReceived { add { } remove { } }
    bool TryGetTelemetry(out Helpers.QuicConnectionTelemetry telemetry) { telemetry = null!; return false; }
    bool TrySetDscp(Helpers.QuicDscpPriority priority) => false;
    bool TrySetCongestionControl(Helpers.QuicCongestionAlgorithm algorithm) => false;
    bool TrySendTransportPing() => false;
}

public interface IQuicConnectionFactory
{
    bool IsSupported { get; }

    ValueTask<IQuicConnectionTransport> ConnectAsync(
        System.Net.Quic.QuicClientConnectionOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class NativeQuicConnectionFactory :
    IQuicConnectionFactory
{
    public static NativeQuicConnectionFactory Instance { get; } =
        new();

    private NativeQuicConnectionFactory()
    {
    }

    public bool IsSupported =>
        System.Net.Quic.QuicConnection.IsSupported;

    public async ValueTask<IQuicConnectionTransport> ConnectAsync(
        System.Net.Quic.QuicClientConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        Helpers.MsQuicTuner.EnsureOptimalConfiguration();

        System.Net.Quic.QuicConnection connection =
            await System.Net.Quic.QuicConnection
                .ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);

        Helpers.MsQuicTuner.EnsureOptimalConfiguration();
        Helpers.MsQuicTuner.TryApplyOptimalTuning(connection);

        return new NativeConnectionTransport(connection);
    }
}