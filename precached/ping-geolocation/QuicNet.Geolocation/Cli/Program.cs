using System.Globalization;
using System.Net;
using System.Text.Json;
using QuicNet.Geolocation;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    try
    {
        var flags = new HashSet<string>(["network", "ipv6", "mtu", "tcp", "no-trace", "help", "consent"], StringComparer.Ordinal);
        var valued = new HashSet<string>(["seconds", "rounds", "batch", "samples", "pps", "maximum-probes", "parallelism", "public-ip",
            "asn", "access", "catalog", "profiles", "metadata", "out", "geojson", "replay", "capture", "latitude", "longitude",
            "country", "region", "locality", "uncertainty", "group"], StringComparer.Ordinal);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected --option: " + args[i]);
            string key = args[i][2..];
            if (options.ContainsKey(key)) throw new ArgumentException("Duplicate option: " + key);
            if (flags.Contains(key)) options[key] = "true";
            else if (valued.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) options[key] = args[++i];
            else throw new ArgumentException("Unknown option or missing value: " + key);
        }
        string? Get(string key) => options.GetValueOrDefault(key);
        int Number(string key, int fallback) => Get(key) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : fallback;
        double Real(string key, double fallback) => Get(key) is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : fallback;
        bool Has(string key) => options.ContainsKey(key);
        if (Has("help"))
        {
            Console.WriteLine("""
                QuicNet.Geolocation — catálogos offline; mediciones solo con --network.
                --network --seconds 90 --rounds 6 --batch 12 --samples 3 --pps 10
                --maximum-probes 1500 --parallelism 4 --ipv6 --mtu --tcp --no-trace
                --public-ip DIRECCION_PROPIA --asn NUMERO --access fiber|mobile|unknown
                --catalog referencias.json --profiles perfiles.json --metadata prefijos.json
                --out resultado.json --geojson mapa.geojson
                --replay resultado.json                       (sin red)
                --capture perfiles.json --replay resultado.json --consent
                  --latitude 41.5 --longitude 2.0 --country ES --region Cataluña
                  --locality Terrassa --uncertainty 10 --group identificador-anonimo
                Los pings no identifican un domicilio. Pesos de afinidad ≠ probabilidades.
                """);
            return 0;
        }
        var profiles = Get("profiles") is { } profilePath ? await JsonFiles.ReadAsync<FingerprintDatabase>(profilePath) : null;
        if (profiles is not null) NetworkFingerprintEstimator.Validate(profiles);
        AnalysisResult result;
        if (Get("replay") is { } replay)
        {
            var archive = await JsonFiles.ReadAsync<EnhancedArchive>(replay);
            if (archive.SchemaVersion != 1) throw new InvalidDataException("Unsupported archive schema.");
            var saved = archive.Result;
            // Re-analysis is always offline; live-network options cannot turn a replay into a probe.
            var update = new ProgressiveLocator().Replay(saved.Catalog, saved.Session);
            var topology = AccessTopologyAnalyzer.Analyze(saved.Session);
            var fingerprints = NetworkFingerprintEstimator.Estimate(saved.Catalog, saved.Session, profiles ?? archive.Profiles, saved.Final.Asn);
            var hierarchy = HierarchicalLocationPlanner.Assess(saved.Catalog, saved.Session, update, fingerprints);
            result = saved with { Final = update with { Enhanced = new(hierarchy, fingerprints, topology,
                saved.Final.Enhanced?.Tunnel ?? TunnelDetector.Assess([]), saved.Final.Enhanced?.Supplemental ?? []) } };
            Console.WriteLine(JsonSerializer.Serialize(result.Final, GeoJsonContext.Default.LocationUpdate));
        }
        else
        {
            if (Has("mtu") && !Has("network") || Has("tcp") && !Has("network")) throw new ArgumentException("--mtu/--tcp requieren --network.");
            var catalog = Get("catalog") is { } c ? await JsonFiles.ReadAsync<Catalog>(c) : null;
            var metadata = Get("metadata") is { } m ? await JsonFiles.ReadAsync<OfflineMetadata>(m) : null;
            var request = new LocateRequest
            {
                EnableNetwork = Has("network"), IPv6 = Has("ipv6"), EnablePathMtu = Has("mtu"), EnableTcp = Has("tcp"), EnableTopology = !Has("no-trace"),
                Budget = TimeSpan.FromSeconds(Number("seconds", 90)), Rounds = Number("rounds", 6), BatchSize = Number("batch", 12),
                Samples = Number("samples", 3), PacketsPerSecond = Number("pps", 10), MaximumProbes = Number("maximum-probes", 1500),
                Parallelism = Number("parallelism", 4), PublicAddress = Get("public-ip") is { } ip ? IPAddress.Parse(ip) : null,
                NetworkAsn = Get("asn") is { } n ? uint.Parse(n, CultureInfo.InvariantCulture) : null,
                AccessTechnology = Get("access") ?? "unknown", AdditionalCatalog = catalog, FingerprintProfiles = profiles, Metadata = metadata
            };
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                // One compact JSON object per progress event; no telemetry or external collector.
                var json = new JsonSerializerOptions(JsonFiles.Options) { WriteIndented = false };
                using var run = new ProgressiveLocator().Start(request,
                    update => Console.WriteLine(JsonSerializer.Serialize(update, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<LocationUpdate>)json.GetTypeInfo(typeof(LocationUpdate)))), stop.Token);
                result = await run.Completion;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        if (Get("capture") is { } destination)
        {
            if (!Has("consent") || Get("latitude") is null || Get("longitude") is null || Get("country") is null || Get("locality") is null)
                throw new ArgumentException("La captura exige --consent y una posición real conocida, país y localidad. No use coordenadas estimadas.");
            if (result.Final.Enhanced?.Tunnel.State is TunnelState.LocalTunnelIdentified or TunnelState.PossibleEncapsulation ||
                result.Final.Enhanced?.Tunnel.MixedRoutes == true)
                throw new InvalidOperationException("No se incorporan automáticamente sesiones con señales de túnel al entrenamiento geográfico físico.");
            var database = File.Exists(destination) ? await JsonFiles.ReadAsync<FingerprintDatabase>(destination) : new();
            database.Profiles.Add(NetworkFingerprintEstimator.Capture(Guid.NewGuid().ToString("N"), result.Catalog, result.Session,
                new(Real("latitude", 0), Real("longitude", 0)), Get("country")!, Get("region") ?? "", Get("locality")!, result.Final.Asn, true,
                Real("uncertainty", 10), Get("access") ?? "unknown", Get("group")));
            NetworkFingerprintEstimator.Validate(database);
            await JsonFiles.WriteAsync(destination, database);
        }
        if (Get("out") is { } output) await JsonFiles.WriteAsync(output, new EnhancedArchive(1, result, null));
        if (Get("geojson") is { } geojson) await HeatmapExporter.WriteAsync(geojson, result.Final);
        return 0;
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or JsonException or FormatException or OverflowException or InvalidOperationException)
    { Console.Error.WriteLine(ex.Message); return 2; }
}
