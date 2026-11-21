using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using QuicPunch.Helpers;

namespace QuicPunch.Discovery;

internal sealed class AvailablePeerRegistry
{
    private readonly object _gate = new();

    public ConcurrentDictionary<Guid, PeerInfo> AvailablePeers { get; } = new();
    private readonly ConcurrentDictionary<string, Guid> _peersByCertHash = new();

    internal bool TryGetPeerByCertHash(byte[] certHash, out PeerInfo? peer)
    {
        peer = null;
        if (certHash == null || certHash.Length == 0) return false;
        string key = Base64Url.EncodeToString(certHash);
        if (_peersByCertHash.TryGetValue(key, out var peerId) && AvailablePeers.TryGetValue(peerId, out peer) &&
            peer.TryGetCertificateHash(out var indexedHash) && CryptographicOperations.FixedTimeEquals(indexedHash, certHash))
        {
            return true;
        }
        peer = AvailablePeers.Values.FirstOrDefault(p => p.TryGetCertificateHash(out var hash) && CryptographicOperations.FixedTimeEquals(hash, certHash));
        if (peer != null)
        {
            _peersByCertHash[key] = peer.Id;
            return true;
        }
        return false;
    }

    private void IndexPeerCertHash(PeerInfo peer)
    {
        if (peer.TryGetCertificateHash(out var hash))
        {
            string key = Base64Url.EncodeToString(hash);
            _peersByCertHash[key] = peer.Id;
        }
    }

    internal bool TryAddAvailablePeer(Guid peerId, PeerInfo peer, int maxDiscoveredPeers, Func<Guid, PeerInfo, bool> isProtected, Action<PeerInfo> onRemoved)
    {
        lock (_gate)
        {
            if (AvailablePeers.ContainsKey(peerId))
                return false;

            if (!isProtected(peerId, peer))
            {
                int limit = Math.Max(1, maxDiscoveredPeers);
                PruneDiscoveredPeersCore(QuicPunch.DiscoveredPeerTtl, limit - 1, isProtected, onRemoved);
                if (AvailablePeers.Count(kvp => !isProtected(kvp.Key, kvp.Value)) >= limit)
                    return false;
            }

            bool added = AvailablePeers.TryAdd(peerId, peer);
            if (added)
            {
                IndexPeerCertHash(peer);
            }
            return added;
        }
    }

    internal bool TryAdmitAuthenticatedPeer(PeerInfo peer, int maxDiscoveredPeers,
        Func<Guid, PeerInfo, bool> isProtected, Action<PeerInfo> onRemoved)
    {
        if (!peer.HasCertificate || !peer.IsSessionReady)
            throw new ArgumentException("An authenticated Hello session is required.", nameof(peer));

        lock (_gate)
        {
            if (!AvailablePeers.TryGetValue(peer.Id, out var existing))
                return TryAddAvailablePeer(peer.Id, peer, maxDiscoveredPeers, isProtected, onRemoved);
            if (existing.HasCertificate)
                return false;

            // Mailbox messages can create a saved identity before its UDP Hello.
            // Replace that placeholder only after validating the full Hello and
            // deriving its session. Keep the registered mailbox identity.
            peer.NostrPubKey ??= existing.NostrPubKey;
            if (!AvailablePeers.TryUpdate(peer.Id, peer, existing))
                return false;
            IndexPeerCertHash(peer);
            if (ReferenceEquals(existing.TorChannel, peer.TorChannel)) existing.TorChannel = null;
            existing.Dispose();
            return true;
        }
    }

    internal int PruneDiscoveredPeers(TimeSpan ttl, int maxDiscoveredPeers, Func<Guid, PeerInfo, bool> isProtected, Action<PeerInfo> onRemoved)
    {
        lock (_gate)
        {
            return PruneDiscoveredPeersCore(ttl, Math.Max(0, maxDiscoveredPeers), isProtected, onRemoved);
        }
    }

