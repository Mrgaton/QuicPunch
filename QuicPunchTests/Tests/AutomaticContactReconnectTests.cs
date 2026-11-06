using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using QuicPunch;
using QuicPunch.Helpers;
using QP = QuicPunch.QuicPunch;

namespace QuicPunchTests.Tests;

internal static class AutomaticContactReconnectTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class DatagramCollector : IAsyncDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _receive;
        internal ConcurrentQueue<long> Received { get; } = new();
        internal ushort Port => (ushort)((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        internal DatagramCollector() => _receive = ReceiveAsync();
        private async Task ReceiveAsync()
        {
            try
            {
                while (true)
                {
                    var packet = await _socket.ReceiveAsync(_stop.Token);
                    if (packet.Buffer.AsSpan().StartsWith(QP.MagicHeader)) Received.Enqueue(Stopwatch.GetTimestamp());
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); await _receive; _socket.Dispose(); _stop.Dispose();
        }
    }

    private static async Task Wait(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(25, ct);
    }

    private static QP Node(string path)
    {
        var node = new QP(appDataPath: path)
        { WanEnabled = false, EnableUpnp = false, PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
        node.Discovery.NostrRelays = ["ws://127.0.0.1:9"];
        node.Discovery.WanNostrDiscoveryEnabled = false;
        node.Discovery.TorNostrDiscoveryEnabled = false;
        node.Discovery.LanDiscoveryPort = 0;
        node.StunServerEndpoints = [new IPEndPoint(IPAddress.Loopback, 9)];
        node.ReputationFilter?.Dispose(); node.ReputationFilter = null;
        return node;
    }

    public static async Task RunAsync()
    {
        TestRecencyBoundary();
        string root = Path.Combine(Path.GetTempPath(), "qp-contact-reconnect-" + Guid.NewGuid().ToString("N"));
        try { await TestPersistentDatagramsAndAnnouncements(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void TestRecencyBoundary()
    {
        DateTime boot = DateTime.UtcNow;
        var peer = new PeerStore.SavedPeer([IPAddress.Loopback], [50000], RandomNumberGenerator.GetBytes(32));
        Check(!QP.HasRecentAuthenticatedActivity(peer, boot), "contacts without authenticated history do not qualify");
        Check(QP.HasRecentAuthenticatedActivity(peer with { LastConnectedUtc = boot.AddDays(-2) }, boot), "48-hour boundary includes legacy connection history");
        Check(!QP.HasRecentAuthenticatedActivity(peer with { LastConnectedUtc = boot.AddDays(-2).AddTicks(-1), LastUsedUtc = boot }, boot),
            "old contacts and outgoing/offline usage do not fabricate recent reachability");
        Check(QP.HasRecentAuthenticatedActivity(peer with { LastConnectedUtc = boot.AddDays(-10), LastAuthenticatedActivityUtc = boot.AddMinutes(-1) }, boot),
            "a long-lived authenticated connection remains recent");
        Check(QP.HasRecentAuthenticatedActivity(peer with { LastConnectedUtc = boot, LastAuthenticatedActivityUtc = boot.AddDays(-3) }, boot),
            "a newer authenticated connection takes precedence over older activity metadata");
    }

    private static async Task TestPersistentDatagramsAndAnnouncements(string root)
    {
        await using var recent = new DatagramCollector();
        await using var old = new DatagramCollector();
        await using var disabled = new DatagramCollector();
        await using var signer = Node(Path.Combine(root, "signer"));
        await using var optOutSigner = Node(Path.Combine(root, "opt-out-signer"));
        await using var node = Node(Path.Combine(root, "node"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        await node.StartAsync(deadline.Token);
        await node.Mailbox.StopAsync();
        byte[] recentHash = RandomNumberGenerator.GetBytes(32);
        var store = node.PeerStore!;
        store.AddOrUpdate([IPAddress.Loopback], [recent.Port], recentHash, name: "Recent",
            connectionFlags: new ConnectionFlags { ExternalPortMayDiffer = true });
        store.RecordConnection(recentHash, IPAddress.Loopback, DateTime.UtcNow.AddHours(-1));
        signer.CurrentPeer.Addresses = [IPAddress.Loopback]; signer.CurrentPeer.PortArray = [old.Port];
        optOutSigner.CurrentPeer.Addresses = [IPAddress.Loopback]; optOutSigner.CurrentPeer.PortArray = [disabled.Port];
        store.AddOrUpdate(signer.CurrentPeer, autoConnect: true, save: true, nostrPubKey: signer.CertManager.NostrPublicKeyHex);
        store.RecordConnection(signer.CurrentPeer.CertHash, IPAddress.Loopback, DateTime.UtcNow.AddDays(-3));
        store.AddOrUpdate(optOutSigner.CurrentPeer, autoConnect: false, save: true, nostrPubKey: optOutSigner.CertManager.NostrPublicKeyHex);
        store.RecordConnection(optOutSigner.CurrentPeer.CertHash, IPAddress.Loopback, DateTime.UtcNow.AddHours(-1));

        await node.StartWanAsync(cancellationToken: deadline.Token);
        await Wait(() => recent.Received.Count >= 4, deadline.Token);
        long[] first = recent.Received.ToArray();
        for (int i = 1; i <= 3; i++)
        {
            double seconds = Stopwatch.GetElapsedTime(first[i - 1], first[i]).TotalSeconds;
            double expected = Math.Pow(2, i - 1) * 0.5;
            Check(seconds >= expected * 0.8 && seconds < expected + 0.8, $"real UDP retry gap {i}: {seconds:F2}s (expected {expected}s)");
        }
        Check(old.Received.IsEmpty && disabled.Received.IsEmpty, "startup skips stale and disabled contacts");

        node.Discovery.WanNostrDiscoveryEnabled = true;
        node.Discovery.OnWanNostrEventDiscovered(signer.CertManager.NostrPublicKeyHex, signer.GetWanToken());
        node.Discovery.OnWanNostrEventDiscovered(signer.CertManager.NostrPublicKeyHex,
            signer.Discovery.GetWanNostrDiscoveryPayload()!.Replace("\"sig\":\"", "\"sig\":\"AA", StringComparison.Ordinal));
        node.Discovery.OnWanNostrEventDiscovered(new string('f', 64), signer.Discovery.GetWanNostrDiscoveryPayload()!);
        await Task.Delay(700, deadline.Token);
        Check(old.Received.IsEmpty, "unsigned, tampered or wrong-author announcements cannot reactivate a stale contact");
        node.Discovery.OnWanNostrEventDiscovered(signer.CertManager.NostrPublicKeyHex, signer.Discovery.GetWanNostrDiscoveryPayload()!);
        await Wait(() => !old.Received.IsEmpty, deadline.Token);
        Check(store.TryGet(signer.CurrentPeer.CertHash, out var stale) && stale!.LastAuthenticatedActivityUtc < DateTime.UtcNow.AddDays(-2),
            "verified reappearance wakes probing without fabricating a successful connection");
        node.PeerTransport = node.PeerTransport with { AutoConnectDiscoveredPeers = true };
        node.Discovery.OnWanNostrEventDiscovered(optOutSigner.CertManager.NostrPublicKeyHex, optOutSigner.Discovery.GetWanNostrDiscoveryPayload()!);
        await Task.Delay(500, deadline.Token);
        Check(disabled.Received.IsEmpty, "a verified announcement respects auto-connect opt-out");

        // 0, .5, 1.5, 3.5, 7.5, 15.5, 31.5, 56.5 seconds: crosses the old 45s deadline.
        await Wait(() => recent.Received.Count >= 8, deadline.Token);
        long[] rounds = recent.Received.ToArray();
        double capped = Stopwatch.GetElapsedTime(rounds[6], rounds[7]).TotalSeconds;
        Check(capped >= 24.5 && capped < 27, $"UDP probes survive 45s and cap at 25s ({capped:F2}s observed)");
        Check(node.ActiveInterrogations.Values.Count(x => x.IsAutomaticContact && x.Peer.CertHash.AsSpan().SequenceEqual(recentHash)) == 1,
            "the supervisor never duplicates a contact's probe worker");

        // The formerly stale device now opens a real UDP/QUIC endpoint. A new
        // signed advertisement must replace the old black-hole target promptly.
        signer.WanEnabled = true;
        await signer.StartAsync(deadline.Token);
        await signer.Mailbox.StopAsync();
        signer.CurrentPeer.Addresses = [IPAddress.Loopback];
        signer.CurrentPeer.PortArray = [(ushort)signer.LocalBoundPort];
        signer.CurrentPeer.PreferredPort = (ushort)signer.LocalBoundPort;
        signer.InvalidateTokenCache();
        node.CurrentPeer.Addresses = [IPAddress.Loopback];
        node.CurrentPeer.PortArray = [(ushort)node.LocalBoundPort];
        node.InvalidateTokenCache();
        node.Discovery.OnWanNostrEventDiscovered(signer.CertManager.NostrPublicKeyHex, signer.Discovery.GetWanNostrDiscoveryPayload()!);
        await Wait(() => node.TryGetPeerMultiplexer(signer.CurrentPeer.Id, out var mux) && mux is { IsDisposed: false }, deadline.Token);
        Check(!node.ActiveInterrogations.Values.Any(x => x.IsAutomaticContact && x.Peer.Id == signer.CurrentPeer.Id),
            "a changed signed endpoint authenticates and stops the persistent probe");
        Check(store.TryGet(signer.CurrentPeer.CertHash, out var live) && live!.LastAuthenticatedActivityUtc >= DateTime.UtcNow.AddMinutes(-1),
            "successful reappearance persists real authenticated activity");

        // Use a separate unanswered old identity for the restart-only assertion.
        byte[] unansweredHash = optOutSigner.CurrentPeer.CertHash;
        store.ToggleAutoConnect(unansweredHash, true);
        store.Remove(unansweredHash);
        store.AddOrUpdate(optOutSigner.CurrentPeer, autoConnect: true, save: true, nostrPubKey: optOutSigner.CertManager.NostrPublicKeyHex);
        store.RecordConnection(unansweredHash, IPAddress.Loopback, DateTime.UtcNow.AddDays(-3));
        node.Discovery.OnWanNostrEventDiscovered(optOutSigner.CertManager.NostrPublicKeyHex, optOutSigner.Discovery.GetWanNostrDiscoveryPayload()!);
        await Wait(() => !disabled.Received.IsEmpty, deadline.Token);

        store.ToggleAutoConnect(recentHash, false);
        await Wait(() => !node.ActiveInterrogations.Values.Any(x => x.Peer.CertHash.AsSpan().SequenceEqual(recentHash)), deadline.Token);
        int count = recent.Received.Count;
        await Task.Delay(600, deadline.Token);
        Check(recent.Received.Count == count, "disabling auto-connect cancels persistent probes promptly");
        store.ToggleAutoConnect(recentHash, true);
        await Wait(() => recent.Received.Count > count, deadline.Token);
        await node.StopWanAsync();
        await Wait(() => node.ActiveInterrogations.IsEmpty, deadline.Token);
        count = recent.Received.Count;
        await Task.Delay(600, deadline.Token);
        Check(recent.Received.Count == count, "WAN stop cancels every persistent probe");
        await node.StartWanAsync(cancellationToken: deadline.Token);
        await Wait(() => recent.Received.Count > count, deadline.Token);
        await node.StopAsync();
        await node.StartAsync(deadline.Token);
        Check(!node.IsAutomaticContactEligible(node.PeerStore!.SavedPeers.Single(x => x.CertHash.AsSpan().SequenceEqual(unansweredHash))),
            "restart clears announcement-only eligibility for old unanswered contacts");
    }
}
