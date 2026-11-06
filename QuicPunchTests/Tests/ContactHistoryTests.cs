using System.Net;
using QuicPunch.Helpers;
using QuicPunch.Discovery;

namespace QuicPunchTests.Tests;

internal static class ContactHistoryTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "qp-contact-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "peers.db");
            byte[] hash = Enumerable.Repeat((byte)1, 32).ToArray();
            byte[] torHash = Enumerable.Repeat((byte)2, 32).ToArray();
            var now = new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);
            using (var store = new PeerStore(path, watch: false))
            {
                store.AddOrUpdate([IPAddress.Parse("81.2.69.160")], [443], hash, name: "Contacto");
                Check(store.SavedPeers[0].LastConnectedUtc == null, "An address-book entry fabricated a successful connection");
                store.RecordConnection(hash, IPAddress.Parse("81.2.69.161"), now);
                Check(!store.RecordAuthenticatedActivity(hash, now.AddSeconds(30)), "Heartbeats must not create a write storm");
                Check(store.RecordAuthenticatedActivity(hash, now.AddMinutes(1)), "Authenticated activity was not refreshed");
                Check(!store.RecordAuthenticatedActivity(hash, now), "Old activity replaced a newer observation");
                store.RecordUsage(hash, now);
                Check(!store.RecordUsage(hash, now.AddSeconds(30)), "Messages must not create a write storm");
                store.RecordUsage(hash, now.AddMinutes(2));
                store.AddOrUpdate([IPAddress.Parse("81.2.69.162")], [444], hash, name: "Nombre actualizado");
                store.SetTrusted(hash, false);
                Check(store.TryGet(hash, out var peer) && peer!.UsageCount == 2 && peer.LastKnownIp == "81.2.69.161" && peer.LastConnectedUtc == now && peer.LastAuthenticatedActivityUtc == now.AddMinutes(1), "Endpoint/trust update erased the authenticated history");
                Check(!store.RecordConnection(hash, IPAddress.Parse("8.8.8.8"), now.AddDays(-1)), "Old connection replaced a newer observation");
                store.AddOrUpdate([IPAddress.Loopback], [443], torHash, name: "Tor", onionAddress: "hidden.onion", networkType: QuicPunch.QuicPunch.NetworkType.Tor);
                store.RecordConnection(torHash, IPAddress.Parse("8.8.8.8"), now);
                Check(store.TryGet(torHash, out var tor) && tor!.LastKnownIp == null, "Tor retained the relay IP");
            }
            using (var reload = new PeerStore(path, watch: false))
            {
                Check(reload.TryGet(hash, out var peer) && peer!.UsageCount == 2 && peer.LastConnectedUtc == now && peer.LastAuthenticatedActivityUtc == now.AddMinutes(1) && peer.LastUsedUtc == now.AddMinutes(2) && peer.LastKnownIp == "81.2.69.161" && !peer.Trusted, "History did not survive restart");
            }
            var gate = new DiscoveryPresenceGate();
            string boot = Guid.NewGuid().ToString("N");
            Check(gate.Observe("wan:A", boot), "First presence did not request the reciprocal token");
            for (int i = 0; i < 100; i++) Check(!gate.Observe("wan:A", boot), "Presence replies created an echo loop");
            Check(gate.Observe("wan:A", Guid.NewGuid().ToString("N")), "Restart with unchanged endpoint did not request a reply");
            Check(!gate.Observe("wan:B", "invalid"), "Malformed presence accepted");
            Check(gate.Observe("tor:A", boot), "WAN/Tor presence scopes were correlated");
            for (int i = 0; i < 300; i++) gate.Observe("wan:" + i, boot);
            Check(gate.Observe("wan:A", boot), "Presence gate failed to evict old identities");
            Console.WriteLine("PASS contact history: restart, usage coalescing, endpoint refresh, trust preservation, Tor privacy, reciprocal presence deduplication/restart/bounds");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
