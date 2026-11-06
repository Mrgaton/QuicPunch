using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using QuicPunch;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicNet.VirtualDisk;

public sealed class VirtualDiskHandler(VirtualDiskService service) : QuicPunch.QuicPunch.IProtocolHandler, IDisposable
{
    public static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-000000000009");
    public Guid ProtocolId => Id;
    public string ProtocolName => "Disco virtual";
    public IReadOnlyCollection<Guid> ConnectedPeers => _sessions.Keys.ToArray();
    private const int MaxFrameBytes = 512 * 1024;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private sealed class Session(PeerInfo peer, Stream stream)
    {
        public readonly PeerInfo Peer = peer;
        public readonly Stream Stream = stream;
        public readonly SemaphoreSlim Writer = new(1, 1);
        public readonly SemaphoreSlim Requests = new(8, 8);
        public readonly SemaphoreSlim Outbound = new(8, 8);
        public readonly ConcurrentDictionary<Guid, TaskCompletionSource<DiskReply>> Pending = new();
        public readonly CancellationTokenSource Lifetime = new();
    }

    public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;

    public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
    {
        var session = new Session(peer, stream);
        if (!_sessions.TryAdd(peer.Id, session)) { stream.Dispose(); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Lifetime.Token);
        try
        {
            byte[] header = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(header, lifetime.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaxFrameBytes) throw new InvalidDataException("Invalid virtual disk frame.");
                byte[] bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, lifetime.Token).ConfigureAwait(false);
                var message = JsonSerializer.Deserialize(bytes, DiskJsonContext.Default.DiskWire) ?? throw new InvalidDataException();
                if (message.Response)
                {
                    if (message.Reply != null && session.Pending.TryRemove(message.Id, out var pending)) pending.TrySetResult(message.Reply);
                    continue;
                }
                if (message.Request == null) throw new InvalidDataException();
                // Never block the frame reader on a response write: both sides can make requests at once.
                if (!session.Requests.Wait(0)) throw new InvalidDataException("Too many outstanding disk operations.");
                _ = RespondAsync(session, message, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<Guid, Session>(peer.Id, session));
            session.Lifetime.Cancel();
            foreach (var pending in session.Pending.Values) pending.TrySetException(new IOException("Disco virtual desconectado."));
            stream.Dispose();
        }
    }

    private async Task RespondAsync(Session session, DiskWire message, CancellationToken ct)
    {
        try
        {
            // The actor comes exclusively from the authenticated transport, never the message.
            DiskReply reply = session.Peer.TryGetCertificateHash(out var hash)
                ? await Task.Run(() => service.Execute(Convert.ToBase64String(hash), message.Request!), ct).ConfigureAwait(false)
                : new(false, "Identidad no autenticada.", "Denied");
            await SendAsync(session, new(message.Id, true, Reply: reply), ct).ConfigureAwait(false);
        }
        catch (Exception) { session.Lifetime.Cancel(); try { session.Stream.Dispose(); } catch { } }
        finally { session.Requests.Release(); }
    }

    public async Task<DiskReply> RequestAsync(Guid peer, DiskRequest request, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(peer, out var session)) throw new IOException("Conecta primero el disco virtual del contacto.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await session.Outbound.WaitAsync(deadline.Token).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var pending = new TaskCompletionSource<DiskReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pending[id] = pending;
        try
        {
            await SendAsync(session, new(id, false, Request: request), deadline.Token).ConfigureAwait(false);
            return await pending.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally { session.Pending.TryRemove(id, out _); session.Outbound.Release(); }
    }

    private static async Task SendAsync(Session session, DiskWire message, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, DiskJsonContext.Default.DiskWire);
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("Disk frame too large.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await session.Writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            // Once a header is written, caller cancellation must not leave a partial frame in the stream.
            await session.Stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await session.Stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await session.Stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch { session.Lifetime.Cancel(); session.Stream.Dispose(); throw; }
        finally { session.Writer.Release(); }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values) { session.Lifetime.Cancel(); session.Stream.Dispose(); }
        _sessions.Clear();
    }
}
