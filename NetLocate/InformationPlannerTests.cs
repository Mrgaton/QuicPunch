using System.Text.Json;
using QuicNet.Geolocation;

namespace NetLocate;

internal static class InformationPlannerTests
{
    public static void Run(Action<bool, string> check)
    {
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var truth = new GeoPoint(40.4168, -3.7038);
        var known = new Landmark("known-frankfurt", "192.0.2.1", new(50.12, 8.65), "synthetic-information-test")
        { SiteId = "known", Asn = 65001, LocationVerified = true, UncertaintyKm = 25 };
        var anchors = new List<Landmark> { known };
        for (int i = 0; i < 60; i++)
            anchors.Add(known with { Id = "cluster-" + i.ToString("D2"), Address = "192.0.2." + (i + 2),
                SiteId = "cluster-" + i, Position = new(50.12 + i * .001, 8.65) });
        var other = new (string Name, double Lat, double Lon)[]
        {
            ("madrid",40.4168,-3.7038), ("lisbon",38.72,-9.14), ("barcelona",41.387,2.17),
            ("north",65,0), ("east",40,38), ("southeast",30,20), ("northeast",62,30),
            ("sydney",-33.87,151.2), ("los-angeles",34,-118)
        };
        for (int i = 0; i < other.Length; i++)
            anchors.Add(known with { Id = other[i].Name, SiteId = other[i].Name, Asn = (uint)(65010 + i),
                Address = "192.0.2." + (100 + i), Position = new(other[i].Lat, other[i].Lon) });
        var catalog = new Catalog(anchors) { CreatedUtc = clock };
        var session = new MeasurementSession("fixed", "same-evidence", "ipv4",
            [new(known.Id, known.Address, clock, [new(25, "Success"), new(29, "Success"), new(26, "Success")])])
        { CreatedUtc = clock, Synthetic = true };
        var model = new EstimatorOptions { ResolutionKm = 40, MaxCells = 12_000 };
        var before = ProgressiveSearch.Replay(catalog, session, model);
        string unchanged = JsonSerializer.Serialize(before, GeoJsonContext.Default.LocationUpdate);
        var oldPlan = ProgressiveSearchPlanner.Next(catalog, session, before, 12, 2, planner: "legacy", model: model);
        var plan = ProgressiveSearchPlanner.Next(catalog, session, before, 12, 2, model: model);
        check(plan.Any(s => s.LandmarkId == "madrid"),
            "se exploran alternativas útiles fuera del grupo de menor latencia");
        check(plan.Count(s => s.LandmarkId.StartsWith("cluster-", StringComparison.Ordinal)) <
            oldPlan.Count(s => s.LandmarkId.StartsWith("cluster-", StringComparison.Ordinal)),
            "la selección por información reduce la repetición del mismo grupo geográfico");
        check(plan.Select(s => catalog.Landmarks.Single(a => a.Id == s.LandmarkId).Asn).Distinct().Count() >= 4,
            "diversidad de ASN locales sin usarla como cota geográfica");
        check(plan.Where(s => s.HypotheticalRemovalFraction.HasValue)
            .All(s => s.HypotheticalRemovalFraction is >= 0 and <= 1 && s.Reason is not null),
            "las puntuaciones hipotéticas quedan identificadas y acotadas");
        check(unchanged == JsonSerializer.Serialize(before, GeoJsonContext.Default.LocationUpdate) &&
            before.Region!.Constraints.Count == 1 && session.Observations.Count == 1,
            "la predicción de RTT no altera muestras, cotas, región ni incertidumbre");

        Observation Observe(Landmark a)
        {
            bool slowRing = a.Id is "north" or "east" or "southeast" or "northeast";
            double rtt = slowRing ? 150 : 6 + Geography.DistanceKm(a.Position, truth) / 80;
            return new(a.Id, a.Address, clock, [new(rtt, "Success")]);
        }
        LocationUpdate Execute(LandmarkSelection[] selection)
        {
            var byId = catalog.Landmarks.ToDictionary(a => a.Id, StringComparer.Ordinal);
            var next = session with { Observations = [.. session.Observations] };
            foreach (var s in selection)
            {
                var observation = Observe(byId[s.LandmarkId]);
                int index = next.Observations.FindIndex(o => o.LandmarkId == observation.LandmarkId);
                if (index < 0) next.Observations.Add(observation);
                else next.Observations[index] = observation with
                { Samples = [.. next.Observations[index].Samples, .. observation.Samples] };
            }
            return ProgressiveSearch.Replay(catalog, next, model);
        }
        var baseline = Execute(oldPlan);
        var improved = Execute(plan);
        check(improved.Region!.AreaKm2 < baseline.Region!.AreaKm2 * .8,
            "mismo presupuesto y RTT sintéticos fijos: menor área frente al plan anterior");
        check(improved.Region.Cells.Any(c => truth.Latitude >= c.South && truth.Latitude <= c.North &&
            truth.Longitude >= c.West && truth.Longitude <= c.East),
            "la mejora sintética mantiene la posición real de la fixture dentro de la región");
        var repeated = ProgressiveSearchPlanner.Next(catalog, session, before, 12, 2, model: model);
        check(plan.SequenceEqual(repeated), "selección reproducible con idéntico catálogo, modelo y muestras");

        var noAsn = catalog with { Landmarks = catalog.Landmarks.Select(a => a with { Asn = null }).ToList() };
        check(ProgressiveSearchPlanner.Next(noAsn, session, before, 12, 2, model: model).Length == 12,
            "el catálogo sin ASN conserva una ruta de selección funcional");
        var duplicateIp = catalog with { Landmarks = [.. catalog.Landmarks,
            known with { Id = "alias", SiteId = "alias", Position = new(40, 0) }] };
        var unique = ProgressiveSearchPlanner.Next(duplicateIp, session, before, 12, 2, model: model);
        check(unique.Select(s => duplicateIp.Landmarks.Single(a => a.Id == s.LandmarkId).Address).Distinct().Count() == unique.Length,
            "una IP duplicada no ocupa varios destinos de la tanda");
    }
}
