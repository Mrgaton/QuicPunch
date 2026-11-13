namespace QuicNet.Geolocation;

/// <summary>Soft network-affinity ranking. Common-path RTT contrasts are empirical features, never physical bounds.
/// This is not a full TBG joint router-position solver and does not identify a handset behind an operator gateway.</summary>
public static class TopologyLocationEstimator
{
    private sealed record Feature(Landmark Anchor, Observation Observation, LatencyStatistics Statistics, string Branch, double Weight);
    public static TopologyLocationAssessment Evaluate(Catalog catalog, MeasurementSession session, AccessTopology topology)
    {
        var known = catalog.Landmarks.Where(a => a.Kind == "anchor" && !a.Anycast).ToDictionary(a => a.Id, StringComparer.Ordinal);
        var traces = topology.Traces.ToDictionary(t => t.LandmarkId, StringComparer.Ordinal);
        var features = session.Observations.Where(o => known.ContainsKey(o.LandmarkId) && o.MinimumRttMs.HasValue)
            .OrderBy(o => o.MinimumRttMs).ThenByDescending(o => traces.GetValueOrDefault(o.LandmarkId)?.StablePathHops ?? 0)
            .ThenBy(o => o.LandmarkId, StringComparer.Ordinal)
            .DistinctBy(o => known[o.LandmarkId].SiteId ?? o.LandmarkId).Select(o =>
            {
                var a = known[o.LandmarkId];
                var trace = traces.GetValueOrDefault(o.LandmarkId);
                string branch = trace is { StablePathHops: > 0 } && !trace.Branch.StartsWith("unresolved:", StringComparison.Ordinal)
                    ? trace.Branch : "unresolved:" + (a.Asn?.ToString() ?? a.SiteId ?? a.Id) + ":" + a.CountryCode + ":" + a.RegionName;
                return new Feature(a, o, RobustStatistics.Summarize(o.Samples), branch, 1);
            }).ToArray();
        var limitations = new List<string>
        {
            "Afinidad de red relativa, sin probabilidades calibradas ni descarte de comunidades. Las coordenadas son las del catálogo de referencias.",
            "Una salida centralizada del operador puede producir la misma topología desde ciudades distintas; el ranking puede situar esa salida y no el dispositivo.",
            "ICMP repetido no controla la identidad del flujo como Paris traceroute. Saltos ocultos, balanceo y distintas IP de un mismo router limitan el grafo."
        };
        int usable = topology.Traces.Count(t => t.StablePathHops >= 2);
        if (features.Length < 4) return new("insufficient-data", [], [], usable, 0, limitations.ToArray());
        // Each observed branch and each administrative area has a bounded vote. Unresolved paths are never counted as proven independent branches.
        var branchCounts = features.GroupBy(f => f.Branch).ToDictionary(g => g.Key, g => g.Count());
        var areaCounts = features.GroupBy(f => Area(f.Anchor)).ToDictionary(g => g.Key, g => g.Count());
        features = features.Select(f => f with { Weight = 1 / (Math.Sqrt(branchCounts[f.Branch]) * Math.Sqrt(areaCounts[Area(f.Anchor)])) }).ToArray();
        var byId = features.ToDictionary(f => f.Anchor.Id, StringComparer.Ordinal);
        var contrasts = topology.RoutePairs.Where(p => p.SharedEdges >= 1 && p.SharedPathNodes.Length >= 2 &&
            byId.ContainsKey(p.FirstLandmarkId) && byId.ContainsKey(p.SecondLandmarkId))
            .Select(p =>
            {
                var a = byId[p.FirstLandmarkId]; var b = byId[p.SecondLandmarkId];
                // Samples far apart in time or poorly repeated cannot cancel a common access delay reliably.
                if (a.Statistics.Successes < 2 || b.Statistics.Successes < 2 ||
                    Math.Abs((a.Observation.MeasuredUtc - b.Observation.MeasuredUtc).TotalSeconds) > 180 ||
                    Geography.DistanceKm(a.Anchor.Position, b.Anchor.Position) < 100) return null;
                double noise = Math.Max(3, 1.4826 * ((a.Statistics.MadMs ?? 0) + (b.Statistics.MadMs ?? 0))) +
                    .01 * (a.Anchor.UncertaintyKm + b.Anchor.UncertaintyKm);
                double stability = Math.Min(traces[a.Anchor.Id].Stability, traces[b.Anchor.Id].Stability);
                return new RouteContrast(a.Anchor.Id, b.Anchor.Id, a.Statistics.MinimumMs!.Value - b.Statistics.MinimumMs!.Value,
                    noise, p.SharedPathNodes.Length, p.SharedEdges, stability * Math.Sqrt(a.Weight * b.Weight));
            }).OfType<RouteContrast>().OrderByDescending(p => Math.Abs(p.DeltaRttMs) / p.NoiseMs)
            .ThenBy(p => p.FirstLandmarkId, StringComparer.Ordinal).ThenBy(p => p.SecondLandmarkId, StringComparer.Ordinal).Take(128).ToArray();
        // Sharing a destination with many pairs does not turn one trace into N independent observations.
        var degrees = contrasts.SelectMany(p => new[] { p.FirstLandmarkId, p.SecondLandmarkId }).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        contrasts = contrasts.Select(p => p with { Weight = p.Weight / Math.Max(degrees[p.FirstLandmarkId], degrees[p.SecondLandmarkId]) }).ToArray();
        int branches = topology.Traces.Where(t => t.StablePathHops >= 2 && !t.Branch.StartsWith("unresolved:", StringComparison.Ordinal))
            .Select(t => t.Branch).Distinct(StringComparer.Ordinal).Count();
        if (usable < 3 || contrasts.Length < 3) limitations.Add("No hay tres contrastes de rutas repetidas utilizables: el orden se apoya en RTT; la topología solo permite reducir redundancias observadas.");
        if (topology.Nodes.Any(n => n.CommonAccess)) limitations.Add("Se observa infraestructura de acceso compartida; no se interpreta su ciudad como la del dispositivo.");
        var zoneGroups = features.Where(f => !string.IsNullOrWhiteSpace(f.Anchor.CountryCode)).GroupBy(f => Area(f.Anchor)).ToArray();
        if (zoneGroups.Length == 0) return new("insufficient-area-metadata", [], contrasts, usable, branches, limitations.ToArray());
        var selected = features.OrderBy(f => f.Statistics.MinimumMs).GroupBy(f => Area(f.Anchor)).SelectMany(g => g.Take(3)).Take(80).ToArray();
        var zones = new List<TopologyZone>();
        double minimumRtt = features.Min(f => f.Statistics.MinimumMs!.Value);
        foreach (var group in zoneGroups)
        {
            var best = group.MinBy(f => f.Statistics.MinimumMs)!;
            // The representative is an actual declared site in this area, never an invented handset coordinate.
            var point = best.Anchor.Position;
            double bestLoss = double.PositiveInfinity, bestLatency = 0; double? bestRoute = null;
            foreach (double slope in new[] { .005, .01, .015, .02 })
            {
                double access = Math.Max(0, RobustStatistics.Median(selected.Select(f => f.Statistics.MinimumMs!.Value - slope * Geography.DistanceKm(point, f.Anchor.Position))));
                double pattern = selected.Sum(f => f.Weight * Loss((f.Statistics.MinimumMs!.Value - access - slope * Geography.DistanceKm(point, f.Anchor.Position)) /
                    Math.Max(4, 1.4826 * (f.Statistics.MadMs ?? 0) + .1 * f.Statistics.MinimumMs.Value))) / selected.Sum(f => f.Weight);
                // Retain the strongest local signal. A global geometric fit cannot outvote a short RTT just by explaining many slow remote paths.
                double nearby = Math.Log(1 + (best.Statistics.MinimumMs!.Value - minimumRtt) / Math.Max(2, minimumRtt * .5));
                double latency = .7 * nearby + .3 * pattern;
                double? route = usable >= 3 && contrasts.Length >= 3 ? contrasts.Sum(p => p.Weight * Loss((p.DeltaRttMs - slope *
                    (Geography.DistanceKm(point, byId[p.FirstLandmarkId].Anchor.Position) - Geography.DistanceKm(point, byId[p.SecondLandmarkId].Anchor.Position))) / p.NoiseMs)) /
                    Math.Max(1e-9, contrasts.Sum(p => p.Weight)) : null;
                double loss = route.HasValue ? .65 * latency + .35 * route.Value : latency;
                if (loss < bestLoss) { bestLoss = loss; bestLatency = latency; bestRoute = route; }
            }
            int tracedSites = group.Count(f => traces.TryGetValue(f.Anchor.Id, out var t) && t.StablePathHops >= 2);
            int zoneBranches = group.Where(f => !f.Branch.StartsWith("unresolved:", StringComparison.Ordinal)).Select(f => f.Branch).Distinct().Count();
            string label = string.IsNullOrWhiteSpace(best.Anchor.RegionName) ? best.Anchor.CountryCode! : best.Anchor.RegionName;
            zones.Add(new(best.Anchor.CountryCode!, best.Anchor.RegionName ?? "", label, point, bestLoss, bestLatency, bestRoute,
                group.Count(), tracedSites, zoneBranches,
                [FormattableString.Invariant($"Referencia más rápida: {best.Anchor.Name ?? best.Anchor.Id}, {best.Statistics.MinimumMs:F2} ms; {best.Statistics.Successes} respuestas."),
                 $"{group.Count()} emplazamientos, {tracedSites} rutas con al menos dos nodos estables y {zoneBranches} ramas compartidas identificadas. Las rutas no resueltas no se cuentan como independientes.",
                 bestRoute.HasValue ? FormattableString.Invariant($"Pérdida del modelo: RTT {bestLatency:F3}; diferencias entre rutas compartidas {bestRoute:F3} ({contrasts.Length} pares). Menor pérdida implica más afinidad, no certeza.")
                    : "Topología insuficiente para comparar zonas: se conserva el orden por patrón de RTT sin fingir una mejora por rutas."]));
        }
        double minimum = zones.Min(z => z.RelativeScore);
        var weighted = zones.Select(z => z with { RelativeScore = Math.Exp(-Math.Min(50, (z.RelativeScore - minimum) / 2)) }).ToArray();
        double total = weighted.Sum(z => z.RelativeScore);
        // Preserve every measured alternative, even with a small score. This ranking never trims the RTT-compatible region.
        return new(usable >= 3 && contrasts.Length >= 3 ? "route-informed-affinity" : "latency-only-fallback",
            weighted.Select(z => z with { RelativeScore = z.RelativeScore / total }).OrderByDescending(z => z.RelativeScore)
                .ThenBy(z => z.Label, StringComparer.Ordinal).ToArray(), contrasts, usable, branches, limitations.ToArray());
    }
    private static string Area(Landmark a) => a.CountryCode + ":" + (a.RegionName ?? "");
    private static double Loss(double value) { double x = Math.Min(8, Math.Abs(value)); return x <= 1 ? x * x / 2 : x - .5; }
}
