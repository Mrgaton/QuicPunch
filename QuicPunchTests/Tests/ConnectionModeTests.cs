using System.Collections.Concurrent;
using QuicNet.Networking;

namespace QuicPunchTests.Tests;

internal static class ConnectionModeTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync()
    {
        bool wan = true, tor = false, desiredWan = true, desiredTor = false, failTor = false;
        var events = new ConcurrentQueue<string>();
        TaskCompletionSource? torEntered = null, torCanceled = null, releaseCleanup = null;
        var controller = new ConnectionModeController(() => (desiredWan, desiredTor),
            (w, t) => { desiredWan = w; desiredTor = t; events.Enqueue($"persist:{w}:{t}"); },
            (value, _, _) => { events.Enqueue("wan:" + value); wan = value; return Task.CompletedTask; },
            async (value, _, ct) =>
            {
                events.Enqueue("tor:" + value);
                if (value && failTor) throw new IOException("Tor fixture unavailable");
                if (value && torEntered != null)
                {
                    torEntered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    catch (OperationCanceledException)
                    {
                        torCanceled!.TrySetResult();
                        await releaseCleanup!.Task;
                        events.Enqueue("tor:cleaned");
                        throw;
                    }
                }
                tor = value;
            }, () => { });

        var timeout = TimeSpan.FromSeconds(3);
        Task<ConnectionModeTransition> SetMode(string mode) => controller.RequestMode(mode, CancellationToken.None).Completion.WaitAsync(timeout);

        Check((await SetMode("both")).phase == "ready" && wan && tor && desiredWan && desiredTor, "Both did not enable both services");
        events.Clear();
        await SetMode("tor");
        Check(!wan && tor && !desiredWan && desiredTor, "Tor-only retained WAN");
        var torOrder = events.ToList();
        Check(torOrder.IndexOf("wan:False") < torOrder.IndexOf("tor:True"), "Tor was enabled before WAN stopped");
        await controller.SetServiceAsync("wan", true, 0, CancellationToken.None);
        Check(wan && tor, "Individual setting changed the other service preference");
        await SetMode("wan");
        Check(wan && !tor, "WAN-only retained Tor");
        failTor = true;
        var failed = await SetMode("tor");
        Check(failed.phase == "failed" && failed.error == "Tor fixture unavailable", "Tor failure was not published");
        Check(!wan && !tor && !desiredWan && desiredTor, "Tor failure fell back to WAN or lost the requested mode");
        failTor = false;

        torEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        torCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        events.Clear();
        var slowTor = controller.RequestMode("tor", CancellationToken.None);
        await torEntered.Task.WaitAsync(timeout);
        Check(slowTor.Accepted.phase == "applying" && !slowTor.Completion.IsCompleted, "Acceptance waited for Tor startup");
        var duplicate = controller.RequestMode("tor", CancellationToken.None);
        Check(duplicate.Accepted.id == slowTor.Accepted.id, "Repeating the same selection restarted Tor");
        var toWan = controller.RequestMode("wan", CancellationToken.None);
        await torCanceled.Task.WaitAsync(timeout);
        Check(toWan.Accepted.phase == "canceling-tor" && controller.Status == toWan.Accepted && !wan && !toWan.Completion.IsCompleted,
            "WAN started before Tor cleanup finished");

        // A rapid intermediate choice must never apply while the transports are still owned by the canceled startup.
        var intermediate = controller.RequestMode("both", CancellationToken.None);
        var latest = controller.RequestMode("wan", CancellationToken.None);
        releaseCleanup.TrySetResult();
        Check((await slowTor.Completion.WaitAsync(timeout)).phase == "superseded", "Cancellation appeared as a Tor failure");
        Check((await toWan.Completion.WaitAsync(timeout)).phase == "superseded", "The first WAN request was not superseded");
        Check((await intermediate.Completion.WaitAsync(timeout)).phase == "superseded", "The intermediate mode applied");
        Check((await latest.Completion.WaitAsync(timeout)).phase == "ready" && wan && !tor && desiredWan && !desiredTor,
            "WAN did not become active after Tor cancellation");
        var cancelOrder = events.ToList();
        Check(cancelOrder.Count(e => e == "tor:True") == 1 && cancelOrder.IndexOf("tor:cleaned") < cancelOrder.IndexOf("wan:True"),
            "A stale Tor start ran or WAN started before cleanup");
        Check(controller.Status?.id == latest.Accepted.id, "A superseded operation overwrote current status");
        try { controller.RequestMode("invalid", CancellationToken.None); throw new Exception("Invalid mode accepted"); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS connection modes: exclusive services, both, legacy settings, cancelable Tor startup, latest choice wins and fail-closed isolation");
    }
}
