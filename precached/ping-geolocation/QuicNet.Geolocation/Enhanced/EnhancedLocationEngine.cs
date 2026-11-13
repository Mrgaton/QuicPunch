using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace QuicNet.Geolocation;

/// <summary>Default on-device orchestrator. No external APIs, DNS, public-IP discovery or tunnel bypass.</summary>
public sealed class EnhancedLocationEngine(EmbeddedCatalog? embedded = null, IEchoTransport? echo = null,
    ILocalRouteInspector? routeInspector = null, ITraceProvider? traceProvider = null, IPayloadEchoTransport? payloadEcho = null)
{
    private readonly EmbeddedCatalog _data = embedded ?? EmbeddedCatalog.Default;
    private readonly IEchoTransport _echo = echo ?? new IcmpTransport(measureElapsed: true);
    private readonly ILocalRouteInspector _routeInspector = routeInspector ?? new LocalRouteInspector();

    public async Task<AnalysisResult> RunAsync(LocateRequest request, LocationUpdate initial,
        Action<LocationUpdate> publish, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(publish);
        ProgressiveLocator.Validate(request);
        var session = new MeasurementSession("this-device", Guid.NewGuid().ToString("N"), request.IPv6 ? "ipv6" : "ipv4", [])
        { TargetAddress = request.PublicAddress?.ToString() };
        if (!request.EnableNetwork) return new(initial, session, new([]));
        if (request.FingerprintProfiles is { } training) NetworkFingerprintEstimator.Validate(training);
        request.AdditionalCatalog?.Validate();
        var metadata = new OfflinePrefixCatalog(request.Metadata ?? new([]));
        var family = request.IPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        var anchors = _data.Anchors.Concat(request.AdditionalCatalog?.Landmarks ?? [])
            .Where(a => a.Kind == "anchor" && a.ProbeEligible && IPAddress.TryParse(a.Address, out var ip) &&
                EmbeddedCatalog.IsPublic(ip) && ip.AddressFamily == family)
            // An explicit application catalog may replace the same stable identifier.
            .GroupBy(a => a.Id, StringComparer.Ordinal).Select(g => g.Last())
            .GroupBy(a => a.Address, StringComparer.Ordinal).Select(g => EmbeddedDelayCalibration.Attach(g.First())).ToList();
        var catalog = new Catalog(anchors); catalog.Validate();
        var byId = anchors.ToDictionary(a => a.Id, StringComparer.Ordinal);
        uint? asn = request.NetworkAsn ?? initial.Asn ?? (request.PublicAddress is null ? null : metadata.Lookup(request.PublicAddress)?.Asn);
        var routes = new Dictionary<string, RouteAssessment>(StringComparer.Ordinal);
        var mtu = new List<PathMtuResult>(); var supplemental = new List<SupplementalObservation>();
        var observationIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var traced = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var model = new EstimatorOptions { ResolutionKm = request.ResolutionKm, MaxCells = request.MaxCells,
            TimingSlackMs = request.TimingSlackMs, PropagationKmPerMs = request.PropagationKmPerMs };
        model.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(request.Budget);
        using var limiter = new ProbeBudget(request.PacketsPerSecond, request.MaximumProbes);
        var ct = timeout.Token; var watch = Stopwatch.StartNew(); long lastEmit = -10000;
        var previous = initial with { Asn = asn };
        var topology = new AccessTopology([], [], 0, "No completed traceroutes yet.");
        bool routeChanged = false;
        var tracer = traceProvider ?? new RepeatedIcmpTraceProvider(_echo, request.TraceRepetitions);
        var pmtu = new PathMtuProbe(limiter, payloadEcho);
        var tcp = new TcpLatencyProbe(limiter);

        void ObserveRoute(string address)
        {
            if (!request.EnableTunnelAssessment) return;
            var current = _routeInspector.Inspect(address);
            if (routes.TryGetValue(address, out var old) && old.Error is null && current.Error is null &&
                old.InterfaceId is not null && current.InterfaceId is not null &&
                (old.InterfaceId != current.InterfaceId || old.SourceAddress != current.SourceAddress))
            {
                routeChanged = true;
                warnings.Add("The source address or selected interface changed during the session. Old and new measurements are not pooled; start a fresh run.");
            }
            routes[address] = current;
        }
        void Store(Landmark anchor, ProbeSample sample)
        {
            if (observationIndex.TryGetValue(anchor.Id, out int index))
            {
                var old = session.Observations[index];
                // Retain the lowest RTT and recent variability while bounding the public model's 100-sample limit.
                var samples = old.Samples.Append(sample).ToList();
                if (samples.Count > 100)
                {
                    var minimum = samples.Where(s => s.Status == "Success" && s.RttMs.HasValue).MinBy(s => s.RttMs);
                    samples = samples.TakeLast(99).ToList(); if (minimum is not null) samples.Insert(0, minimum);
                }
                session.Observations[index] = old with { Samples = samples, MeasuredUtc = DateTimeOffset.UtcNow };
            }
            else
            {
                observationIndex.Add(anchor.Id, session.Observations.Count);
                session.Observations.Add(new(anchor.Id, anchor.Address, DateTimeOffset.UtcNow, [sample]));
            }
        }
        LocationUpdate Emit(string phase, bool final = false, bool force = false)
        {
            if (!force && !final && phase == previous.Phase && watch.ElapsedMilliseconds - lastEmit < request.ProgressIntervalMs) return previous;
            lastEmit = watch.ElapsedMilliseconds;
            var reconciled = EvidenceReconciler.Estimate(catalog, session, model);
            var e = reconciled.Estimate;
            var region = new RegionSnapshot(e.Status, e.Representative, e.CoveringRadiusKm, e.CandidateAreaKm2,
                e.Cells.ToArray(), e.Constraints.ToArray());
            topology = AccessTopologyAnalyzer.Analyze(session, _data);
            var routers = topology.SharedRouters.Where(r => r.Published is not null && r.MinimumRttMs.HasValue)
                .Select(r => new RouterEvidence(r.Address, r.Published!, r.MinimumRttMs!.Value, r.MinimumTtl, r.Paths)
                    { LandmarkId = "published-router:" + r.Address }).ToArray();
            foreach (var router in routers)
                if (catalog.Landmarks.All(a => a.Id != router.LandmarkId))
                    catalog.Landmarks.Add(new(router.LandmarkId!, router.Address, router.Published.Position, router.Published.Source)
                    { Kind = "router", CoordinateEvidence = "published-infrastructure", Name = router.Published.City,
                        CountryCode = router.Published.Country, RegionName = router.Published.Region, UncertaintyKm = 100 });
            var tunnel = TunnelDetector.Assess(routes.Values, mtu,
                request.PublicAddress is not null && metadata.Lookup(request.PublicAddress)?.Hosting == true);
            var fingerprint = routeChanged || tunnel.MixedRoutes == true
                ? new FingerprintAssessment("mixed-network-context", [], "Fingerprint comparison is suspended for changing or mixed tunnel/direct routes.")
                : NetworkFingerprintEstimator.Estimate(catalog, session, request.FingerprintProfiles, asn, request.AccessTechnology, topology);
            var routeLocation = routeChanged || tunnel.MixedRoutes == true
                ? new TopologyLocationAssessment("mixed-network-context", [], [], 0, 0, ["La interfaz o el origen cambió; se suspende el ranking topológico."])
                : TopologyLocationEstimator.Evaluate(catalog, session, topology);
            var raw = session.Observations.Where(o => o.MinimumRttMs.HasValue).OrderBy(o => o.MinimumRttMs)
                .DistinctBy(o => byId[o.LandmarkId].SiteId ?? o.LandmarkId).Take(16)
                .Select(o => new NetworkCandidate(byId[o.LandmarkId].Position, byId[o.LandmarkId].Name ?? o.LandmarkId,
                    byId[o.LandmarkId].Source, 1 / Math.Pow(Math.Max(1, o.MinimumRttMs!.Value), 2), o.MinimumRttMs.Value)).ToArray();
            double weight = raw.Sum(r => r.RelativeScore);
            var candidates = raw.Select(r => r with { RelativeScore = r.RelativeScore / weight }).ToArray();
            var notes = e.Warnings.Concat(warnings).Concat(new[]
            {
                "Only measured RTTs at usable references create physical bounds. Relative rankings and fingerprints do not create certainty.",
                "The launch counter includes ICMP, traceroute, PMTU and TCP attempts, not every packet transmitted by the OS.",
                "Router positions are soft infrastructure evidence. Shared routes do not identify a residence or municipality.",
                "A tunnel can make affinity describe its exit. No route is changed or bypassed to find the underlying connection.",
                "A gazetteer place without a verified network landmark cannot be pinged as a municipality."
            }).ToArray();
            previous = new(previous.Revision + 1, e.Status == "inconsistent" ? EvidenceLevel.Conflicting :
                e.Constraints.Count > 0 ? EvidenceLevel.LatencyRegion : initial.Level,
                phase, final, initial.DatabasePrior, asn, region, ProgressiveLocator.Rank(catalog, session, routers), routers, session.Observations.Count, limiter.Sent,
                routeChanged ? "Network route changed; partial results retained without mixing new measurements." :
                final ? "Session finished; unresolved geographic alternatives are retained." : "New measurements update the geographic search hypotheses.", notes)
            { Diagnostics = reconciled.Diagnostics, Support = SpatialSupportEstimator.Create(region, routers, model, reconciled.Diagnostics) };
            previous = previous with { Localities = LocalityAssessment.Create(previous, catalog, session),
                RegionalRefinement = DeclaredLandmarkRefiner.Evaluate(catalog, session, model) };
            if (region.Constraints.Count == 0 && previous.RegionalRefinement is { Region.Status: "candidate-region" } conditional)
            {
                // Evaluate declared coordinates ONLY in the explicitly conditional layer. The primary region stays unchanged.
                var declared = catalog with { Landmarks = catalog.Landmarks.Select(a =>
                    a.CoordinateEvidence == "operator-declared" ? a with { LocationVerified = true } : a).ToList() };
                var local = conditional.CalibratedRegion is { Status: "candidate-region", Representative: not null, CoveringRadiusKm: not null }
                    ? LocalityAssessment.FromCalibration(conditional)
                    : LocalityAssessment.Create(previous with { Region = conditional.Region }, declared, session);
                previous = previous with { Localities = local with
                {
                    Evidence = "CONDITIONAL operator coordinates · " + local.Evidence,
                    Limitations = local.Limitations.Concat(new[] { conditional.Explanation }).ToArray()
                } };
            }
            var hierarchy = HierarchicalLocationPlanner.Assess(catalog, session, previous, fingerprint, routeLocation);
            var measurements = session.Observations.Select(o =>
            {
                var anchor = byId[o.LandmarkId];
                var samples = o.Samples.Where(s => s.Status == "Success" && s.RttMs is >= 0 && double.IsFinite(s.RttMs.Value))
                    .Select(s => s.RttMs!.Value).Order().ToArray();
                return new LandmarkMeasurement(anchor.Id, anchor.Address, anchor.SiteId ?? anchor.Id, anchor.Name ?? anchor.Id,
                    anchor.CountryCode ?? "", anchor.Position, samples.Length > 0 ? samples[0] : null,
                    samples.Length > 0 ? RobustStatistics.Median(samples) : null, samples.Length, o.Samples.Count,
                    anchor.LocationVerified, anchor.Source);
            }).OrderBy(m => m.MinimumMs ?? double.PositiveInfinity).ToArray();
            previous = previous with { Enhanced = new(hierarchy, fingerprint, topology, tunnel, supplemental.ToArray())
                { Measurements = measurements, RouteLocation = routeLocation } };
            publish(previous); return previous;
        }
        async Task<(Landmark Anchor, ProbeSample? Sample)> SampleAsync(Landmark anchor)
        {
            try
            {
                await limiter.AcquireAsync(ct).ConfigureAwait(false);
                var reply = await _echo.SendAsync(IPAddress.Parse(anchor.Address), 128, request.TimeoutMs, ct).ConfigureAwait(false);
                return (anchor, new ProbeSample(reply.RttMs, reply.Status) { TimingMethod = reply.TimingMethod });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (anchor, null); }
            catch (ProbeBudgetExceededException) { return (anchor, null); }
            catch (Exception ex) when (ex is IOException or SocketException or System.Net.NetworkInformation.PingException)
            { return (anchor, new(null, "Error:" + ex.GetType().Name)); }
        }
        string finalPhase = "complete";
        try
        {
            if (anchors.Count == 0) { warnings.Add("No public, probe-eligible references for this address family. Supply a catalog or rebuild the embedded snapshot."); finalPhase = "no-landmarks"; }
            for (int round = 0; round < request.Rounds && anchors.Count > 0; round++)
            {
                ct.ThrowIfCancellationRequested();
                if (limiter.Sent >= request.MaximumProbes) { finalPhase = "probe-budget-exhausted"; break; }
                // Presets share the same bootstrap; larger budgets extend the same exploration.
                var batch = HierarchicalLocationPlanner.Next(catalog, session, previous, round == 0 ? Math.Min(24, request.BatchSize) : request.BatchSize, round, request.FingerprintProfiles, asn);
                if (batch.Count == 0) break;
                foreach (var anchor in batch) ObserveRoute(anchor.Address);
                if (routeChanged) { finalPhase = "network-context-changed"; break; }
                // Round-robin samples reduce temporal bias. Consume completed tasks one-by-one rather than losing a cancelled batch.
                int repetitions = round == 0 ? Math.Min(2, request.Samples) : request.Samples;
                for (int sample = 0; sample < repetitions; sample++)
                {
                    foreach (var chunk in batch.Chunk(request.Parallelism))
                    {
                        ct.ThrowIfCancellationRequested();
                        var pending = chunk.Select(SampleAsync).ToList();
                        // Publish only after draining this bounded chunk, so callback exceptions cannot orphan active sends.
                        while (pending.Count > 0)
                        {
                            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                            pending.Remove(finished);
                            var result = await finished.ConfigureAwait(false);
                            if (result.Sample is not null) Store(result.Anchor, result.Sample);
                        }
                        Emit("measuring-round-" + (round + 1));
                        if (limiter.Sent >= request.MaximumProbes) break;
                    }
                    if (limiter.Sent >= request.MaximumProbes) break;
                }
                Emit("refining-round-" + (round + 1), force: true);
                var best = session.Observations.Where(o => o.MinimumRttMs.HasValue).OrderBy(o => o.MinimumRttMs)
                    .DistinctBy(o => byId[o.LandmarkId].SiteId ?? o.LandmarkId).Take(request.TraceCount).ToArray();
                if (request.EnableTopology && traced.Count < request.MaximumTraceRoutes)
                {
                    var planned = TopologyTracePlanner.Next(catalog, session, topology, traced,
                        Math.Min(request.TraceCount, request.MaximumTraceRoutes - traced.Count));
                    foreach (var chunk in planned.Chunk(Math.Min(3, request.Parallelism)))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (limiter.Sent >= request.MaximumProbes) break;
                        foreach (var observation in chunk) ObserveRoute(observation.Address);
                        if (routeChanged) break;
                        var pending = chunk.Select(async observation => (Observation: observation,
                            Route: await tracer.TraceAsync(observation.Address, request.MaxHops, request.TimeoutMs, limiter, ct).ConfigureAwait(false))).ToList();
                        // Drain all active traces before publishing/cancelling, preserving serial callbacks and completed partial routes.
                        var completed = await Task.WhenAll(pending).ConfigureAwait(false);
                        foreach (var item in completed)
                        {
                            int idx = observationIndex[item.Observation.LandmarkId];
                            session.Observations[idx] = session.Observations[idx] with { Route = item.Route.ToList() };
                            traced.Add(item.Observation.LandmarkId);
                            ObserveRoute(item.Observation.Address);
                        }
                        Emit("topology", force: true);
                        if (routeChanged) break;
                    }
                }
                // PMTU and optional TCP have bounded scope and use the same shared launch budget.
                if (request.EnablePathMtu && round == 0)
                    foreach (var observation in best.Take(3))
                    {
                        ct.ThrowIfCancellationRequested(); if (limiter.Sent >= request.MaximumProbes) break;
                        routes.TryGetValue(observation.Address, out var route);
                        mtu.Add(await pmtu.MeasureAsync(observation.Address, route?.Mtu, request.TimeoutMs, ct).ConfigureAwait(false));
                        Emit("path-mtu");
                    }
                if (request.EnableTcp && round == 0)
                    foreach (var observation in best.Where(o => byId[o.LandmarkId].TcpProbeAllowed).Take(3))
                    {
                        ct.ThrowIfCancellationRequested(); if (limiter.Sent >= request.MaximumProbes) break;
                        var anchor = byId[observation.LandmarkId];
                        supplemental.Add(await tcp.MeasureAsync(anchor, anchor.TcpPort, Math.Min(3, request.Samples), request.TimeoutMs, ct).ConfigureAwait(false));
                        Emit("tcp-supplement");
                    }
                if (routeChanged) { finalPhase = "network-context-changed"; break; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { finalPhase = cancellation.IsCancellationRequested ? "cancelled-partial" : "time-budget-exhausted"; }
        catch (ProbeBudgetExceededException) { finalPhase = "probe-budget-exhausted"; }
        if (limiter.Sent >= request.MaximumProbes && finalPhase == "complete") finalPhase = "probe-budget-exhausted";
        return new(Emit(finalPhase, final: true), session, catalog);
    }
}
