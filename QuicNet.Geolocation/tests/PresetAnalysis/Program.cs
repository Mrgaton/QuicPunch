using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using QuicNet.Geolocation;
using QuicNet.Geolocation.Workbench;

if (args.Length > 1 && args[0] == "--topology-replay")
{
    foreach (var slug in new[] { "quick", "balanced", "detailed" })
    {
        var archive = await JsonFiles.ReadAsync<EnhancedArchive>(Path.Combine(args[1], slug + ".archive.json"));
        var settings = await JsonFiles.ReadAsync<WorkbenchSettings>(Path.Combine(args[1], slug + ".settings.json"));
        var result = new ProgressiveLocator().Replay(archive.Result.Catalog,archive.Result.Session,settings.ToModel());
        await JsonFiles.WriteAsync(Path.Combine(args[1],slug+".topology-recomputed.json"),result);
        Console.WriteLine($"REPLAY {slug} {result.Enhanced?.RouteLocation?.Status} contrasts={result.Enhanced?.RouteLocation?.Contrasts.Count} zones=" +
            string.Join("; ",result.Enhanced?.RouteLocation?.Zones.Take(3).Select(z=>$"{z.Label}: {z.RelativeScore:F4}") ?? []));
    }
    return;
}

if (args.Length > 1 && args[0] == "--audit-infrastructure")
{
    var candidates = await JsonFiles.ReadAsync<Landmark[]>(args[1]);
    using var limiter = new ProbeBudget(20,10000);
    using var probe = new NetworkProbe(new() { Samples = 3, TimeoutMs = 700, PacketsPerSecond = 20 },sharedBudget:limiter);
    var results = new List<Observation>();
    foreach (var chunk in candidates.Chunk(6)) results.AddRange(await Task.WhenAll(chunk.Select(a => probe.MeasureAsync(a.Id,a.Address))));
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuicNet", "GeoLatency", "diagnostics",
        "infrastructure-audit-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'"));
    Directory.CreateDirectory(directory);
    await JsonFiles.WriteAsync(Path.Combine(directory,"candidates.json"),new Catalog(candidates.ToList()));
    await JsonFiles.WriteAsync(Path.Combine(directory,"session.json"),new MeasurementSession("this-device","infrastructure-audit","ipv4",results));
    Console.WriteLine($"INFRASTRUCTURE={directory} replies={results.Count(o=>o.MinimumRttMs.HasValue)}/{results.Count}");
    return;
}

