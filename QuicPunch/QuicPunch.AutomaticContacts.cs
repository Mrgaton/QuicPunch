using System.Collections.Concurrent;
using QuicPunch.Helpers;

namespace QuicPunch;

public partial class QuicPunch
{
    private DateTime _automaticContactsStartedUtc;
    private Task? _automaticContactProbeTask;
    private readonly ConcurrentDictionary<string, byte> _reactivatedAutomaticContacts = new(StringComparer.Ordinal);
    internal static readonly TimeSpan AutomaticContactActivityWindow = TimeSpan.FromDays(2);

    // Eligibility is anchored to this boot. A contact does not age out mid-run.
    internal bool IsAutomaticContactEligible(PeerStore.SavedPeer contact) => contact.AutoConnect &&
        (_reactivatedAutomaticContacts.ContainsKey(Convert.ToHexString(contact.CertHash)) ||
         HasRecentAuthenticatedActivity(contact, _automaticContactsStartedUtc));

    internal static bool HasRecentAuthenticatedActivity(PeerStore.SavedPeer contact, DateTime startupUtc)
    {
        DateTime activity = contact.LastAuthenticatedActivityUtc ?? DateTime.MinValue;
        if (contact.LastConnectedUtc is { } connected && connected > activity) activity = connected;
        return activity >= startupUtc - AutomaticContactActivityWindow;
    }

    internal static int AutomaticContactRetryDelayMilliseconds(int completedSends) =>
        (int)Math.Min(25000L, 500L << Math.Clamp(completedSends - 1, 0, 6));

    // Called only after discovery has verified the token certificate and author.
    // Saved contacts retain their own opt-out even with global discovery enabled.
    private bool QueueSavedPeerReconnect(PeerInfo peer, bool restartProbe)
    {
        if (!peer.IsCertVerified || !peer.TryGetCertificateHash(out var hash) ||
            PeerStore?.TryGet(hash, out var saved) != true || saved == null) return false;
        if (saved.AutoConnect)
        {
            _reactivatedAutomaticContacts[Convert.ToHexString(hash)] = 0;
            if (restartProbe)
                foreach (var attempt in ActiveInterrogations.Values.Where(x => x.IsAutomaticContact && PeerTargetsMatch(x.Peer, peer)))
                    CancelInterrogation(attempt.Id);
        }
        return true;
    }

    internal void RecordAuthenticatedPeerActivity(PeerInfo peer, DateTime? observedUtc = null)
    {
        try
        {
            if (peer.HasCertificate && peer.IsSessionReady && peer.TryGetCertificateHash(out var hash))
                PeerStore?.RecordAuthenticatedActivity(hash, observedUtc);
        }
        catch (Exception ex) { QuicPunchLog.Info($"[CONTACT HISTORY] Could not save authenticated activity: {ex.Message}"); }
    }

    private async Task AutomaticContactProbeLoopAsync(long generation, CancellationToken token)
    {
        try
        {
            while (IsLifecycleWorkerCurrent(generation, token))
            {
                try
                {
                    var store = PeerStore;
                    if (store != null)
                    {
                        foreach (var attempt in ActiveInterrogations.Values.Where(x => x.IsAutomaticContact))
                            if (!attempt.Peer.TryGetCertificateHash(out var hash) ||
                                !store.TryGet(hash, out var saved) || saved == null || !IsAutomaticContactEligible(saved))
                                CancelInterrogation(attempt.Id);

                        var probingHashes = ActiveInterrogations.Values
                            .Where(x => x.Peer.TryGetCertificateHash(out _))
                            .Select(x => Convert.ToHexString(x.Peer.CertHash)).ToHashSet(StringComparer.Ordinal);
                        if (IsWanStarted)
                            foreach (var saved in store.SavedPeers.Where(IsAutomaticContactEligible))
                            {
                                if (saved.NetworkType == NetworkType.Tor || saved.Addresses.Length == 0) continue;
                                if (TryGetPeerByCertHash(saved.CertHash, out var available) && available != null &&
                                    ((TryGetPeerMultiplexer(available.Id, out var mux) && mux is { IsDisposed: false }) ||
                                     (available.HasCertificate && available.IsSessionReady && available.IsAvailableAndResponsive()))) continue;
                                if (!probingHashes.Add(Convert.ToHexString(saved.CertHash))) continue;

                                await PeerInterrogationCore(CreatePeerInfoFromSavedPeer(saved), TransportLifetime(TransportType.Wan),
                                    ownsPeer: true, useDiscoveryBudget: false, automaticContact: true).ConfigureAwait(false);
                            }
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception ex) when (!token.IsCancellationRequested)
                { QuicPunchLog.Info($"[AUTO-CONNECT] Contact probe deferred: {ex.Message}"); }
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
