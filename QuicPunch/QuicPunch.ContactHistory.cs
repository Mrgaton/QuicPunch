namespace QuicPunch;

public partial class QuicPunch
{
    internal void RefreshSavedConnectionEndpoint(PeerInfo peer)
    {
        if (peer.ActiveTransport == TransportType.Tor || peer.QuicEndPoint?.Address is not { } address) return;
        try
        {
            if (peer.TryGetCertificateHash(out var hash) && PeerStore?.TryGet(hash, out var saved) == true &&
                saved != null && saved.LastKnownIp != address.ToString())
                PeerStore.RecordConnection(hash, address);
        }
        catch (Exception ex) { QuicPunchLog.Info($"[CONTACT HISTORY] Could not save migrated endpoint: {ex.Message}"); }
    }

    /// <summary>Find the invited device without granting application trust before identity review.</summary>
    public Task FindPeerFromInvitationAsync(string token, CancellationToken cancellationToken = default)
    {
        var peer = Helpers.Utilities.DecodeEndpointToken(token);
        return PeerInterrogationCore(peer, cancellationToken, ownsPeer: peer.Addresses is { Length: > 0 }, useDiscoveryBudget: false);
    }

    public void RecordPeerUsage(Guid peerId)
    {
        try
        {
            if (AvailablePeers.TryGetValue(peerId, out var peer) && peer.TryGetCertificateHash(out var hash))
                PeerStore?.RecordUsage(hash);
            else
            {
                // Saved peers have deterministic IDs, including while messaging through the offline mailbox.
                var saved = PeerStore?.SavedPeers.FirstOrDefault(p => new Guid(System.IO.Hashing.XxHash128.Hash(p.CertHash)) == peerId);
                if (saved != null) PeerStore?.RecordUsage(saved.CertHash);
            }
        }
        catch (Exception ex) { QuicPunchLog.Info($"[CONTACT HISTORY] Could not save activity: {ex.Message}"); }
    }
}
