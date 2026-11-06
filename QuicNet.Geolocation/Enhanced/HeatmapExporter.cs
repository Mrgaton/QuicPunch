using System.Text;
using System.Text.Json;

namespace QuicNet.Geolocation;

/// <summary>GeoJSON output for offline map consumers. Weights are qualitative support, not probability.</summary>
public static class HeatmapExporter
{
    public static async Task WriteAsync(string path, LocationUpdate update, CancellationToken ct = default)
    {
        string destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var json = new Utf8JsonWriter(stream, new() { Indented = false });
                json.WriteStartObject(); json.WriteString("type", "FeatureCollection");
                json.WriteString("weightMeaning", "uncalibrated-relative-affinity");
                json.WriteString("phase", update.Phase); json.WriteBoolean("isFinal", update.IsFinal);
                json.WriteStartArray("features");
                bool declaredOnly = update.Region?.Constraints.Count == 0 && update.RegionalRefinement is not null;
                var mapRegion = declaredOnly ? update.RegionalRefinement!.Region : update.Region;
                foreach (var cell in mapRegion?.Cells ?? [])
                {
                    ct.ThrowIfCancellationRequested();
                    double score = update.Enhanced?.Hierarchy.Hypotheses.Where(h => h.Level != "country")
                        .Select(h => h.RelativeScore * Math.Exp(-Geography.DistanceKm(cell.Center, h.Position) / Math.Max(25, h.SearchRadiusKm)))
                        .DefaultIfEmpty(0).Max() ?? 0;
                    json.WriteStartObject(); json.WriteString("type", "Feature");
                    json.WriteStartObject("properties"); json.WriteString("kind", declaredOnly ? "conditional-operator-cell" : "physically-compatible-cell");
                    json.WriteNumber("affinity", score); json.WriteNumber("areaKm2", cell.AreaKm2); json.WriteEndObject();
                    json.WriteStartObject("geometry"); json.WriteString("type", "Polygon");
                    json.WriteStartArray("coordinates"); json.WriteStartArray();
                    foreach (var (lon, lat) in new[] { (cell.West, cell.South), (cell.East, cell.South), (cell.East, cell.North), (cell.West, cell.North), (cell.West, cell.South) })
                    { json.WriteStartArray(); json.WriteNumberValue(lon); json.WriteNumberValue(lat); json.WriteEndArray(); }
                    json.WriteEndArray(); json.WriteEndArray(); json.WriteEndObject(); json.WriteEndObject();
                }
                foreach (var h in update.Enhanced?.Hierarchy.Hypotheses ?? [])
                {
                    json.WriteStartObject(); json.WriteString("type", "Feature");
                    json.WriteStartObject("properties"); json.WriteString("kind", "search-hypothesis"); json.WriteString("label", h.Label);
                    json.WriteString("level", h.Level); json.WriteString("basis", h.Basis); json.WriteNumber("relativeScore", h.RelativeScore);
                    json.WriteBoolean("confirmed", false); json.WriteEndObject();
                    json.WriteStartObject("geometry"); json.WriteString("type", "Point");
                    json.WriteStartArray("coordinates"); json.WriteNumberValue(h.Position.Longitude); json.WriteNumberValue(h.Position.Latitude);
                    json.WriteEndArray(); json.WriteEndObject(); json.WriteEndObject();
                }
                json.WriteEndArray(); json.WriteEndObject(); await json.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
