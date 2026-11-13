using System.Net;
using System.Net.Sockets;
using QuicNet.Geolocation;

namespace QuicNet.Geolocation;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<EvidenceLevel>))]
public enum EvidenceLevel { Unknown, DatabasePrior, LatencyRegion, RouteCorroborated, Conflicting }
public sealed record LocateRequest
{
    /// <summary>Optional IP of THIS connection, supplied by the host application. Never remotely discovered.</summary>
    public IPAddress? PublicAddress { get; init; }
    public bool EnableNetwork { get; init; }
    public bool IPv6 { get; init; }
    public bool UseHierarchicalEngine { get; init; } = true;
    public bool EnableTunnelAssessment { get; init; } = true;
    public bool EnableTopology { get; init; } = true;
    public bool EnablePathMtu { get; init; }
    public bool EnableTcp { get; init; }
    public int MaximumProbes { get; init; } = 1500;
    public int TraceRepetitions { get; init; } = 2;
    public int MaximumTraceRoutes { get; init; } = 24;
    public int ProgressIntervalMs { get; init; } = 750;
    /// <summary>Optional locally supplied operator identity; never looked up by an external API.</summary>
    public uint? NetworkAsn { get; init; }
    public string AccessTechnology { get; init; } = "unknown";
    public Catalog? AdditionalCatalog { get; init; }
    public FingerprintDatabase? FingerprintProfiles { get; init; }
    public OfflineMetadata? Metadata { get; init; }
    public int Rounds { get; init; } = 3;
    public int BatchSize { get; init; } = 12;
    public int Samples { get; init; } = 3;
    public int TimeoutMs { get; init; } = 700;
    public int MaxHops { get; init; } = 16;
    public int TraceCount { get; init; } = 3;
    public int PacketsPerSecond { get; init; } = 20;
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(90);
    public double ResolutionKm { get; init; } = 50;
    public int Parallelism { get; init; } = 6;
    public double TimingSlackMs { get; init; } = 1;
    public int MaxCells { get; init; } = 20_000;
    public double PropagationKmPerMs { get; init; } = 299.792458;
}
public sealed record RouterEvidence(string Address, PublishedLocation Published, double RttMs, int Ttl, int Paths)
{ public string? LandmarkId { get; init; } }
/// <summary>Relative ranking of network nodes, NOT probabilities of where the person lives.</summary>
public sealed record NetworkCandidate(GeoPoint Position, string Label, string Source, double RelativeScore, double MinimumRttMs);
public sealed record RegionSnapshot(string Status, GeoPoint? Representative, double? CoveringRadiusKm,
    double AreaKm2, IReadOnlyList<GeoCell> Cells, IReadOnlyList<DistanceConstraint> Constraints);
public sealed record LocationUpdate(int Revision, EvidenceLevel Level, string Phase, bool IsFinal,
    PublishedLocation? DatabasePrior, uint? Asn, RegionSnapshot? Region,
    IReadOnlyList<NetworkCandidate> Candidates, IReadOnlyList<RouterEvidence> Routers,
    int MeasuredLandmarks, int SentPackets, string Change, IReadOnlyList<string> Notes)
{
    public EvidenceDiagnostics? Diagnostics { get; init; }
    public SpatialSupport? Support { get; init; }
    public RegionalRefinement? RegionalRefinement { get; init; }
    public LocalityAssessment? Localities { get; init; }
    public EnhancedEvidence? Enhanced { get; init; }
}

