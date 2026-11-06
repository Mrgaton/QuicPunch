using System.Text.Json;
using QuicNet.Serialization;
using QuicNet.Video;

namespace QuicPunchTests.Tests;

public static class FfmpegInstallationTests
{
    public static async Task RunAsync()
    {
        static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
        static FfmpegInstallOption[] Linux(string text) => FfmpegInstallation.InstallOptions("linux", FfmpegInstallation.ParseOsRelease(text));
        var windows = await FfmpegInstallation.DetectAsync("windows", "", [], (_, _) => throw new Exception("Unexpected probe"), default);
        Check(!windows.installed && windows.nativeCaptureSupported, "Missing FFmpeg must expose Windows installation help.");
        Check(windows.options[0].command == "winget install --id yt-dlp.FFmpeg --exact --source winget", "Recommend the user's modern FFmpeg package.");
        Check(windows.options[1].command.Contains("Gyan.FFmpeg"), "Offer the alternative Windows package.");
        Check(Linux("ID=debian\nPRETTY_NAME=\"Debian GNU/Linux\"")[0].command == "sudo apt update && sudo apt install ffmpeg", "Debian command.");
        Check(Linux("ID=linuxmint\nID_LIKE=\"ubuntu debian\"")[0].command.Contains("apt install ffmpeg"), "Quoted derivative ID_LIKE.");
        Check(Linux("ID=fedora\nID_LIKE=\"rhel centos\"")[0].command == "sudo dnf install ffmpeg-free", "Fedora's official package name differs.");
        Check(Linux("ID=fedora\nVARIANT_ID=silverblue")[0].command == "", "Do not suggest dnf on Atomic Fedora.");
        Check(Linux("ID=arch")[0].command == "sudo pacman -Syu ffmpeg", "Arch must avoid partial upgrades.");
        Check(Linux("# comment\nID=derivative\nID_LIKE='arch'")[0].command.Contains("pacman"), "Arch derivative.");
        Check(Linux("ID=unknown\nID_LIKE=$(untrusted)")[0].command == "", "Unknown distribution must not produce a guessed/interpolated shell command.");
        int probes = 0;
        var android = await FfmpegInstallation.DetectAsync("android", "", ["not-run"], (_, _) => { probes++; throw new Exception("Android must not probe FFmpeg"); }, default);
        Check(probes == 0 && android.options.Length == 0 && !android.nativeCaptureSupported, "Android must neither probe nor request FFmpeg.");
        var installed = await FfmpegInstallation.DetectAsync("windows", "", ["broken", "working"],
            (candidate, _) => Task.FromResult<string?>(candidate == "working" ? "ffmpeg version test" : null), default);
        Check(installed.installed && installed.executable == "working" && installed.options.Length == 0, "Skip broken installs and accept the usable candidate.");
        var linux = await FfmpegInstallation.DetectAsync("linux", "ID=fedora", [], (_, _) => throw new Exception(), default);
        Check(!linux.nativeCaptureSupported && linux.options[0].command.Contains("ffmpeg-free"), "Installation guidance must not imply Linux native capture support.");
        foreach (var status in new[] { windows, android, installed, linux })
        {
            string json = JsonSerializer.Serialize(status, SharedJson.Options);
            var decoded = JsonSerializer.Deserialize<FfmpegInstallationStatus>(json, SharedJson.Options);
            Check(JsonSerializer.Serialize(decoded, SharedJson.Options) == json, "Installation JSON must preserve the complete generated AOT contract.");
        }
        Check(await FfmpegInstallation.ProbeAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-ffmpeg"), default) == null,
            "Missing executable must return a recoverable status.");
        var actual = await FfmpegInstallation.GetStatusAsync(true, default);
        Console.WriteLine($"FFmpeg detection: {actual.system}; installed={actual.installed}; {actual.version}");
        Console.WriteLine("FFmpeg installation tests passed.");
    }
}
