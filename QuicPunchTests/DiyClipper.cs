using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class DiyClipper
{

    public static bool SetText(string? text)
    {
        text ??= string.Empty;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RunWithInput("clip", "", text);
        }
        return
            RunWithInput("wl-copy", "", text) ||
            RunWithInput("xclip", "-selection clipboard", text) ||
            RunWithInput("xsel", "--clipboard --input", text);
    }

    public static string? GetText()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RunAndCapture("powershell", "-NoProfile -Command Get-Clipboard");
        }

        return
            RunAndCapture("wl-paste", "-n") ??
            RunAndCapture("xclip", "-selection clipboard -o") ??
            RunAndCapture("xsel", "--clipboard --output");
    }

    private static bool RunWithInput(string fileName, string arguments, string input)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.Default,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default
            };

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            using (process.StandardInput)
            {
                process.StandardInput.Write(input);
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? RunAndCapture(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
                return null;

            return output;
        }
        catch
        {
            return null;
        }
    }
}