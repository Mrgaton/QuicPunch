using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using QuicNet.Geolocation;

namespace NetLocate;

internal static class RegionalCatalog
{
    private static readonly Lazy<Landmark[]> Data = new(() =>
    {
        using var stream = typeof(RegionalCatalog).Assembly.GetManifestResourceStream("NetLocate.RegionalProbes.gz")
            ?? throw new InvalidDataException("Falta el catálogo regional incorporado.");
        using var manifest = typeof(RegionalCatalog).Assembly.GetManifestResourceStream("NetLocate.RegionalManifest.json")!;
        using var metadata = JsonDocument.Parse(manifest);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(metadata.RootElement.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La huella del catálogo regional incorporado no coincide.");
        stream.Position = 0;
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(gzip, GeoJsonContext.Default.LandmarkArray)
            ?? throw new InvalidDataException("Catálogo regional vacío.");
    });
    public static IReadOnlyList<Landmark> Landmarks => Array.AsReadOnly(Data.Value);
}
