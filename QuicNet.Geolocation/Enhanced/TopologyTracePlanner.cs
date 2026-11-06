namespace QuicNet.Geolocation;

/// <summary>Diverse measured destinations, not just the smallest RTT. Scheduling cannot exclude a geographic region.</summary>
public static class TopologyTracePlanner
{
    public static Observation[] Next(Catalog catalog, MeasurementSession session, AccessTopology topology,
        IReadOnlySet<string> traced, int count)
    {
        var byId = catalog.Landmarks.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var candidates = session.Observations.Where(o => o.MinimumRttMs.HasValue && !traced.Contains(o.LandmarkId) &&
            byId.TryGetValue(o.LandmarkId, out var a) && a.Kind == "anchor").OrderBy(o => o.MinimumRttMs)
            .ThenBy(o => o.LandmarkId, StringComparer.Ordinal).DistinctBy(o => byId[o.LandmarkId].SiteId ?? o.LandmarkId).ToList();
        var selected = new List<Observation>();
        var covered = session.Observations.Where(o => traced.Contains(o.LandmarkId) && byId.ContainsKey(o.LandmarkId)).Select(o => byId[o.LandmarkId]).ToList();
        string Zone(Landmark a) => a.CountryCode + ":" + (a.RegionName ?? "");
        var fastest = session.Observations.Where(o => o.MinimumRttMs.HasValue && byId.ContainsKey(o.LandmarkId)).MinBy(o => o.MinimumRttMs);
        string? focus = fastest is null ? null : byId[fastest.LandmarkId].CountryCode;
        while (selected.Count < count && candidates.Count > 0)
        {
            // Prefer an uncovered administrative region, then a different AS/site. Keep remote controls too.
            double Information(Observation o)
            {
                var a = byId[o.LandmarkId];
                double latency = 3 / (1 + o.MinimumRttMs!.Value / 20);
                double region = covered.All(b => Zone(b) != Zone(a)) ? 5 : 0;
                double network = a.Asn.HasValue && covered.All(b => b.Asn != a.Asn) ? 1 : 0;
                double local = a.CountryCode == focus ? 3 : 0;
                double distance = covered.Count == 0 ? 0 : Math.Min(2, covered.Min(b => Geography.DistanceKm(a.Position, b.Position)) / 700);
                var oldBranches = topology.Traces.Where(t => t.StablePublicHops > 0 && byId.ContainsKey(t.LandmarkId))
                    .GroupBy(t => t.Branch).ToArray();
                double redundancy = oldBranches.Any(g => g.Count(t => byId[t.LandmarkId].Asn == a.Asn && Zone(byId[t.LandmarkId]) == Zone(a)) >= 2) ? 2 : 0;
                return region + network + local + latency + distance - redundancy;
            }
            var next = selected.Count == 0 && covered.Count == 0 ? candidates[0] : candidates.MaxBy(Information)!;
            selected.Add(next); covered.Add(byId[next.LandmarkId]); candidates.Remove(next);
        }
        return selected.ToArray();
    }
}