    private int PruneDiscoveredPeersCore(TimeSpan ttl, int maxDiscoveredPeers,
        Func<Guid, PeerInfo, bool> isProtected, Action<PeerInfo> onRemoved)
    {
        var removable = AvailablePeers
            .Where(kvp => !isProtected(kvp.Key, kvp.Value))
            .OrderBy(kvp => kvp.Value.LastActivityTimestampMonotonic)
            .ToList();

        int removed = 0;
        foreach (var kvp in removable.ToArray())
        {
            if (Stopwatch.GetElapsedTime(kvp.Value.LastActivityTimestampMonotonic) <= ttl)
                continue;
            if (RemoveDiscoveredPeer(kvp.Key, kvp.Value, onRemoved))
            {
                removable.Remove(kvp);
                removed++;
            }
        }

        while (removable.Count > maxDiscoveredPeers)
        {
            var oldest = removable[0];
            removable.RemoveAt(0);
            if (RemoveDiscoveredPeer(oldest.Key, oldest.Value, onRemoved))
                removed++;
        }

        return removed;
    }

    internal PeerInfo ResolveOrCreatePeerByNostrPubKey(PeerStore? store, string senderPubKey, string? senderName)
    {
        lock (_gate)
        {
            var peer = AvailablePeers.Values.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.NostrPubKey) &&
                string.Equals(p.NostrPubKey, senderPubKey, StringComparison.OrdinalIgnoreCase));

            if (peer != null) return peer;

            if (store != null && store.TryGetByNostrPubKey(senderPubKey, out var savedPeer) && savedPeer != null)
            {
                if (savedPeer.CertHash is { Length: 32 } && TryGetPeerByCertHash(savedPeer.CertHash, out var authenticated) && authenticated != null)
                {
                    authenticated.NostrPubKey = savedPeer.NostrPubKey ?? senderPubKey;
                    return authenticated;
                }
                peer = new PeerInfo();
                if (savedPeer.CertHash != null && savedPeer.CertHash.Length == 32)
                {
                    peer.SetCertificateHash(savedPeer.CertHash);
                }
                if (savedPeer.EcdhPublicKey != null)
                {
                    peer.SetECDHPublicKey(savedPeer.EcdhPublicKey);
                }
                peer.Name = savedPeer.Name ?? senderName ?? (senderPubKey.Length >= 8 ? senderPubKey[..8] : senderPubKey);
                peer.NostrPubKey = savedPeer.NostrPubKey ?? senderPubKey;
                peer.Addresses = savedPeer.Addresses ?? Array.Empty<IPAddress>();
                peer.PortArray = (ushort[])savedPeer.PortArray.Clone();
                peer.PreferredPort = savedPeer.PreferredPort;
                peer.ConnectionFlags = new ConnectionFlags(savedPeer.ConnectionFlags.RawValue);
                peer.OnionAddress = savedPeer.OnionAddress;
                peer.NetworkType = savedPeer.NetworkType;

                AvailablePeers.TryAdd(peer.Id, peer);
                return peer;
            }

            peer = new PeerInfo
            {
                Name = !string.IsNullOrWhiteSpace(senderName) ? senderName : (senderPubKey.Length >= 8 ? senderPubKey[..8] : senderPubKey),
                NostrPubKey = senderPubKey,
                Addresses = Array.Empty<IPAddress>(),
                PortArray = Array.Empty<ushort>()
            };
            AvailablePeers.TryAdd(peer.Id, peer);
            return peer;
        }
    }

    private bool RemoveDiscoveredPeer(Guid peerId, PeerInfo expectedPeer, Action<PeerInfo> onRemoved)
    {
        var pair = new KeyValuePair<Guid, PeerInfo>(peerId, expectedPeer);
        if (!((ICollection<KeyValuePair<Guid, PeerInfo>>)AvailablePeers).Remove(pair))
            return false;

        UnindexPeer(peerId, expectedPeer);
        try { expectedPeer.Dispose(); } catch { }
        onRemoved(expectedPeer);
        return true;
    }

    internal bool TryRemove(Guid peerId, [NotNullWhen(true)] out PeerInfo? peer)
    {
        lock (_gate)
        {
            if (!AvailablePeers.TryRemove(peerId, out peer)) return false;
            UnindexPeer(peerId, peer);
            return true;
        }
    }

    private void UnindexPeer(Guid peerId, PeerInfo peer)
    {
        if (peer.TryGetCertificateHash(out var hash))
            _peersByCertHash.TryRemove(new KeyValuePair<string, Guid>(Base64Url.EncodeToString(hash), peerId));
    }

    internal void ClearAndDispose()
    {
        lock (_gate)
        {
            foreach (PeerInfo peer in AvailablePeers.Values)
            {
                try { peer.Dispose(); } catch { }
            }
            AvailablePeers.Clear();
            _peersByCertHash.Clear();
        }
    }
}
