namespace QuicNet.Geolocation;

/// <summary>Multi-branch search: exploration stays global; refinement never commits irrevocably to one country.</summary>
public static class HierarchicalLocationPlanner
{
    public static HierarchyAssessment Assess(Catalog catalog, MeasurementSession session, LocationUpdate update,
        FingerprintAssessment fingerprints, TopologyLocationAssessment? routeLocation = null)
    {
        var hypotheses = new List<GeographicHypothesis>();
        var known = catalog.Landmarks.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var measured = session.Observations.Where(o => o.MinimumRttMs.HasValue && known.ContainsKey(o.LandmarkId))
            .OrderBy(o => o.MinimumRttMs).DistinctBy(o => known[o.LandmarkId].SiteId ?? o.LandmarkId).ToArray();
        if (fingerprints.Matches.Count > 0)
        {
            foreach (var group in fingerprints.Matches.GroupBy(p => p.CountryCode).OrderByDescending(g => g.Sum(p => p.RelativeScore)).Take(4))
            {
                var best = group.MaxBy(p => p.RelativeScore)!;
                hypotheses.Add(new("country:" + group.Key, "country", group.Key, group.Key, best.Position,
                    group.Sum(p => p.RelativeScore), 1000, best.MatchedSites, "empirical-relative-pattern"));
            }
            foreach (var group in fingerprints.Matches.GroupBy(p => (p.CountryCode, p.Region, p.Locality)).OrderByDescending(g => g.Sum(p => p.RelativeScore)).Take(8))
            {
                var best = group.MaxBy(p => p.RelativeScore)!;
                double spread = group.Max(p => Geography.DistanceKm(best.Position, p.Position) + p.UncertaintyKm);
                hypotheses.Add(new("profile:" + best.ProfileId, "local-area", string.Join(" · ", new[] { best.Locality, best.Region }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    best.CountryCode, best.Position, group.Sum(p => p.RelativeScore), Math.Max(25, spread), best.MatchedSites, "empirical-relative-pattern"));
            }
        }
        else if (measured.Length > 0)
        {
            // Fastest-three per country avoids giving a dense country a vote for every colocated anchor.
            var groups = measured.Where(o => !string.IsNullOrWhiteSpace(known[o.LandmarkId].CountryCode))
                .GroupBy(o => known[o.LandmarkId].CountryCode!).Select(g =>
                {
                    var first = g.Take(3).ToArray(); var best = known[first[0].LandmarkId];
                    double representative = RobustStatistics.Median(first.Select(o => o.MinimumRttMs!.Value));
                    return (Country: g.Key, Best: best, Latency: representative, Count: g.Count());
                }).OrderBy(x => x.Latency).Take(5).ToArray();
            double baseline = groups.Length > 0 ? groups[0].Latency : 0;
            var weights = groups.Select(g => Math.Exp(-Math.Min(50, (g.Latency - baseline) / Math.Max(3, baseline * .4)))).ToArray();
            double total = weights.Sum();
            for (int i = 0; i < groups.Length; i++)
            {
                var g = groups[i];
                hypotheses.Add(new("country:" + g.Country, "country", g.Country, g.Country, g.Best.Position,
                    weights[i] / total, 1200, g.Count, "routing-affinity-not-country-proof"));
            }
        }
        if (fingerprints.Matches.Count == 0 && routeLocation is { Status: "route-informed-affinity" })
        {
            // Prefer explicit measured regional alternatives to a city near the geometric CBG centroid.
            foreach (var zone in routeLocation.Zones.Where(z => !string.IsNullOrWhiteSpace(z.Region)).Take(8))
                hypotheses.Add(new("topology:" + zone.CountryCode + ":" + zone.Region, "regional-area", zone.Label,
                    zone.CountryCode, zone.Position, zone.RelativeScore, 300, zone.Sites, "route-conditioned-affinity-not-device-location"));
        }
        var fit = update.Localities?.Fit;
        if (fit is not null && fingerprints.Matches.Count == 0 && routeLocation?.Status != "route-informed-affinity")
        {
            var nearby = LocalityCatalog.Default.Near(fit.Position, 8, Math.Max(50, Math.Min(500, fit.ModelSpreadKm)));
            // The geographic fit only guides probing. It is deliberately not assigned confidence.
            foreach (var countryGroup in nearby.GroupBy(n => n.Place.CountryCode).Take(3))
            {
                var first = countryGroup.First();
                hypotheses.Add(new("fit:" + countryGroup.Key, "regional-area", string.IsNullOrWhiteSpace(first.Place.Region) ? first.Place.Name : first.Place.Region, countryGroup.Key,
                    fit.Position, .5, Math.Max(50, fit.ModelSpreadKm), fit.IndependentSites, "uncalibrated-network-model"));
            }
        }
        var focus = hypotheses.Where(h => h.Level != "country").OrderByDescending(h => h.RelativeScore).Take(4).ToArray();
        var places = focus.SelectMany(h => LocalityCatalog.Default.Near(h.Position, 6, Math.Min(500, h.SearchRadiusKm), h.CountryCode))
            .Select(p => p.Place).Where(p => p.FeatureCode != "ANCHOR_CITY_LABEL").DistinctBy(p => p.Id).Take(24).ToArray();
        string stage = measured.Length == 0 ? "world-exploration" : focus.Length > 0 ?
            fingerprints.Matches.Count > 0 ? "local-area-candidates" : "regional-candidates" : "country-candidates";
        return new(stage, hypotheses.ToArray(), places,
            "Alternatives remain open. These are search hypotheses, not confirmed borders, municipality identifications or calibrated probabilities. Nearby place names do not imply a probe exists there.");
    }

    public static List<Landmark> Next(Catalog catalog, MeasurementSession session, LocationUpdate update,
        int count, int round, FingerprintDatabase? profiles = null, uint? asn = null)
    {
        var eligible = catalog.Landmarks.Where(a => a.Kind == "anchor" && a.ProbeEligible)
            .OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
        var seen = session.Observations.Select(o => o.LandmarkId).ToHashSet(StringComparer.Ordinal);
        var observedSites = eligible.Where(a => seen.Contains(a.Id)).Select(a => a.SiteId ?? a.Id).ToHashSet(StringComparer.Ordinal);
        var fresh = eligible.Where(a => !seen.Contains(a.Id) && !observedSites.Contains(a.SiteId ?? a.Id)).ToArray();
        var result = new List<Landmark>(); var sites = new HashSet<string>(StringComparer.Ordinal);
        var hypotheses = update.Enhanced?.Hierarchy.Hypotheses ?? [];
        var referencePositions = eligible.Where(a => seen.Contains(a.Id)).Select(a => a.Position).ToList();
        void Add(Landmark a) { if (result.Count < count && sites.Add(a.SiteId ?? a.Id)) result.Add(a); }
        if (round >= 2)
        {
            var indexed = eligible.ToDictionary(a => a.Id, StringComparer.Ordinal);
            foreach (var observation in session.Observations.Where(o => o.MinimumRttMs.HasValue && indexed.ContainsKey(o.LandmarkId))
                .OrderBy(o => o.MinimumRttMs).DistinctBy(o => indexed[o.LandmarkId].SiteId ?? o.LandmarkId).Take(2))
                Add(indexed[observation.LandmarkId]);
        }
        if (round > 0)
        {
            // Audit every available administrative region of the leading country before
            // repeatedly adding its fastest city. A dense Madrid deployment gets no extra votes.
            var country = hypotheses.Where(h => h.Level == "country").MaxBy(h => h.RelativeScore)?.CountryCode;
            var regional = fresh.Where(a => a.CountryCode == country && !string.IsNullOrWhiteSpace(a.RegionName))
                .GroupBy(a => a.RegionName).OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var group in regional)
            {
                if (eligible.Any(a => a.CountryCode == country && a.RegionName == group.Key && observedSites.Contains(a.SiteId ?? a.Id))) continue;
                Add(group.OrderBy(a => a.Id, StringComparer.Ordinal).First());
            }
            // Topology selects new measurements near several competing areas and distinct ASes, never only the leading area.
            foreach (var zone in (update.Enhanced?.RouteLocation?.Zones ?? []).Where(z => z.CountryCode == country).Take(4))
            {
                var branchAsns = eligible.Where(a => session.Observations.Any(o => o.LandmarkId == a.Id && o.Route.Count > 0)).Select(a => a.Asn).ToHashSet();
                var pick = fresh.Where(a => a.CountryCode == zone.CountryCode && a.RegionName == zone.Region && !sites.Contains(a.SiteId ?? a.Id))
                    .OrderBy(a => a.Asn.HasValue && !branchAsns.Contains(a.Asn) ? 0 : 1).ThenBy(a => Geography.DistanceKm(a.Position, zone.Position)).FirstOrDefault();
                if (pick is not null) Add(pick);
            }
        }
        // Farthest-first global coverage is independent of the current "winning" country.
        int exploration = round == 0 ? count : Math.Max(2, count / 8);
        for (int i = 0; i < exploration; i++)
        {
            var candidates = fresh.Where(a => !sites.Contains(a.SiteId ?? a.Id)).ToArray();
            if (candidates.Length == 0) break;
            Landmark pick = referencePositions.Count == 0
                ? candidates.OrderBy(a => a.CountryCode == "ES" ? 0 : 1).ThenBy(a => a.Id, StringComparer.Ordinal).First()
                : candidates.MaxBy(a => referencePositions.Min(p => Geography.DistanceKm(p, a.Position)))!;
            Add(pick); referencePositions.Add(pick.Position);
        }
        if (round > 0)
        {
            // Spend most follow-up probes on independent sites near several fast observations,
            // rather than allowing intercontinental distance variance to consume the whole budget.
            var byIdLocal = eligible.ToDictionary(a => a.Id);
            var fast = session.Observations.Where(o => o.MinimumRttMs.HasValue && byIdLocal.ContainsKey(o.LandmarkId))
                .OrderBy(o => o.MinimumRttMs).Select(o => byIdLocal[o.LandmarkId]).DistinctBy(a => a.SiteId ?? a.Id).Take(4).ToArray();
            if (fast.Length > 0)
                foreach (var a in fresh.Where(a => !sites.Contains(a.SiteId ?? a.Id))
                    .OrderBy(a => fast.Min(f => Geography.DistanceKm(f.Position, a.Position))))
                {
                    if (result.Count >= count - Math.Max(2, count / 4)) break;
                    Add(a);
                }
        }
        // Empirical profile variance is preferred; geometry is only a fallback scheduling proxy.
        double Information(Landmark anchor)
        {
            var empirical = profiles?.Profiles.Where(p => p.AddressFamily == session.AddressFamily && p.GroundTruthConsented &&
                (!asn.HasValue || !p.Asn.HasValue || p.Asn == asn))
                .Select(p => p.Features.FirstOrDefault(f => f.SiteId == (anchor.SiteId ?? anchor.Id)))
                .Where(f => f is not null).Take(128).Select(f => f!).ToArray() ?? [];
            if (empirical.Length >= 3)
            {
                double mean = empirical.Average(f => f.MinimumMs);
                return empirical.Average(f => Math.Pow(f.MinimumMs - mean, 2)) / (1 + empirical.Average(f => f.MadMs * f.MadMs));
            }
            var predictions = hypotheses.Select(h => Geography.DistanceKm(h.Position, anchor.Position) / 100).ToArray();
            double discrimination = predictions.Length < 2 ? 0 : predictions.Max() - predictions.Min();
            double local = hypotheses.Count == 0 ? 0 : hypotheses.Max(h => h.RelativeScore / (1 + Geography.DistanceKm(h.Position, anchor.Position) / Math.Max(50, h.SearchRadiusKm)));
            double same = asn.HasValue && anchor.Asn == asn ? .2 : 0;
            return discrimination + 10 * local + same;
        }
        // Keep one targeted slot for each leading branch before spending remaining slots on information gain.
        foreach (var h in hypotheses.OrderByDescending(h => h.RelativeScore).Take(3))
        {
            var pick = fresh.Where(a => !sites.Contains(a.SiteId ?? a.Id)).MinBy(a => Geography.DistanceKm(h.Position, a.Position));
            if (pick is not null) Add(pick);
        }
        foreach (var a in fresh.OrderByDescending(Information))
        {
            if (result.Count >= Math.Max(exploration, count - 2)) break;
            if (a.Asn.HasValue && result.Count(x => x.Asn == a.Asn) >= Math.Max(2, count / 3)) continue;
            Add(a);
        }
        // Reuse the existing information planner instead of maintaining a disconnected second path.
        if (result.Count < count && eligible.Length > 0)
        {
            var hints = InformationLandmarkPlanner.Next(catalog, session, update, Math.Clamp(count, 6, 64), round + 1, regional: true);
            var indexed = eligible.ToDictionary(a => a.Id, StringComparer.Ordinal);
            foreach (var hint in hints) if (indexed.TryGetValue(hint.LandmarkId, out var anchor)) Add(anchor);
        }
        // Repeat discriminating / low latency destinations when fresh measurements are exhausted.
        var byId = eligible.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var o in session.Observations.Where(o => byId.ContainsKey(o.LandmarkId)).OrderBy(o => o.MinimumRttMs ?? double.MaxValue)) Add(byId[o.LandmarkId]);
        foreach (var a in fresh) Add(a);
        return result;
    }
}
