using System.ComponentModel;
using System.Diagnostics;

namespace QuicNet.Video;

internal sealed record FfmpegInstallOption(string title, string command, string url, string note);
internal sealed record FfmpegInstallationStatus(string platform, string system, bool installed,
    bool nativeCaptureSupported, string? executable, string? version, string message,
    FfmpegInstallOption[] options);

/// <summary>Detects a separately installed FFmpeg. Installation commands are displayed, never executed.</summary>
internal static class FfmpegInstallation
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static FfmpegInstallationStatus? _cached;
    private static DateTimeOffset _checkedAt;

    internal static async Task<FfmpegInstallationStatus> GetStatusAsync(bool refresh, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && _cached != null && DateTimeOffset.UtcNow - _checkedAt < TimeSpan.FromMinutes(1)) return _cached;
            string platform = OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsWindows() ? "windows" :
                OperatingSystem.IsLinux() ? "linux" : "other";
            string release = "";
            if (platform == "linux")
            {
                foreach (string file in new[] { "/etc/os-release", "/usr/lib/os-release" })
                    try { if (File.Exists(file)) { release = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false); break; } }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _cached = await DetectAsync(platform, release, platform == "android" ? [] : Candidates(), ProbeAsync, ct).ConfigureAwait(false);
            _checkedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally { Gate.Release(); }
    }

    internal static string ResolveExecutable()
    {
        if (_cached is { installed: true, executable: { } executable } && File.Exists(executable)) return executable;
        return Candidates().FirstOrDefault(File.Exists) ?? "ffmpeg";
    }

    private static string[] Candidates()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("QUICNET_FFMPEG_PATH") is { Length: > 0 } configured) candidates.Add(configured);
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        // Explicit adjacent installations are supported, but the release does not ship one.
        candidates.Add(Path.Combine(AppContext.BaseDirectory, name));
        if (OperatingSystem.IsWindows())
        {
            // Explorer may retain an old PATH after winget installs a portable package.
            foreach (string root in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet") })
            {
                try
                {
                    string packages = Path.Combine(root, "Packages");
                    if (Directory.Exists(packages))
                        foreach (string package in Directory.EnumerateDirectories(packages, "*FFmpeg*", SearchOption.TopDirectoryOnly)
                            .OrderBy(p => Path.GetFileName(p).StartsWith("yt-dlp.FFmpeg_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
                            foreach (string build in Directory.EnumerateDirectories(package, "ffmpeg*", SearchOption.TopDirectoryOnly).OrderDescending())
                                candidates.Add(Path.Combine(build, "bin", "ffmpeg.exe"));
                    candidates.Add(Path.Combine(root, "Links", "ffmpeg.exe"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        var paths = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        if (OperatingSystem.IsWindows())
        {
            paths.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            paths.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }
        foreach (string path in paths.Where(p => p != null).SelectMany(p => p!.Split(Path.PathSeparator)))
        {
            string directory = path.Trim().Trim('"');
            if (Path.IsPathRooted(directory)) candidates.Add(Path.Combine(directory, name));
        }
        return candidates.Where(File.Exists).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    internal static async Task<FfmpegInstallationStatus> DetectAsync(string platform, string osRelease,
        IEnumerable<string> candidates, Func<string, CancellationToken, Task<string?>> probe, CancellationToken ct)
    {
        if (platform == "android") return new(platform, "Android", true, true, null, "FFmpeg n8.1.3",
            "FFmpeg está integrado. Utiliza los componentes MediaCodec disponibles en este dispositivo.", []);
        var values = ParseOsRelease(osRelease);
        string system = platform == "windows" ? "Windows" : values.GetValueOrDefault("PRETTY_NAME", platform == "linux" ? "Linux" : "Este sistema");
        foreach (string candidate in candidates)
        {
            string? version = await probe(candidate, ct).ConfigureAwait(false);
            if (version != null) return new(platform, system, true, platform == "windows", candidate, version,
                platform == "windows" ? "FFmpeg instalado. Los codificadores se comprueban por separado." :
                "FFmpeg instalado. La captura nativa de esta aplicación está disponible actualmente en Windows; la captura nativa no está disponible aquí.", []);
        }
        return new(platform, system, false, platform == "windows", null, null,
            platform == "windows" ? "No se ha encontrado un FFmpeg que funcione. Instálalo para habilitar la captura nativa de pantalla y sus vistas previas. Puedes seguir usando la captura del navegador." :
            "FFmpeg no está instalado. Su instalación es opcional: la captura nativa de esta aplicación está disponible actualmente en Windows. Aquí puedes usar la captura del navegador.",
            InstallOptions(platform, values));
    }

    internal static Dictionary<string, string> ParseOsRelease(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in content.Split('\n'))
        {
            string text = line.Trim();
            if (text.StartsWith('#')) continue;
            int equal = text.IndexOf('=');
            if (equal <= 0) continue;
            string value = text[(equal + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];
            result[text[..equal].Trim()] = value;
        }
        return result;
    }

    internal static FfmpegInstallOption[] InstallOptions(string platform, Dictionary<string, string> release)
    {
        if (platform == "android") return [];
        if (platform == "windows") return [
            new("WinGet · yt-dlp.FFmpeg (recomendado)", "winget install --id yt-dlp.FFmpeg --exact --source winget",
                "https://github.com/yt-dlp/FFmpeg-Builds/releases", "Compilaciones recientes de FFmpeg mantenidas por yt-dlp. Pega el comando en PowerShell o Terminal de Windows."),
            new("WinGet · Gyan.FFmpeg", "winget install --id Gyan.FFmpeg --exact --source winget",
                "https://www.gyan.dev/ffmpeg/builds/", "Alternativa de FFmpeg. Si no tienes WinGet, puedes descargar una compilación y añadir su carpeta bin al PATH.")];
        if (platform == "linux")
        {
            string id = release.GetValueOrDefault("ID", "").ToLowerInvariant();
            var family = release.GetValueOrDefault("ID_LIKE", "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Is(string name) => id == name || family.Contains(name);
            // Match the actual distribution before falling back to its declared family.
            if (id is "arch" or "manjaro" or "endeavouros" || (id != "fedora" && Is("arch")))
                return [new("Arch Linux y derivados", "sudo pacman -Syu ffmpeg", "https://archlinux.org/packages/extra/x86_64/ffmpeg/", "El comando también actualiza el sistema para evitar una actualización parcial.")];
            if (id == "fedora" || Is("fedora"))
            {
                if (release.GetValueOrDefault("VARIANT_ID", "") is "silverblue" or "kinoite" or "sericea" or "onyx" or "coreos")
                    return [new("Fedora Atomic", "", "https://docs.fedoraproject.org/en-US/fedora-silverblue/getting-started/", "Utiliza el método de instalación de tu variante inmutable; no el comando dnf del sistema tradicional.")];
                return [new("Fedora", "sudo dnf install ffmpeg-free", "https://packages.fedoraproject.org/pkgs/ffmpeg/ffmpeg-free/", "Paquete oficial con un conjunto limitado de códecs."),
                    new("Fedora · más códecs (RPM Fusion)", "", "https://rpmfusion.org/Howto/Multimedia", "Si necesitas códecs que el paquete oficial no incluye, sigue la guía de RPM Fusion.")];
            }
            if (id is "debian" or "ubuntu" || Is("debian") || Is("ubuntu"))
                return [new("Debian / Ubuntu y derivados", "sudo apt update && sudo apt install ffmpeg", "https://packages.debian.org/stable/ffmpeg", "Pega el comando en una terminal. Se instalará la versión disponible en los repositorios de tu distribución.")];
        }
        return [new("Instalación de FFmpeg", "", "https://ffmpeg.org/download.html", "Consulta las instrucciones para tu sistema y añade ffmpeg al PATH.")];
    }

    internal static async Task<string?> ProbeAsync(string executable, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-version");
        try
        {
            if (!process.Start()) return null;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            string? version = (stdout.Result + "\n" + stderr.Result).Split('\n')
                .Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("ffmpeg version ", StringComparison.Ordinal));
            return process.ExitCode == 0 ? version : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
    }
}
