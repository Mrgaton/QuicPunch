using System.Net;
using System.Net.Sockets;
namespace QuicNet.Geolocation;
public static class LegacyProgressiveSearchPlanner
{
    public static LandmarkSelection[] Next(Catalog catalog, MeasurementSession session,
        LocationUpdate? update, int count, int round, int samples = 3)
    {
        catalog.Validate();
        if (count is < 6 or > 64 || round < 1 || samples is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(count));
        var family = session.AddressFamily == "ipv6" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        var observations = session.Observations.GroupBy(o => o.LandmarkId)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var usable = catalog.Landmarks.Where(a => a.Kind == "anchor" && a.Usable &&
            IPAddress.Parse(a.Address).AddressFamily == family).ToArray();
        var byId = usable.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var seenSites = usable.Where(a => observations.ContainsKey(a.Id)).Select(a => a.SiteId ?? a.Id).ToHashSet(StringComparer.Ordinal);
        var available = usable.Where(a => !observations.TryGetValue(a.Id, out var old) || old.Samples.Count + samples <= 100).ToList();
        if (observations.Count == 0)
            return AdaptivePlanner.Next(catalog, session, null, count)
                .Select(a => new LandmarkSelection(a.Id, "global")).ToArray();

        var measured = usable.Where(a => observations.ContainsKey(a.Id)).ToArray();
        var fast = session.Observations.Where(o => o.MinimumRttMs.HasValue && byId.ContainsKey(o.LandmarkId))
            .OrderBy(o => o.MinimumRttMs).ThenBy(o => o.LandmarkId, StringComparer.Ordinal).ToArray();
        double bestRtt = fast.FirstOrDefault()?.MinimumRttMs ?? double.PositiveInfinity;
        var focus = new List<GeoPoint>();
        foreach (var o in fast.Where(o => o.MinimumRttMs <= Math.Max(2, bestRtt * 2)))
        {
            var p = byId[o.LandmarkId].Position;
            if (focus.All(f => Geography.DistanceKm(f, p) > 50)) focus.Add(p);
            if (focus.Count == 4) break;
        }
        if (focus.Count == 0 && update?.Region?.Representative is { } representative) focus.Add(representative);
        double boundary = Math.Clamp((update?.Region?.Constraints.MinBy(c => c.RadiusKm)?.RadiusKm ?? 1000) * .75, 250, 2500);
        int global = Math.Max(1, (int)Math.Round(count * .1));
        int peripheral = Math.Max(1, (int)Math.Round(count * .2));

        var selected = new List<LandmarkSelection>();
        var positions = new List<GeoPoint>();
        for (int slot = 0; slot < count && available.Count > 0; slot++)
        {
            // Reserve the global slots first so a small or sparse catalogue cannot consume them locally.
            string role = slot < global || focus.Count == 0 ? "global" :
                slot < global + peripheral ? "periphery" : "local";
            var center = focus.Count == 0 ? new GeoPoint(0, 0) : focus[(round + slot) % focus.Count];
            bool preferNew = available.Any(a => !seenSites.Contains(a.SiteId ?? a.Id));
            var pool = available.Where(a => !preferNew || !seenSites.Contains(a.SiteId ?? a.Id));
            double Separation(Landmark a) => Math.Min(
                measured.Length == 0 ? 20_000 : measured.Min(m => Geography.DistanceKm(m.Position, a.Position)),
                positions.Count == 0 ? 20_000 : positions.Min(p => Geography.DistanceKm(p, a.Position)));
            double Rank(Landmark a)
            {
                double separation = Separation(a);
                double d = Geography.DistanceKm(center, a.Position);
                int attempts = observations.TryGetValue(a.Id, out var old) ? old.Samples.Count : 0;
                double repeatPenalty = attempts * 50;
                return role switch
                {
                    "global" => separation - repeatPenalty,
                    "periphery" => -Math.Abs(d - boundary) + Math.Min(500, separation) * .25 - repeatPenalty,
                    _ => -d + Math.Min(300, separation) * .15 - repeatPenalty
                };
            }
            var revisit = slot == count - 1 ? fast.Select(o => byId[o.LandmarkId])
                .FirstOrDefault(a => available.Any(candidate => candidate.Id == a.Id)) : null;
            var chosen = revisit ?? pool.OrderByDescending(Rank).ThenBy(a => a.Id, StringComparer.Ordinal).First();
            if (revisit is not null) role = "verify";
            selected.Add(new(chosen.Id, role));
            positions.Add(chosen.Position);
            available.RemoveAll(a => (a.SiteId ?? a.Id) == (chosen.SiteId ?? chosen.Id) ||
                IPAddress.Parse(a.Address).Equals(IPAddress.Parse(chosen.Address)));
        }

        return selected.ToArray();
    }
}

