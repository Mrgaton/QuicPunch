using System.Net;
using System.Security.Cryptography;
using QuicNet.Networking;
using QuicNet.Protocols;
using QuicNet.Serialization;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicNet.WebUi.Modules;

internal sealed class StatusPeerProjection(
    QuicPunch.QuicPunch node, IVirtualLanHandler? lanHandler, LocalIpGeolocation geolocation)
{
    private DateTime? GetDiscoveryAvailableUntilUtc(byte[]? certHash) => certHash != null &&
        node.Discovery.DiscoveredPeers.TryGetValue(Convert.ToHexString(certHash), out var discovered)
            ? discovered.DiscoveryAvailableUntilUtc : null;

    internal List<AvailablePeerStatus> GetAvailable(IReadOnlyDictionary<string, PeerStore.SavedPeer> savedByHash)
    {
        return node.AvailablePeers.Values
            .Where(peer =>
            {
                if (!node.TryGetPeerMultiplexer(peer.Id, out var m) || m == null || m.IsDisposed)
                {
                    if (!peer.IsAvailableAndResponsive(TimeSpan.FromMinutes(1)))
                    {
                        return false;
                    }
                }
                return true;
            })
            .Select(peer =>
        {
            string certHash = peer.TryGetCertificateHash(out var hash) ? Convert.ToBase64String(hash) : "";
            string canonId = Utilities.ToCanonicalPeerId(hash);
            savedByHash.TryGetValue(certHash, out var savedPeer);
            bool trusted = node.IsTrustedPeer(peer);

            bool isQuicConnected = false;
            QuicConnectionTelemetry? liveTelem = null;
            if (node.TryGetPeerMultiplexer(peer.Id, out var mux) && mux != null && !mux.IsDisposed)
            {
                isQuicConnected = true;
                if (mux.TryGetTelemetry(out var muxTelem))
                {
                    liveTelem = muxTelem;
                    peer.LastTelemetry = muxTelem;
                    peer.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(muxTelem.RttMs, 1)));
                    peer.LastPingResponseUtc = DateTime.UtcNow;
                    peer.LastSeen = DateTime.UtcNow;
                }
            }

            int lastSeenSec = peer.LastSeen > DateTime.MinValue ? (int)Math.Max(0, (DateTime.UtcNow - peer.LastSeen).TotalSeconds) : 99999;
            int? unresponsiveSec = isQuicConnected
                ? 0
                : (int)Math.Max(0, System.Diagnostics.Stopwatch.GetElapsedTime(peer.LastActivityTimestampMonotonic).TotalSeconds);

            double? pingMs = liveTelem != null
                ? Math.Round(liveTelem.RttMs, 1)
                : peer.LastPingResponseUtc is { } lastPing && DateTime.UtcNow - lastPing < TimeSpan.FromSeconds(30)
                    ? peer.Ping?.TotalMilliseconds : null;
            if (pingMs is { } measured && (!double.IsFinite(measured) || measured <= 0)) pingMs = null;

            bool isResponsive = isQuicConnected && pingMs.HasValue;
            string displayName = !string.IsNullOrWhiteSpace(savedPeer?.Name) ? savedPeer.Name : (peer.Name ?? "Peer");

            return new AvailablePeerStatus(peer.Id.ToString(), canonId, displayName, savedPeer?.Name, peer.Name, pingMs, isResponsive, isQuicConnected ? "QUIC RTT" : null, isQuicConnected, isQuicConnected && lanHandler != null ? mux?.GetRemoteProtocolAvailability(lanHandler.ProtocolId) : null, isQuicConnected ? null : peer.LastQuicConnectionError, peer.LastQuicConnectionAttemptUtc, isQuicConnected ? "connected" : (node.IsPeerMultiplexerActiveOrConnecting(peer.Id) ? "connecting" : "available"), isQuicConnected ? peer.QuicEndPoint?.ToString() ?? "" : "", unresponsiveSec ?? 99999, peer.ActiveEndPoint?.ToString() ?? "", peer.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(), peer.PortArray ?? Array.Empty<ushort>(), peer.ConnectionFlags?.PortMode.ToString() ?? "Single", peer.NetworkType.ToString(), peer.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrWhiteSpace(peer.OnionAddress), peer.OnionAddress ?? "", certHash, peer.NostrPubKey ?? savedPeer?.NostrPubKey ?? "", !string.IsNullOrEmpty(peer.NostrPubKey ?? savedPeer?.NostrPubKey), trusted, savedPeer != null, savedPeer?.AutoConnect ?? false, savedPeer?.AutomaticPermissions ?? [], savedPeer?.Profile == PeerProfile.Administrator ? "administrator" : "friend", lastSeenSec, StatusApiModule.MapTelemetry(liveTelem))
            {
                activeTransport = isQuicConnected ? peer.ActiveTransport.ToString().ToLowerInvariant() : null,
                discoveryAvailableUntilUtc = GetDiscoveryAvailableUntilUtc(hash),
                lastConnectedUtc = savedPeer?.LastConnectedUtc,
                lastUsedUtc = savedPeer?.LastUsedUtc,
                lastKnownIp = savedPeer?.LastKnownIp,
                usageCount = savedPeer?.UsageCount ?? 0,
                locationIsHistorical = !isQuicConnected && savedPeer?.LastKnownIp != null,
                location = geolocation.Locate(peer.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrWhiteSpace(peer.OnionAddress), isQuicConnected ? peer.QuicEndPoint?.ToString() : savedPeer?.LastKnownIp ?? peer.ActiveEndPoint?.ToString(), peer.Addresses?.Select(a => a.ToString()))
            };
        }).OrderByDescending(p => p.isTrusted).ThenBy(p => p.name).ToList();
    }

    internal List<SavedPeerStatus> GetSaved(IEnumerable<PeerStore.SavedPeer> saved)
    {
        return saved.Select(item => new SavedPeerStatus(new Guid(System.IO.Hashing.XxHash128.Hash(item.CertHash)).ToString(), Utilities.ToCanonicalPeerId(item.CertHash), item.Name ?? "", Convert.ToBase64String(item.CertHash), item.NostrPubKey ?? "", !string.IsNullOrEmpty(item.NostrPubKey), item.AutoConnect, item.AutomaticPermissions, item.Profile == PeerProfile.Administrator ? "administrator" : "friend", item.OnionAddress ?? "", item.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(), item.PortArray ?? Array.Empty<ushort>(), item.ConnectionFlags?.PortMode.ToString() ?? "Single", item.NetworkType.ToString()) { discoveryAvailableUntilUtc = GetDiscoveryAvailableUntilUtc(item.CertHash), isTrusted = node.IsTrustedPeer(item.CertHash), lastConnectedUtc = item.LastConnectedUtc, lastUsedUtc = item.LastUsedUtc, lastKnownIp = item.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor ? null : item.LastKnownIp, usageCount = item.UsageCount, locationIsHistorical = item.LastKnownIp != null, location = geolocation.Locate(item.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrWhiteSpace(item.OnionAddress), item.LastKnownIp, item.Addresses?.Select(a => a.ToString())) }).ToList();
    }

    internal List<DiscoveredPeerStatus> GetDiscovered(
        IReadOnlyDictionary<string, PeerStore.SavedPeer> savedByHash, IReadOnlyCollection<IPAddress> localIps)
    {
        node.Discovery.PruneStaleDiscoveredPeersWithLocalAddresses(localIps);
        var activePeersByHash = IndexPeers(node.AvailablePeers.Values);
        Dictionary<string, PeerInfo>? interrogationsByHash = null;
        var wanHash = node.LocalWanCertHash;
        var torHash = node.LocalTorCertHash;
        string localWanHashB64 = wanHash != null ? Convert.ToBase64String(wanHash) : "";
        string localTorHashB64 = torHash != null ? Convert.ToBase64String(torHash) : "";
        var wanNostrPub = node.WanNostrPublicKeyHex;
        var torNostrPub = node.TorNostrPublicKeyHex;
        var localName = node.CurrentPeer?.Name;
        var localPeerAddrs = node.CurrentPeer?.Addresses;
        var boundPort = node.LocalDiscoveryPort;
        var publicIps = localPeerAddrs?.Where(addr => !localIps.Contains(addr)).ToArray() ?? [];

        return node.Discovery.DiscoveredPeers.Values
            .Where(dp =>
            {
                if (activePeersByHash.ContainsKey(dp.CertHashBase64)) return false;
                if (!string.IsNullOrEmpty(localWanHashB64) && dp.CertHashBase64 == localWanHashB64) return false;
                if (!string.IsNullOrEmpty(localTorHashB64) && dp.CertHashBase64 == localTorHashB64) return false;

                if (!string.IsNullOrEmpty(dp.NostrPubKey))
                {
                    if (!string.IsNullOrEmpty(wanNostrPub) && string.Equals(dp.NostrPubKey, wanNostrPub, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!string.IsNullOrEmpty(torNostrPub) && string.Equals(dp.NostrPubKey, torNostrPub, StringComparison.OrdinalIgnoreCase)) return false;
                }

                if (dp.NetworkType != QuicPunch.QuicPunch.NetworkType.Tor)
                {
                    var parsed = dp.Addresses?.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).Where(a => a != null && !IPAddress.IsLoopback(a)).Cast<IPAddress>().ToList() ?? new List<IPAddress>();

                    // Check: Contains both this machine's local private IP AND public WAN IP for self/unnamed ghost
                    if (parsed.Count > 0 && localPeerAddrs != null && localPeerAddrs.Length > 0)
                    {
                        bool hasLocalIp = parsed.Any(a => localIps.Contains(a));
                        bool hasPublicIp = publicIps.Length > 0 && parsed.Any(a => publicIps.Contains(a));
                        bool isGhostOrSameName = string.IsNullOrWhiteSpace(dp.Name) || dp.Name == "Discovered Peer" ||
                            (!string.IsNullOrWhiteSpace(localName) && string.Equals(localName, dp.Name, StringComparison.OrdinalIgnoreCase));
                        if (hasLocalIp && hasPublicIp && isGhostOrSameName) return false;
                    }

                    bool nameMatches = !string.IsNullOrWhiteSpace(localName) && !string.IsNullOrWhiteSpace(dp.Name) &&
                        string.Equals(localName, dp.Name, StringComparison.OrdinalIgnoreCase);

                    if (nameMatches && parsed.Count > 0)
                    {
                        if (parsed.Any(a => localIps.Contains(a))) return false;
                        if (localPeerAddrs != null && parsed.Any(a => localPeerAddrs.Contains(a))) return false;
                    }

                    if (parsed.Count > 0 && parsed.All(a => localIps.Contains(a)))
                    {
                        if (Utilities.HasExactAdvertisedPort(dp.ConnectionFlags, dp.PortArray, boundPort))
                            return false;
                    }
                }
                else
                {
                    var localTorPeer = node.TorCurrentPeer;
                    if (!string.IsNullOrEmpty(localTorPeer?.OnionAddress) && !string.IsNullOrEmpty(dp.OnionAddress) &&
                        string.Equals(localTorPeer.OnionAddress, dp.OnionAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            })
            .Select(dp =>
            {
                PeerStore.SavedPeer? savedPeer = null;
                if (dp.CertHash != null && node.PeerStore != null)
                {
                    node.PeerStore.TryGet(dp.CertHash, out savedPeer);
                }
                if (savedPeer == null)
                {
                    savedByHash.TryGetValue(dp.CertHashBase64, out savedPeer);
                }
                if (savedPeer == null && !string.IsNullOrEmpty(dp.NostrPubKey) && node.PeerStore != null)
                {
                    node.PeerStore.TryGetByNostrPubKey(dp.NostrPubKey, out savedPeer);
                }

                string displayName = !string.IsNullOrWhiteSpace(savedPeer?.Name)
                    ? savedPeer.Name
                    : (!string.IsNullOrWhiteSpace(dp.Name) && dp.Name != "Discovered Peer" ? dp.Name : "Discovered Peer");

                if (displayName == "Discovered Peer" && dp.CertHash is { } discoveredHash)
                {
                    string certHash = Convert.ToBase64String(discoveredHash);
                    if (activePeersByHash.TryGetValue(certHash, out var available) &&
                        !string.IsNullOrWhiteSpace(available.Name) && available.Name is not ("Peer" or "Discovered Peer"))
                        displayName = available.Name;

                    if (displayName == "Discovered Peer")
                    {
                        interrogationsByHash ??= IndexPeers(node.ActiveInterrogations.Values.Select(session => session.Peer));
                        if (interrogationsByHash.TryGetValue(certHash, out var interrogated) &&
                            !string.IsNullOrWhiteSpace(interrogated.Name) &&
                            interrogated.Name is not ("Saved Peer" or "Peer" or "Unknown" or "Discovered Peer"))
                            displayName = interrogated.Name;
                    }
                }

                bool endpointChanged = false;
                if (savedPeer != null)
                {
                    var savedIps = new HashSet<string>((savedPeer.Addresses ?? Array.Empty<IPAddress>()).Select(a => a.ToString()), StringComparer.OrdinalIgnoreCase);
                    var dpIps = new HashSet<string>(dp.Addresses ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                    endpointChanged = !savedIps.SetEquals(dpIps) ||
                                      !(savedPeer.PortArray ?? Array.Empty<ushort>()).SequenceEqual(dp.PortArray ?? Array.Empty<ushort>()) ||
                                      (!string.IsNullOrEmpty(dp.OnionAddress) && !string.Equals(dp.OnionAddress, savedPeer.OnionAddress, StringComparison.OrdinalIgnoreCase)) ||
                                      (dp.CertHash != null && !CryptographicOperations.FixedTimeEquals(dp.CertHash, savedPeer.CertHash));
                }

                return new DiscoveredPeerStatus(dp.Id, Utilities.ToCanonicalPeerId(dp.CertHash), displayName, dp.Name, savedPeer?.Name, savedPeer != null, endpointChanged, savedPeer?.AutoConnect ?? false, dp.Token, dp.CertHashBase64, dp.NostrPubKey ?? "", dp.Addresses, dp.OnionAddress, dp.PortArray ?? Array.Empty<ushort>(), dp.ConnectionFlags?.PortMode.ToString() ?? (dp.PortArray == null || dp.PortArray.Length <= 1 ? "Single" : (dp.PortArray.Length > PortAdvertisementCodec.MaxExplicitPorts ? "Range" : "Multiple")), dp.NetworkType.ToString(), dp.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrWhiteSpace(dp.OnionAddress), dp.Source, dp.IsCertVerified, dp.CertPublicKeyBase64, dp.DiscoveredAt.ToString("HH:mm:ss"), (int)Math.Max(0, (DateTime.UtcNow - dp.LastSeen).TotalSeconds)) { discoveryAvailableUntilUtc = dp.DiscoveryAvailableUntilUtc, location = geolocation.Locate(dp.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrWhiteSpace(dp.OnionAddress), null, dp.Addresses) };
            }).OrderByDescending(p => p.isSaved).ThenBy(p => p.name).ToList();
    }

    private static Dictionary<string, PeerInfo> IndexPeers(IEnumerable<PeerInfo> peers)
    {
        var index = new Dictionary<string, PeerInfo>(StringComparer.Ordinal);
        foreach (var peer in peers)
        {
            if (peer.TryGetCertificateHash(out var hash))
                index.TryAdd(Convert.ToBase64String(hash), peer);
        }
        return index;
    }
}
