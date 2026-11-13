using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace QuicNet.Geolocation;

public sealed record PublishedLocation(GeoPoint Position, string Country, string Region, string City, string Source);

/// <summary>Immutable embedded snapshot; never downloads, writes a cache or resolves DNS.</summary>
public sealed class EmbeddedCatalog
{
    private static readonly Lazy<(string Source, string? Date)> CityMetadata = new(() =>
    {
        using var stream = Resource("manifest.json");
        using var manifest = JsonDocument.Parse(stream);
        var city = manifest.RootElement.GetProperty("tables").GetProperty("city");
        return (city.GetProperty("source").GetString()!, city.GetProperty("databaseDate").GetString());
    });
    public static EmbeddedCatalog Default { get; } = new();
    private readonly Lazy<IpCatalogTable?> _city = new(() => HasResource("city.bin") ? new(() => Resource("city.bin"), false) : null);
    private readonly Lazy<IpCatalogTable?> _asn = new(() => HasResource("asn.bin") ? new(() => Resource("asn.bin"), true) : null);
    private readonly Lazy<IpCatalogTable?> _routers = new(() => HasResource("routers.bin") ? new(() => Resource("routers.bin"), false) : null);
    private readonly Lazy<Landmark[]> _anchors = new(() =>
    {
        using var stream = Resource("anchors.json.gz");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(gzip, GeoJsonContext.Default.LandmarkArray) ?? throw new InvalidDataException("Empty anchors");
    });
    public string Manifest { get { using var reader = new StreamReader(Resource("manifest.json")); return reader.ReadToEnd(); } }
    public string Notice { get { using var reader = new StreamReader(Resource("NOTICE.txt")); return reader.ReadToEnd(); } }
    public IReadOnlyList<Landmark> Anchors => Array.AsReadOnly(_anchors.Value);

    private static readonly Lazy<string> CitySource = new(() => CityMetadata.Value.Source + " " + CityMetadata.Value.Date);
    public PublishedLocation? Lookup(IPAddress ip) => LookupLocation(_city, ip, CitySource);
    public uint? LookupAsn(IPAddress ip) => IsPublic(ip) ? _asn.Value?.FindValue(Normalize(ip)) : null;
    public PublishedLocation? LookupRouter(IPAddress ip) => LookupLocation(_routers, ip, RouterSource);
    private static readonly Lazy<string> RouterSource = new(() =>
    {
        using var stream = Resource("manifest.json");
        using var manifest = JsonDocument.Parse(stream);
        var table = manifest.RootElement.GetProperty("tables").GetProperty("routers");
        string source = table.TryGetProperty("source", out var value) ? value.GetString()! : "IP router catalogue";
        return table.TryGetProperty("databaseDate", out var date) ? source + " " + date.GetString() : source;
    });
    private static PublishedLocation? LookupLocation(Lazy<IpCatalogTable?> lazy, IPAddress ip, Lazy<string> source)
    {
        if (!IsPublic(ip) || lazy.Value is not { } table) return null;
        return table.FindValue(Normalize(ip)) is { } id ? table.Location(id, source.Value) : null;
    }

    internal static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    public static bool IsPublic(IPAddress address)
    {
        var ip = Normalize(address);
        Span<byte> b = stackalloc byte[16];
        ip.TryWriteBytes(b, out int length);
        if (length == 4)
            return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
                !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
                !(b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2)) &&
                !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) &&
                !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Global unicast only, excluding documentation ranges.
        return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8) &&
            !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
    }

    public bool HasIpLocationDatabase => HasResource("city.bin");
    public string IpLocationSource => CityMetadata.Value.Source;
    public string? IpLocationDatabaseDate => CityMetadata.Value.Date;
    public bool HasAsnDatabase => HasResource("asn.bin");
    public bool HasRouterDatabase => HasResource("routers.bin");
    internal static bool HasResource(string name) => typeof(EmbeddedCatalog).Assembly.GetManifestResourceNames().Contains("GeoData." + name);
    internal static Stream Resource(string name) => typeof(EmbeddedCatalog).Assembly.GetManifestResourceStream("GeoData." + name)
        ?? throw new InvalidDataException("Missing embedded catalogue: " + name);

}
