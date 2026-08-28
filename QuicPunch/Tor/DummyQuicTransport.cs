#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch;

public enum QuicConnectionRole
{
    Client = 0,
    Server = 1
}

public readonly record struct DummyQuicOpenLaneRequest(
    Guid ConnectionId,
    long StreamId,
    QuicStreamType Type);

public readonly record struct DummyQuicInboundLane(
    long StreamId,
    QuicStreamType Type,
    IDummyQuicLane Lane);

public interface IDummyQuicLaneProvider : IAsyncDisposable
{
    EndPoint? LocalEndPoint => null;
    EndPoint? RemoteEndPoint => null;

    ValueTask<IDummyQuicLane> OpenOutboundLaneAsync(
        DummyQuicOpenLaneRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<DummyQuicInboundLane> AcceptInboundLaneAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);

    ValueTask CloseAsync(
        long errorCode,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

public interface IDummyQuicLane : IDisposable, IAsyncDisposable
{
    Stream Stream { get; }

    bool CanRead { get; }
    bool CanWrite { get; }

    Task ReadsClosed { get; }
    Task WritesClosed { get; }

    void CompleteWrites();

    void Abort(
        QuicAbortDirection abortDirection,
        long errorCode);
}

public sealed class DummyQuicConnectionTransport : IQuicConnectionTransport
{
    private const long MaxQuicStreamId = (1L << 62) - 1;

    private readonly IDummyQuicLaneProvider _provider;
    private readonly QuicConnectionRole _role;
    private readonly ConcurrentDictionary<long, byte> _seenInboundIds = new();

    private long _nextBidirectionalIndex = -1;
    private long _nextUnidirectionalIndex = -1;
    private int _disposed;
    private int _closed;

    public DummyQuicConnectionTransport(
        IDummyQuicLaneProvider provider,
        QuicConnectionRole role,
        Guid? connectionId = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _role = role;
        ConnectionId = connectionId ?? Guid.NewGuid();
    }

    public Guid ConnectionId { get; }

    public QuicPunch.TransportType TransportType => QuicPunch.TransportType.Tor;

    public QuicConnectionRole Role => _role;

    public EndPoint? LocalEndPoint => _provider.LocalEndPoint;

    public EndPoint? RemoteEndPoint => _provider.RemoteEndPoint;

    public async ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(
        QuicStreamType type,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();

        long streamId = AllocateLocalStreamId(type);

        var request = new DummyQuicOpenLaneRequest(
            ConnectionId,
            streamId,
            type);

        IDummyQuicLane lane =
            await _provider
                .OpenOutboundLaneAsync(request, cancellationToken)
                .ConfigureAwait(false);

        ArgumentNullException.ThrowIfNull(lane);

        ValidateLaneSemantics(
            lane,
            type,
            openedLocally: true);

        return new DummyQuicStreamTransport(
            lane,
            streamId,
            type);
    }

    public async ValueTask<IQuicStreamTransport> AcceptInboundStreamAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();

        DummyQuicInboundLane inbound =
            await _provider
                .AcceptInboundLaneAsync(ConnectionId, cancellationToken)
                .ConfigureAwait(false);

        ArgumentNullException.ThrowIfNull(inbound.Lane);

        ValidateInboundStreamId(
            inbound.StreamId,
            inbound.Type);

        if (!_seenInboundIds.TryAdd(inbound.StreamId, 0))
        {
            throw new InvalidOperationException(
                $"Duplicate inbound QUIC-like stream id {inbound.StreamId}.");
        }

        ValidateLaneSemantics(
            inbound.Lane,
            inbound.Type,
            openedLocally: false);

        return new DummyQuicStreamTransport(
            inbound.Lane,
            inbound.StreamId,
            inbound.Type);
    }

    public async ValueTask CloseAsync(
        long errorCode,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        await _provider
            .CloseAsync(errorCode, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _provider.DisposeAsync().ConfigureAwait(false);
    }

    public static long GetFirstRemoteStreamId(
        QuicConnectionRole localRole,
        QuicStreamType type)
    {
        int remoteInitiatorBit =
            localRole == QuicConnectionRole.Client ? 1 : 0;

        int directionBit =
            type == QuicStreamType.Unidirectional ? 0b10 : 0;

        return remoteInitiatorBit | directionBit;
    }

    private long AllocateLocalStreamId(
        QuicStreamType type)
    {
        long index = type switch
        {
            QuicStreamType.Bidirectional =>
                Interlocked.Increment(ref _nextBidirectionalIndex),

            QuicStreamType.Unidirectional =>
                Interlocked.Increment(ref _nextUnidirectionalIndex),

            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        if (index > (MaxQuicStreamId >> 2))
            throw new InvalidOperationException("QUIC-like stream id space exhausted.");

        long initiatorBit =
            _role == QuicConnectionRole.Client ? 0L : 1L;

        long directionBit =
            type == QuicStreamType.Unidirectional ? 2L : 0L;

        return (index << 2) | initiatorBit | directionBit;
    }

    private void ValidateInboundStreamId(
        long streamId,
        QuicStreamType type)
    {
        if (streamId < 0 || streamId > MaxQuicStreamId)
            throw new InvalidOperationException("Invalid 62-bit QUIC-like stream id.");

        int initiatorBit = (int)(streamId & 1L);
        int expectedRemoteInitiatorBit =
            _role == QuicConnectionRole.Client ? 1 : 0;

        if (initiatorBit != expectedRemoteInitiatorBit)
        {
            throw new InvalidOperationException(
                $"Inbound stream {streamId} has the wrong initiator bit for role {_role}.");
        }

        bool idSaysUnidirectional = (streamId & 2L) != 0;
        bool typeSaysUnidirectional =
            type == QuicStreamType.Unidirectional;

        if (idSaysUnidirectional != typeSaysUnidirectional)
        {
            throw new InvalidOperationException(
                $"Inbound stream {streamId} type does not match its QUIC stream-id direction bit.");
        }
    }

    private static void ValidateLaneSemantics(
        IDummyQuicLane lane,
        QuicStreamType type,
        bool openedLocally)
    {
        if (lane.Stream is null)
            throw new InvalidOperationException("Dummy lane returned a null Stream.");

        if (type == QuicStreamType.Bidirectional)
        {
            if (!lane.CanRead || !lane.CanWrite)
            {
                throw new InvalidOperationException(
                    "A bidirectional dummy lane must be readable and writable.");
            }

            return;
        }

        if (openedLocally)
        {
            if (lane.CanRead || !lane.CanWrite)
            {
                throw new InvalidOperationException(
                    "A locally opened unidirectional lane must be write-only.");
            }
        }
        else
        {
            if (!lane.CanRead || lane.CanWrite)
            {
                throw new InvalidOperationException(
                    "A remotely opened unidirectional lane must be read-only.");
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();

        if (Volatile.Read(ref _closed) != 0)
            throw new InvalidOperationException("The dummy QUIC connection is closed.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}

internal sealed class DummyQuicStreamTransport : IQuicStreamTransport
{
    private readonly IDummyQuicLane _lane;
    private byte _priority = QuicStream.DefaultPriority;
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

    public byte Priority
    {
        get => _priority;
        set => _priority = value;
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

public sealed class DelegateDummyQuicLaneProvider : IDummyQuicLaneProvider
{
    private readonly Func<DummyQuicOpenLaneRequest, CancellationToken, ValueTask<IDummyQuicLane>> _open;
    private readonly Func<Guid, CancellationToken, ValueTask<DummyQuicInboundLane>> _accept;
    private readonly Func<long, CancellationToken, ValueTask>? _close;
    private readonly Func<ValueTask>? _dispose;

    public DelegateDummyQuicLaneProvider(
        Func<DummyQuicOpenLaneRequest, CancellationToken, ValueTask<IDummyQuicLane>> open,
        Func<Guid, CancellationToken, ValueTask<DummyQuicInboundLane>> accept,
        EndPoint? localEndPoint = null,
        EndPoint? remoteEndPoint = null,
        Func<long, CancellationToken, ValueTask>? close = null,
        Func<ValueTask>? dispose = null)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _accept = accept ?? throw new ArgumentNullException(nameof(accept));
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        _close = close;
        _dispose = dispose;
    }

    public EndPoint? LocalEndPoint { get; }

    public EndPoint? RemoteEndPoint { get; }

    public ValueTask<IDummyQuicLane> OpenOutboundLaneAsync(
        DummyQuicOpenLaneRequest request,
        CancellationToken cancellationToken = default) =>
        _open(request, cancellationToken);

    public ValueTask<DummyQuicInboundLane> AcceptInboundLaneAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default) =>
        _accept(connectionId, cancellationToken);

    public ValueTask CloseAsync(
        long errorCode,
        CancellationToken cancellationToken = default) =>
        _close is null
            ? ValueTask.CompletedTask
            : _close(errorCode, cancellationToken);

    public ValueTask DisposeAsync() =>
        _dispose is null
            ? ValueTask.CompletedTask
            : _dispose();
}

public sealed class SingleStreamDummyQuicLaneProvider : IDummyQuicLaneProvider
{
    private readonly IDummyQuicLane _lane;
    private readonly bool _streamWasOpenedByRemote;
    private readonly QuicStreamType _type;
    private readonly long _inboundStreamId;
    private int _taken;

    public SingleStreamDummyQuicLaneProvider(
        IDummyQuicLane lane,
        QuicConnectionRole localRole,
        bool streamWasOpenedByRemote,
        QuicStreamType type)
    {
        _lane = lane ?? throw new ArgumentNullException(nameof(lane));
        _streamWasOpenedByRemote = streamWasOpenedByRemote;
        _type = type;
        _inboundStreamId =
            DummyQuicConnectionTransport.GetFirstRemoteStreamId(localRole, type);
    }

    public ValueTask<IDummyQuicLane> OpenOutboundLaneAsync(
        DummyQuicOpenLaneRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_streamWasOpenedByRemote)
        {
            throw new InvalidOperationException(
                "This single Stream was configured as inbound and cannot open an outbound lane.");
        }

        TakeOnce();

        if (request.Type != _type)
        {
            throw new InvalidOperationException(
                $"This single Stream was configured as {_type}, not {request.Type}.");
        }

        return ValueTask.FromResult(_lane);
    }

    public ValueTask<DummyQuicInboundLane> AcceptInboundLaneAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_streamWasOpenedByRemote)
        {
            throw new InvalidOperationException(
                "This single Stream was configured as outbound and cannot accept an inbound lane.");
        }

        TakeOnce();

        return ValueTask.FromResult(
            new DummyQuicInboundLane(
                _inboundStreamId,
                _type,
                _lane));
    }

    public ValueTask DisposeAsync() =>
        _lane.DisposeAsync();

    private void TakeOnce()
    {
        if (Interlocked.Exchange(ref _taken, 1) != 0)
        {
            throw new InvalidOperationException(
                "A SingleStreamDummyQuicLaneProvider can provide exactly one QuicStream.");
        }
    }
}
