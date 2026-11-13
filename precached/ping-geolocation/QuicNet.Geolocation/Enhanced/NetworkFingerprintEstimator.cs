namespace QuicNet.Geolocation;

/// <summary>Robust nearest-profile matching. Scores express similarity, never geographic confidence.</summary>
public static class NetworkFingerprintEstimator
{
    public const string FeatureVersion = "common-sites-relative-rtt-v1";
    public static IReadOnlyDictionary<string, FingerprintFeature> Extract(Catalog catalog, MeasurementSession session)
    {
        var index = catalog.Landmarks.ToDictionary(a => a.Id, StringComparer.Ordinal);
        return session.Observations.Where(o => index.ContainsKey(o.LandmarkId))
            .GroupBy(o => index[o.LandmarkId].SiteId ?? o.LandmarkId, StringComparer.Ordinal)
            .Select(g => (Site: g.Key, Stats: RobustStatistics.Summarize(g.SelectMany(o => o.Samples)
                .Where(s => s.TimingMethod == "elapsed-upper-v1"))))
            .Where(x => x.Stats.Successes >= 2)
            .ToDictionary(x => x.Site, x => new FingerprintFeature(x.Site, x.Stats.MinimumMs!.Value,
                x.Stats.MedianMs!.Value, x.Stats.MadMs!.Value, x.Stats.Successes), StringComparer.Ordinal);
    }

