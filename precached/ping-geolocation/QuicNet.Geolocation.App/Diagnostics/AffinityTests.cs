using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using QuicNet.Geolocation;
using QuicNet.Geolocation.Workbench;

namespace QuicNet.Geolocation.App.Diagnostics;

internal static class AffinityTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var barcelona = new GeoPoint(41.39, 2.17);
        RouterEvidence Router(string id, GeoPoint p, double rtt, string city) => new(id, new(p, "ES", "", city, "fixture"), rtt, 3, 1);
        var region = new RegionSnapshot("candidate-region", new(40, -3), 4000, 1, Array.AsReadOnly(new[] { new GeoCell(34, -12, 52, 18) }), []);
        var local = Router("192.0.2.1", barcelona, 3, "Barcelona");
        var map = SpatialSupportEstimator.Create(region, [local])!;
        check(map.Width * map.Height <= 192 * 192 && map.Values.Max() == 255 && map.Values.Any(v => v is > 0 and < 240), "bounded affinity raster contains a gradient, not a solid circle");
        var crowded = SpatialSupportEstimator.Create(region, [local, .. Enumerable.Range(1, 200).Select(i => Router("fixture-" + i, new(46, 6), 50, "Far city"))])!;
        check(crowded.Hints.Count == 2 && crowded.Peaks[0].Position == barcelona &&
            SpatialSupportEstimator.ScoreAt(barcelona, map.Hints) == SpatialSupportEstimator.ScoreAt(barcelona, crowded.Hints),
            "hundreds of slower correlated routers cannot outvote the fast Barcelona signal");
        check(SpatialSupportEstimator.ScoreAt(barcelona, crowded.Hints) > SpatialSupportEstimator.ScoreAt(new(40.42, -3.7), crowded.Hints),
            "affinity peak follows the low-latency evidence rather than the feasible area's centroid");
        var neighbor = SpatialSupportEstimator.Create(region, [local, Router("192.0.2.99", new(42, 3), 100, "Slow neighbor")])!;
        check(neighbor.Peaks.Count == 1 && neighbor.Peaks[0].ScaleKm == map.Peaks[0].ScaleKm,
            "a slow nearby reference does not borrow the fast signal's strength or widen the camera");
        var bad = Router("192.0.2.9", new(46, 6), 1, "Wrong city");
        var diagnostics = new EvidenceDiagnostics("inconsistent", true, [new("router-192.0.2.9", "unique repair")], region, "fixture");
        var repaired = SpatialSupportEstimator.Create(region, [local, bad], diagnostics: diagnostics)!;
        check(repaired.Peaks[0].Position == barcelona && repaired.Hints.All(h => h.Label != "Wrong city"),
            "a quarantined wrong router cannot remain the hottest location just because its RTT is short");
        check(SpatialSupportEstimator.Create(region, [local, bad], diagnostics: diagnostics with { DisputedLocations = [.. diagnostics.DisputedLocations, new("router-192.0.2.1", "alternative repair")] }) is null,
            "ambiguous contradictory declarations withdraw affinity instead of forcing a favored city");
        var slowerModel = SpatialSupportEstimator.Create(region, [local], new EstimatorOptions { PropagationKmPerMs = 100, TimingSlackMs = 0 })!;
        check(slowerModel.Hints[0].ScaleKm < map.Hints[0].ScaleKm, "router kernel scale follows the configured propagation model and slack");
        var duplicate = SpatialSupportEstimator.Create(region, Enumerable.Range(0, 100).Select(i => local with { Address = "duplicate-" + i, Paths = 100 }).ToArray())!;
        check(duplicate.Hints.Count == 1 && duplicate.Values.SequenceEqual(map.Values), "IP, path and packet multiplicity cannot inflate affinity");
        var split = region with { Cells = Array.AsReadOnly(new[] { new GeoCell(40, 0, 43, 4), new GeoCell(45, 12, 48, 16) }) };
        var ambiguity = SpatialSupportEstimator.Create(split, [local, Router("192.0.2.2", new(46, 14), 3, "Alternative")])!;
        check(ambiguity.Peaks.Count == 2, "equally strong disconnected hypotheses both remain visible");
        double top = Y(ambiguity.North), bottom = Y(ambiguity.South);
        bool maskValid = true;
        for (int y = 0; y < ambiguity.Height; y++) for (int x = 0; x < ambiguity.Width; x++) if (ambiguity.Values[y * ambiguity.Width + x] > 0)
        {
            double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (top + (y + .5) / ambiguity.Height * (bottom - top))))) * 180 / Math.PI;
            double lon = ambiguity.West + (x + .5) / ambiguity.Width * (ambiguity.East - ambiguity.West);
            maskValid &= split.Cells.Any(c => lat >= c.South && lat <= c.North && lon >= c.West && lon <= c.East);
        }
        check(maskValid && ambiguity.Values.Contains((byte)0), "heatmap respects disconnected feasible geometry without filling excluded corridors");
        check(SpatialSupportEstimator.Create(region with { Status = "inconsistent" }, [local]) is null &&
            SpatialSupportEstimator.Create(region with { Status = "unbounded" }, [local]) is null && SpatialSupportEstimator.Create(region, []) is null,
            "conflicting, unbounded and signal-free results do not invent colored locations");
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(map, JsonFiles.GetTypeInfo<SpatialSupport>()), JsonFiles.GetTypeInfo<SpatialSupport>())!;
        check(restored.Values.SequenceEqual(map.Values) && restored.Peaks[0].Position == barcelona, "C# affinity data roundtrips through the public library JSON contract");
        var transport = new GeographicEcho();
        using (var run = new ProgressiveLocator(transport: transport).Start(WorkbenchSettings.Preset("Detallado").ToRequest() with
        { Rounds = 3, BatchSize = 24, Samples = 2, MaxHops = 2, TraceCount = 2, Budget = TimeSpan.FromSeconds(30) }, _ => { }))
        {
            var result = await run.Completion;
            var prefix = result.Session.Observations.Take(24).ToArray();
            check(result.Session.Observations.Count > 24 && result.Session.Observations.Any(o => o.Samples.Count > 2) && prefix.All(o => Math.Abs(o.MinimumRttMs!.Value - transport.Baseline(o.Address)) < 1e-9),
                "detail remeasures promising quick references and preserves their minima despite later jitter");
            check((result.Final.Support ?? result.Final.RegionalRefinement?.Support)?.Peaks.Any(p => Geography.DistanceKm(p.Position, barcelona) < 50) == true,
                "detailed continuation keeps local affinity while adding distant measurements");
            var byId = result.Catalog.Landmarks.ToDictionary(a => a.Id);
            check(result.Session.Observations.Select(o => byId[o.LandmarkId].SiteId ?? o.LandmarkId).Distinct().Count() == result.Session.Observations.Count,
                "regional and same-AS scheduling never count colocated sites as independent new references");
            var replay = new ProgressiveLocator().Replay(result.Catalog, result.Session);
            check((replay.Support ?? replay.RegionalRefinement?.Support)!.Values.SequenceEqual((result.Final.Support ?? result.Final.RegionalRefinement?.Support)!.Values), "affinity replay uses frozen samples and router declarations exactly");
        }
        string root = Path.Combine(Path.GetTempPath(), "geolatency-memory-" + Guid.NewGuid().ToString("N"));
        await using (var state = new WorkbenchSession(root, transport: new GeographicEcho(), record: false))
        {
            await state.StartAsync(WorkbenchSettings.Preset("Rápido") with { Rounds = 1, Samples = 1, MaxHops = 2, TraceCount = 1 });
            await state.Completion;
            var view = state.Preview();
            check(!Directory.Exists(root) && state.Snapshot().Directory is null, "lightweight workbench creates no session directory, journal or recording files");
            check(view.PacketCount > 0 && view.PacketCount == state.Snapshot().Packets.Length && ReferenceEquals(view.Update, state.Snapshot().Updates.Last()),
                "lightweight preview returns live counters and the immutable update without copying packet history");
        }
    }
    private static double Y(double lat) => (1 - Math.Asinh(Math.Tan(lat * Math.PI / 180)) / Math.PI) / 2;
    private sealed class GeographicEcho : IEchoTransport
    {
        private readonly ConcurrentDictionary<string, int> counts = new();
        private readonly Dictionary<string, GeoPoint> positions = EmbeddedCatalog.Default.Anchors.DistinctBy(a => a.Address).ToDictionary(a => a.Address, a => a.Position);
        public double Baseline(string address) => Geography.DistanceKm(new(41.39, 2.17), positions[address]) / 95 + 8;
        public Task<EchoResult> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); string ip = address.ToString();
            if (ttl == 1) return Task.FromResult(new EchoResult("TtlExpired", "213.242.114.213", 3));
            int count = counts.AddOrUpdate(ip, 1, (_, n) => n + 1);
            return Task.FromResult(new EchoResult("Success", ip, Baseline(ip) + (count > 2 ? 40 : 0)));
        }
    }
}
