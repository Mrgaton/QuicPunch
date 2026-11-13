using System.Text.Json;
using QuicNet.Geolocation;

namespace NetLocate;

internal static class RegionalTests
{
    public static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var p = new Landmark("primary", "192.0.2.1", new(40, -3), "synthetic")
            { LocationVerified = true, SiteId = "p", UncertaintyKm = 25 };
        var d = p with { Id = "declared", SiteId = "d", Address = "192.0.2.2",
            Position = new(40.1, -3.1), LocationVerified = false, CoordinateEvidence = "operator-declared", UncertaintyKm = 50 };
        var catalog = new Catalog([p, d]);
        var model = new EstimatorOptions { ResolutionKm = 10, MaxCells = 20_000 };
        var baseline = new MeasurementSession("device", "same-network", "ipv4",
            [new(p.Id, p.Address, now, [new(20, "Success")])]);
        var withDeclared = baseline with { Observations = [.. baseline.Observations,
            new(d.Id, d.Address, now, [new(.25, "Success") { TimingMethod = "elapsed-upper-v1" }])] };
        var before = ProgressiveSearch.Replay(catalog, baseline, model);
        var after = ProgressiveSearch.Replay(catalog, withDeclared, model);
        check(after.Region!.AreaKm2 == before.Region!.AreaKm2 && after.Region.Constraints.Count == 1,
            "las coordenadas declaradas no recortan la región principal");
        check(after.RegionalRefinement is { Region.Cells.Count: > 0 } r && r.Region.AreaKm2 < before.Region.AreaKm2 * .1,
            "la evidencia cercana reduce una hipótesis condicional separada");
        check(d.ProbeEligible && !d.Usable && !d.LocationVerified,
            "una sonda declarada es medible sin atribuirle verificación física");
        var incompatible = catalog with { Landmarks = [p, d with { Position = new(-30, 140) }] };
        var conflict = ProgressiveSearch.Replay(incompatible, withDeclared, model);
        check(conflict.Region!.Status == "candidate-region" && conflict.RegionalRefinement is
            { Region.Status: "inconsistent", Region.Representative: null, Support: null },
            "una declaración incompatible retira su hipótesis sin alterar la región principal");
        var badIp = withDeclared with { Observations = [.. baseline.Observations,
            new(d.Id, "192.0.2.99", now, [new(1, "Success")])] };
        try { _ = ProgressiveSearch.Replay(catalog, badIp, model); throw new Exception("IP aceptada"); }
        catch (InvalidDataException) { check(true, "la evidencia declarada también queda vinculada a su IP literal"); }

        var timingSession = baseline with { Observations = [new(p.Id, p.Address, now,
            [new(0, "Success"), new(.25, "Success") { TimingMethod = "elapsed-upper-v1" }])] };
        var timing = new LatencyEstimator(model).Estimate(new([p]), timingSession).Constraints.Single();
        check(Math.Abs(timing.RadiusKm - (25 + .30 * 299.792458 / 2)) < 1e-8 && timing.RttMs == .25,
            "el cronometraje fraccional conserva sobrecoste y margen sin restar acceso");
        check(new LatencyEstimator(model).Estimate(new([p]), baseline).Constraints.Single().RadiusKm ==
            25 + 21 * 299.792458 / 2,
            "los registros antiguos conservan su margen de cuantización y cotas originales");
        var unknownClock = timingSession with { Observations = [new(p.Id, p.Address, now,
            [new(.25, "Success") { TimingMethod = "unknown" }])] };
        check(new LatencyEstimator(model).Estimate(new([p]), unknownClock).Constraints.Single().RadiusKm > timing.RadiusKm,
            "un método de reloj desconocido no obtiene el margen reducido");
        check(timing.RadiusKm < 100 && new LatencyEstimator(model).Estimate(new([p]), timingSession)
            .Cells.Any(c => p.Position.Latitude >= c.South && p.Position.Latitude <= c.North &&
                p.Position.Longitude >= c.West && p.Position.Longitude <= c.East),
            "la fixture submilisegundo admite escala metropolitana y conserva su posición conocida");
        var encoded = JsonSerializer.Serialize(after, GeoJsonContext.Default.LocationUpdate);
        check(JsonSerializer.Deserialize(encoded, GeoJsonContext.Default.LocationUpdate)!.RegionalRefinement!.Region.AreaKm2 ==
            after.RegionalRefinement!.Region.AreaKm2, "la hipótesis condicional se serializa con generación AOT");
        var header = new RecordingHeader(2, ProgressiveSearch.EngineVersion, RecordingWriter.Hash(catalog), catalog,
            new() { Planner = "regional", Model = model });
        string html = HeatmapReport.Render(header, new("complete", [new(1, 2, withDeclared, after, [])]));
        check(html.Contains("regional-layer1", StringComparison.Ordinal) &&
            html.Contains("no demuestra precisión de ciudad", StringComparison.Ordinal) &&
            System.Net.WebUtility.HtmlDecode(html).Contains("Área condicional", StringComparison.Ordinal),
            "el informe separa el mapa condicional y expone su límite de precisión");
        var embedded = Program.DefaultCatalog();
        check(embedded.Landmarks.Count > 8000 && embedded.Landmarks.Count(a => a.CoordinateEvidence == "operator-declared") > 6000 &&
            embedded.Landmarks.Where(a => a.CoordinateEvidence == "operator-declared").All(a => !a.LocationVerified && a.UncertaintyKm >= 50 &&
                EmbeddedCatalog.IsPublic(System.Net.IPAddress.Parse(a.Address))),
            "la ampliación global incorporada conserva la procedencia y su incertidumbre");
        var plan = ProgressiveSearchPlanner.Next(embedded,
            new("device", "fixed", "ipv4", [new(embedded.Landmarks[0].Id, embedded.Landmarks[0].Address, now, [new(10, "Success")])]),
            null, 12, 2, planner: "regional", model: model);
        var byId = embedded.Landmarks.ToDictionary(a => a.Id);
        check(plan.Count(s => byId[s.LandmarkId].Usable) >= 2 && plan.Any(s => s.Role == "global"),
            "la búsqueda regional reserva referencias principales y exploración global");
    }
}
