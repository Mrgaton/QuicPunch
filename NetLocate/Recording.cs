using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuicNet.Geolocation;

namespace NetLocate;

internal sealed class RecordingWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private long _bytes;
    public RecordingHeader Header { get; }
    public RecordingWriter(string path, Catalog catalog, ProgressiveSearchOptions options)
        : this(path, new(2, ProgressiveSearch.EngineVersion, Hash(catalog), catalog, options with { Planner = options.Planner ?? "information" })
        { PlannerVersion = options.Planner == "legacy" ? "legacy-v1" : options.Planner == "regional" ? ProgressiveSearchPlanner.RegionalVersion : InformationLandmarkPlanner.Version }) { }
    public RecordingWriter(string path, RecordingHeader header)
    {
        Header = header;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
        Append(new("header", Header: Header));
    }
    public void Append(JournalRecord value)
    {
        string line = JsonSerializer.Serialize(value, GeoJsonContext.Default.JournalRecord);
        long bytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
        if (line.Length > 16 * 1024 * 1024 || _bytes + bytes > 256L * 1024 * 1024)
            throw new InvalidDataException("El registro alcanzó el límite de tamaño; las líneas previas siguen siendo reproducibles.");
        _writer.WriteLine(line); _bytes += bytes;
    }
    public void Dispose() => _writer.Dispose();
    public static string Hash(Catalog catalog) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(catalog, GeoJsonContext.Default.Catalog)));
}

internal static class RecordingReader
{
    public static (RecordingHeader Header, ProgressiveSearchResult Result) Read(string path)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException("Registro mayor de 256 MiB.");
        using var input = new StreamReader(path, Encoding.UTF8, true);
        RecordingHeader? header = null;
        var rounds = new List<SearchRound>();
        string state = "partial-recording";
        int lineNumber = 0;
        bool finished = false;
        while (input.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > 16 * 1024 * 1024) throw new InvalidDataException("Línea JSONL demasiado grande.");
            if (finished) throw new InvalidDataException("Hay datos después del cierre del registro.");
            JournalRecord row;
            try { row = JsonSerializer.Deserialize(line, GeoJsonContext.Default.JournalRecord)
                    ?? throw new InvalidDataException("Evento vacío."); }
            catch (JsonException e)
            {
                if (header is not null && rounds.Count > 0 && input.Peek() == -1)
                { state = "recovered-truncated-tail"; break; }
                throw new InvalidDataException($"JSONL inválido en línea {lineNumber}.", e);
            }
            if (finished) throw new InvalidDataException("Hay datos después del cierre del registro.");
            switch (row.Kind)
            {
                case "header":
                    if (header is not null || rounds.Count > 0 || row.Header is null)
                        throw new InvalidDataException("Cabecera duplicada o ausente.");
                    header = row.Header;
                    if (header.Options is { Planner: null }) header = header with { Options = header.Options with { Planner = "legacy" } };
                    if (header.SchemaVersion is not (1 or 2) || header.EngineVersion is not ("netlocate-progressive-1" or "netlocate-progressive-2"))
                        throw new InvalidDataException("Versión de registro o motor no compatible.");
                    if (header.Catalog is null || header.Options is null) throw new InvalidDataException("Cabecera incompleta.");
                    header.Catalog.Validate(); header.Options.Validate();
                    if (!string.Equals(header.CatalogSha256, RecordingWriter.Hash(header.Catalog), StringComparison.Ordinal))
                        throw new InvalidDataException("La huella del catálogo no coincide.");
                    break;
                case "round":
                    if (header is null || row.Round is not { } round || round.Number != rounds.Count + 1 ||
                        round.Number > header.Options.Rounds || round.SentPackets < 0 ||
                        round.Session is null || round.Selection is null || round.Session.Observations is null ||
                        round.Session.Synthetic != header.Options.Synthetic)
                        throw new InvalidDataException("Ronda inválida o fuera de secuencia.");
                    if (round.Selection.Any(s => s is null) || round.Session.Observations.Any(o => o is null || o.Samples is null || o.Route is null))
                        throw new InvalidDataException("Ronda incompleta.");
                    if (round.Session.Observations.Select(o => o.LandmarkId).Distinct(StringComparer.Ordinal).Count() != round.Session.Observations.Count)
                        throw new InvalidDataException("Observaciones duplicadas en la ronda.");
                    if (round.Selection.Any(s => !header.Catalog.Landmarks.Any(a => a.Id == s.LandmarkId)))
                        throw new InvalidDataException("Selección con landmark desconocido.");
                    if (rounds.LastOrDefault() is { } prior)
                    {
                        if (prior.Session.NetworkContext != round.Session.NetworkContext ||
                            prior.Session.TargetId != round.Session.TargetId ||
                            prior.Session.AddressFamily != round.Session.AddressFamily ||
                            prior.SentPackets > round.SentPackets)
                            throw new InvalidDataException("El registro mezcla conexiones.");
                        foreach (var old in prior.Session.Observations)
                        {
                            var current = round.Session.Observations.SingleOrDefault(o => o.LandmarkId == old.LandmarkId);
                            if (current is null || !current.Samples.Take(old.Samples.Count).SequenceEqual(old.Samples))
                                throw new InvalidDataException("Una ronda perdió o alteró muestras anteriores.");
                        }
                    }
                    // Never trust a stored point, field or region: rebuild them from frozen evidence.
                    rounds.Add(round with { Update = ProgressiveSearch.Replay(header.Catalog, round.Session,
                        header.Options.Model, round.Number, round.SentPackets) });
                    break;
                case "finished":
                    if (header is null || string.IsNullOrWhiteSpace(row.State))
                        throw new InvalidDataException("Cierre inválido.");
                    state = row.State; finished = true;
                    break;
                default: throw new InvalidDataException("Tipo de evento desconocido.");
            }
        }
        return (header ?? throw new InvalidDataException("Falta la cabecera JSONL."), new(state, rounds.ToArray()));
    }
}

internal static class AtomicFile
{
    public static void Write(string path, string content)
    {
        string destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
