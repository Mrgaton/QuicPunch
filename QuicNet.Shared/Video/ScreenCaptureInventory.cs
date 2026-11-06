using System.Runtime.InteropServices;

namespace QuicNet.Video;

internal sealed record CaptureDisplay(string Id, string Name, int Width, int Height, int RefreshRate, int X, int Y, bool Primary = false);
internal sealed record CaptureWindow(long Handle, string Title, int Width, int Height);

// Read the active modes, not the list of modes a display could potentially use.
internal static class ScreenCaptureInventory
{
    public static IReadOnlyList<CaptureDisplay> Displays()
    {
        var displays = new List<CaptureDisplay>();
        if (!OperatingSystem.IsWindows()) return displays;
        for (uint index = 0; index < 32; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if ((device.Flags & 1) == 0 || (device.Flags & 8) != 0) continue;
            var mode = new DisplayMode { Size = 220 };
            if (!EnumDisplaySettings(device.Name, -1, ref mode) || mode.Width <= 0 || mode.Height <= 0) continue;
            displays.Add(new(device.Name, device.Description, mode.Width, mode.Height,
                mode.Frequency > 1 ? mode.Frequency : 0, mode.X, mode.Y, (device.Flags & 4) != 0));
        }
        return displays;
    }

    public static (int Width, int Height) WindowSize(IntPtr window)
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            return GetClientRect(window, out var rect)
                ? (Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top)) : (0, 0);
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    public static IReadOnlyList<CaptureWindow> Windows()
    {
        var result = new List<CaptureWindow>();
        if (!OperatingSystem.IsWindows()) return result;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            int length = GetWindowTextLength(hwnd);
            if (length < 1 || length > 4096) return true;
            var title = new System.Text.StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            var size = WindowSize(hwnd);
            if (size.Width > 0 && size.Height > 0)
                result.Add(new(hwnd.ToInt64(), title.ToString(), size.Width, size.Height));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder title, int count);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    // DEVMODEW's display union and active mode fields; no driver-private data.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(76)] public int X;
        [FieldOffset(80)] public int Y;
        [FieldOffset(172)] public int Width;
        [FieldOffset(176)] public int Height;
        [FieldOffset(184)] public int Frequency;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice result, uint flags);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode result);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);
}