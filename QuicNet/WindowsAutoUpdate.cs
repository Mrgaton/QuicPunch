using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using QuicNet.Updates;

namespace QuicNet;

internal static class WindowsAutoUpdate
{
    // Separate helper process runs a COPY of the current EXE, never a new/unverified executable.
    public static Task<bool> InstallAsync(string package, UpdateArtifact artifact, bool automatic)
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) || QuicNet.Protocols.VoiceCallHandler.ActiveCalls.Count != 0)
            return Task.FromResult(false);
        string target = Environment.ProcessPath ?? throw new InvalidOperationException("No se encuentra el ejecutable.");
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported && (OperatingSystem.IsLinux() || Path.GetExtension(target) == ".exe"))
        {
            string helper = Path.Combine(Path.GetDirectoryName(package)!, "updater-" + Environment.ProcessId + (OperatingSystem.IsWindows() ? ".exe" : ""));
            File.Copy(target, helper, true);
            MakeExecutable(helper);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--apply-update", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                target, package, artifact.Sha256, artifact.Size.ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            if (Process.Start(start) == null) throw new IOException("No se pudo iniciar el instalador.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                Environment.Exit(0);
            });
            return Task.FromResult(true);
        }
        throw new InvalidOperationException("La actualización automática del PC requiere la distribución Native AOT del CDN.");
    }

    public static async Task<int> ApplyAsync(string[] args)
    {
        if (args.Length != 7) return 2;
        string target = Path.GetFullPath(args[3]), package = Path.GetFullPath(args[4]);
        string log = Path.Combine(Path.GetDirectoryName(package)!, "install.log");
        string replacement = target + ".update-new", backup = target + ".previous";
        bool replaced = false;
        try
        {
            if (target == Environment.ProcessPath || (OperatingSystem.IsWindows() && !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ||
                !package.EndsWith(OperatingSystem.IsWindows() ? ".exe" : ".bin", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ruta de actualización incorrecta.");
            int pid = int.Parse(args[1], CultureInfo.InvariantCulture);
            long started = long.Parse(args[2], CultureInfo.InvariantCulture);
            try
            {
                using var parent = Process.GetProcessById(pid);
                if (parent.StartTime.ToUniversalTime().Ticks != started) throw new InvalidOperationException("El proceso original ha cambiado.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await parent.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { /* The original process has already stopped. */ }
            var artifact = new UpdateArtifact("", 1, new Uri(ReleaseIdentity.FeedUrl), args[5], long.Parse(args[6], CultureInfo.InvariantCulture));
            await UpdateManifest.VerifyFileAsync(package, artifact);
            File.Copy(package, replacement, true);
            await UpdateManifest.VerifyFileAsync(replacement, artifact);
            MakeExecutable(replacement);
            File.Replace(replacement, target, backup, true);
            replaced = true;
            // New process must acknowledge startup before the backup is considered healthy.
            string health = Path.Combine(Path.GetDirectoryName(package)!, Guid.NewGuid().ToString("N") + ".healthy");
            var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
            start.ArgumentList.Add("--update-health");
            start.ArgumentList.Add(health);
            using var child = Process.Start(start) ?? throw new IOException("No se pudo reiniciar QuicNet.");
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!File.Exists(health) && !child.HasExited && DateTime.UtcNow < deadline) await Task.Delay(500);
            if (!File.Exists(health))
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                throw new IOException("La nueva versión no confirmó el arranque. Se restaura la anterior.");
            }
            File.Delete(health);
            File.Delete(package);
            File.WriteAllText(log, DateTimeOffset.Now + " Actualización instalada y arranque confirmado.");
            // Keep target.previous for a manual rollback if a later functional issue is found.
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(package + ".failed", DateTimeOffset.UtcNow + " " + ex.Message);
                if (replaced && File.Exists(backup)) File.Copy(backup, target, true);
                if (File.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                File.WriteAllText(log, DateTimeOffset.Now + " " + ex);
            }
            catch (Exception rollback) { File.AppendAllText(log, "\nRestauración: " + rollback); }
            return 1;
        }
        finally { try { File.Delete(replacement); } catch { } }
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
