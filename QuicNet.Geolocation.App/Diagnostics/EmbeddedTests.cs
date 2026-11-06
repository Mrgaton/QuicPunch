using System.Diagnostics;
using System.Net;
using System.Text.Json;
using QuicNet.Geolocation;

namespace QuicNet.Geolocation.App.Diagnostics;

internal static class EmbeddedTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var data = new EmbeddedCatalog();
        var timer = Stopwatch.StartNew();
        var first = new ProgressiveLocator(data).LocateFast(IPAddress.Parse("213.242.114.213"));
        timer.Stop();
        Console.WriteLine($"Embedded cold lookup: {timer.Elapsed.TotalMilliseconds:F2} ms");
        timer.Restart();
        for (int i = 0; i < 1000; i++) _ = data.Lookup(IPAddress.Parse("213.242.114.213"));
        timer.Stop();
        Console.WriteLine($"Embedded warm city lookup, mean of 1000: {timer.Elapsed.TotalMilliseconds / 1000:F4} ms");
        check(first.DatabasePrior is not null && first.Asn.HasValue && first.Level == EvidenceLevel.DatabasePrior, "embedded IPv4 city and ASN without dependencies");
        check(JsonSerializer.Serialize(first, JsonFiles.GetTypeInfo<LocationUpdate>()).Contains("\"DatabasePrior\"", StringComparison.Ordinal), "JSON evidence levels have readable names instead of unexplained numbers");
        check(data.LookupRouter(IPAddress.Parse("213.242.114.213")) is { City: "Barcelona" }, "exact embedded IPmap router lookup");
        check(data.Lookup(IPAddress.Parse("2001:4860:4860::8888")) is not null && data.LookupAsn(IPAddress.Parse("2001:4860:4860::8888")) == 15169, "embedded IPv6 range lookup");
        check(data.Lookup(IPAddress.Parse("::ffff:213.242.114.213")) == first.DatabasePrior, "IPv4 mapped IPv6 normalization");
        check(data.Lookup(IPAddress.Loopback) is null && data.LookupAsn(IPAddress.Parse("100.64.0.1")) is null &&
            data.Lookup(IPAddress.Parse("192.0.2.1")) is null, "private shared and documentation addresses never geolocated");
        check(EmbeddedCatalog.IsPublic(IPAddress.Parse("192.2.1.1")) && EmbeddedCatalog.IsPublic(IPAddress.Parse("192.0.3.1")),
            "public 192 ranges are not mistaken for documentation or private space");
        using var manifest = JsonDocument.Parse(data.Manifest);
        check(manifest.RootElement.GetProperty("tables").GetProperty("city").GetProperty("records").GetInt32() > 5_000_000 &&
            data.Anchors.Count > 1000, "IPTrust range snapshot and connected public anchors embedded");
        check(data.IpLocationSource == "IPTrust geolocation" && first.DatabasePrior?.Source.StartsWith("IPTrust", StringComparison.Ordinal) == true,
            "IPTrust provenance is used by offline and progressive lookups");
        check(data.Lookup(IPAddress.Parse("2001:218::")) is { Country: "JP", City: "", Region: "" }, "country-only IPTrust record omits nominal capital");
        check(data.Anchors.All(a => EmbeddedCatalog.IsPublic(IPAddress.Parse(a.Address))), "probe catalogue has only public IP literals");
        using (var fixture = typeof(EmbeddedTests).Assembly.GetManifestResourceStream("QuicNet.Geolocation.App.Diagnostics.Fixtures.catalog-boundaries.json"))
        using (var cases = JsonDocument.Parse(fixture!))
        {
            int matched = 0;
            foreach (var expected in cases.RootElement.EnumerateArray())
            {
                var ip = IPAddress.Parse(expected.GetProperty("address").GetString()!);
                if (expected.TryGetProperty("asn", out var asn))
                {
                    if (data.LookupAsn(ip) != asn.GetUInt32()) throw new Exception("ASN source mismatch: " + ip);
                }
                else
                {
                    var actual = data.Lookup(ip) ?? throw new Exception("Missing source range: " + ip);
                    if (actual.Country != expected.GetProperty("country").GetString() || actual.Region != expected.GetProperty("region").GetString() ||
                        actual.City != expected.GetProperty("city").GetString() ||
                        Math.Abs(actual.Position.Latitude - expected.GetProperty("latitude").GetDouble()) > .0001 ||
                        Math.Abs(actual.Position.Longitude - expected.GetProperty("longitude").GetDouble()) > .0001)
                        throw new Exception("City source mismatch: " + ip);
                }
                matched++;
            }
            check(matched > 400, $"{matched} independent source boundary lookups, UTF-8 and both IP families");
        }
        var fake = new ScriptedEcho();
        var locator = new ProgressiveLocator(data, fake);
        // Model-control fixture explicitly verifies its coordinate; downloaded declarations stay unverified.
        var reference = data.Anchors.First(a => IPAddress.Parse(a.Address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            with { LocationVerified = true };
        var frozen = new MeasurementSession("fixture", "fixture", "ipv4", [new(reference.Id, reference.Address, DateTimeOffset.UtcNow, [new(2, "Success")])]);
        var tight = locator.Replay(new([reference]), frozen, new EstimatorOptions { TimingSlackMs = 0, ResolutionKm = 100 });
        var loose = locator.Replay(new([reference]), frozen, new EstimatorOptions { TimingSlackMs = 2, ResolutionKm = 100 });
        check(Math.Abs(loose.Region!.Constraints[0].RadiusKm - tight.Region!.Constraints[0].RadiusKm - 299.792458) < .0001 && fake.Count == 0,
            "offline model controls recalculate bounds from identical measurements without sending packets");
        try { using var invalid = locator.Start(new() { Parallelism = 0, EnableNetwork = true }, _ => { }); throw new Exception("Invalid parallelism accepted"); }
        catch (ArgumentOutOfRangeException) { check(fake.Count == 0, "invalid concurrency is rejected before probing"); }
        int callbacks = 0, active = 0, maximum = 0;
        var updates = new List<LocationUpdate>();
        void Updated(LocationUpdate update)
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            Thread.Sleep(2); updates.Add(update); callbacks++; Interlocked.Decrement(ref active);
        }
        using (var offline = locator.Start(new(), Updated))
        {
            check(callbacks == 1 && offline.Initial.IsFinal, "offline initial callback is synchronous before Start returns");
            await offline.Completion;
            check(fake.Count == 0 && callbacks == 1, "offline mode never invokes transport");
        }
        callbacks = 0; updates.Clear();
        using (var run = locator.Start(new() { EnableNetwork = true, Rounds = 2, BatchSize = 6, Samples = 1,
            MaxHops = 2, TraceCount = 2, Budget = TimeSpan.FromSeconds(15) }, Updated))
        {
            var result = await run.Completion;
            check(result.Session.Observations.All(o => result.Catalog.Landmarks.Single(a => a.Id == o.LandmarkId).Kind == "anchor"),
                "regional scheduler never mistakes inferred routers for Atlas anchors");
            check(maximum == 1 && updates.Select(u => u.Revision).SequenceEqual(Enumerable.Range(0, updates.Count)), "callbacks serialized and revisions ordered");
            check(result.Final.IsFinal && updates.Any(u => u.Phase == "topology") && fake.Ttls.Contains(1), "topology executed before final result");
            check(result.Final.Routers.Any(r => r.Published.City == "Barcelona") && result.Final.Level == EvidenceLevel.Unknown,
                "exact router evidence exposed separately from device city");
            check(result.Final.Candidates.Count > 1 && Math.Abs(result.Final.Candidates.Sum(c => c.RelativeScore) - 1) < 1e-9,
                "multiple candidate network nodes with explicit relative scores");
            var replay = locator.Replay(result.Catalog, result.Session);
            check(replay.Region!.AreaKm2 == result.Final.Region!.AreaKm2 && replay.Level == result.Final.Level &&
                replay.Routers.SequenceEqual(result.Final.Routers) && replay.Candidates.SequenceEqual(result.Final.Candidates),
                "offline replay reproduces feasible region, frozen router evidence and candidate rankings");
            check(updates.Where(u => u.Region is { AreaKm2: > 0 }).Select(u => u.Region!.AreaKm2).Zip(
                updates.Where(u => u.Region is { AreaKm2: > 0 }).Select(u => u.Region!.AreaKm2).Skip(1), (a,b) => b <= a).All(v => v),
                "consistent added evidence narrows or preserves feasible area");
        }
        int before = fake.Count;
        using (var cancelled = new CancellationTokenSource())
        {
            using var run = locator.Start(new() { EnableNetwork = true, Rounds = 10 }, _ => { }, cancelled.Token);
            cancelled.Cancel();
            var cancelledResult = await run.Completion;
            check(cancelledResult.Final.IsFinal && cancelledResult.Final.Phase == "cancelled-partial", "background cancellation preserves its final partial snapshot");
        }
        await Task.Delay(100);
        check(fake.Count <= before + 6, "cancellation leaves no unbounded probe loop");
        bool failed = false;
        using (var run = locator.Start(new() { EnableNetwork = true, Rounds = 1, BatchSize = 6, Samples = 1 }, u =>
            { if (u.Revision > 0) throw new InvalidOperationException("consumer failure"); }))
        {
            try { await run.Completion; } catch (InvalidOperationException) { failed = true; }
        }
        check(failed, "consumer callback exceptions stop analysis and reach Completion");
        using (var run = new ProgressiveLocator(data, new ScriptedEcho { Conflict = true }).Start(new()
            { EnableNetwork = true, Rounds = 1, BatchSize = 6, Samples = 1, TraceCount = 1, MaxHops = 2 }, _ => { }))
        {
            var result = await run.Completion;
            check(result.Final.Level == EvidenceLevel.Unknown && result.Final.Region?.Representative is null &&
                result.Final.RegionalRefinement?.Region.Status == "inconsistent", "conflicting declarations retract the conditional region without manufacturing primary evidence");
        }
        using (var run = new ProgressiveLocator(data, new ScriptedEcho { Delay = true }).Start(new()
            { EnableNetwork = true, Budget = TimeSpan.FromSeconds(1), BatchSize = 6, Samples = 1 }, _ => { }))
        {
            var result = await run.Completion;
            check(result.Final.IsFinal && result.Final.Phase == "time-budget-exhausted" && result.Final.Level == EvidenceLevel.Unknown,
                "budget exhaustion returns an honest final snapshot without claiming topology");
        }
        var emptyLocator = new ProgressiveLocator(data, new ScriptedEcho { Drop = true });
        using (var run = emptyLocator.Start(new() { EnableNetwork = true, Rounds = 1, BatchSize = 6, Samples = 1 }, _ => { }))
        {
            var result = await run.Completion;
            check(result.Final.Level == EvidenceLevel.Unknown && result.Final.Region?.Representative is null && result.Final.Routers.Count == 0,
                "blocked ICMP yields unknown without inventing a city");
        }
    }
    private sealed class ScriptedEcho : IEchoTransport
    {
        public bool Drop { get; init; }
        public bool Conflict { get; init; }
        public bool Delay { get; init; }
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public List<int> Ttls { get; } = [];
        public async Task<EchoResult> SendAsync(IPAddress ip, int ttl, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref _count);
            lock (Ttls) Ttls.Add(ttl);
            if (Delay) await Task.Delay(5000, ct);
            return Drop ? new EchoResult("TimedOut", null, null) : ttl == 1 ?
                new("TtlExpired", "213.242.114.213", 3) : new("Success", ip.ToString(), Conflict ? 1 : 200);
        }
    }
}
