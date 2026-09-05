#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using NativeQuicAbortDirection = global::System.Net.Quic.QuicAbortDirection;
using NativeQuicClientConnectionOptions = global::System.Net.Quic.QuicClientConnectionOptions;
using NativeQuicConnection = global::System.Net.Quic.QuicConnection;
using NativeQuicStream = global::System.Net.Quic.QuicStream;
using NativeQuicStreamType = global::System.Net.Quic.QuicStreamType;

namespace QuicPunch;

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

public interface IQuicConnectionFactory
{
    bool IsSupported { get; }

    ValueTask<IQuicConnectionTransport> ConnectAsync(
        NativeQuicClientConnectionOptions options,
        CancellationToken cancellationToken = default);
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

    private Helpers.MsQuicDatagramChannel? _datagramChannel;

    /// <summary>
    /// Gets the native MsQuic RFC 9221 unreliable datagram channel attached to this connection.
    /// </summary>
    public Helpers.MsQuicDatagramChannel? DatagramChannel
    {
        get
        {
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
        return DatagramChannel?.Send(datagram) ?? false;
    }

    /// <summary>
    /// Queries real-time performance telemetry and protocol metadata from this connection.
    /// </summary>
    /// <param name="telemetry">When this method returns, contains the populated <see cref="Helpers.QuicConnectionTelemetry"/>, or <c>null</c> if the query failed.</param>
    /// <returns><c>true</c> if telemetry was successfully retrieved; otherwise, <c>false</c>.</returns>
    public bool TryGetTelemetry(out Helpers.QuicConnectionTelemetry telemetry)
    {
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
        return NativeConnection != null && Helpers.MsQuicTuner.TrySetDscp(NativeConnection, priority);
    }

    /// <summary>
    /// Dynamically switches congestion control algorithm (e.g. to BBR).
    /// </summary>
    public bool TrySetCongestionControl(Helpers.QuicCongestionAlgorithm algorithm)
    {
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
        Helpers.MsQuicTuner.TrySendTransportPing(this);

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

public sealed class QuicStream : Stream
{
    public const byte DefaultPriority = 127;

    private readonly IQuicStreamTransport _transport;
    private readonly Stream _stream;
    private int _disposed;

    internal QuicStream(IQuicStreamTransport transport)
    {
        _transport = transport ??
            throw new ArgumentNullException(nameof(transport));

        _stream = transport.Stream ??
            throw new ArgumentNullException(nameof(transport.Stream));
    }

    public long Id => _transport.Id;

    public QuicStreamType Type => _transport.Type;

    public Task ReadsClosed => _transport.ReadsClosed;

    public Task WritesClosed => _transport.WritesClosed;

    public byte Priority
    {
        get => _transport.Priority;
        set => _transport.Priority = value;
    }

    /// <summary>
    /// Gets or sets the 16-bit stream priority (0x0000 to 0xFFFF) mapped to native MsQuic QUIC_PARAM_STREAM_PRIORITY.
    /// </summary>
    public ushort Priority16
    {
        get => _transport.Priority16;
        set => _transport.Priority16 = value;
    }

    /// <summary>
    /// Gets or sets the stream priority enum level (Lowest to Critical).
    /// </summary>
    public Helpers.QuicStreamPriority QuicPriority
    {
        get => (Helpers.QuicStreamPriority)_transport.Priority16;
        set => _transport.Priority16 = (ushort)value;
    }

    public override bool CanRead =>
        Volatile.Read(ref _disposed) == 0 &&
        _transport.CanRead &&
        _stream.CanRead;

    public override bool CanWrite =>
        Volatile.Read(ref _disposed) == 0 &&
        _transport.CanWrite &&
        _stream.CanWrite;

    public override bool CanSeek => false;

    public override bool CanTimeout => _stream.CanTimeout;

    public override int ReadTimeout
    {
        get => _stream.ReadTimeout;
        set => _stream.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => _stream.WriteTimeout;
        set => _stream.WriteTimeout = value;
    }

    public override long Length =>
        throw new NotSupportedException(
            "QuicStream does not support Length.");

    public override long Position
    {
        get => throw new NotSupportedException(
            "QuicStream does not support Position.");

        set => throw new NotSupportedException(
            "QuicStream does not support Position.");
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        _stream.Flush();
    }

    public override Task FlushAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _stream.FlushAsync(cancellationToken);
    }

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        EnsureReadable();
        return _stream.Read(buffer, offset, count);
    }

    public override int Read(
        Span<byte> buffer)
    {
        EnsureReadable();
        return _stream.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        return _stream.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        EnsureReadable();
        return _stream.ReadAsync(
            buffer,
            offset,
            count,
            cancellationToken);
    }

    public override void Write(
        byte[] buffer,
        int offset,
        int count)
    {
        EnsureWritable();
        _stream.Write(buffer, offset, count);
    }

    public override void Write(
        ReadOnlySpan<byte> buffer)
    {
        EnsureWritable();
        _stream.Write(buffer);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        return _stream.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        EnsureWritable();
        return _stream.WriteAsync(
            buffer,
            offset,
            count,
            cancellationToken);
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        bool completeWrites,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();

        await _stream
            .WriteAsync(buffer, cancellationToken)
            .ConfigureAwait(false);

        if (completeWrites)
            CompleteWrites();
    }

    public void CompleteWrites()
    {
        EnsureWritable();
        _transport.CompleteWrites();
    }

    public void Abort(
        QuicAbortDirection abortDirection,
        long errorCode)
    {
        ThrowIfDisposed();
        _transport.Abort(abortDirection, errorCode);
    }

    public void Abort(
        NativeQuicAbortDirection abortDirection,
        long errorCode) =>
        Abort(Map(abortDirection), errorCode);

    public override long Seek(
        long offset,
        SeekOrigin origin) =>
        throw new NotSupportedException(
            "QuicStream is not seekable.");

    public override void SetLength(
        long value) =>
        throw new NotSupportedException(
            "QuicStream does not support SetLength.");

    protected override void Dispose(bool disposing)
    {
        if (disposing &&
            Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _transport.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _transport.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private void EnsureReadable()
    {
        ThrowIfDisposed();

        if (!_transport.CanRead || !_stream.CanRead)
        {
            throw new InvalidOperationException(
                "Reading is not allowed on this QUIC stream.");
        }
    }

    private void EnsureWritable()
    {
        ThrowIfDisposed();

        if (!_transport.CanWrite || !_stream.CanWrite)
        {
            throw new InvalidOperationException(
                "Writing is not allowed on this QUIC stream.");
        }
    }

    private static QuicAbortDirection Map(
        NativeQuicAbortDirection direction)
    {
        QuicAbortDirection mapped = 0;

        if ((direction & NativeQuicAbortDirection.Read) != 0)
            mapped |= QuicAbortDirection.Read;

        if ((direction & NativeQuicAbortDirection.Write) != 0)
            mapped |= QuicAbortDirection.Write;

        if (mapped == 0)
            throw new ArgumentOutOfRangeException(nameof(direction));

        return mapped;
    }
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
        NativeQuicConnection.IsSupported;

    public async ValueTask<IQuicConnectionTransport> ConnectAsync(
        NativeQuicClientConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        Helpers.MsQuicTuner.EnsureOptimalConfiguration();

        NativeQuicConnection connection =
            await NativeQuicConnection
                .ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);

        Helpers.MsQuicTuner.EnsureOptimalConfiguration();
        Helpers.MsQuicTuner.TryApplyOptimalTuning(connection);

        return new NativeConnectionTransport(connection);
    }
}

internal sealed class NativeConnectionTransport :
    IQuicConnectionTransport
{
    private readonly NativeQuicConnection _connection;
    private int _disposed;

    internal NativeConnectionTransport(
        NativeQuicConnection connection)
    {
        _connection = connection ??
            throw new ArgumentNullException(nameof(connection));
    }

    internal NativeQuicConnection InnerConnection =>
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

        NativeQuicStream stream =
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

        NativeQuicStream stream =
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private static NativeQuicStreamType Map(
        QuicStreamType type) =>
        type switch
        {
            QuicStreamType.Unidirectional =>
                NativeQuicStreamType.Unidirectional,

            QuicStreamType.Bidirectional =>
                NativeQuicStreamType.Bidirectional,

            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
}

internal sealed class NativeStreamTransport :
    IQuicStreamTransport
{
    private readonly NativeQuicStream _stream;
    private byte _fallbackPriority = QuicStream.DefaultPriority;
    private int _disposed;

    internal NativeStreamTransport(
        NativeQuicStream stream)
    {
        _stream = stream ??
            throw new ArgumentNullException(nameof(stream));
    }

    public long Id =>
        _stream.Id;

    public QuicStreamType Type =>
        _stream.Type switch
        {
            NativeQuicStreamType.Unidirectional =>
                QuicStreamType.Unidirectional,

            NativeQuicStreamType.Bidirectional =>
                QuicStreamType.Bidirectional,

            _ => throw new InvalidOperationException(
                $"Unknown native QUIC stream type: {_stream.Type}.")
        };

    public bool CanRead =>
        _stream.CanRead;

    public bool CanWrite =>
        _stream.CanWrite;

    public Stream Stream =>
        _stream;

    public Task ReadsClosed =>
        _stream.ReadsClosed;

    public Task WritesClosed =>
        _stream.WritesClosed;

    public ushort Priority16
    {
        get
        {
            if (Helpers.MsQuicTuner.TryGetStreamPriority(_stream, out ushort prio))
            {
                return prio;
            }
            return (ushort)((_fallbackPriority << 8) | _fallbackPriority);
        }
        set
        {
            _fallbackPriority = (byte)(value >> 8);
            Helpers.MsQuicTuner.TrySetStreamPriority(_stream, value);
        }
    }

    public byte Priority
    {
        get => (byte)(Priority16 >> 8);
        set => Priority16 = (ushort)((value << 8) | value);
    }

    public void CompleteWrites()
    {
        ThrowIfDisposed();
        _stream.CompleteWrites();
    }

    public void Abort(
        QuicAbortDirection abortDirection,
        long errorCode)
    {
        ThrowIfDisposed();

        _stream.Abort(
            Map(abortDirection),
            errorCode);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private static NativeQuicAbortDirection Map(
        QuicAbortDirection direction)
    {
        NativeQuicAbortDirection mapped = 0;

        if ((direction & QuicAbortDirection.Read) != 0)
            mapped |= NativeQuicAbortDirection.Read;

        if ((direction & QuicAbortDirection.Write) != 0)
            mapped |= NativeQuicAbortDirection.Write;

        if (mapped == 0)
            throw new ArgumentOutOfRangeException(nameof(direction));

        return mapped;
    }
}
