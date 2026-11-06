using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using QuicPunch;
using QuicPunch.Helpers;
using QuicNet.Protocols;
using QuicNet.Watch.Domain;
using QuicNet.Watch.Protocols;
using QP = QuicPunch.QuicPunch;

namespace QuicPunchTests.Tests;

internal static class TorIdentityPrivacyTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "qp-tor-privacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestTorProxyBoundary();
            foreach (bool bundle in new[] { false, true }) TestMigration(Path.Combine(root, bundle ? "bundle" : "legacy"), bundle);
            await TestTransportMetadataAsync(Path.Combine(root, "node"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void TestTorProxyBoundary()
    {
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        var relay = new Uri("wss://example.invalid");
        foreach (System.Net.IWebProxy? proxy in new System.Net.IWebProxy?[] { null, new System.Net.WebProxy() })
        {
            bool blocked = false;
            try { QuicPunch.NostrDiscovery.ConfigureRelayProxy(socket, "tor", proxy, relay); }
            catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "Tor discovery refuses a missing or bypassed proxy before connecting");
        }
        var socks = new System.Net.WebProxy("socks5://127.0.0.1:9050");
        QuicPunch.NostrDiscovery.ConfigureRelayProxy(socket, "tor", socks, relay);
        Check(ReferenceEquals(socket.Options.Proxy, socks), "Tor relay connection keeps its explicit SOCKS proxy");
    }

    private static void TestMigration(string path, bool bundle)
    {
        Directory.CreateDirectory(path);
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var original = CertManager.GenerateIdentityCertificate("PRIVATE-DESKTOP-8231", "private_username_8231", ecdh);
        byte[] pfx = original.Export(X509ContentType.Pfx), key = ecdh.ExportPkcs8PrivateKey();
        byte[] pin = Sha3Crypto.Hash256(original.GetPublicKey());
        byte[] nostr = NostrSchnorr.DerivePrivateKey(key, "QuicPunch:NostrIdentity"u8.ToArray());
        byte[] untouchedWan = RandomNumberGenerator.GetBytes(200);
        File.WriteAllBytes(Path.Combine(path, "wan_identity.bin"), untouchedWan);
        if (bundle)
        {
            using var file = File.Create(Path.Combine(path, "tor_identity.bin"));
            using var writer = new BinaryWriter(file);
            writer.Write("QPID"u8); writer.Write((byte)2);
            writer.Write(pfx.Length); writer.Write(pfx); writer.Write(key.Length); writer.Write(key);
        }
        else
        {
            File.WriteAllBytes(Path.Combine(path, "tor_peerCert.pfx"), pfx);
            File.WriteAllBytes(Path.Combine(path, "tor_ecdhKey.key"), key);
        }
        using var oldPeer = new PeerInfo(original, ecdh.ExportSubjectPublicKeyInfo());
        string pseudonym;
        using (var manager = new CertManager(path, "tor"))
        {
            pseudonym = manager.PeerName;
            var sanitized = manager.PeerCertificate;
            Check(pseudonym.StartsWith("tor-") && pseudonym.Length == 20, "Tor has a stable pseudonym unrelated to the device name");
            Check(sanitized.GetNameInfo(X509NameType.UpnName, false) == "", "Tor certificate has no user principal name");
            Check(sanitized.GetNameInfo(X509NameType.SimpleName, false) == pseudonym, "Tor certificate subject is pseudonymous");
            string encoded = Encoding.UTF8.GetString(sanitized.RawData);
            Check(!encoded.Contains("PRIVATE-DESKTOP") && !encoded.Contains("private_username"), "Legacy user and host are removed from public certificate bytes");
            Check(manager.CertPublicHash.SequenceEqual(pin) && manager.NostrPrivateKey.SequenceEqual(nostr) &&
                manager.EcdhPublicKeyRaw.SequenceEqual(ecdh.ExportSubjectPublicKeyInfo()), "Migration preserves certificate pin, Nostr identity and ECDH key");
            using var newPeer = new PeerInfo(sanitized, manager.EcdhPublicKeyRaw);
            Check(newPeer.Id == oldPeer.Id, "Existing contact IDs and public-key trust survive migration");
            byte[] challenge = RandomNumberGenerator.GetBytes(32);
            using var publicKey = original.GetECDsaPublicKey();
            Check(publicKey!.VerifyData(challenge, manager.Curve.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256), "Migrated certificate retains the original signing key");
        }
        byte[] normalized = File.ReadAllBytes(Path.Combine(path, "tor_identity.bin"));
        using (var reloaded = new CertManager(path, "tor")) Check(reloaded.PeerName == pseudonym, "Tor pseudonym persists across reloads");
        Check(normalized.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "tor_identity.bin"))), "Reload does not renew an already sanitized certificate");
        Check(untouchedWan.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "wan_identity.bin"))), "Tor migration leaves the WAN identity unchanged");
        CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(pfx); CryptographicOperations.ZeroMemory(nostr);
    }

    private static async Task TestTransportMetadataAsync(string path)
    {
        await using var node = new QP(appDataPath: path) { WanEnabled = false, EnableUpnp = false };
        node.ReputationFilter?.Dispose(); node.ReputationFilter = null;
        const string realName = "real-wan-user@real-desktop-8291";
        node.CurrentPeer.Name = realName;
        Check(node.TorCurrentPeer.Name != realName && node.CertManager.CertPublicHash.AsSpan().SequenceEqual(node.TorCertManager.CertPublicHash) == false &&
            node.WanNostrPublicKeyHex != node.TorNostrPublicKeyHex, "Tor and WAN have independent names, signing keys and Nostr keys");
        node.TorCurrentPeer.OnionAddress = new string('a', 56) + ".onion";
        node.TorCurrentPeer.PortArray = [443];
        node.TorCurrentPeer.ConnectionFlags.IsTor = true;
        byte[] hello = node.GenerateHelloPayload(QuicPunch.QuicPunchStructures.MessageType.Interrogation, true, transport: QP.TransportType.Tor);
        Check(!Encoding.UTF8.GetString(hello).Contains(realName) && hello.AsSpan().IndexOf(node.CertManager.PeerCertificate.GetPublicKey()) < 0,
            "Tor handshake contains no WAN display name or signing public key");
        var payloadMethod = typeof(QuicPunch.Discovery.PeerDiscoveryManager).GetMethod("GetTorNostrDiscoveryPayload", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        string announcement = (string)payloadMethod.Invoke(node.Discovery, null)!;
        using (var payload = JsonDocument.Parse(announcement))
        {
            Check(payload.RootElement.GetProperty("name").GetString() == node.TorCurrentPeer.Name && !announcement.Contains(realName), "Tor discovery announces only its Tor pseudonym");
            Check(!payload.RootElement.TryGetProperty("wan", out _), "Tor discovery does not attach a WAN invitation");
        }
        var chat = new ChatHandler { NostrMessagingEnabled = true };
        node.RegisterProtocol(chat);
        using var torPeer = new PeerInfo { NetworkType = QP.NetworkType.Tor, ActiveTransport = QP.TransportType.Tor, NostrPubKey = new string('b', 64) };
        torPeer.SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        node.AvailablePeers[torPeer.Id] = torPeer;
        Check(ReferenceEquals(node.GetLocalIdentityForPeer(torPeer), node.TorCurrentPeer), "Application protocols select the Tor local identity");
        chat.OnGetHistoryForPeer += _ => new() { ("history1", realName, "old message", DateTime.UtcNow, null), ("history2", "Me", "local message", DateTime.UtcNow, null) };
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var chatStream = new CaptureStream("{\"type\":\"chat_sync_req\"}");
        await using var connection = QuicPunch.QuicConnection.CreateDummy(new MemoryStream(), QuicConnectionRole.Client);
        Task chatTask = chat.HandleAsync(connection, chatStream, torPeer, lifetime.Token);
        await chatStream.Waiting.Task.WaitAsync(lifetime.Token);
        Check((await chat.SendMessageAsync(torPeer.Id, realName, "hello")).Success, "Tor chat sends a message");
        Check((await ChatHandler.SendFileOfferAsync(torPeer.Id, realName, Guid.NewGuid(), "sample.txt", 0, "text/plain", new string('a', 64))).Success, "Tor chat sends a file offer");
        var frames = chatStream.Frames();
        Check(frames.Where(p => p.TryGetProperty("sender", out _)).All(p => p.GetProperty("sender").GetString() != realName), "Tor chat messages, file offers and synchronized history do not emit WAN names");
        Check(frames.Where(p => p.GetProperty("type").GetString() is "chat_msg" or "chat_file_offer").All(p => p.GetProperty("sender").GetString() == node.TorCurrentPeer.Name), "Outgoing Tor messages use the local Tor pseudonym");
        var mailbox = node.GetMailboxChannel(chat.ProtocolId);
        bool blocked = false;
        try { await mailbox.SendOfflineAsync(torPeer, "must not fall back to WAN"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Tor")) { blocked = true; }
        Check(blocked, "Tor contacts cannot fall back to the WAN mailbox identity");
        await mailbox.Store.InitializeAsync();
        Check((await mailbox.Store.QueryAsync(new MailboxHistoryQuery(chat.ProtocolId))).Count == 0, "Blocked Tor fallback queues no WAN messages");
        string queuedId = Guid.NewGuid().ToString();
        await mailbox.Store.TryAddAsync(new MailboxStoredMessage(queuedId, chat.ProtocolId, MailboxMessageDirection.Outgoing,
            MailboxDeliveryState.Queued, torPeer.Id, node.WanNostrPublicKeyHex!, torPeer.NostrPubKey!, realName, "old pending message"u8.ToArray(),
            "text/plain", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false, 0, null, null, null, null));
        Check(await mailbox.RetryPendingAsync() == 0, "Old pending Tor messages are not retried through WAN");
        var stopped = (await mailbox.Store.QueryAsync(new MailboxHistoryQuery(chat.ProtocolId))).Single();
        Check(stopped.State == MailboxDeliveryState.Failed && stopped.LastError?.Contains("Tor") == true, "Blocked legacy retry records an explicit Tor isolation error");
        using var watch = new WatchControlHandler(); node.RegisterProtocol(watch);
        using var watchStream = new CaptureStream();
        Task watchTask = watch.HandleAsync(connection, watchStream, torPeer, lifetime.Token);
        await watchStream.Waiting.Task.WaitAsync(lifetime.Token);
        Check(await watch.SendAsync(torPeer.Id, new WatchWireMessage("clock_ping", Guid.Empty, node.CurrentPeer.Id, TargetPeerId: node.CurrentPeer.Id)), "Watch sends a control message over Tor");
        var control = watchStream.Frames().Single();
        Check(control.GetProperty("SenderPeerId").GetGuid() == node.TorCurrentPeer.Id && control.GetProperty("TargetPeerId").GetGuid() == node.TorCurrentPeer.Id, "Watch uses Tor identity IDs instead of WAN IDs on the wire");
        lifetime.Cancel();
        await Task.WhenAll(chatTask, watchTask);
        using var directPeer = new PeerInfo();
        Check(ReferenceEquals(node.GetLocalIdentityForPeer(directPeer), node.CurrentPeer), "Direct peers retain the WAN identity");
    }

    private sealed class CaptureStream : MemoryStream
    {
        private readonly MemoryStream _incoming = new();
        public readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureStream(string? incoming = null)
        {
            if (incoming != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(incoming);
                var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                _incoming.Write(length); _incoming.Write(bytes); _incoming.Position = 0;
            }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_incoming.Position < _incoming.Length) return await _incoming.ReadAsync(buffer, ct);
            Waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0;
        }
        public List<JsonElement> Frames()
        {
            var result = new List<JsonElement>(); var bytes = ToArray(); int offset = 0;
            while (offset < bytes.Length)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)); offset += 4;
                using var doc = JsonDocument.Parse(bytes.AsMemory(offset, length)); result.Add(doc.RootElement.Clone()); offset += length;
            }
            return result;
        }
    }
}
