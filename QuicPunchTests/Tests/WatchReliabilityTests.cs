using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using QuicNet.Watch.Domain;
using QuicNet.Watch.Protocols;
using QuicNet.Watch.Services;
using QuicPunch;

namespace QuicPunchTests.Tests;

internal static class WatchReliabilityTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static PeerInfo Peer() => new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "qp-watch-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await AdmissionAndCleanup(root);
            await GlobalAdmission();
            await TransferAndCache(root);
            await RoomLifecycle(root);
            await TorRooms(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] Request(Guid media, Guid request)
    {
        byte[] bytes = new byte[49]; bytes[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), 44);
        media.TryWriteBytes(bytes.AsSpan(5)); request.TryWriteBytes(bytes.AsSpan(21));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(45), 65536);
        return bytes;
    }

    private static async Task AdmissionAndCleanup(string root)
    {
        using var peer = Peer();
        await using var transfer = new MediaTransferHandler();
        using var library = new MediaLibrary(transfer, Path.Combine(root, "bounded-cache"));
        string file = Path.Combine(root, "source.bin");
        File.WriteAllBytes(file, new byte[65536]);
        var manifest = library.SelectLocalMedia(file, Guid.NewGuid());
        library.RemoteReadAllowed = (p, m) => p == peer.Id && m == manifest.MediaId;
        using var wire = new ControlledStream();
        Task session = transfer.HandleAsync(null!, wire, peer, default);
        Guid first = Guid.NewGuid();
        wire.Push(Request(manifest.MediaId, first));
        for (int i = 1; i < 4; i++) wire.Push(Request(manifest.MediaId, Guid.NewGuid()));
        await Until(() => transfer.ActiveResponseCount == 4 && wire.WriteEntered.Task.IsCompleted);
        wire.Push(Request(manifest.MediaId, first)); // Duplicate must not create or replace a job.
        for (int i = 4; i < MediaTransferHandler.MaxSessionRequests; i++) wire.Push(Request(manifest.MediaId, Guid.NewGuid()));
        await Task.Delay(50);
        Check(transfer.ActiveResponseCount == 4 && !session.IsCompleted, "four active responses plus a bounded backlog; duplicate request is idempotent");
        wire.EndInput();
        await session.WaitAsync(TimeSpan.FromSeconds(5));
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Check(transfer.ActiveResponseCount == 0, "EOF cancels and joins all jobs before releasing the source file");

        using var flood = new ControlledStream();
        Task flooded = transfer.HandleAsync(null!, flood, peer, default);
        for (int i = 0; i < 1000; i++) flood.Push(Request(manifest.MediaId, Guid.NewGuid()));
        try { await flooded.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Unbounded requests accepted."); }
        catch (InvalidDataException) { }
        Check(transfer.ActiveResponseCount == 0, "1,000 pipelined requests close only the overloaded session and drain its work");

        using var oldWire = new ControlledStream();
        Task oldSession = transfer.HandleAsync(null!, oldWire, peer, default);
        oldWire.Push(Request(manifest.MediaId, Guid.NewGuid()));
        await oldWire.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var replacement = new ControlledStream();
        Task newSession = transfer.HandleAsync(null!, replacement, peer, default);
        await oldSession.WaitAsync(TimeSpan.FromSeconds(5));
        replacement.Push(Request(manifest.MediaId, Guid.NewGuid()));
        await replacement.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!newSession.IsCompleted, "replacing a session drains the old writer without closing the new session");
        replacement.EndInput();
        await newSession.WaitAsync(TimeSpan.FromSeconds(5));
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    private static async Task GlobalAdmission()
    {
        await using var handler = new MediaTransferHandler();
        int callbacks = 0;
        handler.RangeRequested = async (_, _, ct) =>
        {
            Interlocked.Increment(ref callbacks);
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { Interlocked.Decrement(ref callbacks); }
        };
        var peers = Enumerable.Range(0, 6).Select(_ => Peer()).ToArray();
        var streams = peers.Select(_ => new ControlledStream()).ToArray();
        var tasks = peers.Select((p, i) => handler.HandleAsync(null!, streams[i], p, default)).ToArray();
        for (int i = 0; i < peers.Length; i++)
            for (int n = 0; n < 4; n++) streams[i].Push(Request(Guid.NewGuid(), Guid.NewGuid()));
        await Until(() => callbacks == MediaTransferHandler.MaxGlobalRequests);
        Check(handler.ActiveResponseCount == 16 && callbacks == 16, "global admission bounds active range callbacks across peers");
        foreach (var stream in streams) stream.EndInput();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Check(handler.ActiveResponseCount == 0 && callbacks == 0, "global reservations are returned after simultaneous disconnects");
        foreach (var stream in streams) stream.Dispose();
        foreach (var peer in peers) peer.Dispose();
    }

    private static async Task<(TcpClient A, TcpClient B)> Pair()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var a = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await a.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        return (a, await accept);
    }

    private static async Task TransferAndCache(string root)
    {
        var pair = await Pair(); using var a = pair.A; using var b = pair.B;
        using var local = Peer(); using var remote = Peer();
        await using var sourceTransfer = new MediaTransferHandler();
        await using var receiver = new MediaTransferHandler();
        using var sourceLibrary = new MediaLibrary(sourceTransfer, Path.Combine(root, "source-cache"));
        string cacheRoot = Path.Combine(root, "old-cache"); Directory.CreateDirectory(cacheRoot);
        string abandoned = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N") + ".cache");
        File.WriteAllBytes(abandoned, [1, 2, 3]);
        string preserved = Path.Combine(cacheRoot, "notes.txt"); File.WriteAllText(preserved, "keep");
        using var cache = new MediaLibrary(receiver, cacheRoot);
        Check(!File.Exists(abandoned) && File.Exists(preserved), "startup reclaims only owned legacy cache files");
        byte[] data = RandomNumberGenerator.GetBytes(6 * 1024 * 1024);
        string file = Path.Combine(root, "movie.bin"); await File.WriteAllBytesAsync(file, data);
        var manifest = sourceLibrary.SelectLocalMedia(file, local.Id);
        sourceLibrary.RemoteReadAllowed = (p, m) => p == remote.Id && m == manifest.MediaId;
        var serve = sourceTransfer.RangeRequested!;
        int served = 0;
        sourceTransfer.RangeRequested = async (p, request, ct) => { Interlocked.Increment(ref served); await serve(p, request, ct); };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task sourceSession = sourceTransfer.HandleAsync(null!, a.GetStream(), remote, stop.Token);
        Task receiverSession = receiver.HandleAsync(null!, b.GetStream(), local, stop.Token);
        cache.RegisterRemoteMedia(manifest);
        Check(!Directory.GetFiles(cacheRoot, "*.cache").Any() && cache.RetainedBytes == 0, "an announcement reserves neither disk nor payload memory");
        for (int i = 0; i < 6; i++)
        {
            byte[] chunk = await cache.ReadRangeAsync(manifest.MediaId, i * 1024L * 1024, 1024 * 1024, stop.Token);
            Check(chunk.AsSpan().SequenceEqual(data.AsSpan(i * 1024 * 1024, chunk.Length)), "real duplex media range passes integrity verification " + i);
        }
        int requests = served;
        _ = await cache.ReadRangeAsync(manifest.MediaId, 5 * 1024L * 1024, 100, stop.Token);
        Check(served == requests && cache.RetainedBytes <= MediaLibrary.CacheBytes, "verified chunks are reused within the byte budget");
        var unaligned = await cache.ReadRangeAsync(manifest.MediaId, 3 * 1024L * 1024 - 31, 100, stop.Token);
        Check(unaligned.AsSpan().SequenceEqual(data.AsSpan(3 * 1024 * 1024 - 31, 100)), "reads spanning chunk boundaries retain exact bytes");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sourceTransfer.RangeRequested = async (_, _, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        };
        cache.RegisterRemoteMedia(manifest with { MediaId = Guid.NewGuid() });
        var suspended = manifest with { MediaId = Guid.NewGuid() };
        cache.RegisterRemoteMedia(suspended);
        Task<byte[]> firstRead = cache.ReadRangeAsync(suspended.MediaId, 0, 32, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<byte[]> queuedRead = cache.ReadRangeAsync(suspended.MediaId, 0, 32, stop.Token);
        cache.KeepRemoteMedia(manifest.MediaId);
        foreach (Task read in new Task[] { firstRead, queuedRead })
        {
            try { await read.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Retired cache read survived."); }
            catch (OperationCanceledException) { }
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(cache.GetManifest(suspended.MediaId) == null, "retirement cancels active and queued reads and forwards Cancel to the origin");
        sourceTransfer.RangeRequested = serve;
        byte[] healthy = await receiver.RequestRangeAsync(local.Id, manifest.MediaId, 0, 32, stop.Token);
        Check(healthy.AsSpan().SequenceEqual(data.AsSpan(0, 32)), "range cancellation leaves the duplex protocol usable");

        sourceLibrary.RemoteReadAllowed = (_, _) => false;
        try { await receiver.RequestRangeAsync(local.Id, manifest.MediaId, 0, 32, stop.Token); throw new Exception("Revoked media access was served."); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        sourceLibrary.RemoteReadAllowed = (_, _) => true;
        try { await receiver.RequestRangeAsync(local.Id, manifest.MediaId, long.MaxValue, 32, stop.Token); throw new Exception("Out-of-bounds media range was served."); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        healthy = await receiver.RequestRangeAsync(local.Id, manifest.MediaId, 0, 32, stop.Token);
        Check(healthy.AsSpan().SequenceEqual(data.AsSpan(0, 32)), "revoked access and invalid ranges are rejected without damaging the media session");

        for (int i = 0; i < 1000; i++) cache.RegisterRemoteMedia(manifest with { MediaId = Guid.NewGuid() });
        Check(cache.RemoteCacheCount <= MediaLibrary.MaximumPeerCaches, "1,000 announcements remain within the origin quota");
        for (int i = 0; i < 20; i++) cache.RegisterRemoteMedia(manifest with { MediaId = Guid.NewGuid(), SourcePeerId = Guid.NewGuid() });
        Check(cache.RemoteCacheCount <= MediaLibrary.MaximumCaches, "independent origins share a global metadata quota");
        try { cache.RegisterRemoteMedia(manifest with { MediaId = Guid.NewGuid(), Size = MediaLibrary.MaximumMediaBytes + 1 }); throw new Exception("Oversized manifest accepted."); }
        catch (InvalidDataException) { }
        cache.KeepRemoteMedia(null);
        Check(cache.RemoteCacheCount == 0 && cache.RetainedBytes == 0, "leaving a room releases every remote cache");
        stop.Cancel();
        await Task.WhenAll(sourceSession, receiverSession).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task RoomLifecycle(string root)
    {
        using var owner = Peer();
        using var control = new WatchControlHandler();
        using var transfer = new MediaTransferHandler();
        using var library = new MediaLibrary(transfer, Path.Combine(root, "room-lifecycle"));
        Guid local = Guid.NewGuid();
        var coordinator = new RoomCoordinator(control, library, local);
        var invite = new WatchWireMessage("room_invite", Guid.NewGuid(), owner.Id, TargetPeerId: local);
        await coordinator.HandleMessageAsync(owner, invite with { SenderPeerId = Guid.NewGuid() });
        await coordinator.HandleMessageAsync(owner, invite with { TargetPeerId = Guid.NewGuid() });
        Check(coordinator.Snapshot() == null, "WAN invitations reject incorrect sender and target identities");
        await coordinator.HandleMessageAsync(owner, invite);
        Check(coordinator.LocalPeerId == local && coordinator.Snapshot()!.Participants.ContainsKey(local), "WAN invitation retains the local WAN identity");
        Guid last = Guid.Empty;
        for (int i = 1; i <= 1000; i++)
        {
            last = Guid.NewGuid();
            await coordinator.HandleMessageAsync(owner, invite with
            { Type = "media", MediaId = last, MediaSize = 4096, ChunkSize = 4096, Revision = i });
            if (library.RemoteCacheCount != 1 || library.RetainedBytes != 0) throw new Exception("Room media changes retain old caches.");
        }
        Check(library.GetManifest(last) != null, "1,000 room media changes retain only current metadata and no payload");
        await coordinator.HandleMessageAsync(owner, invite with { Type = "room_closed" });
        Check(coordinator.Snapshot() == null && library.RemoteCacheCount == 0, "room_closed retires the room's caches");
        await coordinator.HandleMessageAsync(owner, invite with { RoomId = Guid.NewGuid() });
        await coordinator.LeaveAsync(default);
        Check(coordinator.Snapshot() == null && library.RemoteCacheCount == 0, "a fresh WAN invitation and local leave preserve cleanup");
    }

    private static async Task TorRooms(string root)
    {
        var pair = await Pair(); using var a = pair.A; using var b = pair.B;
        using var alice = Peer(); using var bob = Peer();
        alice.ActiveTransport = bob.ActiveTransport = QuicPunch.QuicPunch.TransportType.Tor;
        Guid wanAlice = Guid.NewGuid(), wanBob = Guid.NewGuid();
        using var ca = new WatchControlHandler(); using var cb = new WatchControlHandler();
        ca.OnLocalIdentityProviderAttached(_ => alice); cb.OnLocalIdentityProviderAttached(_ => bob);
        using var ma = new MediaTransferHandler(); using var mb = new MediaTransferHandler();
        using var la = new MediaLibrary(ma, Path.Combine(root, "tor-a"));
        using var lb = new MediaLibrary(mb, Path.Combine(root, "tor-b"));
        var ra = new RoomCoordinator(ca, la, wanAlice); var rb = new RoomCoordinator(cb, lb, wanBob);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task ta = ca.HandleAsync(null!, a.GetStream(), bob, stop.Token);
        Task tb = cb.HandleAsync(null!, b.GetStream(), alice, stop.Token);
        var invalid = new WatchWireMessage("room_invite", Guid.NewGuid(), alice.Id, TargetPeerId: wanBob);
        await rb.HandleMessageAsync(alice, invalid);
        await rb.HandleMessageAsync(alice, invalid with { SenderPeerId = Guid.NewGuid(), TargetPeerId = bob.Id });
        Check(rb.Snapshot() == null, "WAN target is rejected on an authenticated Tor channel");
        WatchRoom room = await ra.CreateRoomAsync("Tor fixture", [bob.Id], stop.Token);
        await Until(() => rb.Snapshot() != null);
        Check(room.OwnerPeerId == alice.Id && room.LocalPeerId == alice.Id && rb.LocalPeerId == bob.Id,
            "real control frames invite Tor identities without requiring WAN IDs");
        await ra.SetPermissionAsync(bob.Id, ParticipantRole.Controller, stop.Token);
        await Until(() => rb.Snapshot()!.Participants[bob.Id] == ParticipantRole.Controller);
        await rb.RequestPlaybackAsync(false, 42, 1, stop.Token);
        await Until(() => ra.Snapshot()!.Playback?.PositionSeconds == 42);
        Check(ra.Snapshot()!.Participants.ContainsKey(wanAlice) == false && rb.Snapshot()!.Participants.ContainsKey(wanBob) == false,
            "Tor permission and playback use only the room's local identity");
        try { await ra.InviteAsync(Guid.NewGuid(), stop.Token); throw new Exception("Mixed identity room accepted."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("WAN y Tor")) { }
        await rb.LeaveAsync(stop.Token);
        await Until(() => !ra.Snapshot()!.Participants.ContainsKey(bob.Id));
        Check(rb.Snapshot() == null, "Tor leave removes the correct participant");
        stop.Cancel();
        try { await Task.WhenAll(ta, tb).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException) when (stop.IsCancellationRequested) { }
    }

    private sealed class ControlledStream : Stream
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        private ReadOnlyMemory<byte> _current;
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Push(byte[] bytes) => _input.Writer.TryWrite(bytes);
        public void EndInput() => _input.Writer.TryComplete();
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_current.IsEmpty)
            {
                if (!await _input.Reader.WaitToReadAsync(ct)) return 0;
                if (_input.Reader.TryRead(out var bytes)) _current = bytes;
            }
            int count = Math.Min(buffer.Length, _current.Length);
            _current[..count].CopyTo(buffer); _current = _current[count..]; return count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { WriteEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) EndInput(); base.Dispose(disposing); }
    }
}