public sealed class AnalysisRun : IDisposable
{
    private readonly CancellationTokenSource _stop;
    private int _disposed;
    internal AnalysisRun(LocationUpdate initial, CancellationTokenSource stop, Task<AnalysisResult> completion)
    { Initial = initial; _stop = stop; Completion = completion; }
    public LocationUpdate Initial { get; }
    public Task<AnalysisResult> Completion { get; }
    public void Cancel() { if (Volatile.Read(ref _disposed) == 0) _stop.Cancel(); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _ = Completion.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
public sealed record AnalysisResult(LocationUpdate Final, MeasurementSession Session, Catalog Catalog);

/// <summary>Offline lookup plus optional on-device network analysis. All callbacks are serial, synchronous actions.</summary>
public sealed class ProgressiveLocator(EmbeddedCatalog? catalogue = null, IEchoTransport? transport = null)
{
    private readonly EmbeddedCatalog _data = catalogue ?? EmbeddedCatalog.Default;

    public LocationUpdate LocateFast(IPAddress? publicAddress = null)
    {
        var prior = publicAddress is null ? null : _data.Lookup(publicAddress);
        var update = new LocationUpdate(0, prior is null ? EvidenceLevel.Unknown : EvidenceLevel.DatabasePrior, "offline", true,
            prior, publicAddress is null ? null : _data.LookupAsn(publicAddress), null, [], [], 0, 0,
            prior is null ? "No hay señales suficientes para situar esta conexión." : "Ubicación publicada de la IP; pendiente de verificar.",
            ["La base de geolocalización puede ubicar la red o la salida del operador en otra ciudad o país.",
             "Los niveles expresan el tipo de evidencia; no son porcentajes de acierto."]);
        return update with { Localities = LocalityAssessment.Create(update) };
    }

    /// <summary>Calls onUpdate(initial) before returning. Later callbacks run serially on a worker.
    /// Marshal to your UI explicitly. Callback exceptions fault Completion and stop the analysis.</summary>
    public AnalysisRun Start(LocateRequest request, Action<LocationUpdate> onUpdate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(onUpdate);
        Validate(request); ct.ThrowIfCancellationRequested();
        var initial = LocateFast(request.PublicAddress) with { IsFinal = !request.EnableNetwork };
        var supplied = request.PublicAddress is null || request.Metadata is null ? null : new OfflinePrefixCatalog(request.Metadata).Lookup(request.PublicAddress);
        if (supplied is not null)
            initial = initial with { DatabasePrior = supplied.Location ?? initial.DatabasePrior, Asn = request.NetworkAsn ?? supplied.Asn ?? initial.Asn,
                Level = supplied.Location is not null ? EvidenceLevel.DatabasePrior : initial.Level };
        else if (request.NetworkAsn.HasValue) initial = initial with { Asn = request.NetworkAsn };
        onUpdate(initial);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(() => Analyze(request, initial, onUpdate, stop.Token), CancellationToken.None);
        return new(initial, stop, task);
    }

    private async Task<AnalysisResult> Analyze(LocateRequest request, LocationUpdate initial,
        Action<LocationUpdate> publish, CancellationToken cancellation)
    {
        if (request.UseHierarchicalEngine)
            return await new EnhancedLocationEngine(_data, transport).RunAsync(request, initial, publish, cancellation).ConfigureAwait(false);
        var session = new MeasurementSession("this-device", Guid.NewGuid().ToString("N"), request.IPv6 ? "ipv6" : "ipv4", [])
        { TargetAddress = request.PublicAddress?.ToString() };
        if (!request.EnableNetwork) return new(initial, session, new([]));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(request.Budget);
        var ct = budget.Token;
        var anchors = _data.Anchors.Where(a => IPAddress.Parse(a.Address).AddressFamily ==
            (request.IPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork)).ToList();
        // The mutable catalogue also receives routers. Keep the scheduler's anchor list separate.
        var catalog = new Catalog(anchors.ToList());
        var options = new EstimatorOptions
        {
            ResolutionKm = request.ResolutionKm,
            TimingSlackMs = request.TimingSlackMs,
            MaxCells = request.MaxCells,
            PropagationKmPerMs = request.PropagationKmPerMs
        };
        var echoOptions = new ProbeOptions
        {
            Samples = request.Samples,
            TimeoutMs = request.TimeoutMs,
            PacketsPerSecond = request.PacketsPerSecond,
            MaxHops = request.MaxHops
        };
        using var sharedLaunches = new ProbeBudget(request.PacketsPerSecond, request.MaximumProbes);
        using var probe = new NetworkProbe(echoOptions, transport, sharedLaunches);
        using var trace = new NetworkProbe(echoOptions with { Samples = 1, Trace = true }, transport, sharedLaunches);
        using var bootstrap = new NetworkProbe(echoOptions with { Samples = Math.Min(2, request.Samples) }, transport, sharedLaunches);
        using var bootstrapTrace = new NetworkProbe(echoOptions with { Samples = 1, Trace = true, MaxHops = Math.Min(12, request.MaxHops) }, transport, sharedLaunches);
        LocationEstimate? estimate = null;
        var previous = initial;
        var routers = new Dictionary<string, RouterEvidence>(StringComparer.Ordinal);
        bool topologyAttempted = false;
        string topologyStatus = "Pendiente de analizar rutas.";
        LocationUpdate Emit(string phase, bool final, string? extra = null)
        {
            var reconciled = EvidenceReconciler.Estimate(catalog, session, options);
            estimate = reconciled.Estimate;
            var snapshot = Snapshot(estimate);
            bool conflicting = estimate.Status == "inconsistent";
            bool changed = previous.Region is null || previous.Region.AreaKm2 != snapshot.AreaKm2 || previous.Level == EvidenceLevel.DatabasePrior;
            string change = conflicting ? "Evidencias incompatibles; se retira la localización." :
                previous.Region is { } old && snapshot.AreaKm2 > old.AreaKm2 ? "La zona se amplía al revisar la evidencia." :
                changed ? "Nueva zona compatible con la evidencia disponible." : "No se ha podido reducir más la zona.";
            var level = conflicting ? EvidenceLevel.Conflicting : estimate.Constraints.Count == 0 ? initial.Level :
                EvidenceLevel.LatencyRegion;
            var notes = estimate.Warnings.Concat(new[] { topologyStatus,
                "Las coordenadas de referencias y routers son declaraciones de terceros, no posiciones certificadas.",
                "La ciudad de un router o de un candidato describe infraestructura; no identifica el municipio del dispositivo.",
                FormattableString.Invariant($"La cota usa {options.PropagationKmPerMs:F6} km/ms · (RTT+{options.TimingSlackMs:F3} ms)/2 más incertidumbre declarada. No se resta latencia de acceso ni se interpreta RTT alto como lejanía."),
                "Las rutas parten de este equipo: compartir prefijo no mide la distancia entre otro peer y una referencia." });
            if (extra is not null) notes = notes.Append(extra);
            if (initial.DatabasePrior is { } p && snapshot.Cells.Count > 0 && !snapshot.Cells.Any(c => Contains(c, p.Position)))
                notes = notes.Append("La ubicación publicada queda fuera de la zona medida; se conserva solo como dato histórico sin restringir el resultado.");
            previous = new(previous.Revision + 1, level, phase, final, initial.DatabasePrior, initial.Asn, snapshot,
                Rank(catalog, session, routers.Values), Array.AsReadOnly(routers.Values.OrderBy(r => r.RttMs).ToArray()),
                session.Observations.Count, probe.SentPackets + trace.SentPackets + bootstrap.SentPackets + bootstrapTrace.SentPackets, change, Array.AsReadOnly(notes.ToArray()))
            { Diagnostics = reconciled.Diagnostics, Support = SpatialSupportEstimator.Create(snapshot, routers.Values.ToArray(), options, reconciled.Diagnostics) };
            previous = previous with { Localities = LocalityAssessment.Create(previous, catalog, session) };
            publish(previous); return previous;
        }
        try
        {
            for (int round = 0; round < request.Rounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                // All presets share the same short initial exploration. Larger budgets extend it.
                int batchSize = round < 2 ? 6 : request.BatchSize;
                var focus = round >= 2 ? previous.Localities?.Fit : null;
                var batch = AdaptivePlanner.Next(catalog, session, estimate, batchSize, focus?.Position, focus?.ModelSpreadKm);
                if (round > 0 && routers.Count > 0)
                {
                    // Router cities guide exploration only. Probe actual anchors in several hinted cities,
                    // and keep half the batch for independent exploration of the primary region.
                    var hints = routers.Values.OrderBy(r => r.RttMs).DistinctBy(r => (Math.Round(r.Published.Position.Latitude), Math.Round(r.Published.Position.Longitude))).Take(3).ToArray();
                    var seen = session.Observations.Select(o => o.LandmarkId).ToHashSet();
                    var usedSites = anchors.Where(a => seen.Contains(a.Id)).Select(a => a.SiteId ?? a.Id).ToHashSet();
                    var local = anchors.Where(a => !seen.Contains(a.Id) && !usedSites.Contains(a.SiteId ?? a.Id)).OrderBy(a => hints.Min(h => Geography.DistanceKm(h.Published.Position, a.Position)))
                        .DistinctBy(a => a.SiteId ?? a.Id).Take(batchSize / 2);
                    batch = local.Concat(batch).DistinctBy(a => a.SiteId ?? a.Id).Take(batchSize).ToList();
                }
                if (round > 0 && initial.Asn is { } asn)
                {
                    // Same-AS is a scheduling hint only. Keep global exploration and all feasible branches.
                    var seen = session.Observations.Select(o => o.LandmarkId).ToHashSet();
                    var usedSites = anchors.Where(a => seen.Contains(a.Id)).Select(a => a.SiteId ?? a.Id).ToHashSet();
                    var same = anchors.Where(a => !seen.Contains(a.Id) && !usedSites.Contains(a.SiteId ?? a.Id) && _data.LookupAsn(IPAddress.Parse(a.Address)) == asn)
                        .OrderBy(a => estimate?.Representative is { } point ? Geography.DistanceKm(a.Position, point) : 0)
                        .Take(Math.Max(1, batchSize / 3)).ToArray();
                    batch = same.Concat(batch).DistinctBy(a => a.SiteId ?? a.Id).Take(batchSize).ToList();
                }
                if (round >= 2)
                {
                    // Detail extends the complete quick phase and refines its best native minima.
                    var known = anchors.ToDictionary(a => a.Id);
                    var best = session.Observations.Where(o => o.MinimumRttMs.HasValue).OrderBy(o => o.MinimumRttMs)
                        .DistinctBy(o => known[o.LandmarkId].SiteId ?? o.LandmarkId).Take(3).ToArray();
                    foreach (var old in best)
                    {
                        var more = await probe.MeasureAsync(old.LandmarkId, old.Address, ct).ConfigureAwait(false);
                        session.Observations[session.Observations.IndexOf(old)] = old with
                        { Samples = [.. old.Samples, .. more.Samples], MeasuredUtc = more.MeasuredUtc };
                    }
                }
                // Bounded concurrency; one shared probe enforces the launch rate across the batch.
                foreach (var chunk in batch.Chunk(request.Parallelism))
                    session.Observations.AddRange(await Task.WhenAll(chunk.Select(a => (round < 2 ? bootstrap : probe).MeasureAsync(a.Id, a.Address, ct))).ConfigureAwait(false));
                Emit(round == 0 ? "global-latency" : "regional-latency", false);
                if (!topologyAttempted || round == 2 && request.TraceCount > 2)
                {
                    topologyAttempted = true;
                    var byId = anchors.ToDictionary(a => a.Id);
                    var targets = session.Observations.Where(o => o.MinimumRttMs.HasValue).OrderBy(o => o.MinimumRttMs)
                        .Where(o => o.Route.Count == 0)
                        .DistinctBy(o => byId[o.LandmarkId].SiteId ?? o.LandmarkId).Take(round == 0 ? Math.Min(2, request.TraceCount) : request.TraceCount - Math.Min(2, request.TraceCount)).ToArray();
                    foreach (var measured in targets)
                    {
                        var observation = await (round == 0 ? bootstrapTrace : trace).MeasureAsync(measured.LandmarkId, measured.Address, ct).ConfigureAwait(false);
                        // Keep the original echo samples and append the actual trace, without mixing timing protocols.
                        int index = session.Observations.IndexOf(measured);
                        session.Observations[index] = measured with { Route = observation.Route };
                        foreach (var hop in observation.Route.Where(h => h.Status == "TtlExpired" && h.RttMs.HasValue)
                            .GroupBy(h => h.Address).Select(g => g.MinBy(h => h.RttMs)!))
                        {
                            if (!IPAddress.TryParse(hop.Address, out var ip) || _data.LookupRouter(ip) is not { } position) continue;
                            var address = ip.ToString();
                            int paths = routers.TryGetValue(address, out var old) ? old.Paths + 1 : 1;
                            var best = old is not null && old.RttMs < hop.RttMs ? old : new RouterEvidence(address, position, hop.RttMs!.Value, hop.Ttl, paths) { LandmarkId = "router-" + address };
                            routers[address] = best with { Paths = paths };
                            if (!catalog.Landmarks.Any(a => a.Address == address))
                                catalog.Landmarks.Add(new("router-" + address, address, position.Position, position.Source)
                                {
                                    Kind = "router",
                                    SiteId = FormattableString.Invariant($"router-cell-{Math.Round(position.Position.Latitude, 1)}:{Math.Round(position.Position.Longitude, 1)}"),
                                    Name = position.City,
                                    CountryCode = position.Country,
                                    RegionName = position.Region,
                                    LocationVerified = true,
                                    UncertaintyKm = 50
                                });
                        }
                    }
                    topologyStatus = targets.Length == 0 ? "Topología sin destinos que respondan; no hay evidencia de routers." :
                        routers.Count == 0 ? "Traceroute intentado, sin coincidencias exactas en IPmap; topología no corroborada." :
                        $"Topología: {routers.Count} IP de router coinciden exactamente con el IPmap empaquetado; ubicaciones condicionadas a ese catálogo.";
                    Emit("topology", false);
                }
                if (estimate?.Status == "inconsistent") break;
            }
            Emit("complete", true);
        }
        catch (OperationCanceledException)
        {
            Emit(cancellation.IsCancellationRequested ? "cancelled-partial" : "budget-exhausted", true, topologyAttempted ? "Se agotó el presupuesto; se conserva la evidencia completada." :
                "Se agotó el presupuesto antes de traceroute; el resultado no tiene corroboración topológica.");
        }
        return new(previous, session, catalog);
    }

    /// <summary>Pure offline reanalysis. Inputs must contain the original catalogue, routes and network context.</summary>
    public LocationUpdate Replay(Catalog catalog, MeasurementSession session, double resolutionKm = 50)
        => Replay(catalog, session, new EstimatorOptions { ResolutionKm = resolutionKm });

    /// <summary>Reanalyse the same measurements with different model parameters, without probing again.</summary>
    public LocationUpdate Replay(Catalog catalog, MeasurementSession session, EstimatorOptions model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (session.Mode != "target-to-anchors") throw new InvalidDataException("Replay requires a session measured on the target device.");
        var reconciled = EvidenceReconciler.Estimate(catalog, session, model with { MaxAge = TimeSpan.FromDays(36500) },
            session.Observations.Count == 0 ? session.CreatedUtc : session.Observations.Max(o => o.MeasuredUtc));
        var estimate = reconciled.Estimate;
        // Replay must keep the original router positions and labels, even after the embedded snapshot changes.
        var known = catalog.Landmarks.Where(a => a.Kind == "router" && (a.Usable || a.CoordinateEvidence == "published-infrastructure"))
            .GroupBy(a => IPAddress.Parse(a.Address)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var matches = new List<RouterEvidence>();
        foreach (var observation in session.Observations)
            foreach (var hop in observation.Route.Where(h => h.Status == "TtlExpired" && h.RttMs is >= 0 && double.IsFinite(h.RttMs.Value))
                .GroupBy(h => h.Address).Select(g => g.MinBy(h => h.RttMs)!))
                if (IPAddress.TryParse(hop.Address, out var ip) && known.TryGetValue(ip, out var router))
                    matches.Add(new(ip.ToString(), new(router.Position, router.CountryCode ?? "", router.RegionName ?? "", router.Name ?? "", router.Source),
                        hop.RttMs!.Value, hop.Ttl, 1)
                    { LandmarkId = router.Id });
        var routers = matches.GroupBy(r => r.Address).Select(g => g.MinBy(r => r.RttMs)! with { Paths = g.Count() })
            .OrderBy(r => r.RttMs).ToArray();
        var update = new LocationUpdate(1, estimate.Status == "inconsistent" ? EvidenceLevel.Conflicting : estimate.Constraints.Count == 0 ? EvidenceLevel.Unknown :
            EvidenceLevel.LatencyRegion,
            "offline-replay", true, null, null, Snapshot(estimate), Rank(catalog, session, routers), Array.AsReadOnly(routers), session.Observations.Count, 0,
            "Reanálisis de la sesión y del catálogo originales; sin tráfico de red.", Array.AsReadOnly(estimate.Warnings.ToArray()))
        { Diagnostics = reconciled.Diagnostics, Support = SpatialSupportEstimator.Create(Snapshot(estimate), routers, model, reconciled.Diagnostics) };
        var refinement = DeclaredLandmarkRefiner.Evaluate(catalog, session, model ?? new());
        var localities = LocalityAssessment.Create(update, catalog, session);
        if (update.Region?.Constraints.Count == 0 && refinement is { Region.Status: "candidate-region" })
        {
            var declared = catalog with { Landmarks = catalog.Landmarks.Select(a => a.Kind == "anchor" && a.CoordinateEvidence == "operator-declared"
                ? a with { LocationVerified = true } : a).ToList() };
            localities = refinement.CalibratedRegion is { Status: "candidate-region", Representative: not null, CoveringRadiusKm: not null }
                ? LocalityAssessment.FromCalibration(refinement)
                : LocalityAssessment.Create(update with { Region = refinement.Region }, declared, session);
        }
        update = update with { Localities = localities, RegionalRefinement = refinement };
        var topology = AccessTopologyAnalyzer.Analyze(session, _data);
        var routeLocation = TopologyLocationEstimator.Evaluate(catalog, session, topology);
        var fingerprints = new FingerprintAssessment("no-trained-data", [], "Offline replay without empirical profiles.");
        return update with { Enhanced = new(HierarchicalLocationPlanner.Assess(catalog, session, update, fingerprints, routeLocation),
            fingerprints, topology, new(TunnelState.Indeterminate, [], [], ["Offline replay; no current interface inspection."]), [])
            { RouteLocation = routeLocation } };
    }

    private static RegionSnapshot Snapshot(LocationEstimate estimate) => new(estimate.Status, estimate.Representative,
        estimate.CoveringRadiusKm, estimate.CandidateAreaKm2, Array.AsReadOnly(estimate.Cells.ToArray()), Array.AsReadOnly(estimate.Constraints.ToArray()));
    private static bool Contains(GeoCell c, GeoPoint p) => p.Latitude >= c.South && p.Latitude <= c.North && p.Longitude >= c.West && p.Longitude <= c.East;
    internal static IReadOnlyList<NetworkCandidate> Rank(Catalog catalog, MeasurementSession session, IEnumerable<RouterEvidence> routers)
    {
        var byId = catalog.Landmarks.ToDictionary(a => a.Id);
        var raw = session.Observations.Where(o => o.MinimumRttMs.HasValue && byId.ContainsKey(o.LandmarkId))
            .Select(o => new NetworkCandidate(byId[o.LandmarkId].Position, byId[o.LandmarkId].Name ?? o.LandmarkId,
                byId[o.LandmarkId].Source, 1 / Math.Pow(Math.Max(1, o.MinimumRttMs!.Value), 2), o.MinimumRttMs.Value))
            .Concat(routers.Select(r => new NetworkCandidate(r.Published.Position, r.Published.City, r.Published.Source,
                Math.Min(3, r.Paths) / Math.Pow(Math.Max(1, r.RttMs), 2), r.RttMs)))
            // Dense clusters and correlated routers do not gain weight just by appearing many times.
            .GroupBy(c => (Math.Round(c.Position.Latitude * 2), Math.Round(c.Position.Longitude * 2)))
            .Select(g => g.MaxBy(c => c.RelativeScore)!).OrderByDescending(c => c.RelativeScore).Take(12).ToArray();
        double sum = raw.Sum(c => c.RelativeScore);
        return Array.AsReadOnly(raw.Select(c => c with { RelativeScore = c.RelativeScore / sum }).ToArray());
    }
    internal static void Validate(LocateRequest r)
    {
        if (r.Rounds is < 1 or > 10 || r.BatchSize is < 6 or > 64 || r.Samples is < 1 or > 20 ||
            r.TimeoutMs is < 100 or > 10_000 || r.MaxHops is < 1 or > 64 || r.TraceCount is < 1 or > 8 ||
            r.PacketsPerSecond is < 1 or > 20 || r.Budget < TimeSpan.FromSeconds(1) || r.Budget > TimeSpan.FromMinutes(30) ||
            !double.IsFinite(r.ResolutionKm) || r.ResolutionKm is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(r), "Invalid analysis budget or probe configuration.");
        if (r.Parallelism is < 1 or > 32 || r.MaximumProbes is < 1 or > 100000 || r.TraceRepetitions is < 1 or > 3 || r.MaximumTraceRoutes is < 1 or > 64 ||
            r.ProgressIntervalMs is < 100 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(r), "Invalid concurrency, launch budget, repetition or progress interval.");
        r.AdditionalCatalog?.Validate();
        if (r.FingerprintProfiles is { } profiles) NetworkFingerprintEstimator.Validate(profiles);
        new EstimatorOptions
        {
            ResolutionKm = r.ResolutionKm,
            TimingSlackMs = r.TimingSlackMs,
            MaxCells = r.MaxCells,
            PropagationKmPerMs = r.PropagationKmPerMs
        }.Validate();
        if (r.PublicAddress is { } ip && (!EmbeddedCatalog.IsPublic(ip) ||
            (EmbeddedCatalog.Normalize(ip).AddressFamily == AddressFamily.InterNetworkV6) != r.IPv6))
            throw new ArgumentException("PublicAddress must belong to the measured connection and selected IP family.");
    }
}
