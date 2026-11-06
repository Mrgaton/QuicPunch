using System.Net;
using System.Net.Sockets;

namespace QuicNet.Geolocation;

/// <summary>
/// Hypothetical cuts of the complete feasible region guide probing. Predicted RTTs and ASN
/// diversity are scheduling heuristics only; they never become distance constraints.
/// </summary>
public static class InformationLandmarkPlanner
{
    public const string Version = "information-v2.1";
    private const int QuadratureSize = 192;
    private sealed record Known(Landmark Anchor, Observation Observation, double Rtt);
    private sealed record WeightedPoint(GeoPoint Position, double CellRadiusKm);
    private sealed record Candidate(Landmark Anchor, double[] Distances, double[] Excluded,
        double SeparationKm, double FastAffinity, double Opportunity, double Spread, string Network, double Reliability);

    public static LandmarkSelection[] Next(Catalog catalog, MeasurementSession session,
        LocationUpdate? update, int count, int round, int samples = 3, EstimatorOptions? model = null, bool regional = false) =>
        NextCore(catalog, session, update, count, round, samples, model ?? new EstimatorOptions(), regional);
    private static LandmarkSelection[] NextCore(Catalog catalog, MeasurementSession session,
        LocationUpdate? update, int count, int round, int samples, EstimatorOptions model, bool regional)
    {
        catalog.Validate(); model.Validate();
        if (count is < 6 or > 64 || round < 1 || samples is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (session.Observations.Count == 0)
            return LegacyProgressiveSearchPlanner.Next(catalog, session, update, count, round, samples);
        var family = session.AddressFamily == "ipv6" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        var anchors = catalog.Landmarks.Where(a => (regional ? a.ProbeEligible : a.Usable) && a.Kind == "anchor" &&
            IPAddress.Parse(a.Address).AddressFamily == family).ToArray();
        var byId = anchors.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var observed = session.Observations.GroupBy(o => o.LandmarkId)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var known = observed.Values.Where(o => o.MinimumRttMs.HasValue && byId.ContainsKey(o.LandmarkId))
            .Select(o => new Known(byId[o.LandmarkId], o, o.MinimumRttMs!.Value))
            .OrderBy(k => k.Rtt).ThenBy(k => k.Anchor.Id, StringComparer.Ordinal).ToArray();
        var measured = anchors.Where(a => observed.ContainsKey(a.Id)).ToArray();
        var seenSites = measured.Select(a => a.SiteId ?? a.Id).ToHashSet(StringComparer.Ordinal);
        var networks = known.GroupBy(k => Network(k.Anchor)).ToDictionary(g => g.Key,
            g => g.Select(k => k.Anchor.SiteId ?? k.Anchor.Id).Distinct().Count(), StringComparer.Ordinal);
        var available = anchors.Where(a => !observed.TryGetValue(a.Id, out var old) ||
            old.Samples.Count + samples <= 100).ToArray();
        var points = SupportPoints(update?.Region?.Cells);
        return Select(available, known, measured, observed, seenSites, networks, points,
            Enumerable.Repeat(1.0, points.Length).ToArray(), count, round, model, regional, update?.Region?.Representative);
    }

    private static LandmarkSelection[] Select(Landmark[] available, Known[] known, Landmark[] measured,
        Dictionary<string, Observation> observed, HashSet<string> seenSites, Dictionary<string, int> networks,
        WeightedPoint[] points, double[] weights, int count, int round, EstimatorOptions model, bool regional, GeoPoint? representative)
    {
        var candidates = available.Select(a => Feature(a, known, measured, observed, points, model, !networks.ContainsKey(Network(a)))).ToList();
        var selected = new List<LandmarkSelection>();
        var positions = new List<GeoPoint>();
        var selectedNetworks = new Dictionary<string, int>(StringComparer.Ordinal);
        int globalSlots = Math.Max(1, (int)Math.Round(count * .1));
        int boundarySlots = Math.Max(1, (int)Math.Round(count * .25));
        int nearFast = known.Length == 0 ? 0 : known.Count(k => Geography.DistanceKm(k.Anchor.Position, known[0].Anchor.Position) < 250);
        int localSlots = known.Length == 0 ? 0 : Math.Min(count - globalSlots - boundarySlots - 1,
            Math.Max(1, (int)Math.Round(count * (regional ? (nearFast < 8 ? .45 : .25) : nearFast < 8 ? .25 : .1))));
        for (int slot = 0; slot < count && candidates.Count > 0; slot++)
        {
            string role = slot < globalSlots || known.Length == 0 ? "global" :
                slot < globalSlots + localSlots ? "local" :
                slot < globalSlots + localSlots + boundarySlots ? "boundary" : "information";
            // Preserve primary anchors while exploring declared coordinates: no soft hypothesis
            // is allowed to consume the entire measurement budget or remove a global branch.
            bool requirePrimary = regional && (role == "global" || slot == globalSlots);
            if (requirePrimary && role != "global") role = "anchor";
            if (regional && count >= 10 && slot > globalSlots && slot <= globalSlots + 2 && known.Length > 0)
                role = "regional";
            Candidate? chosen = null;
            if (slot == count - 1 && known.Length > 0)
            {
                var eligible = candidates.Where(c => known.Any(k => k.Anchor.Id == c.Anchor.Id)).ToArray();
                if (eligible.Length > 0)
                {
                    chosen = eligible.OrderByDescending(c => Verification(c, known, points, model))
                        .ThenBy(c => c.Anchor.Id, StringComparer.Ordinal).First();
                    role = "verify";
                }
            }
            if (chosen is null)
            {
                bool fresh = candidates.Any(c => !seenSites.Contains(c.Anchor.SiteId ?? c.Anchor.Id));
                var pool = candidates.Where(c => !fresh || !seenSites.Contains(c.Anchor.SiteId ?? c.Anchor.Id));
                if (requirePrimary && candidates.Any(c => c.Anchor.Usable))
                {
                    var primaryPool = candidates.Where(c => c.Anchor.Usable);
                    pool = primaryPool.Any(c => !seenSites.Contains(c.Anchor.SiteId ?? c.Anchor.Id))
                        ? primaryPool.Where(c => !seenSites.Contains(c.Anchor.SiteId ?? c.Anchor.Id)) : primaryPool;
                }
                double Rank(Candidate c)
                {
                    double separation = positions.Count == 0 ? c.SeparationKm :
                        Math.Min(c.SeparationKm, positions.Min(p => Geography.DistanceKm(p, c.Anchor.Position)));
                    int oldNetwork = networks.GetValueOrDefault(c.Network);
                    int newNetwork = selectedNetworks.GetValueOrDefault(c.Network);
                    double networkDiversity = 1 / Math.Sqrt(1 + .12 * oldNetwork + 1.5 * newNetwork);
                    if (role == "global")
                        return separation / 20_000 + .12 * networkDiversity;
                    if (role == "regional")
                    {
                        // Scout new nearby areas rather than repeatedly sampling one dense city.
                        // The geometric centre is only another search hint, never a claimed location.
                        double centralAffinity = representative is null ? 0 :
                            1 / (1 + Geography.DistanceKm(representative, c.Anchor.Position) / 600);
                        double newArea = .1 + .9 * Math.Min(1, separation / 120);
                        return (.6 * c.FastAffinity + .4 * centralAffinity + .08 * c.Opportunity) *
                            newArea * networkDiversity * c.Reliability;
                    }
                    if (role is "local" or "anchor")
                    {
                        double sameFastNetwork = regional && known.Length > 0 && c.Anchor.Asn.HasValue &&
                            known.Take(4).Any(k => k.Anchor.Asn == c.Anchor.Asn && k.Rtt <= known[0].Rtt * 1.5 + 1) ? 1.25 : 1;
                        double spatialDiversity = !regional || positions.Count == 0 ? 1 :
                            .55 + .45 * Math.Min(1, positions.Min(p => Geography.DistanceKm(p, c.Anchor.Position)) / 40);
                        return (c.FastAffinity + .05 * c.Opportunity + .1 * Gain(c.Excluded, weights)) *
                            networkDiversity * spatialDiversity * sameFastNetwork * c.Reliability;
                    }
                    double distinctDirection = positions.Count == 0 ? 1 :
                        .2 + .8 * Math.Min(1, positions.Min(p => Geography.DistanceKm(p, c.Anchor.Position)) / 300);
                    double novelty = .3 + .7 * (1 - Math.Exp(-c.SeparationKm / 200));
                    double gain = Gain(c.Excluded, weights);
                    double utility = gain * novelty + .035 * c.Opportunity +
                        (role == "boundary" ? .09 * c.Spread : .005 * c.Spread);
                    return utility * networkDiversity * distinctDirection * c.Reliability;
                }
                chosen = pool.OrderByDescending(Rank).ThenBy(c => c.Anchor.Id, StringComparer.Ordinal).First();
            }
            double hypothetical = Gain(chosen.Excluded, weights);
            selected.Add(new(chosen.Anchor.Id, role)
            {
                HypotheticalRemovalFraction = role is "global" or "verify" or "local" or "anchor" or "regional" ? null : hypothetical,
                Reason = role switch
                {
                    "global" => "Exploración exterior y diversidad de red; no elimina ninguna hipótesis.",
                    "local" => "Exploración cercana a señales rápidas con diversidad de red. Busca RTT menores sin asumir cercanía física.",
                    "anchor" => "Referencia principal cerca de señales rápidas, incluidas pistas declaradas; conserva evidencia independiente de esas declaraciones.",
                    "regional" => "Exploración de zonas cercanas todavía no medidas; evita agotar el presupuesto repitiendo una ciudad densa.",
                    "verify" => "Revisión de una cota útil o variable; se conservan muestras y mínimos anteriores.",
                    _ => "Corte hipotético de la región completa y diversidad de emplazamiento/ASN. Los RTT previstos solo ordenan sondas."
                }
            });
            positions.Add(chosen.Anchor.Position);
            selectedNetworks[chosen.Network] = selectedNetworks.GetValueOrDefault(chosen.Network) + 1;
            // Soft, batch-local discount prevents selecting many copies of the same possible cut.
            // It has no connection to the estimator's cells, samples or distance bounds.
            if (role is "information" or "boundary" or "local" or "anchor" or "regional")
                for (int i = 0; i < weights.Length; i++) weights[i] *= 1 - .7 * chosen.Excluded[i];
            candidates.RemoveAll(c => (c.Anchor.SiteId ?? c.Anchor.Id) == (chosen.Anchor.SiteId ?? chosen.Anchor.Id) ||
                IPAddress.Parse(c.Anchor.Address).Equals(IPAddress.Parse(chosen.Anchor.Address)));
        }
        return selected.ToArray();
    }

    private static Candidate Feature(Landmark a, Known[] known, Landmark[] measured,
        Dictionary<string, Observation> observed, WeightedPoint[] points, EstimatorOptions model, bool networkNovel)
    {
        var distances = points.Select(p => Geography.DistanceKm(a.Position, p.Position)).ToArray();
        double separation = measured.Length == 0 ? 20_000 : measured.Min(m => Geography.DistanceKm(a.Position, m.Position));
        var neighbors = known.Select(k => (Known: k, Distance: Geography.DistanceKm(k.Anchor.Position, a.Position)))
            .OrderBy(p => p.Distance).Take(8).ToArray();
        double opportunity = 0;
        var excluded = new double[points.Length];
        if (neighbors.Length > 0)
        {
            // +/- distance/100 supplies deliberately broad planning scenarios, NOT measured distances,
            // latency lower bounds, or permission to subtract an access delay from real RTTs.
            var reference = neighbors.MinBy(p => p.Known.Rtt + p.Distance / 100);
            double nominal = reference.Known.Rtt + reference.Distance / 100;
            double optimistic = Math.Max(.25, reference.Known.Rtt - reference.Distance / 100);
            double[] scenarios = [nominal, (nominal + optimistic) / 2, optimistic];
            opportunity = (networkNovel ? 1 : .35) / (1 + nominal / 30);
            foreach (double rtt in scenarios)
            {
                double radius = Math.Min(Math.PI * Geography.EarthRadiusKm,
                    (rtt + model.TimingSlackMs) * model.PropagationKmPerMs / 2 + a.UncertaintyKm);
                if (!distances.Where((d, i) => d <= radius + points[i].CellRadiusKm).Any()) continue;
                for (int i = 0; i < distances.Length; i++)
                    if (distances[i] > radius + points[i].CellRadiusKm) excluded[i] += 1.0 / scenarios.Length;
            }
        }
        double mean = distances.Length == 0 ? 0 : distances.Average();
        double deviation = distances.Length == 0 ? 0 : Math.Sqrt(distances.Sum(d => (d - mean) * (d - mean)) / distances.Length);
        double reliability = observed.TryGetValue(a.Id, out var old) && old.MinimumRttMs is null ? .15 : 1;
        double fastAffinity = known.Length == 0 ? 0 : known.Take(8).Max(k =>
            Math.Pow((known[0].Rtt + 1) / (k.Rtt + 1), 2) / (1 + Geography.DistanceKm(a.Position, k.Anchor.Position) / 300));
        return new(a, distances, excluded, separation, fastAffinity, opportunity,
            Math.Min(1, deviation / (mean + 500)), Network(a), reliability);
    }



    private static double Gain(double[] excluded, double[] weights)
    {
        double sum = 0, total = 0;
        for (int i = 0; i < weights.Length; i++) { sum += weights[i] * excluded[i]; total += weights[i]; }
        return total == 0 ? 0 : sum / total;
    }

    private static double Verification(Candidate c, Known[] known, WeightedPoint[] points, EstimatorOptions model)
    {
        var k = known.First(n => n.Anchor.Id == c.Anchor.Id);
        var values = k.Observation.Samples.Where(s => s.Status == "Success" && s.RttMs.HasValue)
            .Select(s => s.RttMs!.Value).Order().ToArray();
        double variation = values[values.Length / 2] - k.Rtt;
        double hypothetical = Math.Min(5, variation / 2 + 1 / Math.Sqrt(values.Length));
        double radius = Math.Max(0, (k.Rtt - hypothetical + model.TimingSlackMs) *
            model.PropagationKmPerMs / 2 + c.Anchor.UncertaintyKm);
        double removable = c.Distances.Where((d, i) => d > radius + points[i].CellRadiusKm).Count() / (double)points.Length;
        if (removable >= 1) removable = 0;
        return removable / Math.Sqrt(values.Length) + .001 / (1 + k.Rtt);
    }

    private static WeightedPoint[] SupportPoints(IReadOnlyList<GeoCell>? source)
    {
        var cells = source is { Count: > 0 } ? source : Geography.WorldCells().ToArray();
        double total = cells.Sum(c => c.AreaKm2), cumulative = 0;
        int cell = 0;
        var points = new WeightedPoint[QuadratureSize];
        for (int i = 0; i < points.Length; i++)
        {
            double target = total * (i + .5) / points.Length;
            while (cell + 1 < cells.Count && cumulative + cells[cell].AreaKm2 < target)
            { cumulative += cells[cell].AreaKm2; cell++; }
            points[i] = new(cells[cell].Center, cells[cell].CoverRadiusKm);
        }
        return points;
    }


    private static string Network(Landmark a)
    {
        if (a.Asn is { } asn) return "AS" + asn;
        var bytes = IPAddress.Parse(a.Address).GetAddressBytes();
        return (bytes.Length == 4 ? "prefix4-" : "prefix6-") +
            Convert.ToHexString(bytes.AsSpan(0, bytes.Length == 4 ? 3 : 6));
    }
}
