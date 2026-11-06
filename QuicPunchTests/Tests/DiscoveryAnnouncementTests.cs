using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

internal static class DiscoveryAnnouncementTests
{
    public static async Task RunAsync()
    {
        if (JsonSerializer.IsReflectionEnabledByDefault)
            throw new Exception("This regression must run with reflection disabled, like the release APK.");
        var root = Path.Combine(Path.GetTempPath(), "qp-discovery-json-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var sender = new QuicPunch.QuicPunch(appDataPath: Path.Combine(root, "sender"))
                { WanEnabled = false, EnableUpnp = false, PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
            await using var receiver = new QuicPunch.QuicPunch(appDataPath: Path.Combine(root, "receiver"))
                { WanEnabled = false, EnableUpnp = false, PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
            sender.CurrentPeer.Name = "Phone <release>";
            sender.CurrentPeer.Addresses = [IPAddress.Parse("198.51.100.31")];
            sender.CurrentPeer.PortArray = [45678];
            sender.TorCurrentPeer.OnionAddress = new string('a', 56) + ".onion";
            sender.TorCurrentPeer.PortArray = [443];
            await receiver.StartAsync();
            receiver.Discovery.WanNostrDiscoveryEnabled = true;
            receiver.Discovery.TorNostrDiscoveryEnabled = true;
            foreach (bool tor in new[] { false, true })
            {
                var payload = tor ? sender.Discovery.GetTorNostrDiscoveryPayload() : sender.Discovery.GetWanNostrDiscoveryPayload();
                if (string.IsNullOrWhiteSpace(payload)) throw new Exception("Trimmed discovery announcement disappeared.");
                using var json = JsonDocument.Parse(payload);
                var peer = tor ? sender.TorCurrentPeer : sender.CurrentPeer;
                string token = json.RootElement.GetProperty(tor ? "tor" : "wan").GetString()!;
                byte[] key = Convert.FromBase64String(json.RootElement.GetProperty("certPubKey").GetString()!);
                byte[] signature = Convert.FromBase64String(json.RootElement.GetProperty("sig").GetString()!);
                if (json.RootElement.EnumerateObject().Count() != 5 || !Guid.TryParseExact(json.RootElement.GetProperty("presence").GetString(), "N", out _) || json.RootElement.GetProperty("name").GetString() != peer.Name ||
                    !Utilities.TryVerifyTokenCertificate(token, key, signature, out var hash) || hash == null ||
                    !CryptographicOperations.FixedTimeEquals(hash, peer.CertHash))
                    throw new Exception("Announcement changed its wire contract or lost its authenticated identity.");
                if (tor) receiver.Discovery.OnTorNostrEventDiscovered(sender.TorCertManager.NostrPublicKeyHex, payload);
                else receiver.Discovery.OnWanNostrEventDiscovered(sender.CertManager.NostrPublicKeyHex, payload);
                if (!receiver.Discovery.DiscoveredPeers.TryGetValue(Convert.ToHexString(peer.CertHash), out var discovered) ||
                    !discovered.IsCertVerified || discovered.Name != peer.Name)
                    throw new Exception("A release announcement was not discovered and verified by the other node.");

                receiver.PeerStore!.AddOrUpdate(token, autoConnect: false);
                using var active = Utilities.DecodeEndpointToken(token);
                active.Addresses = [IPAddress.Parse("198.51.100.99")];
                active.PortArray = [12345];
                active.PreferredPort = 12345;
                if (tor) active.OnionAddress = new string('b', 56) + ".onion";
                receiver.PeerStore.AddOrUpdate(active, autoConnect: false);
                receiver.AvailablePeers[active.Id] = active;
                // The exact same announcement is already inside the candidate cooldown.
                if (tor) receiver.Discovery.OnTorNostrEventDiscovered(sender.TorCertManager.NostrPublicKeyHex, payload);
                else receiver.Discovery.OnWanNostrEventDiscovered(sender.CertManager.NostrPublicKeyHex, payload);
                if (!active.PortArray.SequenceEqual(peer.PortArray) || active.PreferredPort != peer.PreferredPort ||
                    !receiver.PeerStore.TryGet(peer.CertHash, out var refreshed) || refreshed!.PreferredPort != peer.PreferredPort || (tor
                    ? active.OnionAddress != peer.OnionAddress
                    : !active.Addresses.SequenceEqual(peer.Addresses)))
                    throw new Exception("Candidate cooldown prevented a verified connection-target refresh.");

                // Signed address changes update saved contacts without granting trust or auto-connect.
                receiver.PeerStore.SetTrusted(peer.CertHash, false);
                peer.PortArray = [54321];
                if (tor) peer.OnionAddress = new string('c', 56) + ".onion";
                else peer.Addresses = [IPAddress.Parse("198.51.100.42")];
                sender.InvalidateTokenCache();
                var changedPayload = tor ? sender.Discovery.GetTorNostrDiscoveryPayload() : sender.Discovery.GetWanNostrDiscoveryPayload();
                if (tor) receiver.Discovery.OnTorNostrEventDiscovered(sender.TorCertManager.NostrPublicKeyHex, changedPayload!);
                else receiver.Discovery.OnWanNostrEventDiscovered(sender.CertManager.NostrPublicKeyHex, changedPayload!);
                if (!receiver.PeerStore.TryGet(peer.CertHash, out var saved) || saved == null || saved.Trusted || saved.AutoConnect ||
                    !saved.PortArray.SequenceEqual(peer.PortArray) || !active.PortArray.SequenceEqual(peer.PortArray) || (tor
                        ? saved.OnionAddress != peer.OnionAddress || active.OnionAddress != peer.OnionAddress
                        : !saved.Addresses.SequenceEqual(peer.Addresses) || !active.Addresses.SequenceEqual(peer.Addresses)))
                    throw new Exception("Verified endpoint refresh lost the saved/active address or changed contact permissions.");
                using var reloaded = new PeerStore(Path.Combine(root, "receiver", "peers.db"), watch: false);
                if (!reloaded.TryGet(peer.CertHash, out var persisted) || persisted == null ||
                    !persisted.PortArray.SequenceEqual(peer.PortArray) || persisted.Trusted || persisted.AutoConnect)
                    throw new Exception("Verified endpoint refresh was not persisted with its existing permissions.");

                // Neither a bad certificate signature nor an unsigned token may overwrite these endpoints.
                peer.PortArray = [11111]; sender.InvalidateTokenCache();
                string spoofed = (tor ? sender.GetTorToken() : sender.GetWanToken()) ?? throw new Exception("Missing spoofing test token.");
                if (tor) {
                    receiver.Discovery.ProcessTorNostrEndpointToken(spoofed, certPubKey: key, signature: signature);
                    receiver.Discovery.ProcessTorNostrEndpointToken(spoofed);
                } else {
                    receiver.Discovery.ProcessWanNostrEndpointToken(spoofed, certPubKey: key, signature: signature);
                    receiver.Discovery.ProcessWanNostrEndpointToken(spoofed);
                }
                if (!active.PortArray.SequenceEqual(new ushort[] {54321}) ||
                    !receiver.PeerStore.TryGet(peer.CertHash, out saved) || !saved!.PortArray.SequenceEqual(active.PortArray))
                    throw new Exception("Unauthenticated discovery changed saved or active endpoints.");
                receiver.AvailablePeers.TryRemove(active.Id, out _);
            }
            Console.WriteLine("[OK] WAN/Tor announcements authenticate with JSON reflection disabled; endpoint refresh bypasses cooldown, persists without granting trust, and rejects unsigned/invalid updates.");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
