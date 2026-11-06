using System.Diagnostics;
using QuicNet.Notifications;
using QuicNet.Settings;
using QuicPunch.Multiplexer;

namespace QuicNet.Presence;

internal sealed record UserPresence(bool isAfk, bool acceptsNotifications, bool doNotDisturb, int idleAfterMinutes, string activitySource, DateTimeOffset? doNotDisturbUntil);
internal sealed class UserPresenceService : IDisposable
{
    private readonly QuicPunch.QuicPunch _node;
    private readonly AppPreferencesStore _preferences;
    private readonly Func<IAppPlatform?> _platform;
    private readonly CancellationTokenSource _stop;
    private readonly Task _worker;
    private long _lastActivity = Stopwatch.GetTimestamp();
    private UserPresence? _last;
    private string _remoteState = "";
    private readonly Dictionary<QuicPeerMultiplexer, (bool Afk, bool Accepts, long At)> _sent = new();
    internal UserPresenceService(QuicPunch.QuicPunch node, AppPreferencesStore preferences, Func<IAppPlatform?> platform, CancellationToken lifetime)
    {
        _node = node; _preferences = preferences; _platform = platform;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _worker = RunAsync();
    }
    internal void Activity() => Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());
    internal Dictionary<string, QuicPeerMultiplexer.UserPresence> RemoteSnapshot()
    {
        var result = new Dictionary<string, QuicPeerMultiplexer.UserPresence>();
        foreach (var peer in _node.AvailablePeers.Values)
            if (_node.IsTrustedPeer(peer) && peer.ActiveTransport != QuicPunch.QuicPunch.TransportType.Tor &&
                peer.NetworkType != QuicPunch.QuicPunch.NetworkType.Tor && string.IsNullOrEmpty(peer.OnionAddress) &&
                _node.TryGetPeerMultiplexer(peer.Id, out var mux) && mux?.RemoteUserPresence is { } presence)
                result[peer.Id.ToString()] = presence;
        return result;
    }
    internal UserPresence Snapshot()
    {
        var preferences = _preferences.Snapshot();
        TimeSpan? systemIdle = null;
        try { systemIdle = _platform()?.GetUserIdleTime(); } catch { }
        return Evaluate(preferences, systemIdle ?? Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivity)), DateTimeOffset.Now,
            systemIdle.HasValue ? "system" : "app");
    }
    internal static UserPresence Evaluate(AppPreferences preferences, TimeSpan idle, DateTimeOffset now, string source) => new(
        idle >= TimeSpan.FromMinutes(preferences.PresenceIdleMinutes), !preferences.Notifications.IsQuiet(now),
        preferences.Notifications.DoNotDisturb && (preferences.Notifications.DoNotDisturbUntil == null || now < preferences.Notifications.DoNotDisturbUntil),
        preferences.PresenceIdleMinutes, source, preferences.Notifications.DoNotDisturbUntil);

    // Published only on existing, authenticated, trusted transports; never in
    // public discovery announcements, and never across a private Tor identity.
    private async Task RunAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            do
            {
                try
                {
                    var current = Snapshot();
                    var remote = string.Join(';', RemoteSnapshot().OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value.IsAfk}:{p.Value.AcceptsNotifications}"));
                    if (_last != current || remote != _remoteState) { _last = current; _remoteState = remote; WebUi.WebUiServer.BroadcastStatusUpdate(); }
                    foreach (var old in _sent.Keys.Where(mux => mux.IsDisposed).ToArray()) _sent.Remove(old);
                    foreach (var peer in _node.AvailablePeers.Values)
                    {
                        if (!_node.IsTrustedPeer(peer) || peer.ActiveTransport == QuicPunch.QuicPunch.TransportType.Tor ||
                            peer.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrEmpty(peer.OnionAddress) ||
                            !_node.TryGetPeerMultiplexer(peer.Id, out var mux) || mux == null || mux.IsDisposed) continue;
                        if (_sent.TryGetValue(mux, out var sent) && sent.Afk == current.isAfk && sent.Accepts == current.acceptsNotifications &&
                            Stopwatch.GetElapsedTime(sent.At) < TimeSpan.FromSeconds(30)) continue;
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        deadline.CancelAfter(_node.PeerTransport.HeartbeatTimeout);
                        try
                        {
                            await mux.SendUserPresenceAsync(current.isAfk, current.acceptsNotifications, deadline.Token).ConfigureAwait(false);
                            _sent[mux] = (current.isAfk, current.acceptsNotifications, Stopwatch.GetTimestamp());
                        }
                        catch (Exception) when (!_stop.IsCancellationRequested) { /* Retry on the next poll/reconnection. */ }
                    }
                }
                catch (Exception error) when (!_stop.IsCancellationRequested) { WebUi.WebUiServer.LogEvent("[PRESENCE] " + error.Message); }
            } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }
    public void Dispose() => _stop.Cancel();
}
