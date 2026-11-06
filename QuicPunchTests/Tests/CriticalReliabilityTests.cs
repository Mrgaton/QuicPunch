using System.Collections;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using QuicNet.Protocols;
using QuicNet.WebUi;
using QuicNet.WebUi.Modules;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunch.Multiplexer;
using QuicPunch.Security;

namespace QuicPunchTests.Tests;

internal static class CriticalReliabilityTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException or IOException) { return; }
        throw new Exception("An invalid transfer was accepted.");
    }

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "qp-critical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await TestDownloads(root);
            await TestChatDispatch(root);
            await TestNativeChatFileTransfer(root);
            TestTrust(root);
            TestIdentity(root);
            TestSchnorr();
            TestNativeLease();
            await TestSessionReservations();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ChatFileDownload Download(string root, byte[] data, Guid peer, string? expectedHash = null, FileStream? stream = null)
    {
        Guid id = Guid.NewGuid();
        string part = Path.Combine(root, id + ".part");
        if (stream != null) part = stream.Name;
        return new ChatFileDownload(peer, id, "file.bin", data.LongLength,
            expectedHash ?? Convert.ToHexString(SHA256.HashData(data)), part, Path.Combine(root, id + ".bin"),
            stream ?? new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None));
    }

    private static async Task TestDownloads(string root)
    {
        Guid peer = Guid.NewGuid();
        byte[] data = RandomNumberGenerator.GetBytes(2 * ChatHandler.FileChunkBytes + 7);
        await using (var download = Download(root, data, peer))
        {
            for (int offset = 0; offset < data.Length; offset += ChatHandler.FileChunkBytes)
                Check(!await download.WriteChunkAsync(peer, offset, data.AsMemory(offset, Math.Min(ChatHandler.FileChunkBytes, data.Length - offset)), false, default),
                    "data remains temporary until its end marker");
            Check(await download.WriteChunkAsync(peer, data.Length, ReadOnlyMemory<byte>.Empty, true, default), "full file passes integrity verification");
            Check(File.ReadAllBytes(download.FinalPath).SequenceEqual(data), "downloaded bytes exactly match the source");
        }
        await using (var empty = Download(root, [], peer))
            Check(await empty.WriteChunkAsync(peer, 0, ReadOnlyMemory<byte>.Empty, true, default) && new FileInfo(empty.FinalPath).Length == 0,
                "zero-byte files complete correctly");

        foreach (string invalid in new[] { "peer", "offset", "end", "hash", "oversize" })
        {
            await using var download = Download(root, data, peer, invalid == "hash" ? new string('0', 64) : null);
            await Reject(async () =>
            {
                if (invalid == "hash")
                {
                    for (int offset = 0; offset < data.Length; offset += ChatHandler.FileChunkBytes)
                        await download.WriteChunkAsync(peer, offset, data.AsMemory(offset, Math.Min(ChatHandler.FileChunkBytes, data.Length - offset)), false, default);
                    await download.WriteChunkAsync(peer, data.Length, ReadOnlyMemory<byte>.Empty, true, default);
                }
                else await download.WriteChunkAsync(invalid == "peer" ? Guid.NewGuid() : peer, invalid == "offset" ? 999999 : 0,
                    invalid == "oversize" ? data : data.AsMemory(0, 3), invalid == "end", default);
            });
            Check(!File.Exists(download.FinalPath), invalid + " cannot publish a final file");
            await download.DisposeAsync();
            Check(!File.Exists(download.TempPath), "failed transfer temporary is removed");
        }

        var delayedStream = new DelayedFileStream(Path.Combine(root, "ordered.part"));
        await using (var ordered = Download(root, [1, 1, 1, 2, 2, 2], peer, stream: delayedStream))
        {
            Task first = ordered.WriteChunkAsync(peer, 0, new byte[] { 1, 1, 1 }, false, default);
            await delayedStream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task<bool> last = ordered.WriteChunkAsync(peer, 3, new byte[] { 2, 2, 2 }, true, default);
            Check(!last.IsCompleted && !File.Exists(ordered.FinalPath), "end waits for an earlier pending disk write");
            delayedStream.Release.TrySetResult();
            await first;
            Check(await last && File.ReadAllBytes(ordered.FinalPath).SequenceEqual(new byte[] { 1, 1, 1, 2, 2, 2 }), "concurrent callbacks preserve chunk order");
        }
        var cancelledStream = new DelayedFileStream(Path.Combine(root, "cancelled.part"));
        await using (var cancelled = Download(root, [1, 1, 1], peer, stream: cancelledStream))
        {
            Task write = cancelled.WriteChunkAsync(peer, 0, new byte[] { 1, 1, 1 }, true, default);
            await cancelledStream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await cancelled.DisposeAsync();
            await Reject(() => write);
            Check(!File.Exists(cancelled.FinalPath) && !File.Exists(cancelled.TempPath), "cancellation aborts disk work and removes the partial file");
        }
        await using (var conflict = Download(root, [3], peer))
        {
            File.WriteAllBytes(conflict.FinalPath, [42]);
            await Reject(() => conflict.WriteChunkAsync(peer, 0, new byte[] { 3 }, true, default));
            Check(File.ReadAllBytes(conflict.FinalPath).SequenceEqual(new byte[] { 42 }), "completion never overwrites an existing file");
        }
        await using (var failedDisk = Download(root, [1], peer, stream: new FailedFileStream(Path.Combine(root, "disk-failure.part"))))
        {
            await Reject(() => failedDisk.WriteChunkAsync(peer, 0, new byte[] { 1 }, true, default));
            Check(!File.Exists(failedDisk.FinalPath), "disk failure cannot produce a completed file");
        }
    }

    private static async Task TestChatDispatch(string root)
    {
        using var node = new QuicPunch.QuicPunch(appDataPath: Path.Combine(root, "chat"));
        using var cts = new CancellationTokenSource();
        await using var hub = new WebUiWebSocketHub(cts);
        var handler = new ChatHandler();
        using var module = new ChatApiModule(node, handler, hub, Path.Combine(root, "downloads"), Path.Combine(root, "outbox"));
        using var owner = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        using var other = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        node.TrustPeer(owner.CertHash);
        node.TrustPeer(other.CertHash);
        byte[] bytes = [4, 5, 6];
        var download = Download(root, bytes, owner.Id);
        var active = (IDictionary)typeof(ChatApiModule).GetField("_activeDownloads", PrivateInstance)!.GetValue(module)!;
        active.Add((owner.Id, download.FileId), download);
        using var output = new MemoryStream();
        using var session = new ChatHandler.ChatSession(owner, output);
        MethodInfo process = typeof(ChatHandler).GetMethod("ProcessFrameAsync", PrivateInstance)!;
        async Task Frame(PeerInfo peer, byte[] payload) => await (Task)process.Invoke(handler, new object[] { session, peer, payload, CancellationToken.None })!;
        byte[] frame = ChatHandler.BuildFileChunkFrame(download.FileId, 0, bytes, bytes.Length, true);
        await Frame(other, frame);
        Check(download.ReceivedBytes == 0 && !File.Exists(download.FinalPath), "another trusted peer cannot inject data into the owner's download");
        byte[] cancel = JsonSerializer.SerializeToUtf8Bytes(new { type = "chat_file_cancel", fileId = download.FileId });
        await Frame(other, cancel);
        Check(active.Contains((owner.Id, download.FileId)), "another peer cannot cancel the owner's download");
        await Frame(owner, frame);
        Check(File.Exists(download.FinalPath) && !active.Contains((owner.Id, download.FileId)), "chat awaits verified disk completion before processing the next frame");
        await Frame(owner, frame);
        Check(File.ReadAllBytes(download.FinalPath).SequenceEqual(bytes), "late duplicate chunks cannot modify a completed file");

        int offers = 0, receipts = 0;
        handler.OnVerifiedFileOfferReceived += (_, _, _, _) => offers++;
        handler.OnFileCompletedReceived += (_, _, _) => receipts++;
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        Guid offeredId = Guid.NewGuid();
        foreach (string? invalidHash in new[] { null, "", new string('0', 63), new string('Z', 64) })
        {
            long before = output.Length;
            await Reject(() => Frame(owner, JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "chat_file_offer", fileId = offeredId, name = "current.bin", size = bytes.Length,
                mime = "application/octet-stream", sha256 = invalidHash
            })));
            Check(offers == 0 && output.Length == before, "invalid hash rejects the offer before dispatch or acknowledgement");
            await Reject(() => Frame(owner, JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "chat_file_complete", fileId = offeredId, sha256 = invalidHash
            })));
            Check(receipts == 0, "invalid hash cannot acknowledge a completed transfer");
            await Reject(async () => await ChatHandler.SendFileOfferAsync(owner.Id, "sender", offeredId,
                "current.bin", bytes.Length, "application/octet-stream", invalidHash!));
            await Reject(async () => await ChatHandler.SendFileCompletionAsync(owner.Id, offeredId, invalidHash!));
        }
        await Reject(() => Frame(owner, JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "chat_file_offer", fileId = offeredId, name = "legacy.bin", size = bytes.Length
        })));
        Check(offers == 0, "legacy offers without a digest are rejected");
        await Frame(owner, JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "chat_file_offer", fileId = offeredId, name = "current.bin", size = bytes.Length,
            mime = "application/octet-stream", sha256 = hash
        }));
        await Frame(owner, JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "chat_file_complete", fileId = offeredId, sha256 = hash
        }));
        Check(offers == 1 && receipts == 1, "the current verified contract dispatches offers and receipts");
        Check(QuicPunch.QuicPunchConnection.SupportedProtocols.Count == 1 &&
            QuicPunch.QuicPunchConnection.SupportedProtocols[0] == new System.Net.Security.SslApplicationProtocol("h3"),
            "QUIC advertises only the current ALPN without legacy fallbacks");
        cts.Cancel();
    }

    private static void TestTrust(string root)
    {
        byte[] hash = RandomNumberGenerator.GetBytes(32);
        string path = Path.Combine(root, "peers.db");
        using (var store = new PeerStore(path, watch: false))
        {
            store.AddOrUpdate(new[] { IPAddress.Loopback }, new ushort[] { 12345 }, hash);
            var acl = new PeerAccessController(() => store);
            acl.TrustPeer(hash);
            acl.UntrustPeer(hash);
            Check(!acl.IsTrusted(hash) && store.TryGet(hash, out _), "revocation retains the saved contact without its authority");
            store.AddOrUpdate(new[] { IPAddress.Loopback }, new ushort[] { 12346 }, hash);
            Check(!acl.IsTrusted(hash), "endpoint updates cannot restore revoked trust");
        }
        using (var store = new PeerStore(path, watch: false))
        {
            var acl = new PeerAccessController(() => store);
            Check(!acl.IsTrusted(hash), "revocation persists after store restart");
            acl.TrustPeer(hash);
            Check(acl.IsTrusted(hash), "explicit approval restores the contact's trust");
        }
        var set = new ExpectedPeerCertSet();
        byte[] mutable = hash.ToArray();
        set.Add(mutable);
        mutable[0] ^= 1;
        Check(set.Contains(hash), "caller mutation cannot change a pinned identity");
    }

    private static async Task TestNativeChatFileTransfer(string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        async Task<QuicPunch.QuicPunch> Node(string name)
        {
            var node = new QuicPunch.QuicPunch(appDataPath: Path.Combine(root, name));
            node.EnableUpnp = false;
            node.StunServerEndpoints = [];
            node.Discovery.LanDiscoveryPort = 0;
            node.Discovery.NostrRelays = ["ws://127.0.0.1:9"];
            node.PeerTransport = new() { AutoConnectDiscoveredPeers = false };
            node.ReputationFilter?.Dispose();
            node.ReputationFilter = null;
            await node.StartAsync(deadline.Token);
            await node.Mailbox.StopAsync();
            node.CurrentPeer.Addresses = [IPAddress.Loopback];
            node.CurrentPeer.PortArray = [(ushort)node.LocalBoundPort];
            return node;
        }
        await using var a = await Node("native-a");
        await using var b = await Node("native-b");
        var sender = new ChatHandler();
        var receiver = new ChatHandler();
        a.RegisterProtocol(sender);
        b.RegisterProtocol(receiver);
        await using var aHub = new WebUiWebSocketHub(deadline);
        await using var bHub = new WebUiWebSocketHub(deadline);
        using var aFiles = new ChatApiModule(a, sender, aHub, Path.Combine(root, "native-a-downloads"), Path.Combine(root, "native-a-outbox"));
        using var bFiles = new ChatApiModule(b, receiver, bHub, Path.Combine(root, "native-b-downloads"), Path.Combine(root, "native-b-outbox"));
        Task? chatTask = null;
        try
        {
            a.TrustPeer(b.CurrentPeer.CertHash);
            b.TrustPeer(a.CurrentPeer.CertHash);
            await a.ProcessIncomingPacketAsync(b.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true),
                new IPEndPoint(IPAddress.Loopback, b.LocalBoundPort), QuicPunch.QuicPunch.TransportType.Wan);
            await b.ProcessIncomingPacketAsync(a.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true),
                new IPEndPoint(IPAddress.Loopback, a.LocalBoundPort), QuicPunch.QuicPunch.TransportType.Wan);
            chatTask = a.InitQuicConnection(sender.ProtocolId, a.AvailablePeers[b.CurrentPeer.Id], cancellationToken: deadline.Token);
            while (!ChatHandler.ActiveChats.ContainsKey(a.CurrentPeer.Id) || !ChatHandler.ActiveChats.ContainsKey(b.CurrentPeer.Id))
                await Task.Delay(10, deadline.Token);

            foreach (int length in new[] { 4 * 1024 * 1024 + 7, 3 * 1024 * 1024 + 11, 0 })
            {
                byte[] data = RandomNumberGenerator.GetBytes(length);
                Guid id = Guid.NewGuid();
                string source = Path.Combine(root, id + ".source");
                File.WriteAllBytes(source, data);
                string hash = Convert.ToHexString(SHA256.HashData(data));
                var shared = (IDictionary)typeof(ChatApiModule).GetField("_sharedFiles", PrivateInstance)!.GetValue(aFiles)!;
                shared.Add(id, new ChatApiModule.SharedFileInfo(id, "native.bin", source, length, "application/octet-stream", b.CurrentPeer.Id, hash));
                var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                long requestedOffset = -1;
                void Request(PeerInfo peer, Guid fileId, long offset) { if (fileId == id) requestedOffset = offset; }
                void Offer(PeerInfo peer, string msgId, ChatHandler.FileOffer offer, string name) { if (offer.FileId == id) offered.TrySetResult(); }
                void Receipt(PeerInfo peer, Guid fileId, string sha) { if (fileId == id && sha == hash) received.TrySetResult(); }
                receiver.OnVerifiedFileOfferReceived += Offer;
                sender.OnFileCompletedReceived += Receipt;
                sender.OnFileRequestReceived += Request;
                try
                {
                    Check((await ChatHandler.SendFileOfferAsync(b.CurrentPeer.Id, "sender", id, "native.bin", length,
                        "application/octet-stream", hash, deadline.Token)).Success, "file manifest delivered over native QUIC");
                    await offered.Task.WaitAsync(deadline.Token);
                    bool resume = length == 3 * 1024 * 1024 + 11;
                    if (resume)
                    {
                        var store = new ChatTransferStore(Path.Combine(b.NodeAppDataPath, "chat-transfers.db"));
                        var record = new ChatTransferRecord(a.CurrentPeer.Id, id, "native.bin", length, "application/octet-stream", hash,
                            Path.Combine(root, id + ".partial"), Path.Combine(root, id + ".resumed"), ChatHandler.FileChunkBytes,
                            Convert.ToHexString(SHA256.HashData(data.AsSpan(0, ChatHandler.FileChunkBytes))));
                        File.WriteAllBytes(record.TempPath, data[..ChatHandler.FileChunkBytes]);
                        store.Save(record);
                    }
                    await bFiles.BeginDownloadAsync(a.CurrentPeer.Id, id, deadline.Token);
                    await received.Task.WaitAsync(deadline.Token);
                    Check(requestedOffset == (resume ? ChatHandler.FileChunkBytes : 0), "native sender starts at the requested durable offset");
                    var completed = (IDictionary)typeof(ChatApiModule).GetField("_completedDownloads", PrivateInstance)!.GetValue(bFiles)!;
                    string destination = (string)completed[(a.CurrentPeer.Id, id)]!;
                    Check(File.ReadAllBytes(destination).SequenceEqual(data), "native chat transfer and receiver receipt verify " + length + " bytes");
                    var cached = await bFiles.BeginDownloadAsync(a.CurrentPeer.Id, id, deadline.Token);
                    Check(cached.Cached && cached.Path == destination, "repeated download returns the verified local file");
                }
                finally { receiver.OnVerifiedFileOfferReceived -= Offer; sender.OnFileCompletedReceived -= Receipt; sender.OnFileRequestReceived -= Request; }
            }
        }
        finally
        {
            deadline.Cancel();
            await a.StopAsync();
            await b.StopAsync();
            if (chatTask != null) try { await chatTask; } catch (OperationCanceledException) { }
        }
    }

    private static void TestIdentity(string root)
    {
        string directory = Path.Combine(root, "identity");
        string path;
        using (var identity = new CertManager(directory, "wan")) { _ = identity.PeerCertificate; path = identity.IdentityPath; }
        byte[] corrupt = [1, 2, 3];
        File.WriteAllBytes(path, corrupt);
        using var second = new CertManager(directory, "wan");
        bool rejected = false;
        try { _ = second.PeerCertificate; } catch (InvalidDataException) { rejected = true; }
        Check(rejected && File.ReadAllBytes(path).SequenceEqual(corrupt), "corrupt identity is rejected and preserved for recovery");
    }

    private static void TestSchnorr()
    {
        int checkedVectors = 0;
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bip340-test-vectors.csv")).Skip(1))
        {
            string[] fields = line.Split(',', 8);
            byte[] message = Convert.FromHexString(fields[4]);
            if (message.Length != 32) continue; // Nostr signs fixed 32-byte event hashes.
            byte[] pubkey = Convert.FromHexString(fields[2]);
            byte[] signature = Convert.FromHexString(fields[5]);
            Check(NostrSchnorr.Verify(message, pubkey, signature) == bool.Parse(fields[6]), "official BIP-340 verification vector " + fields[0]);
            if (fields[1].Length > 0)
            {
                byte[] secret = Convert.FromHexString(fields[1]);
                Check(NostrSchnorr.GetPublicKeyX(secret).SequenceEqual(pubkey) &&
                    NostrSchnorr.Sign(message, secret, Convert.FromHexString(fields[3])).SequenceEqual(signature), "official BIP-340 signing vector " + fields[0]);
            }
            checkedVectors++;
        }
        Check(checkedVectors == 15, "all fixed-message BIP-340 vectors covered");
        BigInteger orderMinusOne = new(Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364140"), true, true);
        for (int i = 0; i < 64; i++)
        {
            byte[] master = RandomNumberGenerator.GetBytes(32);
            byte[] derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "identity"u8.ToArray());
            BigInteger expected = new BigInteger(derived, true, true) % orderMinusOne + 1;
            Check(new BigInteger(NostrSchnorr.DerivePrivateKey(master, "identity"u8.ToArray()), true, true) == expected, "Nostr identity derivation remains compatible");
        }
    }

    private static void TestNativeLease()
    {
        var handle = new ProbeHandle();
        using (var lease = new NativeHandleLease(handle))
        {
            handle.Dispose();
            Check(!handle.Released && lease.Handle == new IntPtr(123), "native lease survives concurrent owner disposal");
        }
        Check(handle.Released, "native lease releases its reference after the native call");
    }

    private static async Task TestSessionReservations()
    {
        using var peer = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = new ProbeProtocol();
        await using var physical = QuicConnection.CreateDummy(new MemoryStream(), QuicConnectionRole.Server);
        await using var mux = new QuicPeerMultiplexer(physical, peer, true, _ => protocol, _ => true,
            sessionAccessCheck: (_, _, _) => approval.Task, initialControlStream: new MemoryStream());
        MethodInfo incoming = typeof(QuicPeerMultiplexer).GetMethod("HandleIncomingSessionRequestAsync", PrivateInstance)!;
        var requests = Enumerable.Range(0, 256).Select(i => (Task)incoming.Invoke(mux, new object[] {
            new QuicPeerMultiplexer.ControlMessage { Type = QuicPeerMultiplexer.ControlMessageType.SessionRequest,
                SessionShortId = (ushort)(i * 2 + 1), SessionGuid = Guid.NewGuid(), ProtocolId = protocol.ProtocolId }, CancellationToken.None })!).ToArray();
        Check(mux.ReservedSessionCount == 128 && requests.Count(t => !t.IsCompleted) == 128, "active and pending incoming sessions share an atomic hard cap");
        bool outboundRejected = false;
        try { await mux.OpenVirtualSessionAsync(protocol.ProtocolId); } catch (InvalidOperationException) { outboundRejected = true; }
        Check(outboundRejected, "outbound sessions use the same capacity budget");
        approval.SetResult(false);
        await Task.WhenAll(requests);
        Check(mux.ReservedSessionCount == 0, "rejected authorization releases every session reservation");
        approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingMessage = new QuicPeerMultiplexer.ControlMessage { Type = QuicPeerMultiplexer.ControlMessageType.SessionRequest,
            SessionShortId = 513, SessionGuid = Guid.NewGuid(), ProtocolId = protocol.ProtocolId };
        Task pending = (Task)incoming.Invoke(mux, new object[] { pendingMessage, CancellationToken.None })!;
        await mux.RevokeApplicationSessionsAsync();
        approval.SetResult(true);
        await pending;
        Check(mux.ReservedSessionCount == 0 && !mux.HasApplicationSessions, "revocation invalidates an earlier pending approval even after trust returns");
        approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelledDecision = new CancellationTokenSource();
        pendingMessage = new QuicPeerMultiplexer.ControlMessage { Type = QuicPeerMultiplexer.ControlMessageType.SessionRequest,
            SessionShortId = 515, SessionGuid = Guid.NewGuid(), ProtocolId = protocol.ProtocolId };
        pending = (Task)incoming.Invoke(mux, new object[] { pendingMessage, cancelledDecision.Token })!;
        cancelledDecision.Cancel();
        await pending;
        Check(mux.ReservedSessionCount == 0, "cancellation releases capacity when the approval callback ignores its token");
        await using var virtualTransport = new MultiplexedQuicConnectionTransport(mux, Guid.NewGuid(), 2, protocol.ProtocolId);
        CancellationToken sessionToken = virtualTransport.SessionToken;
        await virtualTransport.CloseAsync(0);
        Check(sessionToken.IsCancellationRequested, "session closure cancels the application lifetime without closing the peer transport");
        await using var otherPhysical = QuicConnection.CreateDummy(new MemoryStream(), QuicConnectionRole.Server);
        await using var failedLookup = new QuicPeerMultiplexer(otherPhysical, peer, true,
            _ => throw new InvalidOperationException("Simulated protocol lookup failure."), _ => true, initialControlStream: new MemoryStream());
        try { await failedLookup.OpenVirtualSessionAsync(protocol.ProtocolId); } catch (InvalidOperationException) { }
        Check(failedLookup.ReservedSessionCount == 0, "protocol lookup failure cannot leak outbound session capacity");
    }

    private sealed class ProbeHandle : SafeHandle
    {
        public bool Released { get; private set; }
        public ProbeHandle() : base(IntPtr.Zero, ownsHandle: true) => SetHandle(new IntPtr(123));
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() { Released = true; return true; }
    }

    private sealed class ProbeProtocol : QuicPunch.QuicPunch.IProtocolHandler
    {
        public Guid ProtocolId { get; } = Guid.NewGuid();
        public string ProtocolName => "Reliability";
        public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
        public Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DelayedFileStream(string path) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (!buffer.IsEmpty && buffer.Span[0] == 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            await base.WriteAsync(buffer, ct);
        }
    }

    private sealed class FailedFileStream(string path) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromException(new IOException("Simulated disk full."));
    }
}
