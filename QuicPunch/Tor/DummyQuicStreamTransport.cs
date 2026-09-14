namespace QuicPunch;

internal sealed class DummyQuicStreamTransport : IQuicStreamTransport
{
    private readonly IDummyQuicLane _lane;
    private ushort _priority16 = (ushort)Helpers.QuicStreamPriority.Normal;
    private int _disposed;

    public DummyQuicStreamTransport(
        IDummyQuicLane lane,
        long id,
        QuicStreamType type)
    {
        _lane = lane ?? throw new ArgumentNullException(nameof(lane));
        Id = id;
        Type = type;
    }

    public long Id { get; }

    public QuicStreamType Type { get; }

    public bool CanRead => _lane.CanRead;

    public bool CanWrite => _lane.CanWrite;

    public Stream Stream => _lane.Stream;

    public Task ReadsClosed => _lane.ReadsClosed;

    public Task WritesClosed => _lane.WritesClosed;

    public ushort Priority16
    {
        get => _priority16;
        set => _priority16 = value;
    }

    public byte Priority
    {
        get => (byte)(_priority16 >> 8);
        set => _priority16 = (ushort)((value << 8) | value);
    }

    public void CompleteWrites()
    {
        ThrowIfDisposed();
        _lane.CompleteWrites();
    }

    public void Abort(
        QuicAbortDirection abortDirection,
        long errorCode)
    {
        ThrowIfDisposed();
        _lane.Abort(abortDirection, errorCode);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lane.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lane.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}