using System.Text.Json;

namespace QuicNet.Geolocation.App.Diagnostics;

internal static class CalibrationTests
{
    public static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var truth in new GeoPoint[] { new(41.39, 2.17), new(40.42, -3.70), new(39.47, -.376), new(37.39, -5.98), new(-33.87, 151.21) })
        {
            var positions = new[] { new GeoPoint(truth.Latitude-2, truth.Longitude-2), new(truth.Latitude-2, truth.Longitude+2),
                new GeoPoint(truth.Latitude+2, truth.Longitude-2), new(truth.Latitude+2, truth.Longitude+2) };
            var anchors = positions.Select((p, i) => new Landmark("cbg-"+i, "192.0.2."+(i+1), p, "synthetic mesh fixture")
            { SiteId = "independent-"+i, CoordinateEvidence = "operator-declared", UncertaintyKm = 10,
                DelayCalibration = new("cbg-"+i, "192.0.2."+(i+1), .01, 1, 20, i+1, now) }).ToList();
            var catalog = new Catalog(anchors);
            var session = new MeasurementSession("synthetic", "fixture", "ipv4", anchors.Select((a,i) =>
                new Observation(a.Id, a.Address, now, [new(3 + .015 * Geography.DistanceKm(truth,a.Position) + (i==0 ? 30 : 0), "Success")])).ToList());
            var result = new ProgressiveLocator().Replay(catalog, session);
            var cbg = result.RegionalRefinement?.CalibratedRegion;
            check(result.Region?.Constraints.Count == 0 && cbg is { Status: "candidate-region" } &&
                cbg.Constraints.All(c => Geography.DistanceKm(c.Center,truth) <= c.RadiusKm),
                $"CBG retains independent synthetic truth {truth} with a circuitous route; declarations never become primary bounds");
            check(result.Localities?.Fit is { Method: "cbg-region-centroid", DiverseGeometry: false, ResidualMs: null, AccessDelayMs: null },
                "CBG centroid is explicitly a regional reference, never a municipal fit");
            var frozen = JsonSerializer.Deserialize(JsonSerializer.Serialize(catalog, GeoJsonContext.Default.Catalog), GeoJsonContext.Default.Catalog)!;
            var replay = new ProgressiveLocator().Replay(frozen,session);
            check(replay.RegionalRefinement?.CalibratedRegion?.Representative == cbg!.Representative &&
                replay.RegionalRefinement?.Support?.Values.SequenceEqual(result.RegionalRefinement!.Support!.Values) == true,
                "frozen per-address calibration reproduces the CBG region and heat without live metadata");
        }
        var valid = new DelayCalibration("test", "192.0.2.1", .01, 1, 10, 1, now);
        check(!valid.Applicable("192.0.2.2",now) && !(valid with { InterceptMs = -1 }).Applicable(valid.Address,now) &&
            !(valid with { SlopeMsPerKm = double.NaN }).Applicable(valid.Address,now) &&
            !(valid with { CapturedUtc = now.AddDays(-31) }).Applicable(valid.Address,now),
            "wrong addresses, invalid lines and stale calibration cannot refine a location");
        var anchor = new Landmark("test",valid.Address,new(40,-3),"fixture")
            { LocationVerified = true, DelayCalibration = valid with { InterceptMs = 20 }, UncertaintyKm = 5 };
        var observation = new MeasurementSession("synthetic","fixture","ipv4",[new(anchor.Id,anchor.Address,now,[new(1,"Success")])]);
        var region = new LatencyEstimator(useEmpiricalCalibration:true).Estimate(new([anchor]),observation);
        check(region.Constraints.Single().RadiusKm > 100 && !region.Constraints.Single().Evidence.StartsWith("cbg-"),
            "RTT below a learned intercept invalidates that calibration instead of creating a zero-radius circle");
        using var coverage = JsonDocument.Parse(EmbeddedCatalog.Default.Manifest);
        check(coverage.RootElement.GetProperty("coverage").GetProperty("spanishReferenceSites").GetInt32() >= 60 &&
            EmbeddedCatalog.Default.Anchors.All(a => !a.LocationVerified),
            "expanded real catalogue has diverse Spanish sites without pretending downloaded coordinates are certified");
    }
}
