using System.Net;
using System.Text.Json;
using QuicNet.Geolocation;
using QuicNet.Geolocation.Workbench;

namespace QuicNet.Geolocation.App.Diagnostics;

internal static class PrecisionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var local = new Landmark("local", "192.0.2.1", new(41.39, 2.17), "fixture") { LocationVerified = true, UncertaintyKm = 0 };
        Observation Sample(Landmark a, double rtt) => new(a.Id, a.Address, now, [new(rtt, "Success")]);
        MeasurementSession Session(IEnumerable<Observation> observations) => new("fixture", "fixture", "ipv4", observations.ToList());
        var estimator = new LatencyEstimator(new() { ResolutionKm = 50, TimingSlackMs = 1 });
        var initial = estimator.Estimate(new([local]), Session([Sample(local, 2)]));
        var distant = Enumerable.Range(0, 80).Select(i => local with { Id = "distant-" + i, Address = "198.51.100." + (i + 1), Position = new(20 + i % 30, -60 + i % 100) }).ToArray();
        var extended = estimator.Estimate(new([local, .. distant]), Session([Sample(local, 2), .. distant.Select(a => Sample(a, 150))]));
        check(initial.Representative == extended.Representative && initial.Cells.SequenceEqual(extended.Cells) && initial.CoveringRadiusKm == extended.CoveringRadiusKm,
            "redundant distant references cannot pull the point towards Madrid or Geneva");
        check(extended.Constraints.Count == 81, "non-binding evidence remains available for inspection");
        var global = estimator.Estimate(new([local]), Session([Sample(local, 200)]));
        check(global.Status == "unbounded" && global.Representative is null && global.CandidateAreaKm2 > 500_000_000, "global RTT bounds never invent a city or a point");
        var profiles = new[] { "Rápido", "Equilibrado", "Detallado" }.Select(WorkbenchSettings.Preset).ToArray();
        check(profiles.Select(p => p.ToModel()).Distinct().Count() == 1, "all profiles use the same geometry, slack and resolution");
        string? prefix = null;
        foreach (var profile in profiles)
        {
            var request = profile.ToRequest() with { Rounds = 1, EnableTopology = false, Budget = TimeSpan.FromSeconds(15) };
            using var run = new ProgressiveLocator(transport: new Echo()).Start(request, _ => { });
            var result = await run.Completion;
            var signature = string.Join('|', result.Session.Observations.Select(o => $"{o.LandmarkId}:{o.Samples.Count}:{o.Route.Count}"));
            prefix ??= signature;
            check(result.Session.Observations.Count == 24 && result.Session.Observations.All(o => o.Samples.Count == 2) && signature == prefix,
                "profiles share the same 24-site bootstrap and two samples per site");
        }
        using var stream = typeof(PrecisionTests).Assembly.GetManifestResourceStream("QuicNet.Geolocation.App.Diagnostics.Fixtures.profile-drift.json")!;
        using var fixture = JsonDocument.Parse(stream);
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            int index = 0;
            var anchors = new List<Landmark>(); var observations = new List<Observation>();
            foreach (var c in item.GetProperty("constraints").EnumerateArray())
            {
                var a = new Landmark("reference-" + index, "192.0.2." + (++index), new(c.GetProperty("latitude").GetDouble(), c.GetProperty("longitude").GetDouble()), "recorded reference geometry")
                { LocationVerified = true, UncertaintyKm = c.GetProperty("uncertainty").GetDouble() };
                anchors.Add(a); observations.Add(Sample(a, c.GetProperty("rtt").GetDouble()));
            }
            var region = estimator.Estimate(new(anchors), Session(observations));
            bool contains = region.Cells.Any(c => c.South <= 41.39 && c.North >= 41.39 && c.West <= 2.17 && c.East >= 2.17);
            check(contains && region.CoveringRadiusKm > 100, item.GetProperty("profile").GetString() + " recorded geometry retains Barcelona and honest regional uncertainty");
            Console.WriteLine($"  Recorded {item.GetProperty("profile").GetString()}: centre={region.Representative}, radius={region.CoveringRadiusKm:F0} km, area={region.CandidateAreaKm2:F0} km²");
        }
        using var cell = JsonDocument.Parse(JsonSerializer.Serialize(new GeoCell(0, 0, 1, 1), JsonFiles.GetTypeInfo<GeoCell>()));
        check(cell.RootElement.EnumerateObject().Count() == 4, "cell serialization sends only bounds, avoiding derived geometry payloads");
    }
    private sealed class Echo : IEchoTransport
    {
        public Task<EchoResult> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ttl == 1 ? new EchoResult("TtlExpired", "213.242.114.213", 3) : new EchoResult("Success", address.ToString(), 200));
        }
    }
}