    public static void Validate(FingerprintDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.SchemaVersion != 1 || database.FeatureVersion != FeatureVersion || database.Profiles is null || database.Profiles.Count > 50000)
            throw new InvalidDataException("Unsupported fingerprint database or excessive profile count.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in database.Profiles)
        {
            p.Position.Validate();
            if (string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id) || p.Features is null || p.Features.Count is < 4 or > 20000 ||
                p.AddressFamily is not ("ipv4" or "ipv6") || p.Protocol != "icmp" || p.TimingMethod != "elapsed-upper-v1" ||
                !p.GroundTruthConsented || !double.IsFinite(p.GroundTruthUncertaintyKm) || p.GroundTruthUncertaintyKm is < 0 or > 20100)
                throw new InvalidDataException("Each profile requires consented ground truth, a unique ID, and compatible ICMP measurements.");
            var sites = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in p.Features)
                if (string.IsNullOrWhiteSpace(f.SiteId) || !sites.Add(f.SiteId) || f.Samples < 2 ||
                    !double.IsFinite(f.MinimumMs) || !double.IsFinite(f.MedianMs) || !double.IsFinite(f.MadMs) ||
                    f.MinimumMs < 0 || f.MedianMs < f.MinimumMs || f.MadMs < 0)
                    throw new InvalidDataException("Invalid or duplicate fingerprint feature.");
        }
    }

    public static FingerprintAssessment Estimate(Catalog catalog, MeasurementSession session, FingerprintDatabase? database,
        uint? asn = null, string accessTechnology = "unknown", AccessTopology? topology = null)
    {
        if (database is null || database.Profiles.Count == 0)
            return new("no-trained-data", [], "No empirical profiles were supplied. Geographic candidates remain uncalibrated heuristics.");
        var current = Extract(catalog, session);
        if (current.Count < 4) return new("insufficient-overlap", [], "At least four independent sites with repeated compatible timing samples are required.");
        var observedRouters = (topology?.SharedRouters ?? []).Select(r => r.Address).ToHashSet(StringComparer.Ordinal);
        var matches = new List<FingerprintMatch>();
        foreach (var profile in database.Profiles.Where(p => p.AddressFamily == session.AddressFamily && p.GroundTruthConsented &&
            p.Protocol == "icmp" && p.TimingMethod == "elapsed-upper-v1" &&
            p.CapturedUtc <= DateTimeOffset.UtcNow.AddMinutes(5) && p.CapturedUtc >= DateTimeOffset.UtcNow.AddDays(-180)))
        {
            var pairs = profile.Features.Where(f => current.ContainsKey(f.SiteId)).Select(f => (Reference: f, Current: current[f.SiteId])).ToArray();
            if (pairs.Length < 4) continue;
            // Both baselines use exactly the same intersection: absent dimensions must not change the baseline.
            double baselineReference = pairs.Min(p => p.Reference.MinimumMs), baselineCurrent = pairs.Min(p => p.Current.MinimumMs);
            double loss = 0;
            foreach (var pair in pairs)
            {
                double noise = Math.Max(.2, 1.4826 * (pair.Reference.MadMs + pair.Current.MadMs));
                double delta = ((pair.Current.MinimumMs - baselineCurrent) - (pair.Reference.MinimumMs - baselineReference)) / noise;
                double z = Math.Abs(delta);
                loss += z <= 2 ? .5 * z * z : 2 * (z - 1);
            }
            loss /= pairs.Length;
            int comparisons = 0, inversions = 0;
            for (int i = 0; i < Math.Min(32, pairs.Length); i++) for (int j = i + 1; j < Math.Min(32, pairs.Length); j++)
            {
                double observed = pairs[i].Current.MinimumMs - pairs[j].Current.MinimumMs;
                double reference = pairs[i].Reference.MinimumMs - pairs[j].Reference.MinimumMs;
                double noise = Math.Max(.2, pairs[i].Current.MadMs + pairs[j].Current.MadMs + pairs[i].Reference.MadMs + pairs[j].Reference.MadMs);
                if (Math.Abs(observed) <= noise || Math.Abs(reference) <= noise) continue;
                comparisons++; if (Math.Sign(observed) != Math.Sign(reference)) inversions++;
            }
            if (comparisons > 0) loss += 2.0 * inversions / comparisons;
            // Operator/access and topology are bounded soft hints, not a substitute for RTT agreement.
            bool sameAsn = asn.HasValue && profile.Asn == asn;
            if (asn.HasValue && profile.Asn.HasValue && !sameAsn) loss += .75;
            if (accessTechnology != "unknown" && profile.AccessTechnology != "unknown" && accessTechnology != profile.AccessTechnology) loss += .4;
            var referenceRouters = profile.AccessRouters.ToHashSet(StringComparer.Ordinal);
            if (referenceRouters.Count > 0 && observedRouters.Count > 0)
                loss += .25 * (1 - (double)referenceRouters.Intersect(observedRouters).Count() / referenceRouters.Union(observedRouters).Count());
            loss += 1.0 / Math.Sqrt(pairs.Length);
            matches.Add(new(profile.Id, profile.Position, profile.CountryCode, profile.Region, profile.Locality,
                0, loss, pairs.Length, sameAsn, Math.Max(5, profile.GroundTruthUncertaintyKm)));
        }
        // Repeated observations from a household/group must not multiply that location's weight.
        var groups = database.Profiles.ToDictionary(p => p.Id, p => p.DatasetGroup ?? p.Id, StringComparer.Ordinal);
        var selected = matches.OrderBy(m => m.Distance).DistinctBy(m => groups[m.ProfileId]).Take(12).ToArray();
        if (selected.Length == 0) return new("insufficient-overlap", [], "No recent compatible profile overlaps at four independent sites.");
        double best = selected[0].Distance;
        var weights = selected.Select(m => Math.Exp(-Math.Min(50, (m.Distance - best) / 2))).ToArray();
        double sum = weights.Sum();
        return new("relative-profile-matches", selected.Select((m, i) => m with { RelativeScore = weights[i] / sum }).ToArray(),
            "Relative RTT/ranking similarity; not calibrated probabilities. Coverage gaps, VPN exits and operator changes can cause wrong matches.");
    }

    public static NetworkFingerprint Capture(string id, Catalog catalog, MeasurementSession session, GeoPoint knownPosition,
        string country, string region, string locality, uint? asn, bool groundTruthConsented,
        double uncertaintyKm = 10, string accessTechnology = "unknown", string? datasetGroup = null)
    {
        if (!groundTruthConsented) throw new InvalidOperationException("Ground truth must be explicitly provided with consent; predicted positions are not training labels.");
        knownPosition.Validate();
        if (session.Synthetic || session.Observations.Count == 0)
            throw new InvalidOperationException("Synthetic or empty sessions cannot become empirical training profiles.");
        var capturedUtc = session.Observations.Max(o => o.MeasuredUtc);
        var profile = new NetworkFingerprint(id, knownPosition, country, region, locality, asn, session.AddressFamily, "icmp",
            capturedUtc, Extract(catalog, session).Values.ToList())
        {
            GroundTruthConsented = true, GroundTruthUncertaintyKm = uncertaintyKm,
            AccessTechnology = accessTechnology, DatasetGroup = datasetGroup,
            AccessRouters = AccessTopologyAnalyzer.Analyze(session).SharedRouters.Select(r => r.Address).ToArray()
        };
        Validate(new() { Profiles = [profile] });
        return profile;
    }
}