if (args.Contains("--audit-catalog"))
{
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuicNet", "GeoLatency", "diagnostics",
        "catalog-audit-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'"));
    Directory.CreateDirectory(directory);
    var rows = new List<object>();
    using var limiter = new ProbeBudget(20, 10000);
    using var probe = new NetworkProbe(new() { Samples = 3, TimeoutMs = 700, PacketsPerSecond = 20 }, sharedBudget: limiter);
    foreach (var chunk in EmbeddedCatalog.Default.Anchors.Where(a => a.CountryCode == "ES" &&
        System.Net.IPAddress.Parse(a.Address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Chunk(6))
    {
        var observations = await Task.WhenAll(chunk.Select(a => probe.MeasureAsync(a.Id,a.Address)));
        foreach (var (a,o) in chunk.Zip(observations))
            rows.Add(new { a.Id, a.Name, a.Address, a.RegionName, a.Source, a.Position, a.LocationVerified,
                successes = o.Samples.Count(s => s.Status == "Success"), minimumMs = o.MinimumRttMs, samples = o.Samples });
    }
    await File.WriteAllTextAsync(Path.Combine(directory,"spain-ipv4.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"AUDIT={directory} targets={rows.Count} launches={probe.SentPackets}");
    return;
}

if (args.Length > 1 && args[0] == "--offline")
{
    var cases = new List<object>();
    foreach (var slug in new[] { "quick", "balanced", "detailed" })
    {
        var archive = await JsonFiles.ReadAsync<EnhancedArchive>(Path.Combine(args[1], slug + ".archive.json"));
        var settings = await JsonFiles.ReadAsync<WorkbenchSettings>(Path.Combine(args[1], slug + ".settings.json"));
        var saved = archive.Result;
        foreach (var variant in new[] { "original-recomputed", "europe-only", "spain-only", "without-madrid-area" })
        {
            bool Keep(Landmark a) => variant switch
            {
                "europe-only" => a.CountryCode is "ES" or "DE" or "CZ" or "EE" or "FI",
                "spain-only" => a.CountryCode == "ES",
                "without-madrid-area" => Geography.DistanceKm(a.Position, new(40.4168, -3.7038)) > 70,
                _ => true
            };
            var catalog = saved.Catalog with { Landmarks = saved.Catalog.Landmarks.Where(Keep).ToList() };
            var ids = catalog.Landmarks.Select(a => a.Id).ToHashSet();
            var session = saved.Session with { Observations = saved.Session.Observations.Where(o => ids.Contains(o.LandmarkId)).ToList() };
            var conditional = DeclaredLandmarkRefiner.Evaluate(catalog, session, settings.ToModel());
            var declared = catalog with { Landmarks = catalog.Landmarks.Select(a => a.CoordinateEvidence == "operator-declared" ? a with { LocationVerified = true } : a).ToList() };
            var fit = conditional is null ? null : LatencyPositionEstimator.Estimate(declared, session, conditional.Region);
            cases.Add(new { preset = slug, variant, observations = session.Observations.Count, status = conditional?.Region.Status, fit,
                distanceFromBarcelonaKm = fit is null ? (double?)null : Geography.DistanceKm(fit.Position, new(41.3874, 2.1686)) });
            Console.WriteLine($"OFFLINE {slug} {variant}: fit={fit?.Position} distanceBarcelona={(fit is null ? null : Geography.DistanceKm(fit.Position, new(41.3874, 2.1686)))}");
        }
    }
    await File.WriteAllTextAsync(Path.Combine(args[1], "offline-ablations.json"), JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuicNet", "GeoLatency", "diagnostics", "presets-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'"));
Directory.CreateDirectory(output);
Console.WriteLine("RESULTS=" + output);
var sources = new[] { "QuicNet.Geolocation/Workbench/WorkbenchSettings.cs", "QuicNet.Geolocation/Enhanced/EnhancedLocationEngine.cs", "QuicNet.Geolocation/Enhanced/HierarchicalLocationPlanner.cs", "QuicNet.Geolocation/Enhanced/TopologyLocationEstimator.cs", "QuicNet.Geolocation/Enhanced/TopologyTracePlanner.cs", "QuicNet.Geolocation/Enhanced/AccessTopologyAnalyzer.cs", "QuicNet.Geolocation/Networking/TraceProvider.cs", "QuicNet.Geolocation/Core/LatencyPositionEstimator.cs", "QuicNet.Geolocation/Core/SpatialSupportEstimator.cs", "QuicNet.Geolocation/Data/anchors.json.gz", "QuicNet.Geolocation/Data/localities.bin" };
var provenance = sources.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
await File.WriteAllTextAsync(Path.Combine(output, "provenance.json"), JsonSerializer.Serialize(new { startedUtc = DateTimeOffset.UtcNow, libraryVersion = typeof(ProgressiveLocator).Assembly.GetName().Version?.ToString(), sourceHashes = provenance, publicIpProvided = false, fingerprintProfilesProvided = false, knownPositionProvided = false, execution = "direct ProgressiveLocator.Start(WorkbenchSettings.Preset(name).ToRequest()); no web server; sequential runs" }, new JsonSerializerOptions { WriteIndented = true }));
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
foreach (var (name, slug) in new[] { ("Rápido", "quick"), ("Equilibrado", "balanced"), ("Detallado", "detailed") })
{
    if (stop.IsCancellationRequested) break;
    var settings = WorkbenchSettings.Preset(name);
    var request = settings.ToRequest();
    var updates = new List<LocationUpdate>();
    string? phase = null;
    var started = DateTimeOffset.UtcNow;
    var watch = Stopwatch.StartNew();
    Console.WriteLine($"START {slug}: budget={settings.Seconds}s rounds={settings.Rounds} batch={settings.BatchSize} samples={settings.Samples} traces={settings.TraceCount} hops={settings.MaxHops}");
    await JsonFiles.WriteAsync(Path.Combine(output, slug + ".settings.json"), settings);
    using var run = new ProgressiveLocator().Start(request, u =>
    {
        updates.Add(u);
        if (u.Phase != phase || u.IsFinal) { phase = u.Phase; Console.WriteLine($"PROGRESS {slug} {watch.Elapsed.TotalSeconds:F1}s {u.Phase} references={u.MeasuredLandmarks} launches={u.SentPackets}"); }
    }, stop.Token);
    var result = await run.Completion;
    watch.Stop();
    await JsonFiles.WriteAsync(Path.Combine(output, slug + ".archive.json"), new EnhancedArchive(1, result, null));
    await File.WriteAllBytesAsync(Path.Combine(output, slug + ".updates.json"), JsonSerializer.SerializeToUtf8Bytes(updates.ToArray(), GeoJsonContext.Default.LocationUpdateArray));
    var final = result.Final;
    var withoutRoutes = result.Session with { Observations = result.Session.Observations.Select(o => o with { Route = [] }).ToList() };
    var ablation = new ProgressiveLocator().Replay(result.Catalog, withoutRoutes, settings.ToModel());
    await JsonFiles.WriteAsync(Path.Combine(output, slug + ".without-routes.json"), ablation);
    await JsonFiles.WriteAsync(Path.Combine(output, slug + ".explanation.json"), WorkbenchExplanation.Create(final));
    await File.WriteAllTextAsync(Path.Combine(output, slug + ".execution.json"), JsonSerializer.Serialize(new { name, startedUtc = started, endedUtc = DateTimeOffset.UtcNow, elapsedSeconds = watch.Elapsed.TotalSeconds, phase = final.Phase, observations = result.Session.Observations.Count, respondingSites = final.RegionalRefinement?.RespondingSites, launches = final.SentPackets, primaryStatus = final.Region?.Status, conditionalStatus = final.RegionalRefinement?.Region.Status, hierarchyStage = final.Enhanced?.Hierarchy.Stage, fit = final.Localities?.Fit, fingerprintStatus = final.Enhanced?.Fingerprints.Status,
        routeStatus = final.Enhanced?.RouteLocation?.Status, routes = final.Enhanced?.Topology.DistinctRoutes, usableRoutes = final.Enhanced?.RouteLocation?.UsableRoutes, contrasts = final.Enhanced?.RouteLocation?.Contrasts.Count,
        zones = final.Enhanced?.RouteLocation?.Zones.Take(8), withoutRoutesZones = ablation.Enhanced?.RouteLocation?.Zones.Take(8) }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"FINISHED {slug}: seconds={watch.Elapsed.TotalSeconds:F2} phase={final.Phase} primary={final.Region?.Status} conditional={final.RegionalRefinement?.Region.Status} method={final.Localities?.Fit?.Method} fit={final.Localities?.Fit?.Position} spread={final.Localities?.Fit?.ModelSpreadKm:F2} CBG={final.RegionalRefinement?.CalibrationStatus}");
}
Console.WriteLine("COMPLETE=" + output);
