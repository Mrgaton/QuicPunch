using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace QuicNet.Settings;

/// <summary>Only bounded, square AVIF images and animations are stored; source photos never leave the editor.</summary>
internal sealed class ProfileAvatarStore
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxSide = 512;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, string> _versions = new();
    public event Action? Changed;
    public ProfileAvatarStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        foreach (string path in Directory.EnumerateFiles(directory, "*.avif").Take(513))
        {
            string key = Path.GetFileNameWithoutExtension(path);
            if (key != "local" && !Guid.TryParseExact(key, "N", out _)) continue;
            try { if (new FileInfo(path).Length > MaxBytes) continue; var bytes = File.ReadAllBytes(path); if (IsValid(bytes)) _versions[key] = Hash(bytes); } catch { }
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public string LocalVersion => _versions.GetValueOrDefault("local", "");
    public Dictionary<string, string> Versions(Guid localId)
    {
        var versions = _versions.Where(p => p.Key != "local").ToDictionary(p => Guid.ParseExact(p.Key, "N").ToString(), p => p.Value);
        if (LocalVersion.Length > 0) versions[localId.ToString()] = LocalVersion;
        return versions;
    }
    public byte[]? Read(Guid? peerId = null)
    {
        string key = peerId?.ToString("N") ?? "local";
        lock (_gate)
        {
            if (!_versions.ContainsKey(key)) return null;
            try { return File.ReadAllBytes(Path.Combine(_directory, key + ".avif")); } catch { return null; }
        }
    }
    public void Set(byte[] bytes, Guid? peerId = null)
    {
        if (bytes.Length != 0 && !IsValid(bytes)) throw new InvalidDataException("La foto debe ser un AVIF cuadrado de hasta 512 × 512 píxeles y 512 KB.");
        string key = peerId?.ToString("N") ?? "local";
        lock (_gate)
        {
            if (bytes.Length > 0 && !_versions.ContainsKey(key) && _versions.Count >= 513) throw new InvalidDataException("Se ha alcanzado el límite de fotos guardadas.");
            string path = Path.Combine(_directory, key + ".avif");
            if (bytes.Length == 0) { File.Delete(path); _versions.TryRemove(key, out _); }
            else
            {
                File.WriteAllBytes(path + ".tmp", bytes);
                File.Move(path + ".tmp", path, true);
                _versions[key] = Hash(bytes);
            }
        }
        Changed?.Invoke();
    }
    public static bool IsValid(ReadOnlySpan<byte> bytes) => AvatarAvifValidator.IsValid(bytes);
}
