using System.Text.Json;
using QuicNet.Geolocation;

namespace QuicNet.Geolocation.Workbench;

public sealed record WorkbenchArchive(WorkbenchSettings Settings, LocationUpdate[] Updates, PacketRecord[] Packets, Landmark[] Landmarks)
{
    /// <summary>Reads persisted workbench recordings without sending packets.</summary>
    public static async Task<WorkbenchArchive> ReadAsync(string directory, CancellationToken ct = default)
    {
        var settings = File.Exists(Path.Combine(directory, "settings.json"))
            ? await JsonFiles.ReadAsync<WorkbenchSettings>(Path.Combine(directory, "settings.json"), ct) : new();
        var updates = new List<LocationUpdate>(); var packets = new Dictionary<int, PacketRecord>();
        var landmarks = new Dictionary<string, Landmark>();
        string journal = Path.Combine(directory, "events.jsonl");
        if (File.Exists(journal))
        {
            using var input = new StreamReader(journal, detectEncodingFromByteOrderMarks: true);
            while (await input.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                    var data = root.GetProperty("data");
                    switch (root.GetProperty("kind").GetString())
                    {
                        case "started": if (data.TryGetProperty("settings", out var s)) settings = s.Deserialize(JsonFiles.GetTypeInfo<WorkbenchSettings>()) ?? settings; break;
                        case "update": updates.Add(data.Deserialize(JsonFiles.GetTypeInfo<LocationUpdate>())!); break;
                        case "packet": var p = data.Deserialize(JsonFiles.GetTypeInfo<PacketRecord>())!; packets[p.Id] = p; break;
                        case "catalog": foreach (var a in data.Deserialize(JsonFiles.GetTypeInfo<Catalog>())!.Landmarks) landmarks[a.Address] = a; break;
                    }
                }
                catch (JsonException)
                {
                    // Only a truncated final event is recoverable. Corrupt middle events are errors.
                    if (await input.ReadLineAsync(ct) is not null) throw;
                }
            }
        }
        if (updates.Count == 0 && File.Exists(Path.Combine(directory, "updates.json")))
            updates.AddRange(await JsonFiles.ReadAsync<LocationUpdate[]>(Path.Combine(directory, "updates.json"), ct));
        if (updates.Count == 0 && File.Exists(Path.Combine(directory, "result.json")))
            updates.Add(await JsonFiles.ReadAsync<LocationUpdate>(Path.Combine(directory, "result.json"), ct));
        if (File.Exists(Path.Combine(directory, "catalog.json")))
            foreach (var a in (await JsonFiles.ReadAsync<Catalog>(Path.Combine(directory, "catalog.json"), ct)).Landmarks) landmarks[a.Address] = a;
        if (packets.Count == 0 && File.Exists(Path.Combine(directory, "session.json")))
        {
            var session = await JsonFiles.ReadAsync<MeasurementSession>(Path.Combine(directory, "session.json"), ct);
            int id = 0;
            foreach (var o in session.Observations)
            {
                foreach (var s in o.Samples)
                    packets[++id] = new(id, o.Address, 64, settings.TimeoutMs, s.Status, o.MeasuredUtc)
                    { RttMs = s.RttMs, Responder = s.Status == "Success" ? o.Address : null, Reconstructed = true };
                foreach (var h in o.Route)
                    packets[++id] = new(id, o.Address, h.Ttl, settings.TimeoutMs, h.Status, o.MeasuredUtc)
                    { RttMs = h.RttMs, Responder = h.Address, Reconstructed = true };
            }
        }
        if (updates.Count == 0) throw new InvalidDataException("La carpeta no contiene revisiones de GeoLatency.");
        return new(settings, updates.ToArray(), packets.Values.OrderBy(p => p.Id).ToArray(), landmarks.Values.ToArray());
    }
}
