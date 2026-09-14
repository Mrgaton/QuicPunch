using System.Net.Sockets;

namespace QuicPunch;

public sealed class StreamDummyQuicLane : IDummyQuicLane
{
    private readonly TrackingStream _stream;
    private readonly Action? _completeWrites;
    private readonly Action<QuicAbortDirection, long>? _abort;
    private readonly TaskCompletionSource _readsClosed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _writesClosed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _disposed;
    private int _writesCompleted;

    public StreamDummyQuicLane(
        Stream stream,
        QuicStreamType type,
        bool openedByRemote,
        Action? completeWrites = null,
        Action<QuicAbortDirection, long>? abort = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        bool canRead;
        bool canWrite;

        if (type == QuicStreamType.Bidirectional)
        {
            canRead = true;
            canWrite = true;
        }
        else if (openedByRemote)
        {
            canRead = true;
            canWrite = false;
        }
        else
        {
            canRead = false;
            canWrite = true;
        }

        if (canRead && !stream.CanRead)
            throw new ArgumentException("The supplied Stream is not readable.", nameof(stream));

        if (canWrite && !stream.CanWrite)
            throw new ArgumentException("The supplied Stream is not writable.", nameof(stream));

        _completeWrites = completeWrites;
        _abort = abort;

        _stream = new TrackingStream(
            stream,
            canRead,
            canWrite,
            OnReadClosed,
            OnWriteFaulted);

        CanRead = canRead;
        CanWrite = canWrite;
    }

    public Stream Stream => _stream;

    public bool CanRead { get; }

    public bool CanWrite { get; }

    public Task ReadsClosed => _readsClosed.Task;

    public Task WritesClosed => _writesClosed.Task;

    public void CompleteWrites()
    {
        ThrowIfDisposed();

        if (!CanWrite)
            throw new InvalidOperationException("This lane is not writable.");

        if (Interlocked.Exchange(ref _writesCompleted, 1) != 0)
            return;

        try
        {
            _stream.Flush();

            if (_completeWrites is not null)
            {
                _completeWrites();
            }
            else if (_stream.Inner is NetworkStream networkStream)
            {
                networkStream.Socket.Shutdown(SocketShutdown.Send);
            }
            else
            {
                throw new NotSupportedException(
                    "This Stream cannot be half-closed automatically. Supply a completeWrites callback or an IDummyQuicLane implementation specific to your Tor stream class.");
            }

            _writesClosed.TrySetResult();
        }
        catch (Exception ex)
        {
            _writesClosed.TrySetException(ex);
            throw;
        }
    }

    public void Abort(
        QuicAbortDirection abortDirection,
        long errorCode)
    {
        ThrowIfDisposed();

        if (_abort is not null)
        {
            _abort(abortDirection, errorCode);
        }
        else if (_stream.Inner is NetworkStream networkStream)
        {
            SocketShutdown shutdown = abortDirection switch
            {
                QuicAbortDirection.Read => SocketShutdown.Receive,
                QuicAbortDirection.Write => SocketShutdown.Send,
                QuicAbortDirection.Both => SocketShutdown.Both,
                _ => throw new ArgumentOutOfRangeException(nameof(abortDirection))
            };

            networkStream.Socket.Shutdown(shutdown);

            if (abortDirection == QuicAbortDirection.Both)
                _stream.Dispose();
        }
        else
        {
            _stream.Dispose();
        }

        if ((abortDirection & QuicAbortDirection.Read) != 0)
            _readsClosed.TrySetCanceled();

        if ((abortDirection & QuicAbortDirection.Write) != 0)
            _writesClosed.TrySetCanceled();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stream.Dispose();
        _readsClosed.TrySetResult();
        _writesClosed.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stream.DisposeAsync().ConfigureAwait(false);
        _readsClosed.TrySetResult();
        _writesClosed.TrySetResult();
    }

    private void OnReadClosed(Exception? error)
    {
        if (error is null)
            _readsClosed.TrySetResult();
        else
            _readsClosed.TrySetException(error);
    }

    private void OnWriteFaulted(Exception error) =>
        _writesClosed.TrySetException(error);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private sealed class TrackingStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _canRead;
        private readonly bool _canWrite;
        private readonly Action<Exception?> _readClosed;
        private readonly Action<Exception> _writeFaulted;

        public TrackingStream(
            Stream inner,
            bool canRead,
            bool canWrite,
            Action<Exception?> readClosed,
            Action<Exception> writeFaulted)
        {
            _inner = inner;
            _canRead = canRead;
            _canWrite = canWrite;
            _readClosed = readClosed;
            _writeFaulted = writeFaulted;
        }

        public Stream Inner => _inner;

        public override bool CanRead => _canRead && _inner.CanRead;
        public override bool CanWrite => _canWrite && _inner.CanWrite;
        public override bool CanSeek => false;
        public override bool CanTimeout => _inner.CanTimeout;

        public override int ReadTimeout
        {
            get => _inner.ReadTimeout;
            set => _inner.ReadTimeout = value;
        }

        public override int WriteTimeout
        {
            get => _inner.WriteTimeout;
            set => _inner.WriteTimeout = value;
        }

        public override long Length =>
            throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            EnsureReadable();

            try
            {
                int read = _inner.Read(buffer, offset, count);
                if (read == 0)
                    _readClosed(null);
                return read;
            }
            catch (Exception ex)
            {
                _readClosed(ex);
                throw;
            }
        }

        public override int Read(Span<byte> buffer)
        {
            EnsureReadable();

            try
            {
                int read = _inner.Read(buffer);
                if (read == 0)
                    _readClosed(null);
                return read;
            }
            catch (Exception ex)
            {
                _readClosed(ex);
                throw;
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureReadable();

            try
            {
                int read = await _inner
                    .ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                    _readClosed(null);

                return read;
            }
            catch (Exception ex)
            {
                _readClosed(ex);
                throw;
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadArrayAsync(buffer, offset, count, cancellationToken);

        private async Task<int> ReadArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureReadable();

            try
            {
                int read = await _inner
                    .ReadAsync(buffer, offset, count, cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                    _readClosed(null);

                return read;
            }
            catch (Exception ex)
            {
                _readClosed(ex);
                throw;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWritable();

            try
            {
                _inner.Write(buffer, offset, count);
            }
            catch (Exception ex)
            {
                _writeFaulted(ex);
                throw;
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWritable();

            try
            {
                _inner.Write(buffer);
            }
            catch (Exception ex)
            {
                _writeFaulted(ex);
                throw;
            }
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureWritable();
            return WriteMemoryAsync(buffer, cancellationToken);
        }

        private async ValueTask WriteMemoryAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            try
            {
                await _inner
                    .WriteAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _writeFaulted(ex);
                throw;
            }
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureWritable();
            return WriteArrayAsync(buffer, offset, count, cancellationToken);
        }

        private async Task WriteArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            try
            {
                await _inner
                    .WriteAsync(buffer, offset, count, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _writeFaulted(ex);
                throw;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() =>
            _inner.DisposeAsync();

        private void EnsureReadable()
        {
            if (!CanRead)
                throw new NotSupportedException("This lane is not readable.");
        }

        private void EnsureWritable()
        {
            if (!CanWrite)
                throw new NotSupportedException("This lane is not writable.");
        }
    }
}