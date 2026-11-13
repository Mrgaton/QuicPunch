using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;

namespace QuicNet.Geolocation;

public sealed record Locality(int Id, string Name, string Municipality, string Region, string CountryCode,
    GeoPoint Position, long Population, string FeatureCode)
{
    [JsonIgnore] public string Aliases { get; init; } = "";
    public string DisplayName => string.IsNullOrWhiteSpace(Municipality) || Name == Municipality ? Name : $"{Name} · {Municipality}";
}
public sealed record NearbyLocality(Locality Place, double DistanceKm);

/// <summary>Offline GeoNames points. Municipal relationships are declared in the data, not guessed from distance.</summary>
public sealed class LocalityCatalog
{
    public static LocalityCatalog Default { get; } = new();
    private sealed record Page(long Offset, int Length, int Count);
    private sealed record Index(int Count, Dictionary<(int Lat, int Lon), Page> Cells);
    private readonly Lazy<Index> _index = new(Load);
    private readonly object _gate = new();
    private readonly Dictionary<(int, int), Locality[]> _cache = [];
    private readonly Queue<(int, int)> _order = [];
    public int Count => _index.Value.Count;

    public NearbyLocality[] Near(GeoPoint point, int count = 6, double maxDistanceKm = 100, string? country = null)
    {
        point.Validate();
        if (count is < 1 or > 1024 || !double.IsFinite(maxDistanceKm) || maxDistanceKm is < 0 or > 20100)
            throw new ArgumentOutOfRangeException(nameof(count));
        double latSpan = maxDistanceKm / 110.5;
        double extreme = Math.Min(90, Math.Abs(point.Latitude) + latSpan);
        double lonSpan = extreme >= 89.9 ? 180 : Math.Min(180, maxDistanceKm / (110.5 * Math.Cos(extreme * Math.PI / 180)));
        return Cells(point.Latitude - latSpan, point.Longitude - lonSpan, point.Latitude + latSpan, point.Longitude + lonSpan)
            .Where(p => p.FeatureCode != "ANCHOR_CITY_LABEL")
            .Where(p => country == null || p.CountryCode.Equals(country, StringComparison.OrdinalIgnoreCase))
            .Select(p => new NearbyLocality(p, Geography.DistanceKm(point, p.Position)))
            .Where(p => p.DistanceKm <= maxDistanceKm).OrderBy(p => p.DistanceKm).ThenBy(p => p.Place.Id).Take(count).ToArray();
    }

    public Locality[] InBounds(double south, double west, double north, double east, int limit = 400)
    {
        if (!double.IsFinite(south) || !double.IsFinite(north) || !double.IsFinite(west) || !double.IsFinite(east) ||
            south < -90 || north > 90 || south > north || west < -180 || east > 180 || west > east || limit is < 1 or > 600)
            throw new ArgumentException("Límites de mapa inválidos.");
        return Cells(south, west, north, east).Where(p => p.Position.Latitude >= south && p.Position.Latitude <= north &&
            p.Position.Longitude >= west && p.Position.Longitude <= east)
            .OrderByDescending(p => p.Population).ThenBy(p => p.Id).Take(limit).ToArray();
    }

    public Locality? MatchPublished(PublishedLocation place)
    {
        if (string.IsNullOrWhiteSpace(place.City)) return null;
        string name = NormalizeName(place.City);
        return Near(place.Position, 1024, 75, place.Country).Select(n => n.Place)
            .FirstOrDefault(p => NormalizeName(p.Name) == name || p.Aliases.Split('|').Any(a => NormalizeName(a) == name));
    }

    private IEnumerable<Locality> Cells(double south, double west, double north, double east)
    {
        int minLat = Math.Clamp((int)Math.Floor(south), -90, 89), maxLat = Math.Clamp((int)Math.Floor(north), -90, 89);
        int minLon = (int)Math.Floor(west), maxLon = (int)Math.Floor(east);
        if (east - west >= 360) { minLon = -180; maxLon = 179; }
        maxLon = Math.Min(maxLon, minLon + 359);
        for (int lat = minLat; lat <= maxLat; lat++) for (int lon = minLon; lon <= maxLon; lon++)
        {
            var key = (lat, ((lon + 180) % 360 + 360) % 360 - 180);
            if (!_index.Value.Cells.TryGetValue(key, out var page)) continue;
            Locality[] values;
            lock (_gate)
            {
                if (!_cache.TryGetValue(key, out values!))
                {
                    values = Read(page);
                    if (_cache.Count >= 128) _cache.Remove(_order.Dequeue());
                    _cache.Add(key, values); _order.Enqueue(key);
                }
            }
            foreach (var value in values) yield return value;
        }
    }

    private static string NormalizeName(string name) => new(name.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
        .Select(char.ToUpperInvariant).ToArray());

    private static Stream Resource() => typeof(LocalityCatalog).Assembly.GetManifestResourceStream("GeoData.localities.bin")
        ?? throw new InvalidDataException("Falta el catálogo de localidades.");

    private static Index Load()
    {
        using var source = Resource();
        using var reader = new BinaryReader(source, Encoding.UTF8);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "QPL2") throw new InvalidDataException("Catálogo de localidades incompatible.");
        int count = reader.ReadInt32();
        if (count is < 1 or > 500_000) throw new InvalidDataException("Tamaño de catálogo inválido.");
        int cells = reader.ReadInt32();
        if (cells is < 1 or > 64_800) throw new InvalidDataException("Índice geográfico inválido.");
        var index = new Dictionary<(int, int), Page>();
        for (int i = 0; i < cells; i++)
        {
            var key = (reader.ReadInt16(), reader.ReadInt16());
            var page = new Page(reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32());
            if (page.Offset < 12 + cells * 20 || page.Length <= 0 || page.Offset > source.Length - page.Length || page.Count is < 1 or > 500_000)
                throw new InvalidDataException("Página de localidades inválida.");
            index.Add(key, page);
        }
        return new(count, index);
    }

    private static Locality[] Read(Page page)
    {
        using var source = Resource(); source.Seek(page.Offset, SeekOrigin.Begin);
        var bytes = new byte[page.Length]; source.ReadExactly(bytes);
        using var input = new MemoryStream(bytes);
        using var compressed = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(compressed, Encoding.UTF8);
        var places = new Locality[page.Count];
        string Text() => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
        for (int i = 0; i < places.Length; i++)
        {
            int id = reader.ReadInt32(); var point = new GeoPoint(reader.ReadSingle(), reader.ReadSingle()); long population = reader.ReadInt64();
            point.Validate(); string name = Text(), municipality = Text(), region = Text(), country = Text(), feature = Text(), aliases = Text();
            places[i] = new(id, name, municipality, region, country, point, population, feature) { Aliases = aliases };
        }
        return places;
    }
}
