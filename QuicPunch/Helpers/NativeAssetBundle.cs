using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace QuicPunch.Helpers;

/// <summary>Extracts native dependencies embedded in the signed single-file AOT executable.</summary>
internal static class NativeAssetBundle
{
    private static string? _directory;
    internal static string? Prepare()
    {
        if (_directory != null) return _directory;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return null;
        var assembly = typeof(NativeAssetBundle).Assembly;
        string[] resources = assembly.GetManifestResourceNames().Where(n => n.StartsWith("QuicNet.Native.", StringComparison.Ordinal)).Order().ToArray();
        if (resources.Length == 0) return null;
        // Content-addressed files isolate running and staged releases, including the updater helper.
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var assets = new List<(string Name, byte[] Bytes)>();
        foreach (string resource in resources)
        {
            using var input = assembly.GetManifestResourceStream(resource)!;
            using var output = new MemoryStream();
            input.CopyTo(output);
            byte[] bytes = output.ToArray();
            identity.AppendData(bytes);
            assets.Add((resource["QuicNet.Native.".Length..], bytes));
        }
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuicNet", "native", Convert.ToHexString(identity.GetHashAndReset()));
        Directory.CreateDirectory(directory);
        foreach (var asset in assets)
        {
            string path = Path.Combine(directory, asset.Name);
            byte[] expected = SHA256.HashData(asset.Bytes);
            bool valid = File.Exists(path) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), expected);
            if (!valid)
            {
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temporary, asset.Bytes); File.Move(temporary, path, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        if (OperatingSystem.IsWindows() && !SetDllDirectory(directory)) throw new IOException("No se pudo configurar la carpeta de bibliotecas nativas.");
        // Explicit full paths prevent a library beside the downloaded EXE from shadowing embedded assets.
        string? mediaOverride = Environment.GetEnvironmentVariable("QUICNET_MEDIA_LIBRARY_DIRECTORY");
        string[] mediaLibraries = ["avutil-60.dll", "swscale-9.dll", "avcodec-62.dll", "nettt_media.dll"];
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(mediaOverride))
        {
            // Explicit user opt-in permits replacing LGPL libraries with ABI-compatible builds.
            // Other signed native components continue to use the verified embedded bundle.
            mediaOverride = Path.GetFullPath(mediaOverride);
            foreach (string name in mediaLibraries)
            {
                string path = Path.Combine(mediaOverride, name);
                if (!File.Exists(path) || LoadLibraryEx(path, 0, 8) == 0)
                    throw new IOException("No se pudo cargar la biblioteca multimedia alternativa: " + path);
            }
        }
        foreach (var asset in assets.Where(a => a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Name == "Photino.Native.dll" ? 1 : 0))
            if (string.IsNullOrWhiteSpace(mediaOverride) || !mediaLibraries.Contains(asset.Name))
                NativeLibrary.Load(Path.Combine(directory, asset.Name));
        if (OperatingSystem.IsLinux())
            foreach (var asset in assets.Where(a => a.Name.EndsWith(".so", StringComparison.Ordinal) && !a.Name.Contains("Photino", StringComparison.OrdinalIgnoreCase)))
                NativeLibrary.Load(Path.Combine(directory, asset.Name));
        return _directory = directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string path);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
}
