namespace QuicNet.Geolocation;

public static class AdaptivePlanner
{
    public static List<Landmark> Next(Catalog catalog, MeasurementSession session, LocationEstimate? estimate, int count,
        GeoPoint? focusPoint = null, double? focusRadiusKm = null)
    {
        catalog.Validate();
        if (count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(count));
        var measured = session.Observations.Select(o => o.LandmarkId).ToHashSet(StringComparer.Ordinal);
        var used = catalog.Landmarks.Where(a => a.Kind == "anchor" && measured.Contains(a.Id)).ToArray();
        var sites = used.Select(a => a.SiteId ?? a.Id).ToHashSet(StringComparer.Ordinal);
        var family = session.AddressFamily == "ipv4" ? System.Net.Sockets.AddressFamily.InterNetwork : System.Net.Sockets.AddressFamily.InterNetworkV6;
        var available = catalog.Landmarks.Where(a => a.Usable && a.Kind == "anchor" && !measured.Contains(a.Id) && !sites.Contains(a.SiteId ?? a.Id)
            && System.Net.IPAddress.Parse(a.Address).AddressFamily == family).ToArray();
        var byId = catalog.Landmarks.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var fastest = session.Observations.Where(o => o.MinimumRttMs.HasValue && byId.ContainsKey(o.LandmarkId))
            .OrderBy(o => o.MinimumRttMs).Select(o => byId[o.LandmarkId]).FirstOrDefault();
        var regions = estimate?.Cells.Where((_, index) => index % Math.Max(1, estimate.Cells.Count / 256) == 0).Select(c => c.Center).ToArray() ?? [];
        double spacing = estimate?.CoveringRadiusKm is { } radius ? Math.Clamp(radius / 4, 100, 600) : 600;
        if (focusRadiusKm is { } localRadius) spacing = Math.Clamp(localRadius / 2, 25, 200);
        // Compute the expensive spherical statistics once per anchor, rather than once per selection.
        var scored = available.Select(a =>
        {
            double sum = 0, squares = 0;
            foreach (var p in regions) { double distance = Geography.DistanceKm(p, a.Position); sum += distance; squares += distance * distance; }
            double mean = regions.Length == 0 ? 0 : sum / regions.Length;
            double variation = regions.Length == 0 ? 0 : Math.Sqrt(Math.Max(0, squares / regions.Length - mean * mean));
            return new Scored(a,
                used.Length == 0 ? Geography.DistanceKm(a.Position, new(0, 0)) : used.Min(u => Geography.DistanceKm(u.Position, a.Position)),
                focusPoint is not null ? 3 / (1 + Geography.DistanceKm(focusPoint, a.Position) / 250) :
                fastest is null ? 0 : 3 / (1 + Geography.DistanceKm(fastest.Position, a.Position) / 500),
                regions.Length == 0 ? 0 : 2 / (1 + mean / 1000) + variation / (mean + 100));
        }).ToList();
        GeoPoint[] seeds = [new(50, 10), new(40, -100), new(30, 110), new(-15, -60), new(0, 20), new(-25, 135)];
        var selected = new List<Landmark>();
        while (selected.Count < count && scored.Count > 0)
        {
            var best = session.Observations.Count == 0 && selected.Count < seeds.Length
                ? scored.MinBy(s => Geography.DistanceKm(s.Anchor.Position, seeds[selected.Count]))!
                : scored.OrderByDescending(s => regions.Length == 0 ? s.Separation / 20000 : s.Focus * Math.Min(1, s.Separation / spacing) + s.Geometry + s.Separation / 20000)
                    .ThenBy(s => s.Anchor.Id, StringComparer.Ordinal).First();
            selected.Add(best.Anchor);
            scored.RemoveAll(s => (s.Anchor.SiteId ?? s.Anchor.Id) == (best.Anchor.SiteId ?? best.Anchor.Id));
            foreach (var s in scored) s.Separation = Math.Min(s.Separation, Geography.DistanceKm(s.Anchor.Position, best.Anchor.Position));
        }
        return selected;
    }
    private sealed class Scored(Landmark anchor, double separation, double focus, double geometry)
    {
        public Landmark Anchor { get; } = anchor;
        public double Separation { get; set; } = separation;
        public double Focus { get; } = focus;
        public double Geometry { get; } = geometry;
    }
}
