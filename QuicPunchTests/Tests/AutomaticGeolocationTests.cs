using System.Net;
using System.Text.Json;
using QuicNet.Geolocation;
using QuicNet.Networking;
using QuicNet.Serialization;

namespace QuicPunchTests.Tests;

internal static class AutomaticGeolocationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Clock
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        private readonly List<(DateTimeOffset due, TaskCompletionSource completion)> _waits = [];
        internal DateTimeOffset Now() { lock (_gate) return _now; }
        internal bool Waiting { get { lock (_gate) return _waits.Any(w => !w.completion.Task.IsCompleted); } }
        internal async Task Delay(TimeSpan duration, CancellationToken token)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _waits.Add((_now + duration, completion));
            }
            try { await completion.Task.WaitAsync(token); }
            finally { lock (_gate) _waits.RemoveAll(w => ReferenceEquals(w.completion, completion)); }
        }
        internal void Advance(TimeSpan elapsed)
        {
            lock (_gate)
            {
                _now += elapsed;
                foreach (var wait in _waits.Where(w => w.due <= _now)) wait.completion.TrySetResult();
                _waits.RemoveAll(w => w.completion.Task.IsCompleted);
            }
        }
    }

    private sealed class Analyzer
    {
        private readonly object _gate = new();
        private readonly List<(string ip, TaskCompletionSource finish)> _runs = [];
        internal int Count { get { lock (_gate) return _runs.Count; } }
        internal string Address(int index) { lock (_gate) return _runs[index].ip; }
        internal void Finish(int index) { lock (_gate) _runs[index].finish.TrySetResult(); }
        internal void Fail(int index) { lock (_gate) _runs[index].finish.TrySetException(new IOException("Synthetic probe failure")); }
        internal async Task Run(IPAddress address, Action<LocationUpdate> publish, CancellationToken token)
        {
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _runs.Add((address.ToString(), finish));
            publish(new(1, EvidenceLevel.LatencyRegion, "complete", true, null, null,
                new("candidate-region", new(40, -3), 120, 1000, [], []), [], [], 2, 4, "test", []));
            await finish.Task.WaitAsync(token);
        }
    }

    internal static async Task RunAsync(string directory)
    {
        var clock = new Clock();
        var analyzer = new Analyzer();
        var service = new AutomaticIpGeolocation(directory, analyze: analyzer.Run, now: clock.Now, delay: clock.Delay);
        IPAddress[] Ips(params string[] values) => values.Select(IPAddress.Parse).ToArray();
        try
        {
            service.Observe(Ips("127.0.0.1", "10.0.0.1", "::1", "100.64.0.1", "2001:db8::1"));
            Check(service.Status.pending == 0 && analyzer.Count == 0, "Private/special addresses scheduled a probe");
            service.Observe(Ips("8.8.8.8", "::ffff:8.8.8.8"));
            await Until(() => analyzer.Count == 1 && service.Status.locations[0].latitude == 40);
            for (int i = 0; i < 1000; i++) service.Observe(Ips("::ffff:8.8.8.8", "8.8.8.8"));
            Check(analyzer.Count == 1 && service.Status.running, "Repeated/reordered/mapped IP restarted an active analysis");
            clock.Advance(TimeSpan.FromSeconds(45));
            analyzer.Finish(0);
            await Until(() => service.Status.locations[0].state == "complete" && !service.Status.running);
            Check(service.Status.nextAllowedUtc == clock.Now() + TimeSpan.FromMinutes(5), "Cooldown must start at completion");
            service.Observe(Ips("8.8.8.8", "1.1.1.1"));
            await Until(() => clock.Waiting);
            service.Observe(Ips("8.8.8.8", "9.9.9.9")); // Drop an obsolete pending address.
            clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
            Check(analyzer.Count == 1, "A new IP bypassed the five-minute limit");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => analyzer.Count == 2);
            Check(analyzer.Address(1) == "9.9.9.9", "A stale pending IP was analyzed");
            service.Observe(Ips("4.4.4.4", "2001:4860:4860::8888"));
            await Until(() => !service.Status.running && clock.Waiting);
            Check(analyzer.Count == 2, "An IP change overlapped analyses");
            clock.Advance(TimeSpan.FromMinutes(5));
            await Until(() => analyzer.Count == 3);
            Check(analyzer.Address(2) == "2001:4860:4860::8888", "IPv6 was not scheduled");
            analyzer.Fail(2);
            await Until(() => service.Status.locations.Any(l => l.state == "failed") && !service.Status.running && clock.Waiting);
            clock.Advance(TimeSpan.FromMinutes(5));
            await Until(() => analyzer.Count == 4);
            analyzer.Finish(3);
            await Until(() => !service.Status.running && service.Status.locations.All(l => l.state != "running"));
        }
        finally { service.Dispose(); await service.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }

        // Reopening the app must keep both deduplication (including failed/cancelled attempts) and cooldown.
        string historyPath = Path.Combine(directory, "geoip", "connection-locations.json");
        var history = JsonSerializer.Deserialize(File.ReadAllText(historyPath), SharedJsonContext.Default.GeolocationHistory)!;
        history = history with { locations = history.locations.Select(l => l with { publishedCity = "Legacy DB-IP city", countryCode = "ZZ" }).ToArray() };
        File.WriteAllText(historyPath, JsonSerializer.Serialize(history, SharedJsonContext.Default.GeolocationHistory));
        using (var reloaded = new AutomaticIpGeolocation(directory, analyze: analyzer.Run, now: clock.Now, delay: clock.Delay))
        {
            reloaded.Observe(Ips("8.8.8.8", "9.9.9.9", "4.4.4.4", "2001:4860:4860::8888", "1.1.1.1"));
            Check(reloaded.Status.pending == 1 && reloaded.Status.locations.Any(l => l.state == "interrupted"), "Restart lost cached attempts");
            Check(reloaded.Status.locations.Single(l => l.address == "8.8.8.8") is { publishedCity: "Mountain View", countryCode: "US", latitude: 40 },
                "Catalogue update retained legacy published labels or discarded measured results");
            await Until(() => clock.Waiting);
            Check(analyzer.Count == 4, "Restart bypassed persistent cooldown");
            clock.Advance(TimeSpan.FromMinutes(5));
            await Until(() => analyzer.Count == 5);
            Check(analyzer.Address(4) == "1.1.1.1", "Restart repeated a cached IP");
            analyzer.Finish(4);
            await Until(() => !reloaded.Status.running && reloaded.Status.pending == 0);
            reloaded.Observe([]); reloaded.Observe(Ips("8.8.8.8"));
            Check(reloaded.Status.locations[0].state == "complete", "Returning to an earlier IP lost its result");
            var json = JsonSerializer.Serialize(new GeolocationStatus(false, false, 0, null, null) { connection = reloaded.Status },
                SharedJsonContext.Default.GeolocationStatus);
            Check(json.Contains("latitude") && json.Contains("40"), "Generated JSON contract dropped analysis details");
            reloaded.Dispose(); await reloaded.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        // A shutdown halfway through must persist the cancelled attempt as well.
        var interrupted = new AutomaticIpGeolocation(directory, analyze: analyzer.Run, now: clock.Now, delay: clock.Delay);
        interrupted.Observe(Ips("208.67.222.222"));
        await Until(() => clock.Waiting);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Until(() => analyzer.Count == 6);
        interrupted.Dispose(); await interrupted.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        using var final = new AutomaticIpGeolocation(directory, analyze: analyzer.Run, now: clock.Now, delay: clock.Delay);
        final.Observe(Ips("208.67.222.222"));
        Check(final.Status is { pending: 0, running: false } && final.Status.locations[0].state == "interrupted", "Shutdown lost the in-flight attempt");
        final.Dispose(); await final.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var manualClock = new Clock(); var manualAnalyzer = new Analyzer();
        using var automatic = new AutomaticIpGeolocation(Path.Combine(directory, "manual"), analyze: manualAnalyzer.Run,
            now: manualClock.Now, delay: manualClock.Delay);
        automatic.Observe(Ips("8.8.8.8")); await Until(() => manualAnalyzer.Count == 1);
        using (await automatic.BeginManualAsync(CancellationToken.None))
        {
            Check(!automatic.Status.running, "Manual test overlapped automatic probes");
            automatic.Observe(Ips("1.1.1.1")); manualClock.Advance(TimeSpan.FromMinutes(10));
            Check(manualAnalyzer.Count == 1 && automatic.Status.pending == 1, "Automatic work ran during a manual test");
        }
        Check(automatic.Status.nextAllowedUtc == manualClock.Now() + TimeSpan.FromMinutes(5), "Manual completion bypassed automatic cooldown");
        await Until(() => manualClock.Waiting); manualClock.Advance(TimeSpan.FromMinutes(5));
        await Until(() => manualAnalyzer.Count == 2); manualAnalyzer.Finish(1);
        await Until(() => !automatic.Status.running); automatic.Dispose(); await automatic.Completion;
        Console.WriteLine("Verified per-IP persistent analysis, five-minute global cooldown, rapid changes, IPv4/IPv6, cancellation, failed attempts and generated JSON.");
    }
}
