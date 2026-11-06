using System.Diagnostics;

namespace QuicPunch.Discovery;

public sealed partial class PeerDiscoveryManager
{
    private string _wanPresenceNonce = Guid.NewGuid().ToString("N");
    private string _torPresenceNonce = Guid.NewGuid().ToString("N");
    private readonly DiscoveryPresenceGate _presenceGate = new();
    private readonly int[] _presenceReplyPending = new int[2];
    private readonly long[] _lastPresenceReply = new long[2];

    private void SchedulePresenceReply(bool tor)
    {
        int scope = tor ? 1 : 0;
        if (Interlocked.CompareExchange(ref _presenceReplyPending[scope], 1, 0) != 0) return;
        _ = ReplyAsync();
        async Task ReplyAsync()
        {
            try
            {
                var ct = _context.LifecycleToken;
                // Coalesce startup announcements; one broadcast supplies our token to every requester.
                double elapsed = _lastPresenceReply[scope] == 0 ? 1000 : Stopwatch.GetElapsedTime(_lastPresenceReply[scope]).TotalMilliseconds;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(100, 1000 - elapsed)), ct).ConfigureAwait(false);
                var discovery = tor ? TorNostrDiscovery : WanNostrDiscovery;
                if (discovery?.IsRunning == true)
                {
                    await discovery.PublishNowAsync(ct).ConfigureAwait(false);
                    _lastPresenceReply[scope] = Stopwatch.GetTimestamp();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { QuicPunchLog.Info($"[DISCOVERY] Presence reply deferred: {ex.Message}"); }
            finally { Volatile.Write(ref _presenceReplyPending[scope], 0); }
        }
    }
}
