using System.Diagnostics;

namespace QuicPunch.Vpn;

/// <summary>Configuration only: no shell, bounded waits, and concurrent draining of both output streams.</summary>
internal static class VpnCommand
{
    internal static string Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new IOException($"{executable} configuration timed out.");
        }
        string stdout = output.GetAwaiter().GetResult();
        string stderr = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new IOException($"{executable} {string.Join(' ', arguments)} failed (exit {process.ExitCode}): {stderr.Trim()} {stdout.Trim()}");
        return stdout;
    }
}
