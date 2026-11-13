namespace QuicNet.Geolocation;

/// <summary>A qualitative network affinity field, never a calibrated location probability.</summary>
public sealed record SpatialHint(GeoPoint Position, double RttMs, double ScaleKm, double Strength, string Label, bool Inferred);
public sealed record SpatialPeak(GeoPoint Position, double ScaleKm, string Label, bool Inferred);
public sealed record SpatialSupport(double West, double North, double East, double South, int Width, int Height,
    byte[] Values, IReadOnlyList<SpatialHint> Hints, IReadOnlyList<SpatialPeak> Peaks, string Explanation);

public static class SpatialSupportEstimator
{
    /// <summary>Maximum of independent kernels: repeating a site or adding slower remote sites cannot outvote a fast local signal.</summary>
    public static double ScoreAt(GeoPoint point, IEnumerable<SpatialHint> hints)
    {
        double best = 0;
        foreach (var h in hints)
        {
            if (h.Strength <= best) continue;
            double d = Geography.DistanceKm(point, h.Position) / h.ScaleKm;
            best = Math.Max(best, h.Strength * Math.Exp(-.5 * d * d));
        }
        return best;
    }

    public static SpatialSupport? Create(RegionSnapshot? region, IReadOnlyList<RouterEvidence> routers, EstimatorOptions? model = null, EvidenceDiagnostics? diagnostics = null)
    {
        if (region is null || region.Cells.Count == 0 || region.Status is "inconsistent" or "unbounded" or "insufficient-data") return null;
        var selectedModel = model ?? new EstimatorOptions(); selectedModel.Validate();
        bool quarantined = diagnostics?.InferredRouterConflict == true;
        if (quarantined)
        {
            if (diagnostics!.DisputedLocations.Count != 1) return null;
            string disputed = diagnostics.DisputedLocations[0].LandmarkId;
            routers = routers.Where(r => (r.LandmarkId ?? "router-" + r.Address) != disputed).ToArray();
        }
        var raw = region.Constraints.Select(c => new SpatialHint(c.Center, c.RttMs, Math.Max(50, c.RadiusKm / 2),
            Strength(c.RttMs), "", c.Evidence == "known-router-ttl-rtt"))
            .Concat(routers.Where(r => r.RttMs >= 0 && double.IsFinite(r.RttMs)).Select(r => new SpatialHint(r.Published.Position,
                r.RttMs, Math.Max(50, Math.Min(Math.PI * Geography.EarthRadiusKm, selectedModel.PropagationKmPerMs * (r.RttMs + selectedModel.TimingSlackMs) / 2 + 50) / 2), Strength(r.RttMs), r.Published.City, true)))
            .OrderByDescending(h => h.Strength).ThenBy(h => h.Label, StringComparer.Ordinal);
        var hints = new List<SpatialHint>();
        foreach (var h in raw)
        {
            // One spatial site gets one kernel, regardless of IP count, route count or packet count.
            if (hints.Any(previous => Geography.DistanceKm(previous.Position, h.Position) < 50)) continue;
            hints.Add(h); if (hints.Count == 16) break;
        }
        if (hints.Count == 0) return null;
        double west = region.Cells.Min(c => c.West), east = region.Cells.Max(c => c.East),
            north = Math.Min(85.05112878, region.Cells.Max(c => c.North)), south = Math.Max(-85.05112878, region.Cells.Min(c => c.South));
        if (east <= west || north <= south) return null;
        double top = MercatorY(north), bottom = MercatorY(south), spanX = (east - west) / 360, spanY = bottom - top;
        int width = Math.Clamp((int)Math.Round(192 * Math.Min(1, spanX / spanY)), 32, 192),
            height = Math.Clamp((int)Math.Round(192 * Math.Min(1, spanY / spanX)), 32, 192);
        var mask = new bool[width * height];
        foreach (var c in region.Cells)
        {
            int x0 = Math.Clamp((int)Math.Ceiling((c.West - west) / (east - west) * width - .5), 0, width),
                x1 = Math.Clamp((int)Math.Floor((c.East - west) / (east - west) * width - .5), -1, width - 1),
                y0 = Math.Clamp((int)Math.Ceiling((MercatorY(c.North) - top) / spanY * height - .5), 0, height),
                y1 = Math.Clamp((int)Math.Floor((MercatorY(c.South) - top) / spanY * height - .5), -1, height - 1);
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) mask[y * width + x] = true;
        }
        var scores = new double[mask.Length]; double maximum = 0; GeoPoint? maximumPosition = null;
        for (int y = 0; y < height; y++)
        {
            double latitude = Latitude(top + (y + .5) / height * spanY);
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x; if (!mask[i]) continue;
                var p = new GeoPoint(latitude, west + (x + .5) / width * (east - west));
                scores[i] = ScoreAt(p, hints);
                if (scores[i] > maximum) { maximum = scores[i]; maximumPosition = p; }
            }
        }
        if (maximum <= 1e-12 || maximumPosition is null) return null;
        var values = new byte[mask.Length];
        for (int i = 0; i < values.Length; i++) if (mask[i]) values[i] = (byte)(1 + Math.Round(254 * scores[i] / maximum));
        bool Inside(GeoPoint p) => region.Cells.Any(c => p.Longitude >= c.West && p.Longitude <= c.East && p.Latitude >= c.South && p.Latitude <= c.North);
        var peaks = hints.Where(h => Inside(h.Position) && h.Strength >= maximum * .5)
            .Select(h => new SpatialPeak(h.Position, h.ScaleKm, h.Label, h.Inferred)).Take(8).ToArray();
        if (peaks.Length == 0) peaks = [new(maximumPosition, hints[0].ScaleKm, "", hints[0].Inferred)];
        return new(west, north, east, south, width, height, values, Array.AsReadOnly(hints.ToArray()), Array.AsReadOnly(peaks),
            "Afinidad relativa con señales de baja latencia; no es una probabilidad calibrada. Los RTT limitan distancias máximas, no miden distancias exactas. Las ciudades de routers siguen siendo declaraciones sin verificar. La anchura de los colores es una heurística visual, no precisión obtenida.");
    }
    private static double Strength(double rtt) => 1 / Math.Pow(Math.Max(1, rtt), 2);
    private static double MercatorY(double latitude) => (1 - Math.Asinh(Math.Tan(Math.Clamp(latitude, -85.05112878, 85.05112878) * Math.PI / 180)) / Math.PI) / 2;
    private static double Latitude(double y) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y))) * 180 / Math.PI;
}
