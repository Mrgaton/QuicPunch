using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using QuicPunch;
using QuicPunch.Helpers;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicNet.Watch.Protocols;

public sealed record MediaRangeRequest(Guid MediaId, Guid RequestId, long Offset, int Length);

public sealed class MediaTransferHandler : QuicPunch.QuicPunch.IProtocolHandler, IDisposable, IAsyncDisposable
{
    private const int MaxFrameBytes = 128 * 1024;
    public const int MaxRangeBytes = 1024 * 1024;
    private const int WireChunkBytes = 64 * 1024;
    internal const int MaxSessionRequests = 20;
    internal const int SessionWorkers = 4;
    internal const int MaxGlobalRequests = 16;
    private readonly SemaphoreSlim _servingSlots = new(MaxGlobalRequests, MaxGlobalRequests);
    private readonly SemaphoreSlim _fetchSlots = new(MaxGlobalRequests, MaxGlobalRequests);
    private readonly AsyncLocal<Session?> _responseSession = new();
    private readonly object _lifecycle = new();
    private readonly HashSet<Session> _liveSessions = new();
    private bool _disposed;
    internal int ActiveResponseCount => MaxGlobalRequests - _servingSlots.CurrentCount;

    private enum FrameType : byte
    {
        Request = 1,
        Data = 2,
        Complete = 3,
        Cancel = 4
    }

    public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000008");
    public string ProtocolName => "QuicWatch Media";
    public ushort StreamPriority => (ushort)QuicStreamPriority.Low;

    private sealed class Session
    {
        public PeerInfo Peer { get; }
        public Stream Stream { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public SemaphoreSlim FetchSlots { get; } = new(SessionWorkers, SessionWorkers);
        public CancellationTokenSource Lifetime { get; }
        public CancellationToken Token { get; }
        public Channel<MediaRangeRequest> Requests { get; } = Channel.CreateBounded<MediaRangeRequest>(MaxSessionRequests);
        public Dictionary<Guid, CancellationTokenSource> Serving { get; } = new();
        public object Gate { get; } = new();
        public Task[] Workers { get; set; } = [];
        private int _stopped;

        public Session(PeerInfo peer, Stream stream, CancellationToken ct)
        {
            Peer = peer;
            Stream = stream;
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Token = Lifetime.Token;
        }

        public async Task SendFrameAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (payload.Length > MaxFrameBytes) throw new InvalidDataException("Media frame is too large.");
            byte[] header = new byte[5];
            header[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, Token);
            await WriteLock.WaitAsync(waiting.Token).ConfigureAwait(false);
            try
            {
                waiting.Token.ThrowIfCancellationRequested();
                // A cancelled range may stop between frames, never halfway through a header/body.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await Stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                    if (!payload.IsEmpty) await Stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                    await Stream.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                catch { Stop(); throw; } // A partial frame cannot be followed by another frame.
            }
            finally
            {
                WriteLock.Release();
            }
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            try { Lifetime.Cancel(); } catch (AggregateException ex) { QuicPunchLog.Error("[QuicWatch] Session cancellation callback failed.", ex); }
            Requests.Writer.TryComplete();
            try { Stream.Dispose(); } catch { }
            // API callers may still unwind WaitAsync/Release. Managed gates live with their references.
        }
    }

