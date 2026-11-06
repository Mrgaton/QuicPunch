using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace QuicNet.Geolocation.App.Diagnostics;

internal static class LocalityTests
{
    public static void Run(Action<bool, string> check)
    {
        var data = LocalityCatalog.Default;
        var village = new GeoPoint(38.75397, -.37991);
        check(data.Count > 250_000 && data.Near(village, 1, 2).Single().Place is { Name: "Benillup", Municipality: "Benillup", Population: < 500 },
            "offline gazetteer resolves a sub-500 Spanish settlement and its declared municipality");
        check(data.Near(new(38.91667, -.26667), 1, 2).Single().Place.Municipality == "Castellonet de la Conquesta",
            "small municipalities are not replaced by a regional capital");
        check(data.MatchPublished(new(village, "ES", "", "Benillup", "fixture"))?.Municipality == "Benillup" &&
            data.MatchPublished(new(village, "ES", "", "Not a real municipality", "fixture")) == null,
            "municipal labels require matching the published place, not snapping a foreign city name to a nearby village");
        var dateline = data.Near(new(-17.5, 179.9), 20, 400);
        check(dateline.Length > 0 && dateline.Select(n => n.Place.Id).Distinct().Count() == dateline.Length && dateline.All(n => n.DistanceKm <= 400),
            "locality searches cross the antimeridian without duplicates or distance errors");
        check(data.InBounds(38.7, -.45, 38.8, -.3).Any(p => p.Name == "Benillup") && data.InBounds(-90, -180, 90, 180).Length <= 400,
            "offline map viewport queries expose small places with bounded output");

        GeoPoint target = new(39.47, -.376);
        GeoPoint[] positions = [new(40.42, -3.70), new(41.39, 2.17), new(38.35, -.49), new(39.57, 2.65),
            new(41.65, -.89), new(37.99, -1.13), new(40.34, -.09), new(39.99, -.05), new(38.99, -.52)];
        var anchors = positions.Select((p, i) => new Landmark("site-" + i, "192.0.2." + (i + 1), p, "fixture")
            { LocationVerified = true, SiteId = "independent-" + i, UncertaintyKm = 5 }).ToList();
        var now = DateTimeOffset.UtcNow;
        var session = new MeasurementSession("synthetic-town", "fixture", "ipv4", anchors.Select(a =>
        {
            double minimum = 6 + Geography.DistanceKm(target, a.Position) * .0105;
            return new Observation(a.Id, a.Address, now, [new(minimum, "Success"), new(minimum + 7, "Success"), new(minimum + .2, "Success")]);
        }).ToList()) { Synthetic = true };
        var catalog = new Catalog(anchors);
        var watch = Stopwatch.StartNew();
        var update = new ProgressiveLocator().Replay(catalog, session);
        var fit = update.Localities?.Fit;
        Console.WriteLine($"Locality fit: {fit?.Position}, spread={fit?.ModelSpreadKm:F1} km, residual={fit?.ResidualMs:F2} ms, time={watch.ElapsedMilliseconds} ms; region center={update.Region?.Representative}");
        check(fit != null && Geography.DistanceKm(target, fit.Position) < 35 && fit.AccessDelayMs is {} delay && Math.Abs(delay - 6) < 2,
            "multi-anchor fit recovers a synthetic location and common access delay instead of using a region centroid");
        check(fit!.ModelSpreadKm >= 25 && fit.IndependentSites == anchors.Count && update.Localities!.Candidates.Count > 0,
            "model sensitivity retains an uncertainty floor and reports independent sites with named locality alternatives");
        check(watch.Elapsed < TimeSpan.FromSeconds(5), "locality fit has a bounded interactive computation cost");
        var duplicated = catalog with { Landmarks = anchors.Select(a => a with { SiteId = "one-site" }).ToList() };
        check(new ProgressiveLocator().Replay(duplicated, session).Localities?.Fit == null,
            "duplicate site identifiers cannot manufacture enough evidence for a fit");
        var sparseSession = session with { Observations = session.Observations.Take(4).ToList() };
        var sparseFit = new ProgressiveLocator().Replay(catalog, sparseSession).Localities?.Fit;
        check(sparseFit is { DiverseGeometry: false, ModelSpreadKm: >= 150 },
            "four independent references retain broad model sensitivity instead of implying municipal precision");
        var delayedSession = session with { Observations = session.Observations.Select((o, i) => i == 0
            ? o with { Samples = o.Samples.Select(s => s with { RttMs = s.RttMs + 40 }).ToList() } : o).ToList() };
        var delayedFit = new ProgressiveLocator().Replay(catalog, delayedSession).Localities?.Fit;
        check(delayedFit != null && Geography.DistanceKm(target, delayedFit.Position) < 150,
            "a circuitous high-latency route does not pull the synthetic fit toward its anchor");
        var wrongPrior = update with { DatabasePrior = new(new(51.5, -.12), "GB", "England", "London", "fixture") };
        check(LocalityAssessment.Create(wrongPrior, catalog, session) is { Status: "disagreement", PublishedPlace: not null },
            "published IP and measured network disagreement remains explicit with the published reference preserved");
        var conflicting = update with { Level = EvidenceLevel.Conflicting };
        check(LocalityAssessment.Create(conflicting, catalog, session) is { Position: null, Status: "conflicting" },
            "contradictory measurements never produce a confirmed town or a location point");
        var offline = new ProgressiveLocator().LocateFast(IPAddress.Parse("81.2.69.160"));
        check(offline.Localities is { Status: "published-only", Candidates.Count: > 0 },
            "a blocked/offline measurement still exposes its published reference and nearby locality alternatives");
        var empty = new ProgressiveLocator().LocateFast();
        check(empty.Localities is { Status: "insufficient-data", Position: null, Candidates.Count: 0 },
            "lack of evidence is explained rather than answered with an invented municipality");
        string json = JsonSerializer.Serialize(update, JsonFiles.GetTypeInfo<LocationUpdate>());
        var roundtrip = JsonSerializer.Deserialize(json, JsonFiles.GetTypeInfo<LocationUpdate>())!;
        check(roundtrip.Localities?.Candidates.Count == update.Localities?.Candidates.Count && !json.Contains("aliases", StringComparison.OrdinalIgnoreCase),
            "locality results roundtrip through AOT JSON without leaking gazetteer alias tables");
    }
}
