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

    public static async ValueTask<QuicConnection> ConnectAsync(
        NativeQuicClientConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        IQuicConnectionTransport transport =
            await TransportFactory
                .ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);

        return new QuicConnection(transport);
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

        return new QuicConnection(transport);
    }

    public static QuicConnection CreateDummy(
        IDummyQuicLaneProvider provider,
        QuicConnectionRole role,
        Guid? connectionId = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return new QuicConnection(
            new DummyQuicConnectionTransport(
                provider,
                role,
                connectionId));
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
        return new QuicConnection(
            new NativeConnectionTransport(connection));
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

        NativeQuicConnection connection =
            await NativeQuicConnection
                .ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);

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

    public byte Priority
    {
        get => _fallbackPriority;
        set => _fallbackPriority = value;
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
