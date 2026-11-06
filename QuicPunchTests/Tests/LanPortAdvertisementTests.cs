using System.Net;
using System.Security.Cryptography;
using QuicPunch;
using QuicPunch.Helpers;
using QP = QuicPunch.QuicPunch;

namespace QuicPunchTests.Tests;

internal static class LanPortAdvertisementTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    public static async Task RunAsync()
    {
        IPAddress lan = IPAddress.Parse("192.168.5.47");
        IPAddress wan = IPAddress.Parse("93.184.216.34");
        Check(!Utilities.GetConnectionFlags([new IPEndPoint(wan, 40000)], 40000).ExternalPortMayDiffer,
            "port-preserving STUN observation leaves bit 5 clear");
        Check(Utilities.GetConnectionFlags([new IPEndPoint(wan, 45000)], 40000).ExternalPortMayDiffer,
            "one translated external port sets bit 5 without changing Single mode");
        Check(Utilities.GetConnectionFlags([new IPEndPoint(wan, 40000), new IPEndPoint(wan, 45000)], 40000).ExternalPortMayDiffer,
            "multiple external ports set bit 5");
        Check(Utilities.GetConnectionFlags(Enumerable.Range(40000, 9).Select(p => new IPEndPoint(wan, p)), 40000).ExternalPortMayDiffer,
            "an external port range sets bit 5");
        var packed = new ConnectionFlags(0xdf) { ExternalPortMayDiffer = true };
        Check(packed.RawValue == 0xff, "bit 5 does not overwrite existing flags");
        packed.ExternalPortMayDiffer = false;
        Check(packed.RawValue == 0xdf, "clearing bit 5 preserves all other flags");
        foreach (ushort[] ports in new ushort[][] { [40000], [7042, 10000, 20000, 30000, 40000, 50000, 60000, 65310], [7042, 65310] })
        {
            bool range = ports.Length == 2;
            using var peer = new PeerInfo
            {
                Addresses = [wan, lan], PortArray = ports, PreferredPort = 49123,
                ConnectionFlags = new() { PortMode = range ? PortMode.Range : PortAdvertisementCodec.SelectMode(ports) }
            };
            peer.SetCertificateHash(RandomNumberGenerator.GetBytes(32));
            string encoded = Utilities.EncodeEndpointToken(peer);
            using var decoded = Utilities.DecodeEndpointToken(encoded);
            Check(decoded.ConnectionFlags.ExternalPortMayDiffer, "endpoint tokens retain external-port metadata for Single/Multiple/Range");
            byte[] invalidHost = System.Buffers.Text.Base64Url.DecodeFromChars(encoded);
            invalidHost[^34] = invalidHost[^33] = 0;
            bool rejected = false;
            try { using var invalid = Utilities.DecodeEndpointToken(System.Buffers.Text.Base64Url.EncodeToString(invalidHost)); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "zero host port is rejected instead of falling through to a legacy token decoder");
            Check(decoded.PreferredPort == 49123 && decoded.PortArray.SequenceEqual(ports),
                $"{decoded.ConnectionFlags.PortMode} token retains independent LAN port and exact WAN advertisement");
            var targets = QuicPunchConnection.BuildProbeTargets(decoded, null, 0);
            Check(targets.Count <= 64 && targets.Where(ep => ep.Address.Equals(lan)).Select(ep => ep.Port).SequenceEqual(new[] { 49123 }),
                "LAN probes only the bound socket, never the WAN port range/list");
            Check(!targets.Contains(new IPEndPoint(wan, 49123)) && targets.Any(ep => ep.Address.Equals(wan)),
                "the local socket port is not incorrectly associated with the public IP");
            var candidates = new[] { new CandidateEndpoint(new IPEndPoint(lan, 50234), CandidateType.Host),
                new CandidateEndpoint(new IPEndPoint(wan, 60234), CandidateType.ServerReflexive) };
            decoded.ActiveEndPoint = new IPEndPoint(lan, 49123);
            var quicTargets = QuicPunchConnection.BuildProbeTargets(decoded, candidates, 60234);
            Check(quicTargets.Where(ep => ep.Address.Equals(lan)).Select(ep => ep.Port).SequenceEqual(new[] { 50234 }),
                "QUIC host candidate retains its own port, independent of control/WAN ports");
        }
        string root = Path.Combine(Path.GetTempPath(), "qp-lan-port-" + Guid.NewGuid().ToString("N"));
        try
        {
            string storePath = Path.Combine(root, "peers.json");
            using var peer = new PeerInfo { Addresses = [wan, lan], PortArray = [7042, 65310], PreferredPort = 49123,
                ActiveEndPoint = new IPEndPoint(lan, 49123), ConnectionFlags = new() { PortMode = PortMode.Range, HasHostPort = true, ExternalPortMayDiffer = true } };
            peer.SetCertificateHash(RandomNumberGenerator.GetBytes(32));
            using (var store = new PeerStore(storePath, watch: false))
                store.AddOrUpdate(peer, save: true);
            using (var store = new PeerStore(storePath, watch: false))
            {
                Check(store.TryGet(peer.CertHash, out var saved) && saved != null, "saved LAN contact reloads");
                using var restored = QP.CreatePeerInfoFromSavedPeer(saved!);
                Check(restored.ConnectionFlags.ExternalPortMayDiffer, "saved contacts preserve the new flag across restart");
                Check(restored.PreferredPort == 49123 && restored.PortArray.SequenceEqual(new ushort[] { 7042, 65310 }),
                    "saved contacts retain LAN port without widening WAN bounds");
            }

            await using var sender = new QP(appDataPath: Path.Combine(root, "sender"))
                { WanEnabled = false, EnableUpnp = false, PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
            await using var receiver = new QP(appDataPath: Path.Combine(root, "receiver"))
                { WanEnabled = false, EnableUpnp = false, PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
            sender.ReputationFilter?.Dispose(); receiver.ReputationFilter?.Dispose();
            sender.ReputationFilter = null; receiver.ReputationFilter = null;
            sender.CurrentPeer.Addresses = [wan, lan];
            sender.CurrentPeer.PortArray = [7042, 10000, 20000, 30000, 40000, 50000, 60000, 65310];
            sender.CurrentPeer.PreferredPort = 49123;
            await receiver.StartAsync();
            receiver.Discovery.WanNostrDiscoveryEnabled = true;
            byte[] oldHeader = sender.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
            System.Text.Encoding.ASCII.GetBytes("PNch").CopyTo(oldHeader, 0);
            await receiver.ProcessIncomingPacketAsync(oldHeader, new IPEndPoint(lan, 49123), QP.TransportType.Wan);
            Check(!receiver.AvailablePeers.ContainsKey(sender.CurrentPeer.Id), "previous control header is rejected before parsing the new discovery layout");
            string payload = sender.Discovery.GetWanNostrDiscoveryPayload()!;
            receiver.Discovery.OnWanNostrEventDiscovered(sender.CertManager.NostrPublicKeyHex, payload);
            Check(receiver.Discovery.DiscoveredPeers.TryGetValue(Convert.ToHexString(sender.CurrentPeer.CertHash), out var discovered)
                && discovered.IsCertVerified && discovered.PreferredPort == 49123 && discovered.PortArray.Length == 8,
                "signed Nostr discovery preserves eight ports and the LAN socket");
            using var rediscovered = Utilities.DecodeEndpointToken(discovered!.Token);
            Check(rediscovered.ConnectionFlags.ExternalPortMayDiffer, "signed Nostr discovery preserves the new flag");
            await receiver.ProcessIncomingPacketAsync(sender.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true),
                new IPEndPoint(lan, 49123), QP.TransportType.Wan);
            Check(receiver.AvailablePeers[sender.CurrentPeer.Id].ConnectionFlags.ExternalPortMayDiffer, "authenticated Hello preserves bit 5 on the wire");
            Check(QuicPunchConnection.BuildProbeTargets(rediscovered, null, 0).Single(ep => ep.Address.Equals(lan)).Port == 49123,
                "Nostr token connects through the LAN without any STUN lookup for that address");
            sender.CurrentPeer.PreferredPort = 49234;
            sender.InvalidateTokenCache();
            receiver.Discovery.OnWanNostrEventDiscovered(sender.CertManager.NostrPublicKeyHex, sender.Discovery.GetWanNostrDiscoveryPayload()!);
            Check(receiver.Discovery.DiscoveredPeers[Convert.ToHexString(sender.CurrentPeer.CertHash)].PreferredPort == 49234,
                "a signed LAN port change refreshes Nostr discovery even when WAN ports do not change");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        await TestLanSocketAsync();
    }
    private static async Task TestLanSocketAsync()
    {
        IPAddress lan = Utilities.GetValidLocalIPAddresses().FirstOrDefault(Utilities.IsPrivateLanAddress)
            ?? throw new Exception("LAN socket regression needs a local private IPv4 address.");
        string root = Path.Combine(Path.GetTempPath(), "qp-lan-socket-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<QP> Node(string name)
        {
            var node = new QP(appDataPath: Path.Combine(root, name))
                { EnableUpnp = false, PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
            node.StunServerEndpoints = [new IPEndPoint(IPAddress.Loopback, 9)];
            node.Discovery.LanDiscoveryPort = 0;
            node.Discovery.NostrRelays = ["ws://127.0.0.1:9"];
            node.ReputationFilter?.Dispose(); node.ReputationFilter = null;
            await node.StartAsync(deadline.Token);
            await node.Mailbox.StopAsync();
            node.CurrentPeer.Addresses = [IPAddress.Parse("93.184.216.34"), lan];
            node.CurrentPeer.PortArray = [7042, 65310];
            node.CurrentPeer.ConnectionFlags.PortMode = PortMode.Range;
            node.CurrentPeer.PreferredPort = (ushort)node.LocalBoundPort;
            return node;
        }
        try
        {
            await using var a = await Node("a");
            await using var b = await Node("b");
            a.TrustPeer(b.CurrentPeer.CertHash); b.TrustPeer(a.CurrentPeer.CertHash);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await a.PeerInterrogation(Utilities.EncodeEndpointToken(b.CurrentPeer), deadline.Token);
            while (!a.AvailablePeers.TryGetValue(b.CurrentPeer.Id, out var peer) || !peer.IsSessionReady)
                await Task.Delay(10, deadline.Token);
            var connected = a.AvailablePeers[b.CurrentPeer.Id];
            Check(connected.ActiveEndPoint!.Equals(new IPEndPoint(lan, b.LocalBoundPort)),
                "real UDP sockets authenticate through the exact LAN port despite a broad WAN range");
            Check(connected.PreferredPort == b.LocalBoundPort, "Hello retains the peer's local port");
            // A fresh token must resolve instead of leaving an endless interrogation
            // just because its local port lies outside the public NAT bounds.
            while (!a.ActiveInterrogations.IsEmpty) await Task.Delay(10, deadline.Token);
            Check(watch.Elapsed < TimeSpan.FromSeconds(5), "direct LAN connection resolves promptly without UPnP or STUN responses");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

}
