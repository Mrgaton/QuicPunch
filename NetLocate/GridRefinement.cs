using System.Globalization;
using System.Security.Cryptography;
using QuicNet.Geolocation;

namespace NetLocate;

/// <summary>A derived offline recording changes only discretisation, never measurements or physical bounds.</summary>
internal static class GridRefinement
{
    public static int Run(string[] args, CancellationToken cancellation = default)
    {
        if (args.Length < 2) throw new ArgumentException("Falta el registro JSONL.");
        string? output = null;
        double resolution = 5;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 2; i < args.Length; i++)
        {
            string key = args[i];
            if (!seen.Add(key) || ++i >= args.Length) throw new ArgumentException("Opción duplicada o sin valor.");
            if (key == "--out") output = args[i];
            else if (key == "--resolution-km") resolution = double.Parse(args[i], CultureInfo.InvariantCulture);
            else throw new ArgumentException("Opción desconocida: " + key);
        }
        output = Path.GetFullPath(output ?? "netlocate-refined-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        var (header, original) = RecordingReader.Read(args[1]);
        cancellation.ThrowIfCancellationRequested();
        if (resolution > header.Options.Model.ResolutionKm) throw new ArgumentException("refine-grid solo admite objetivos de resolución iguales o menores que el original.");
        // Bound cumulative JSONL size as well as compute cost. A finer target is a request;
        // all conservative cells survive if the budget prevents reaching that target.
        var model = header.Options.Model with { ResolutionKm = resolution, MaxCells = 40_000 };
        model.Validate();
        string parent;
        using (var source = File.OpenRead(args[1])) parent = Convert.ToHexString(SHA256.HashData(source));
        var derivedHeader = header with { SchemaVersion = 2, EngineVersion = ProgressiveSearch.EngineVersion,
            Options = header.Options with { Model = model }, RefinedFromSha256 = parent };
        var frames = new List<SearchRound>();
        using (var journal = new RecordingWriter(Path.Combine(output, "measurements.jsonl"), derivedHeader))
        {
            foreach (var round in original.Rounds)
            {
                cancellation.ThrowIfCancellationRequested();
                var fine = round with { Update = ProgressiveSearch.Replay(header.Catalog, round.Session, model, round.Number, round.SentPackets) };
                frames.Add(fine); journal.Append(new("round", Round: fine));
            }
            journal.Append(new("finished", State: "offline-grid-refined"));
        }
        Program.Save(output, derivedHeader, new("offline-grid-refined", frames.ToArray()), replay: true);
        Console.WriteLine("Malla refinada SIN RED: mismas muestras y cotas; resolución objetivo " + resolution.ToString(CultureInfo.InvariantCulture) + " km. No implica esa precisión geográfica.");
        Console.WriteLine(Path.Combine(output, "report.html"));
        return frames.Count == 0 ? 2 : 0;
    }
}
