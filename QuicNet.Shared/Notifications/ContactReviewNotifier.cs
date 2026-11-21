using System.Diagnostics;
using System.Runtime.CompilerServices;
using QuicNet.Settings;
using QuicNet.WebUi;
using QuicPunch;

namespace QuicNet.Notifications;

internal sealed class ContactReviewNotifier : IDisposable
{
    internal static readonly TimeSpan MinimumPresence = TimeSpan.FromMinutes(1);
    // The signed availability probe runs after 20 seconds of inactivity. Allow one missed probe.
    internal static readonly TimeSpan MaximumConfirmationGap = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FreshConfirmationAge = TimeSpan.FromSeconds(10);
    private const int RememberedContactLimit = 4096;

    private readonly QuicPunch.QuicPunch _node;
    private readonly AppPreferencesStore _preferences;
    private readonly AppNotificationService _notifications;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(2));
    private readonly object _sync = new();
    private readonly ConditionalWeakTable<PeerInfo, PresenceWindow> _windows = new();
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private readonly Queue<string> _notificationOrder = new();
    private bool _disposed;

    internal ContactReviewNotifier(QuicPunch.QuicPunch node, AppPreferencesStore preferences,
        AppNotificationService notifications, CancellationToken shutdown)
    {
        _node = node;
        _preferences = preferences;
        _notifications = notifications;
        _ = RunAsync(shutdown);
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(shutdown).ConfigureAwait(false))
            {
                try
                {
                    lock (_sync)
                    {
                        if (_disposed || shutdown.IsCancellationRequested) return;
                        foreach (var (_, peer) in _node.AvailablePeers)
                            CheckPeer(peer);
                    }
                }
                catch (Exception ex) { WebUiServer.LogEvent("[NOTIFICATIONS] " + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private void CheckPeer(PeerInfo peer)
    {
        if (peer.LastAvailabilityConfirmation is not { } proof || !peer.IsSessionReady ||
            proof.Transport != peer.ActiveTransport || !proof.SessionNonce.AsSpan().SequenceEqual(peer.SessionNonce) ||
            Stopwatch.GetElapsedTime(proof.Timestamp) > FreshConfirmationAge)
            return;

        var lifetime = _node.TransportLifetime(proof.Transport);
        if (lifetime.IsCancellationRequested) return;
        if (_node.IsTrustedPeer(peer))
        {
            _windows.Remove(peer);
            return;
        }

        var window = _windows.GetOrCreateValue(peer);
        if (window.Handled || !window.Confirm(proof, lifetime)) return;

        var certHash = peer.CertHash;
        string identity = Convert.ToBase64String(certHash);
        if (lifetime.IsCancellationRequested || !_node.AvailablePeers.TryGetValue(peer.Id, out var current) ||
            !ReferenceEquals(current, peer) || _node.IsTrustedPeer(peer))
            return;

        window.Handled = true;
        if (_notified.Contains(identity) || _preferences.Snapshot().ReviewedUntrustedPeers.Contains(identity)) return;

        _notified.Add(identity);
        _notificationOrder.Enqueue(identity);
        if (_notificationOrder.Count > RememberedContactLimit) _notified.Remove(_notificationOrder.Dequeue());
        _notifications.Publish("system", "Contacto por revisar", peer.Name ?? "Nuevo dispositivo", "/",
            Convert.ToHexString(certHash), "peer:" + peer.Id);
    }

    internal sealed class PresenceWindow
    {
        private long _startedAt;
        private PeerInfo.AvailabilityConfirmation? _last;
        private CancellationToken _transportLifetime;
        internal bool Handled { get; set; }

        internal bool Confirm(PeerInfo.AvailabilityConfirmation proof, CancellationToken transportLifetime)
        {
            if (_last is { } previous && proof.Timestamp <= previous.Timestamp) return false;

            if (_last == null || _transportLifetime != transportLifetime || _last.Transport != proof.Transport ||
                !_last.SessionNonce.AsSpan().SequenceEqual(proof.SessionNonce) ||
                Stopwatch.GetElapsedTime(_last.Timestamp, proof.Timestamp) > MaximumConfirmationGap)
                _startedAt = proof.Timestamp;

            _last = proof;
            _transportLifetime = transportLifetime;
            return Stopwatch.GetElapsedTime(_startedAt, proof.Timestamp) >= MinimumPresence;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
