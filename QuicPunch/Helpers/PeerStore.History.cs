using System.Net;

namespace QuicPunch.Helpers;

public sealed partial class PeerStore
{
    /// <summary>Only call after an authenticated transport is established, never for an announcement or ping.</summary>
    public bool RecordConnection(byte[] certificate, IPAddress? address, DateTime? utcNow = null) =>
        RecordHistory(certificate, address, false, utcNow ?? DateTime.UtcNow);

    /// <summary>Activity is counted in one-minute windows, so messages cannot create a disk-write storm.</summary>
    public bool RecordUsage(byte[] certificate, DateTime? utcNow = null) =>
        RecordHistory(certificate, null, true, utcNow ?? DateTime.UtcNow);

    /// <summary>Authenticated Hello/heartbeat activity, persisted at most once per minute. Announcements and outgoing sends do not count.</summary>
    public bool RecordAuthenticatedActivity(byte[] certificate, DateTime? utcNow = null)
    {
        ThrowIfDisposed();
        SavedPeer updated;
        lock (_sync)
        {
            if (!_peers.TryGetValue(Key(certificate), out var peer)) return false;
            DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
            DateTime previous = peer.LastAuthenticatedActivityUtc ?? peer.LastConnectedUtc ?? DateTime.MinValue;
            if (now - previous < TimeSpan.FromMinutes(1)) return false;
            updated = peer with { LastAuthenticatedActivityUtc = now };
            _peers[updated.Key] = updated;
            UpdateCacheLocked();
        }
        Save();
        Safe(() => PeerModified?.Invoke(updated.Copy(), false));
        return true;
    }

    private bool RecordHistory(byte[] certificate, IPAddress? address, bool usage, DateTime now)
    {
        ThrowIfDisposed();
        SavedPeer updated;
        lock (_sync)
        {
            if (!_peers.TryGetValue(Key(certificate), out var peer)) return false;
            now = now.ToUniversalTime();
            if (usage && peer.LastUsedUtc is { } lastUsed && now - lastUsed < TimeSpan.FromMinutes(1)) return false;
            if (!usage && peer.LastConnectedUtc is { } connected && now < connected) return false;
            bool tor = peer.NetworkType == QuicPunch.NetworkType.Tor || !string.IsNullOrWhiteSpace(peer.OnionAddress);
            updated = usage
                ? peer with { LastUsedUtc = now, UsageCount = peer.UsageCount == long.MaxValue ? long.MaxValue : peer.UsageCount + 1 }
                : peer with { LastConnectedUtc = now,
                    LastAuthenticatedActivityUtc = peer.LastAuthenticatedActivityUtc > now ? peer.LastAuthenticatedActivityUtc : now,
                    LastKnownIp = tor ? null : address?.ToString() ?? peer.LastKnownIp };
            _peers[updated.Key] = updated;
            UpdateCacheLocked();
        }
        Save();
        Safe(() => PeerModified?.Invoke(updated.Copy(), false));
        return true;
    }
}
