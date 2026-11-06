namespace QuicNet.Geolocation;

/// <summary>An explicitly conditional best fit, separate from physical bounds and never a confidence interval.</summary>
public sealed record LatencyPosition(GeoPoint Position, double ModelSpreadKm, double? ResidualMs,
    double? AccessDelayMs, int IndependentSites, bool DiverseGeometry, IReadOnlyList<GeoPoint> Alternatives)
{
    public string Method { get; init; } = "asymmetric-rtt-fit";
}

public static class LatencyPositionEstimator
{
    private sealed record Signal(GeoPoint Position, double Rtt, double Noise);
    private sealed record Fit(GeoPoint Position, double Cost, double Residual, double Delay);

    public static LatencyPosition? Estimate(Catalog catalog, MeasurementSession session, RegionSnapshot region, GeoPoint? prior = null)
    {
        if (region.Status != "candidate-region" || region.Cells.Count == 0) return null;
        var anchors = catalog.Landmarks.Where(a => a.Usable && a.Kind == "anchor").ToDictionary(a => a.Id);
        var accepted = region.Constraints.Where(c => c.Evidence.StartsWith("icmp-", StringComparison.Ordinal)).Select(c => c.LandmarkId).ToHashSet();
        var signals = new List<Signal>();
        foreach (var observation in session.Observations.Where(o => accepted.Contains(o.LandmarkId) && anchors.ContainsKey(o.LandmarkId))
            .OrderBy(o => o.MinimumRttMs).DistinctBy(o => anchors[o.LandmarkId].SiteId ?? o.LandmarkId))
        {
            var anchor = anchors[observation.LandmarkId];
            if (signals.Any(s => Geography.DistanceKm(s.Position, anchor.Position) < 20)) continue;
            var samples = observation.Samples.Where(s => s.Status == "Success" && s.RttMs is >= 0 && double.IsFinite(s.RttMs.Value))
                .Select(s => s.RttMs!.Value).Order().ToArray();
            if (samples.Length == 0) continue;
            signals.Add(new(anchor.Position, samples[0], Math.Max(.5, samples[samples.Length / 2] - samples[0])));
            if (signals.Count == 24) break;
        }
        if (signals.Count < 4) return null;
        var bounds = region.Constraints.Where(c => anchors.ContainsKey(c.LandmarkId)).ToArray();
        bool Allowed(GeoPoint p) => bounds.All(c => Geography.DistanceKm(p, c.Center) <= c.RadiusKm);
        // Several propagation/routing assumptions expose sensitivity instead of hiding it in a percentage.
        double[] slopes = [.008, .0105, .013, .016, .020];
        var seeds = region.Cells.Where((_, i) => i % Math.Max(1, region.Cells.Count / 96) == 0).Select(c => c.Center)
            .Concat(signals.Take(6).Select(s => s.Position)).Concat(prior == null ? [] : new[] { prior }).Where(Allowed).ToArray();
        if (seeds.Length == 0) return null;
        Fit Score(GeoPoint point, double slope)
        {
            var offsets = signals.Select(s => s.Rtt - Geography.DistanceKm(point, s.Position) * slope).ToArray();
            var noises = signals.Select(s => Math.Max(1, s.Noise + s.Rtt * .025)).ToArray();
            // Minimize the actual asymmetric convex loss. The old quartile was not its optimum
            // and could claim an access delay greater than the fastest complete round trip.
            double lo = 0, hi = signals.Min(s => s.Rtt);
            for (int iteration = 0; iteration < 22; iteration++)
            {
                double middle = (lo + hi) / 2, derivative = 0;
                for (int i = 0; i < offsets.Length; i++)
                {
                    double delta = offsets[i] - middle;
                    derivative -= (delta < 0 ? 3 : 1) * Math.Sign(delta) * Math.Min(4, 2 * Math.Abs(delta) / noises[i]) / noises[i];
                }
                if (derivative < 0) lo = middle; else hi = middle;
            }
            double delay = (lo + hi) / 2;
            double cost = 0, residual = 0;
            for (int i = 0; i < signals.Count; i++)
            {
                double delta = offsets[i] - delay;
                double noise = noises[i], z = Math.Abs(delta) / noise;
                // Congestion and circuitous routes add delay; an implausibly fast observation is more informative.
                cost += (delta < 0 ? 3 : 1) * (z <= 2 ? z * z : 4 * z - 4);
                residual += Math.Min(Math.Abs(delta), noise * 4);
            }
            return new(point, cost / signals.Count, residual / signals.Count, delay);
        }
        var fits = new List<Fit>();
        foreach (double slope in slopes)
        {
            var starts = seeds.Select(p => Score(p, slope)).OrderBy(f => f.Cost).Take(8);
            foreach (var start in starts)
            {
                var best = start;
                for (double step = Math.Min(1000, region.CoveringRadiusKm ?? 1000); step >= 1; step /= 2)
                {
                    for (int iteration = 0; iteration < 8; iteration++)
                    {
                        var next = best;
                        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            double lat = Math.Clamp(best.Position.Latitude + dy * step / 111.195, -89.99, 89.99);
                            double lon = best.Position.Longitude + dx * step / (111.195 * Math.Max(.02, Math.Cos(lat * Math.PI / 180)));
                            var point = new GeoPoint(lat, ((lon + 180) % 360 + 360) % 360 - 180);
                            if (Allowed(point) && Score(point, slope) is var candidate && candidate.Cost < next.Cost) next = candidate;
                        }
                        if (next == best) break;
                        best = next;
                    }
                }
                fits.Add(best);
            }
        }
        var winner = fits.MinBy(f => f.Cost)!;
        var alternatives = fits.Where(f => f.Cost <= winner.Cost + Math.Max(1, winner.Cost * .2))
            .OrderBy(f => f.Cost).Select(f => f.Position).DistinctBy(p => (Math.Round(p.Latitude, 1), Math.Round(p.Longitude, 1))).ToArray();
        int bearings = signals.Select(s => BearingBucket(winner.Position, s.Position)).Distinct().Count();
        bool diverse = signals.Count >= 6 && bearings >= 3;
        double spread = Math.Max(diverse ? 25 : 150, alternatives.Max(p => Geography.DistanceKm(winner.Position, p)));
        return new(winner.Position, spread, winner.Residual, winner.Delay, signals.Count, diverse, Array.AsReadOnly(alternatives));
    }

    private static int BearingBucket(GeoPoint from, GeoPoint to)
    {
        const double rad = Math.PI / 180;
        double lon = (to.Longitude - from.Longitude) * rad;
        double angle = Math.Atan2(Math.Sin(lon) * Math.Cos(to.Latitude * rad),
            Math.Cos(from.Latitude * rad) * Math.Sin(to.Latitude * rad) - Math.Sin(from.Latitude * rad) * Math.Cos(to.Latitude * rad) * Math.Cos(lon));
        return (int)Math.Floor((angle + Math.PI) / (Math.PI / 2)) % 4;
    }
}
