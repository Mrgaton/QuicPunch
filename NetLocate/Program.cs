using System.Globalization;
using System.Text.Json;
using QuicNet.Geolocation;

namespace NetLocate;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Console.WriteLine(Help); return 0; }
            if (args[0] is "self-test" or "--self-test") return await SelfTests.RunAsync();
            if (args[0] == "version") { Console.WriteLine($"{ProgressiveSearch.EngineVersion} / {ProgressiveSearchPlanner.RegionalVersion} / .NET {Environment.Version} / AOT {!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}"); return 0; }
            if (args[0] == "analyze") return MeasurementAudit.Run(args);
            if (args[0] == "refine-grid") return GridRefinement.Run(args, stop.Token);
            if (args[0] == "benchmark") return SelfTests.Benchmark();
            if (args[0] == "notices") { Console.WriteLine(EmbeddedCatalog.Default.Notice); using var license = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream("NetLocate.LICENSE")!); Console.WriteLine(license.ReadToEnd()); return 0; }
            if (args[0] == "export-catalog")
            {
                if (args.Length != 2) throw new ArgumentException("Uso: NetLocate export-catalog landmarks.json");
                await JsonFiles.WriteAsync(args[1], DefaultCatalog(), stop.Token);
                Console.WriteLine(Path.GetFullPath(args[1])); return 0;
            }
            string command = args[0];
            if (command is not ("live" or "replay" or "demo")) throw new ArgumentException("Comando desconocido.");
            int start = command == "replay" ? 2 : 1;
            if (command == "replay" && args.Length < 2) throw new ArgumentException("Falta el archivo JSONL.");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var allowed = new HashSet<string>(["--out", "--catalog", "--rounds", "--batch", "--samples",
                "--parallel", "--timeout-ms", "--pps", "--seconds", "--resolution-km", "--ipv6", "--planner"], StringComparer.Ordinal);
            for (int i = start; i < args.Length; i++)
            {
                string key = args[i];
                if (!allowed.Contains(key) || options.ContainsKey(key)) throw new ArgumentException("Opción desconocida o duplicada: " + key);
                if (key == "--ipv6") { options.Add(key, "true"); continue; }
                if (++i >= args.Length) throw new ArgumentException("Falta valor para " + key);
                options.Add(key, args[i]);
            }
            string output = Path.GetFullPath(options.GetValueOrDefault("--out") ??
                Path.Combine(Environment.CurrentDirectory, "netlocate-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)));
            if (command == "replay")
            {
                if (options.Keys.Any(k => k != "--out")) throw new ArgumentException("REPLAY usa el catálogo y modelo congelados; solo acepta --out.");
                var (header, replayed) = RecordingReader.Read(args[1]);
                Save(output, header, replayed, replay: true);
                Console.WriteLine("REPLAY terminado sin sondas ni resolución DNS.\n" + Path.Combine(output, "report.html"));
                return replayed.Rounds.Length == 0 ? 2 : 0;
            }
            int Number(string key, int fallback) => options.TryGetValue(key, out var value)
                ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : fallback;
            double Real(string key, double fallback) => options.TryGetValue(key, out var value)
                ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;
            bool demo = command == "demo";
            if (demo && (options.ContainsKey("--catalog") || options.ContainsKey("--ipv6")))
                throw new ArgumentException("DEMO usa exclusivamente su catálogo IPv4 sintético.");
            var config = new ProgressiveSearchOptions
            {
                Rounds = Number("--rounds", demo ? 6 : 30), BatchSize = Number("--batch", 12),
                Samples = Number("--samples", demo ? 2 : 3), Parallelism = Number("--parallel", 4),
                TimeoutMs = Number("--timeout-ms", 1000), PacketsPerSecond = Number("--pps", demo ? 20 : 10),
                Planner = options.GetValueOrDefault("--planner") ?? "regional",
                Seconds = Number("--seconds", 180), IPv6 = options.ContainsKey("--ipv6"), Synthetic = demo,
                Model = new() { ResolutionKm = Real("--resolution-km", 40), MaxCells = 12_000 }
            };
            config.Validate();
            var synthetic = demo ? new DemoNetwork() : null;
            Catalog catalog = synthetic?.Catalog ?? (options.TryGetValue("--catalog", out var source)
                ? await JsonFiles.ReadAsync<Catalog>(source, stop.Token) : DefaultCatalog());
            catalog.Validate();
            Directory.CreateDirectory(output);
            using var journal = new RecordingWriter(Path.Combine(output, "measurements.jsonl"), catalog, config);
            var frames = new List<SearchRound>();
            Console.WriteLine(demo ? "SIMULACIÓN: las latencias no son mediciones de Internet." :
                "LIVE: sondas ICMP únicamente a IP literales del catálogo local.");
            Console.WriteLine("Mapa local: " + Path.Combine(output, "report.html"));
            AtomicFile.Write(Path.Combine(output, "report.html"), HeatmapReport.Render(journal.Header, new("running", []), running: true));
            var result = await ProgressiveSearch.RunAsync(catalog, config, round =>
            {
                frames.Add(round);
                journal.Append(new("round", Round: round));
                AtomicFile.Write(Path.Combine(output, "report.html"),
                    HeatmapReport.Render(journal.Header, new("running", frames.ToArray()), running: true));
                Console.WriteLine(FormattableString.Invariant($"Ronda {round.Number}: {round.Session.Observations.Count(o => o.MinimumRttMs.HasValue)} referencias responden; área {round.Update.Region?.AreaKm2:F0} km²; radio {round.Update.Region?.CoveringRadiusKm:F0} km; {round.SentPackets} paquetes."));
            }, synthetic, stop.Token);
            journal.Append(new("finished", State: result.State));
            Save(output, journal.Header, result);
            Console.WriteLine("Estado: " + result.State + "\n" + Path.Combine(output, "report.html"));
            return result.State == "cancelled" ? 130 :
                result.Rounds.LastOrDefault()?.Update.Region?.Status is "candidate-region" or "unresolved" ? 0 : 2;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelado."); return 130; }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or
            InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("NetLocate: " + e.Message); return 1; }
    }

    internal static Catalog DefaultCatalog() => new(EmbeddedCatalog.Default.Anchors.Select(a => a with { Asn = EmbeddedCatalog.Default.LookupAsn(System.Net.IPAddress.Parse(a.Address)) })
        .Concat(RegionalCatalog.Landmarks).DistinctBy(a => System.Net.IPAddress.Parse(a.Address)).ToList())
    { CreatedUtc = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero) };

    internal static void Save(string output, RecordingHeader header, ProgressiveSearchResult result, bool replay = false)
    {
        AtomicFile.Write(Path.Combine(output, "report.html"), HeatmapReport.Render(header, result, replay: replay));
        AtomicFile.Write(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, GeoJsonContext.Default.ProgressiveSearchResult));
    }

    private const string Help = """
        NetLocate · .NET 10 / Native AOT
          NetLocate live [--out carpeta] [--catalog landmarks.json]
                         [--rounds 30] [--batch 12] [--samples 3] [--seconds 180]
                         [--parallel 4] [--timeout-ms 1000] [--pps 10]
                         [--resolution-km 40] [--ipv6] [--planner regional|information|legacy]
          NetLocate replay measurements.jsonl [--out carpeta]
          NetLocate demo [--out carpeta] [--rounds 6]
          NetLocate export-catalog landmarks.json
          NetLocate version
          NetLocate self-test
          NetLocate analyze measurements.jsonl [--out audit.json]
          NetLocate refine-grid measurements.jsonl [--out carpeta] [--resolution-km 5]
          NetLocate benchmark
          NetLocate notices

        LIVE solo envía ICMP a landmarks. No consulta HTTP, DNS ni servicios remotos de ASN o geolocalización.
        REPLAY y DEMO no envían paquetes. El mapa HTML/SVG funciona como archivo local.
        Abre report.html para revisar las rondas. No se ejecutan procesos auxiliares.
        El color indica afinidad relativa; la región compatible no es un intervalo de confianza.
        Ctrl+C conserva las rondas y endpoints completados. Cada registro requiere una carpeta nueva.
        """;
}