    private async Task ServeRequestsAsync(Session session)
    {
        try
        {
            await foreach (var request in session.Requests.Reader.ReadAllAsync(session.Token).ConfigureAwait(false))
            {
                CancellationTokenSource job;
                lock (session.Gate) job = session.Serving[request.RequestId];
                bool admitted = false;
                try
                {
                    job.CancelAfter(TimeSpan.FromSeconds(30));
                    await _servingSlots.WaitAsync(job.Token).ConfigureAwait(false);
                    admitted = true;
                    _responseSession.Value = session;
                    if (RangeRequested is { } serve) await serve(session.Peer, request, job.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (!session.Token.IsCancellationRequested)
                        try { await session.SendFrameAsync(FrameType.Cancel, request.RequestId.ToByteArray(), session.Token).ConfigureAwait(false); } catch { }
                }
                finally
                {
                    _responseSession.Value = null;
                    if (admitted) _servingSlots.Release();
                    lock (session.Gate) session.Serving.Remove(request.RequestId);
                    job.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
    }

    private sealed class PendingRange
    {
        public Session Session { get; }
        public int ExpectedLength { get; }
        public MemoryStream Stream { get; }
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public object Sync { get; } = new();
        public bool Closed { get; set; }

        public PendingRange(Session session, int expectedLength)
        {
            Session = session;
            ExpectedLength = expectedLength;
            Stream = new MemoryStream(expectedLength);
        }
    }

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<Guid, PendingRange> _pending = new();

    public Func<PeerInfo, MediaRangeRequest, CancellationToken, Task>? RangeRequested { get; set; }
    public IReadOnlyCollection<Guid> ConnectedPeerIds => _sessions.Keys.ToArray();

    public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;

    public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
    {
        stream.SetQuicPriority(QuicStreamPriority.Low);
        Session session;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = new Session(peer, stream, ct);
            session.Workers = Enumerable.Range(0, SessionWorkers).Select(_ => ServeRequestsAsync(session)).ToArray();
            if (_sessions.TryRemove(peer.Id, out var previous)) previous.Stop();
            _sessions[peer.Id] = session;
            _liveSessions.Add(session);
        }
        var token = session.Token;
        byte[] header = new byte[5];

        try
        {
            while (!token.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                FrameType type = (FrameType)header[0];
                int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
                if (length < 0 || length > MaxFrameBytes) throw new InvalidDataException("Invalid media frame.");

                byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
                try
                {
                    if (length > 0) await stream.ReadExactlyAsync(rented.AsMemory(0, length), token).ConfigureAwait(false);
                    ProcessFrame(session, type, rented.AsMemory(0, length), token);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (IOException) when (token.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<Guid, Session>(peer.Id, session));
            foreach (PendingRange pending in _pending.Values.Where(p => ReferenceEquals(p.Session, session)))
                pending.Completion.TrySetException(new IOException("Media connection closed."));
            session.Stop();
            await Task.WhenAll(session.Workers).ConfigureAwait(false);
            lock (session.Gate)
            {
                foreach (var job in session.Serving.Values) job.Dispose();
                session.Serving.Clear();
            }
            lock (_lifecycle) _liveSessions.Remove(session);
            session.Lifetime.Dispose();
        }
    }

    private void ProcessFrame(Session session, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ReadOnlySpan<byte> span = payload.Span;
        switch (type)
        {
            case FrameType.Request when span.Length == 44:
            {
                var request = new MediaRangeRequest(
                    new Guid(span[..16]),
                    new Guid(span.Slice(16, 16)),
                    BinaryPrimitives.ReadInt64LittleEndian(span.Slice(32, 8)),
                    BinaryPrimitives.ReadInt32LittleEndian(span.Slice(40, 4)));
                if (request.MediaId == Guid.Empty || request.RequestId == Guid.Empty || request.Offset < 0 ||
                    request.Length is <= 0 or > MaxRangeBytes) throw new InvalidDataException("Invalid media request.");
                lock (session.Gate)
                {
                    if (session.Serving.ContainsKey(request.RequestId)) break; // Retransmission cannot create another job.
                    if (session.Serving.Count >= MaxSessionRequests) throw new InvalidDataException("Media request capacity exceeded.");
                    var job = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    session.Serving.Add(request.RequestId, job);
                    if (!session.Requests.Writer.TryWrite(request))
                    { session.Serving.Remove(request.RequestId); job.Dispose(); throw new InvalidDataException("Media request queue closed."); }
                }
                break;
            }
            case FrameType.Data when span.Length >= 20:
            {
                Guid requestId = new(span[..16]);
                int relativeOffset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(16, 4));
                if (!_pending.TryGetValue(requestId, out var pending) || !ReferenceEquals(pending.Session, session)) break;
                lock (pending.Sync)
                {
                    if (pending.Closed) break;
                    if (relativeOffset != pending.Stream.Length || pending.Stream.Length + span.Length - 20 > pending.ExpectedLength)
                    {
                        pending.Completion.TrySetException(new InvalidDataException("Out-of-order media data."));
                        break;
                    }
                    pending.Stream.Write(span[20..]);
                }
                break;
            }
            case FrameType.Complete when span.Length == 48:
            {
                Guid requestId = new(span[..16]);
                if (!_pending.TryGetValue(requestId, out var pending) || !ReferenceEquals(pending.Session, session)) break;
                byte[] bytes;
                lock (pending.Sync)
                {
                    if (pending.Closed) break;
                    bytes = pending.Stream.ToArray();
                }
                byte[] actual = SHA256.HashData(bytes);
                if (bytes.Length != pending.ExpectedLength || !CryptographicOperations.FixedTimeEquals(actual, span[16..]))
                    pending.Completion.TrySetException(new InvalidDataException("Media range integrity check failed."));
                else
                    pending.Completion.TrySetResult(bytes);
                break;
            }
            case FrameType.Cancel when span.Length == 16:
            {
                Guid requestId = new(span);
                if (_pending.TryGetValue(requestId, out var pending) && ReferenceEquals(pending.Session, session))
                    pending.Completion.TrySetCanceled();
                lock (session.Gate)
                    if (session.Serving.TryGetValue(requestId, out var serving)) serving.Cancel();
                break;
            }
        }
    }

    public async Task<byte[]> RequestRangeAsync(Guid peerId, Guid mediaId, long offset, int length, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(peerId, out var session)) throw new InvalidOperationException("Media connection is not active.");
        if (offset < 0 || length is <= 0 or > MaxRangeBytes) throw new ArgumentOutOfRangeException(nameof(length));

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        ct = lifetime.Token;
        await session.FetchSlots.WaitAsync(ct).ConfigureAwait(false);
        try { await _fetchSlots.WaitAsync(ct).ConfigureAwait(false); }
        catch { session.FetchSlots.Release(); throw; }

        Guid requestId = Guid.NewGuid();
        var pending = new PendingRange(session, length);
        if (!_pending.TryAdd(requestId, pending)) throw new InvalidOperationException("Could not allocate media request.");

        byte[] payload = new byte[44];
        mediaId.TryWriteBytes(payload.AsSpan(0, 16));
        requestId.TryWriteBytes(payload.AsSpan(16, 16));
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(32, 8), offset);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(40, 4), length);

        try
        {
            await session.SendFrameAsync(FrameType.Request, payload, ct).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
            lock (pending.Sync)
            {
                pending.Closed = true;
                pending.Stream.Dispose();
            }
            _fetchSlots.Release();
            session.FetchSlots.Release();
            if (!pending.Completion.Task.IsCompletedSuccessfully && !session.Token.IsCancellationRequested)
                try { await session.SendFrameAsync(FrameType.Cancel, requestId.ToByteArray(), session.Token).ConfigureAwait(false); } catch { }
        }
    }

    public async Task SendRangeAsync(Guid peerId, MediaRangeRequest request, Stream source, CancellationToken ct)
    {
        var session = _responseSession.Value;
        if (session == null && !_sessions.TryGetValue(peerId, out session)) return;
        if (session.Peer.Id != peerId) throw new InvalidOperationException("Media response belongs to another session.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        ct = lifetime.Token;
        source.Seek(request.Offset, SeekOrigin.Begin);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(WireChunkBytes);
        int remaining = request.Length;
        int relativeOffset = 0;

        try
        {
            while (remaining > 0)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Media source ended before the requested range.");
                hash.AppendData(buffer, 0, read);
                byte[] payload = new byte[20 + read];
                request.RequestId.TryWriteBytes(payload.AsSpan(0, 16));
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), relativeOffset);
                buffer.AsSpan(0, read).CopyTo(payload.AsSpan(20));
                await session.SendFrameAsync(FrameType.Data, payload, ct).ConfigureAwait(false);
                relativeOffset += read;
                remaining -= read;
            }

            byte[] complete = new byte[48];
            request.RequestId.TryWriteBytes(complete.AsSpan(0, 16));
            hash.GetHashAndReset().CopyTo(complete.AsSpan(16));
            await session.SendFrameAsync(FrameType.Complete, complete, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            foreach (Session session in _liveSessions) session.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task[] workers;
        lock (_lifecycle) workers = _liveSessions.SelectMany(s => s.Workers).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }
}
