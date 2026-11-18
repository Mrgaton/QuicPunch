using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace QuicNet;

internal static class DesktopUpdateIdle
{
    internal static TimeSpan? GetIdleTime()
    {
        if (OperatingSystem.IsWindows()) return DesktopAppPlatform.GetWindowsIdleTime();
        if (!OperatingSystem.IsLinux()) return null;

        // XWayland only sees X11 inputs and cannot determine inactivity on a Wayland desktop.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland")
        {
            var mutter = ReadSessionBus("org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core", "org.gnome.Mutter.IdleMonitor.GetIdletime", "uint64");
            if (mutter.HasValue) return TimeSpan.FromMilliseconds(mutter.Value);
            // KDE's implementation returns its lock timer in milliseconds.
            var locked = ReadSessionBus("org.kde.screensaver", "/ScreenSaver", "org.freedesktop.ScreenSaver.GetActiveTime", "uint32");
            return locked.HasValue ? TimeSpan.FromMilliseconds(locked.Value) : null;
        }

        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ? null : GetX11IdleTime();
    }

    private static long? ReadSessionBus(string service, string path, string method, string resultType)
    {
        var start = new ProcessStartInfo("gdbus") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { "call", "--session", "--timeout", "1", "--dest", service, "--object-path", path, "--method", method })
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process == null) return null;
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            if (process.ExitCode != 0) return null;
            string output = process.StandardOutput.ReadToEnd().Trim();
            string prefix = "(" + resultType + " ";
            return output.StartsWith(prefix, StringComparison.Ordinal) && output.EndsWith(",)", StringComparison.Ordinal) &&
                long.TryParse(output.AsSpan(prefix.Length, output.Length - prefix.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out long elapsed)
                && elapsed <= uint.MaxValue ? elapsed : null;
        }
        catch (Win32Exception) { return null; }
    }

    private static TimeSpan? GetX11IdleTime()
    {
        try
        {
            nint display = XOpenDisplay(0);
            if (display == 0) return null;
            try
            {
                if (XScreenSaverQueryExtension(display, out _, out _) == 0) return null;
                return XScreenSaverQueryInfo(display, XDefaultRootWindow(display), out var info) != 0
                    ? TimeSpan.FromMilliseconds((uint)info.Idle) : null;
            }
            finally { XCloseDisplay(display); }
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenSaverInfo
    {
        public nuint Window;
        public int State, Kind;
        public nuint TilOrSince, Idle, EventMask;
    }

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint displayName);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libXss.so.1")] private static extern int XScreenSaverQueryExtension(nint display, out int events, out int errors);
    [DllImport("libXss.so.1")] private static extern int XScreenSaverQueryInfo(nint display, nuint window, out ScreenSaverInfo info);
}
