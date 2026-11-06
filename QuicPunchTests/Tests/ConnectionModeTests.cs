using QuicNet.Networking;

namespace QuicPunchTests.Tests;

internal static class ConnectionModeTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync()
    {
        bool wan = true, tor = false, desiredWan = true, desiredTor = false, failTor = false;
        var events = new List<string>();
        TaskCompletionSource? started = null, release = null;
        var controller = new ConnectionModeController(() => (desiredWan, desiredTor),
            (w,t) => { desiredWan = w; desiredTor = t; events.Add($"persist:{w}:{t}"); },
            async (value, _, ct) => {
                events.Add("wan:" + value);
                if (value && release != null) { started!.TrySetResult(); await release.Task.WaitAsync(ct); }
                wan = value;
            },
            (value, _, _) => {
                events.Add("tor:" + value);
                if (value && failTor) throw new IOException("Tor fixture unavailable");
                tor = value; return Task.CompletedTask;
            }, () => { });

        await controller.SetModeAsync("both", CancellationToken.None);
        Check(wan && tor && desiredWan && desiredTor, "Both did not enable both services");
        events.Clear();
        await controller.SetModeAsync("tor", CancellationToken.None);
        Check(!wan && tor && !desiredWan && desiredTor, "Tor-only retained WAN");
        Check(events.IndexOf("wan:False") < events.IndexOf("tor:True"), "Tor was enabled before WAN stopped");
        await controller.SetServiceAsync("wan", true, 0, CancellationToken.None);
        Check(wan && tor, "Individual setting changed the other service preference");
        await controller.SetModeAsync("wan", CancellationToken.None);
        Check(wan && !tor, "WAN-only retained Tor");
        failTor = true;
        try { await controller.SetModeAsync("tor", CancellationToken.None); throw new Exception("Tor failure was swallowed"); }
        catch (IOException) { }
        Check(!wan && !tor && !desiredWan && desiredTor, "Tor failure fell back to WAN or lost the requested mode");
        failTor = false;
        started = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = controller.SetModeAsync("wan", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = controller.SetModeAsync("tor", CancellationToken.None);
        Check(!second.IsCompleted, "Concurrent mode change bypassed the service gate");
        release.SetResult(); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Check(!wan && tor && !desiredWan && desiredTor, "An older WAN start completed after selecting Tor-only");
        try { await controller.SetModeAsync("invalid", CancellationToken.None); throw new Exception("Invalid mode accepted"); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS connection modes: exclusive services, both, legacy settings, serialized requests and no direct fallback after Tor failure");
    }
}
