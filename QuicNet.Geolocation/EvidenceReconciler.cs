using QuicNet.Geolocation;

namespace QuicNet.Geolocation;

public sealed record LocationDispute(string LandmarkId, string Reason);
public sealed record EvidenceDiagnostics(string StrictStatus, bool InferredRouterConflict,
    IReadOnlyList<LocationDispute> DisputedLocations, RegionSnapshot? ConditionalRegion, string Explanation);

/// <summary>Published router positions remain hypotheses, never mandatory bounds on the device.</summary>
public static class EvidenceReconciler
{
    public static (LocationEstimate Estimate, EvidenceDiagnostics? Diagnostics) Estimate(Catalog catalog,
        MeasurementSession session, EstimatorOptions model, DateTimeOffset? now = null)
    {
        var inferred = catalog.Landmarks.Where(a => a.Kind == "router" && a.Source.StartsWith("RIPE IPmap", StringComparison.Ordinal))
            .Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var strict = new LatencyEstimator(model).Estimate(catalog, session, now);
        if (inferred.Count == 0) return (strict, null);
        // Turning off a coordinate affects only geolocation; the original route and packet remain intact.
        Catalog Without(HashSet<string> omit) => new(catalog.Landmarks.Select(a => omit.Contains(a.Id) ? a with { LocationVerified = false } : a).ToList());
        var primary = new LatencyEstimator(model).Estimate(Without(inferred), session, now);
        var disputes = new List<LocationDispute>(); RegionSnapshot? conditional = null;
        bool conflict = strict.Status == "inconsistent";
        if (!conflict) conditional = Snapshot(strict);
        else if (primary.Status != "inconsistent")
        {
            // Diagnose a single bad declaration. If several explanations work, do not arbitrarily pick one.
            var alternatives = new List<(string Id, LocationEstimate Region)>();
            foreach (string id in strict.Constraints.Where(c => inferred.Contains(c.LandmarkId)).Select(c => c.LandmarkId))
            {
                var repaired = new LatencyEstimator(model).Estimate(Without([id]), session, now);
                if (repaired.Status is "candidate-region" or "unresolved") alternatives.Add((id, repaired));
            }
            foreach (var a in alternatives) disputes.Add(new(a.Id,
                "Al omitir esta ubicación publicada, las otras cotas vuelven a ser compatibles. No prueba cuál de las declaraciones es incorrecta."));
            if (alternatives.Count == 1) conditional = Snapshot(alternatives[0].Region);
        }
        bool routerConflict = conflict && primary.Status != "inconsistent";
        string explanation = primary.Status == "inconsistent"
            ? "Las referencias de medición también se contradicen. No se fuerza un punto ni se atribuye el fallo únicamente a los routers."
            : routerConflict
            ? "Las ubicaciones IPmap se contradicen. La zona principal conserva solo las referencias; la hipótesis de routers, si existe, se muestra por separado y no confirma tu ciudad."
            : "La zona principal usa las referencias. La zona condicional añade ciudades inferidas de routers; su aparente precisión depende de esas declaraciones.";
        primary = primary with { Warnings = [.. primary.Warnings, explanation] };
        return (primary, new(strict.Status, routerConflict, disputes.AsReadOnly(), conditional, explanation));
    }
    private static RegionSnapshot Snapshot(LocationEstimate e) => new(e.Status, e.Representative, e.CoveringRadiusKm,
        e.CandidateAreaKm2, Array.AsReadOnly(e.Cells.ToArray()), Array.AsReadOnly(e.Constraints.ToArray()));
}
