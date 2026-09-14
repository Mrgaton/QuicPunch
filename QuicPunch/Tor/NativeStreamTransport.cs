namespace QuicPunch;

internal sealed class NativeStreamTransport :
    IQuicStreamTransport
{
    private readonly System.Net.Quic.QuicStream _stream;
    private byte _fallbackPriority = QuicStream.DefaultPriority;
    private int _disposed;

    internal NativeStreamTransport(
        System.Net.Quic.QuicStream stream)
    {
        _stream = stream ??
                  throw new ArgumentNullException(nameof(stream));
    }

    public long Id =>
        _stream.Id;

    public QuicStreamType Type =>
        _stream.Type switch
        {
            System.Net.Quic.QuicStreamType.Unidirectional =>
                QuicStreamType.Unidirectional,

            System.Net.Quic.QuicStreamType.Bidirectional =>
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

    private static System.Net.Quic.QuicAbortDirection Map(
        QuicAbortDirection direction)
    {
        System.Net.Quic.QuicAbortDirection mapped = 0;

        if ((direction & QuicAbortDirection.Read) != 0)
            mapped |= System.Net.Quic.QuicAbortDirection.Read;

        if ((direction & QuicAbortDirection.Write) != 0)
            mapped |= System.Net.Quic.QuicAbortDirection.Write;

        if (mapped == 0)
            throw new ArgumentOutOfRangeException(nameof(direction));

        return mapped;
    }
}