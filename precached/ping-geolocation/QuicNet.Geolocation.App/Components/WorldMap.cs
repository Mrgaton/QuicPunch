using System.IO.Compression;

namespace QuicNet.Geolocation.App.Components;

public static class WorldMap
{
    public static readonly byte[] Json = Load();
    private static byte[] Load()
    {
        using var compressed = typeof(WorldMap).Assembly.GetManifestResourceStream("GeoLatency.Map.gz")!;
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
