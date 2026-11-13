namespace QuicNet.Geolocation;

public sealed record RegionalRefinement(RegionSnapshot Region, SpatialSupport? Support,
    int RespondingSites, int NetworkGroups, double MinimumRttMs, string Explanation)
{
    public RegionSnapshot? CalibratedRegion { get; init; }
    public string CalibrationStatus { get; init; } = "no-applicable-mesh-data";
}

/// <summary>Host-declared probe coordinates form an explicit conditional hypothesis, never primary evidence.</summary>
public static class DeclaredLandmarkRefiner
{
    public static RegionalRefinement? Evaluate(Catalog catalog, MeasurementSession session, EstimatorOptions model)
    {
        var declared = catalog.Landmarks.Where(a => a.Kind == "anchor" && a.CoordinateEvidence == "operator-declared" && !a.Anycast)
            .ToDictionary(a => a.Id, StringComparer.Ordinal);
        var successes = session.Observations.Where(o => o.MinimumRttMs.HasValue && declared.ContainsKey(o.LandmarkId)).ToArray();
        if (successes.Length == 0) return null;
        // All declarations participate. Conflicts remain conflicts; no convenient outlier is silently removed.
        var conditional = catalog with { Landmarks = catalog.Landmarks.Select(a => declared.ContainsKey(a.Id)
            ? a with { LocationVerified = true } : a).ToList() };
        var estimate = new LatencyEstimator(model).Estimate(conditional, session,
            session.Observations.Max(o => o.MeasuredUtc));
        var region = new RegionSnapshot(estimate.Status, estimate.Representative, estimate.CoveringRadiusKm,
            estimate.CandidateAreaKm2, Array.AsReadOnly(estimate.Cells.ToArray()), Array.AsReadOnly(estimate.Constraints.ToArray()));
        var at = session.Observations.Max(o => o.MeasuredUtc);
        int calibratedSites = successes.Where(o => declared[o.LandmarkId].DelayCalibration is {} line && line.Applicable(o.Address, at) &&
            o.MinimumRttMs + model.TimingSlackMs >= line.InterceptMs)
            .Select(o => declared[o.LandmarkId].SiteId ?? o.LandmarkId).Distinct().Count();
        var cbg = calibratedSites >= 4 ? new LatencyEstimator(model, useEmpiricalCalibration: true).Estimate(conditional, session,
            session.Observations.Max(o => o.MeasuredUtc)) : null;
        var cbgRegion = cbg is null ? null : new RegionSnapshot(cbg.Status, cbg.Representative, cbg.CoveringRadiusKm,
            cbg.CandidateAreaKm2, cbg.Cells.ToArray(), cbg.Constraints.ToArray());
        return new(region, SpatialSupportEstimator.Create(cbgRegion?.Status == "candidate-region" ? cbgRegion : region, [], model),
            successes.Select(o => declared[o.LandmarkId].SiteId ?? o.LandmarkId).Distinct(StringComparer.Ordinal).Count(),
            successes.Select(o => declared[o.LandmarkId].Asn?.ToString() ?? "unknown").Where(n => n != "unknown").Distinct().Count(),
            successes.Min(o => o.MinimumRttMs!.Value),
            "Hipótesis condicional: añade coordenadas declaradas por propietarios de sondas públicas, sin certificación física. " +
            "Las respuestas ICMP no autentican identidad o posición. El margen nominal de 50 km no es un intervalo calibrado. " +
            "La calibración CBG usa retardos públicos entre anclas y puede fallar por cambios de ruta o asimetría. " +
            "No sustituye ni recorta la región principal; una contradicción de la calibración se conserva explícitamente.")
        { CalibratedRegion = cbgRegion, CalibrationStatus = cbgRegion?.Status ?? "no-applicable-mesh-data" };
    }
}
