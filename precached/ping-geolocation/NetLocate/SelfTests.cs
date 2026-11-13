using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using QuicNet.Geolocation;

namespace NetLocate;

internal static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            Console.WriteLine("PASS " + message); passed++;
        }
        void Reject(Action action, string message)
        {
            try { action(); } catch (Exception e) when (e is InvalidDataException or ArgumentException or JsonException)
            { Check(true, message); return; }
            throw new InvalidOperationException("No se rechazó: " + message);
        }
        string testRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "obj", "self-tests"));
        string directory = Path.Combine(testRoot, "netlocate-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(Math.Abs(Geography.DistanceKm(new(0, 0), new(0, 1)) - 111.195) < .01, "distancias geográficas");
            Check(Geography.DistanceKm(new(0, 179.9), new(0, -179.9)) < 23, "antimeridiano");
            Check(Geography.DistanceKm(new(89.99, 0), new(89.99, 180)) < 3, "polos");
            var embedded = Program.DefaultCatalog();
            embedded.Validate();
            Check(embedded.Landmarks.Count > 1000 && embedded.Landmarks.All(a => IPAddress.TryParse(a.Address, out _)), "catálogo local incorporado y direcciones literales");

            Check(embedded.Landmarks.Count(a => a.Asn.HasValue) > embedded.Landmarks.Count / 2, "metadatos ASN incorporados sin consultas remotas");
            InformationPlannerTests.Run(Check);
            RegionalTests.Run(Check);
            var network = new DemoNetwork();
            var madrid = network.Catalog.Landmarks.Single(a => a.Id == "madrid");
            var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var focused = new MeasurementSession("test", "same-network", "ipv4",
                [new(madrid.Id, madrid.Address, clock, [new(1, "Success"), new(10, "Success")])]) { Synthetic = true, CreatedUtc = clock };
            var model = new EstimatorOptions { ResolutionKm = 40, MaxCells = 12_000 };
            var update = ProgressiveSearch.Replay(network.Catalog, focused, model);
            var plan = ProgressiveSearchPlanner.Next(network.Catalog, focused, update, 12, 2);
            Check(plan.Length == 12 && plan.Count(s => s.Role == "global") >= 1 &&
                plan.Count(s => s.Role == "boundary") >= 1 && plan.Count(s => s.Role == "information") >= 3 && plan.Count(s => s.Role == "local") >= 1,
                "cuotas de información, frontera y exploración global");
            var byId = network.Catalog.Landmarks.ToDictionary(a => a.Id);
            Check(plan.Where(s => s.Role == "information").Any(s => Geography.DistanceKm(byId[s.LandmarkId].Position, network.Truth) < 500),
                "las señales rápidas atraen nuevas sondas locales");
            Check(plan.Where(s => s.Role == "global").Any(s => Geography.DistanceKm(byId[s.LandmarkId].Position, network.Truth) > 2000),
                "se sigue explorando lejos de la hipótesis principal");
            Check(plan.Select(s => byId[s.LandmarkId].SiteId).Distinct().Count() == plan.Length, "no se duplican emplazamientos en una tanda");

            var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try
            {
                await ProgressiveSearch.RunAsync(network.Catalog, new() { Synthetic = true }, _ => { }, network, cancelled.Token);
                throw new InvalidOperationException("Cancelación ignorada.");
            }
            catch (OperationCanceledException) { Check(true, "cancelación antes de sondear"); }
            cancelled.Dispose();
            try
            {
                await ProgressiveSearch.RunAsync(network.Catalog, new(), _ => { });
                throw new InvalidOperationException("LIVE aceptó direcciones de documentación.");
            }
            catch (InvalidDataException) { Check(true, "LIVE impide sondear el catálogo sintético"); }

            var options = new ProgressiveSearchOptions
            { Synthetic = true, Rounds = 3, BatchSize = 6, Samples = 2, PacketsPerSecond = 20, Seconds = 30, Model = model };
            var result = await ProgressiveSearch.RunAsync(network.Catalog, options, _ => { }, network);
            Check(result.Rounds.Length == 3, "ejecución progresiva completa");
            Check(result.Rounds[0].Session.Observations.Count == 6 && result.Rounds[^1].Session.Observations.Count > 6,
                "instantáneas independientes conservan el historial");
            Check(result.Rounds[^1].Update.Region!.AreaKm2 < result.Rounds[0].Update.Region!.AreaKm2, "el escenario sintético reduce el área");
            Check(result.Rounds.All(r => r.Update.Region!.Cells.Any(c => network.Truth.Latitude >= c.South &&
                network.Truth.Latitude <= c.North && network.Truth.Longitude >= c.West && network.Truth.Longitude <= c.East)),
                "el refinamiento conserva la posición sintética conocida");
            Check(result.Rounds[0].Session.Observations.All(old =>
            {
                var current = result.Rounds[^1].Session.Observations.Single(o => o.LandmarkId == old.LandmarkId);
                return current.Samples.Take(old.Samples.Count).SequenceEqual(old.Samples) && current.MinimumRttMs <= old.MinimumRttMs;
            }), "las re-mediciones conservan muestras y mínimos anteriores");
            var last = result.Rounds[^1];
            var recalculated = ProgressiveSearch.Replay(network.Catalog, last.Session, options.Model, last.Number, last.SentPackets);
            Check(JsonSerializer.Serialize(last.Update, GeoJsonContext.Default.LocationUpdate) ==
                JsonSerializer.Serialize(recalculated, GeoJsonContext.Default.LocationUpdate), "REPLAY determinista de región, advertencias y mapa");
            var noNetwork = new ProgressiveLocator(transport: new RejectNetwork());
            Check(noNetwork.Replay(network.Catalog, last.Session, options.Model).Region!.AreaKm2 == last.Update.Region!.AreaKm2,
                "reproducción con transporte de red prohibido");

            Directory.CreateDirectory(directory);
            string journal = Path.Combine(directory, "measurements.jsonl");
            RecordingHeader header;
            using (var writer = new RecordingWriter(journal, network.Catalog, options))
            {
                header = writer.Header;
                foreach (var round in result.Rounds) writer.Append(new("round", Round: round));
                writer.Append(new("finished", State: result.State));
            }
            var restored = RecordingReader.Read(journal);
            Check(restored.Result.Rounds.Length == result.Rounds.Length &&
                JsonSerializer.Serialize(restored.Result, GeoJsonContext.Default.ProgressiveSearchResult) ==
                JsonSerializer.Serialize(result, GeoJsonContext.Default.ProgressiveSearchResult), "JSONL y serialización generada sin reflexión");
            string refinedPath = Path.Combine(directory, "refined");
            Check(GridRefinement.Run(["refine-grid", journal, "--out", refinedPath, "--resolution-km", "10"]) == 0,
                "refinamiento de malla completamente local con registro derivado");
            var refined = RecordingReader.Read(Path.Combine(refinedPath, "measurements.jsonl"));
            Check(refined.Header.RefinedFromSha256 is { Length: 64 } && refined.Header.Options.Model.ResolutionKm == 10 &&
                refined.Result.Rounds[^1].Update.Region!.Constraints.SequenceEqual(last.Update.Region!.Constraints) &&
                JsonSerializer.Serialize(refined.Result.Rounds[^1].Session, GeoJsonContext.Default.MeasurementSession) ==
                JsonSerializer.Serialize(last.Session, GeoJsonContext.Default.MeasurementSession),
                "el refinamiento conserva muestras, reloj y cotas físicas y registra su procedencia");
            string corrupt = Path.Combine(directory, "corrupt.jsonl");
            File.WriteAllText(corrupt, JsonSerializer.Serialize(new JournalRecord("header",
                Header: header with { CatalogSha256 = "bad-hash" }), GeoJsonContext.Default.JournalRecord) + "\n");
            Reject(() => RecordingReader.Read(corrupt), "catálogo alterado detectado por su huella");
            string closed = Path.Combine(directory, "closed.jsonl");
            File.Copy(journal, closed);
            File.AppendAllText(closed, "{\"kind\":");
            Reject(() => RecordingReader.Read(closed), "datos después del cierre no se confunden con recuperación parcial");
            string partial = Path.Combine(directory, "partial.jsonl");
            using (var writer = new RecordingWriter(partial, network.Catalog, options))
                foreach (var round in result.Rounds) writer.Append(new("round", Round: round));
            File.AppendAllText(partial, "{\"kind\":");
            var recovered = RecordingReader.Read(partial);
            Check(recovered.Result.Rounds.Length == 3 && recovered.Result.State == "recovered-truncated-tail",
                "recuperación de una última línea incompleta sin perder rondas");

            string html = HeatmapReport.Render(header, result);
            Check(!html.Contains("<script", StringComparison.OrdinalIgnoreCase) &&
                !html.Contains("http-equiv=\"refresh\"", StringComparison.Ordinal), "informe desconectado sin scripts ni refresco permanente");
            var svgs = Regex.Matches(html, @"<svg\b.*?</svg>", RegexOptions.Singleline);
            foreach (Match svg in svgs) _ = XDocument.Parse(svg.Value);
            Check(svgs.Count == 7 && html.Contains("Radio de cobertura", StringComparison.Ordinal),
                "SVG válido para todas las rondas y exposición de incertidumbre");
            var hostile = network.Catalog with
            { Landmarks = network.Catalog.Landmarks.Select(a => a with { Name = "<img src=x onerror=evil()>" }).ToList() };
            Check(!HeatmapReport.Render(header with { Catalog = hostile }, result).Contains("<img src=x", StringComparison.Ordinal),
                "metadatos escapados en HTML");

            var far = madrid with { Id = "far", Address = "192.0.2.250", Position = new(0, 120) };
            var conflictSession = focused with { Observations = [focused.Observations[0], new(far.Id, far.Address, clock, [new(1, "Success")])] };
            var conflict = ProgressiveSearch.Replay(new([madrid, far]), conflictSession, model);
            Check(conflict.Region!.Status == "inconsistent" && conflict.Region.Representative is null && conflict.Support is null,
                "datos incompatibles no fuerzan una coordenada ni un mapa de afinidad");
            var dateline = madrid with { Position = new(0, 179.9) };
            var datelineSession = focused with { Observations = [new(dateline.Id, dateline.Address, clock, [new(2, "Success")])] };
            var dateRegion = ProgressiveSearch.Replay(new([dateline]), datelineSession, model).Region!;
            Check(dateRegion.Cells.Any(c => c.West < -179) && dateRegion.Cells.Any(c => c.East > 179),
                "las dos ramas del antimeridiano siguen presentes");
            Console.WriteLine($"OK: {passed} comprobaciones; ninguna sonda a Internet.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally
        {
            // Resolve and verify the fresh GUID directory before deleting its test outputs.
            if (Directory.Exists(directory))
            {
                string resolved = Path.GetFullPath(directory);
                if (!string.Equals(Path.GetDirectoryName(resolved), testRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(resolved).StartsWith("netlocate-test-", StringComparison.Ordinal))
                    throw new IOException("La carpeta de limpieza no pertenece a las pruebas.");
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    public static int Benchmark()
    {
        var network = new DemoNetwork();
        var session = new MeasurementSession("benchmark", "offline", "ipv4", network.Catalog.Landmarks.Take(24)
            .Select(a => new Observation(a.Id, a.Address, DateTimeOffset.UtcNow,
                [new(Geography.DistanceKm(a.Position, network.Truth) / 100 * 1.12 + .35, "Success")])).ToList()) { Synthetic = true };
        _ = ProgressiveSearch.Replay(network.Catalog, session, new());
        long before = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) _ = ProgressiveSearch.Replay(network.Catalog, session, new());
        watch.Stop();
        Console.WriteLine(FormattableString.Invariant($"Motor + afinidad: {watch.Elapsed.TotalMilliseconds / 5:F2} ms/iteración; {(GC.GetTotalAllocatedBytes(true) - before) / 5} bytes asignados/iteración (5 repeticiones, sintético, sin red)."));
        return 0;
    }

    private sealed class RejectNetwork : IEchoTransport
    {
        public Task<EchoResult> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken ct) =>
            throw new InvalidOperationException("REPLAY intentó acceder a la red.");
    }
}
