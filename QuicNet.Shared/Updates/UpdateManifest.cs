using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuicNet.Updates;

public sealed record CompressedUpdate(Uri Url, string Sha256, long Size, int WindowLog);
public sealed record UpdateArtifact(string Version, long Build, Uri Url, string Sha256, long Size, string Channel = "stable", CompressedUpdate? Compressed = null);
public sealed record UpdateStatus(string CurrentVersion, long CurrentBuild, string Platform, bool Automatic,
    string Stage = "idle", string? LatestVersion = null, long? LatestBuild = null,
    long Downloaded = 0, long Total = 0, string? Message = null, DateTimeOffset? LastCheck = null,
    string Channel = "stable", string CurrentChannel = "stable", string? LatestChannel = null, long Revision = 0);

public static class ReleaseIdentity
{
    public const string FeedUrl = "https://cdn.gato.ovh/QuicNet/latest.json";
    public static string Channel => typeof(ReleaseIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "ReleaseChannel")?.Value == "beta" ? "beta" : "stable";
    public static bool IsValidChannel(string? channel) => channel is "stable" or "beta";
    public static string GetFeedUrl(string channel) => channel switch
    {
        "stable" => FeedUrl,
        "beta" => "https://cdn.gato.ovh/QuicNet/latest-beta.json",
        _ => throw new ArgumentException("Canal de actualización desconocido.", nameof(channel))
    };
    public static string Version => typeof(ReleaseIdentity).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.1.0";
    public static long Build => long.Parse(typeof(ReleaseIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(a => a.Key == "ReleaseNumber").Value!, CultureInfo.InvariantCulture);
}

public static class UpdateManifest
{
    public const int MaximumManifestBytes = 64 * 1024;
    public const long MaximumArtifactBytes = 512L * 1024 * 1024;

    public static string ReadPublicKey()
    {
        using var stream = typeof(UpdateManifest).Assembly.GetManifestResourceStream("QuicNet.Updates.release-public.pem")
            ?? throw new InvalidOperationException("Falta la clave pública de actualizaciones.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // The signature covers the EXACT payload bytes. No JSON reserialization/canonicalization.
    public static UpdateArtifact Parse(ReadOnlyMemory<byte> envelope, Uri feed, string platform, string publicKey, string expectedChannel = "stable")
    {
        if (envelope.Length > MaximumManifestBytes) throw new InvalidDataException("Manifiesto demasiado grande.");
        using var document = JsonDocument.Parse(envelope);
        byte[] payload = Convert.FromBase64String(document.RootElement.GetProperty("payload").GetString()!);
        byte[] signature = Convert.FromBase64String(document.RootElement.GetProperty("signature").GetString()!);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("La firma de la actualización no es válida.");
        using var manifest = JsonDocument.Parse(payload);
        var root = manifest.RootElement;
        string? channel = root.TryGetProperty("channel", out var channelElement) ? channelElement.GetString() : "stable";
        if (!ReleaseIdentity.IsValidChannel(expectedChannel) || !ReleaseIdentity.IsValidChannel(channel) || channel != expectedChannel)
            throw new InvalidDataException("El manifiesto no pertenece al canal seleccionado.");
        if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Versión de manifiesto desconocida.");
        string version = root.GetProperty("version").GetString()!;
        if (!System.Version.TryParse(version, out _)) throw new InvalidDataException("Versión inválida.");
        long build = root.GetProperty("build").GetInt64();
        var artifact = root.GetProperty("artifacts").GetProperty(platform);
        var url = new Uri(feed, artifact.GetProperty("url").GetString());
        ValidateDownloadUri(feed, url);
        string hash = artifact.GetProperty("sha256").GetString()!;
        long size = artifact.GetProperty("size").GetInt64();
        if (build <= 0 || size <= 0 || size > MaximumArtifactBytes || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Metadatos de actualización inválidos.");
        string extension = PlatformExtension(platform);
        if (!url.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Formato de actualización incorrecto.");
        CompressedUpdate? compressed = null;
        if (artifact.TryGetProperty("compressed", out var zstd))
        {
            var compressedUrl = new Uri(feed, zstd.GetProperty("url").GetString());
            ValidateDownloadUri(feed, compressedUrl);
            string compressedHash = zstd.GetProperty("sha256").GetString()!;
            long compressedSize = zstd.GetProperty("size").GetInt64();
            int windowLog = zstd.GetProperty("windowLog").GetInt32();
            if (zstd.GetProperty("algorithm").GetString() != "zstd" || !compressedUrl.AbsolutePath.EndsWith(extension + ".zst", StringComparison.OrdinalIgnoreCase) ||
                compressedSize <= 0 || compressedSize > MaximumArtifactBytes || compressedHash.Length != 64 || !compressedHash.All(Uri.IsHexDigit) || windowLog is < 10 or > 27)
                throw new InvalidDataException("Metadatos Zstandard inválidos.");
            compressed = new(compressedUrl, compressedHash, compressedSize, windowLog);
        }
        return new(version, build, url, hash, size, channel!, compressed);
    }

    public static string PlatformExtension(string platform) => platform switch
    {
        "win-x64" => ".exe", "android-arm64" => ".apk", "linux-x64" => ".bin",
        _ => throw new InvalidDataException("Plataforma de actualización no compatible.")
    };

    public static void ValidateDownloadUri(Uri feed, Uri url)
    {
        string directory = feed.AbsolutePath[..(feed.AbsolutePath.LastIndexOf('/') + 1)];
        if (feed.Scheme != "https" || url.Scheme != "https" || url.Authority != feed.Authority ||
            !url.AbsolutePath.StartsWith(directory, StringComparison.Ordinal) || !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Fragment) || !string.IsNullOrEmpty(url.Query) ||
            Uri.UnescapeDataString(url.AbsolutePath).Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("La descarga debe pertenecer al CDN HTTPS configurado.");
    }

    public static async Task VerifyFileAsync(string path, UpdateArtifact artifact, CancellationToken token = default)
    {
        using var file = File.OpenRead(path);
        if (file.Length != artifact.Size) throw new InvalidDataException("Tamaño de actualización incorrecto.");
        byte[] actual = await SHA256.HashDataAsync(file, token);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(artifact.Sha256)))
            throw new InvalidDataException("El SHA-256 de la actualización no coincide.");
    }
}
