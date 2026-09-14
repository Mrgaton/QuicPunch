namespace QuicPunch;

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

    internal IQuicStreamTransport Transport => _transport;

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
        System.Net.Quic.QuicAbortDirection abortDirection,
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
        System.Net.Quic.QuicAbortDirection direction)
    {
        QuicAbortDirection mapped = 0;

        if ((direction & System.Net.Quic.QuicAbortDirection.Read) != 0)
            mapped |= QuicAbortDirection.Read;

        if ((direction & System.Net.Quic.QuicAbortDirection.Write) != 0)
            mapped |= QuicAbortDirection.Write;

        if (mapped == 0)
            throw new ArgumentOutOfRangeException(nameof(direction));

        return mapped;
    }
}