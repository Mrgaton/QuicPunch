using System.Runtime.InteropServices;

namespace QuicNet.Video;

internal sealed class X11RemoteInput : IRemoteInputDevice
{
    private readonly nint _display = XOpenDisplay(0);
    private readonly HashSet<uint> _heldKeys = [];
    private readonly HashSet<uint> _heldButtons = [];
    private static readonly uint[] MouseButtons = [1, 2, 3];

    internal static bool IsSession => OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
        !string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    internal static RemoteInputArea? CaptureArea(string source, bool requireInput = true)
    {
        if (!IsSession || source != "desktop" && !source.StartsWith("display:", StringComparison.Ordinal)) return null;
        try
        {
            var display = XOpenDisplay(0);
            if (display == 0) return null;
            try
            {
                if (requireInput && XTestQueryExtension(display, out _, out _, out _, out _) == 0) return null;
                if (source == "desktop")
                {
                    int screen = XDefaultScreen(display);
                    return new(0, 0, XDisplayWidth(display, screen), XDisplayHeight(display, screen));
                }
                var monitor = Displays(display).FirstOrDefault(item => item.Id == source[8..]);
                return monitor == null ? null : new(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            }
            finally { XCloseDisplay(display); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    internal static IReadOnlyList<CaptureDisplay> Displays()
    {
        if (!IsSession) return [];
        try
        {
            var display = XOpenDisplay(0);
            if (display == 0) return [];
            try { return Displays(display); }
            finally { XCloseDisplay(display); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return []; }
    }

    private static IReadOnlyList<CaptureDisplay> Displays(nint display)
    {
        if (XRRQueryVersion(display, out int major, out int minor) == 0 || major < 1 || major == 1 && minor < 5)
            return [];
        var monitors = XRRGetMonitors(display, XDefaultRootWindow(display), 1, out int count);
        if (monitors == 0) return [];
        try
        {
            var result = new List<CaptureDisplay>();
            for (int index = 0; index < Math.Min(count, 32); index++)
            {
                var info = Marshal.PtrToStructure<MonitorInfo>(monitors + index * Marshal.SizeOf<MonitorInfo>());
                if (info.Width <= 0 || info.Height <= 0) continue;
                var name = XGetAtomName(display, info.Name);
                try
                {
                    string id = Marshal.PtrToStringUTF8(name) ?? $"X11-{index}";
                    result.Add(new(id, id, info.Width, info.Height, 0, info.X, info.Y, info.Primary != 0));
                }
                finally { if (name != 0) XFree(name); }
            }
            return result;
        }
        finally { XRRFreeMonitors(monitors); }
    }

    public void Apply(RemoteInputArea area, RemoteDesktopInput input)
    {
        if (_display == 0) return;
        switch (input.kind)
        {
            case "release": Release(); return;
            case "keydown":
            case "keyup":
                if (RemoteKeyboard.TryScan(input.key, out ushort scan))
                {
                    uint key = Evdev(scan);
                    bool down = input.kind == "keydown";
                    if (key != 0 && (!down && _heldKeys.Contains(key) || down && KeyboardInside(area)))
                    {
                        XTestFakeKeyEvent(_display, key, down ? 1 : 0, 0);
                        if (down) _heldKeys.Add(key);
                        else _heldKeys.Remove(key);
                    }
                }
                break;
            case "key":
                if (KeyboardInside(area) && RemoteKeyboard.TryScan(input.key, out ushort named))
                {
                    uint key = Evdev(named);
                    if (key != 0) TapKey(key);
                }
                break;
            case "text":
                if (KeyboardInside(area))
                    foreach (var rune in input.text!.EnumerateRunes()) Type(rune.Value);
                break;
            default:
                int x = area.X + (int)Math.Round(input.x * (area.Width - 1));
                int y = area.Y + (int)Math.Round(input.y * (area.Height - 1));
                XTestFakeMotionEvent(_display, XDefaultScreen(_display), x, y, 0);
                if (input.kind is "down" or "up")
                {
                    uint button = MouseButtons[input.button];
                    bool changed = input.kind == "down" ? _heldButtons.Add(button) : _heldButtons.Remove(button);
                    if (changed) XTestFakeButtonEvent(_display, button, input.kind == "down" ? 1 : 0, 0);
                }
                else if (input.kind == "scroll")
                {
                    uint button = input.delta < 0 ? 4u : 5u;
                    for (int tick = 0; tick < Math.Max(1, Math.Abs(input.delta) / 120); tick++)
                    {
                        XTestFakeButtonEvent(_display, button, 1, 0);
                        XTestFakeButtonEvent(_display, button, 0, 0);
                    }
                }
                break;
        }
        XFlush(_display);
    }

    private bool KeyboardInside(RemoteInputArea area)
    {
        int screen = XDefaultScreen(_display);
        if (area == new RemoteInputArea(0, 0, XDisplayWidth(_display, screen), XDisplayHeight(_display, screen))) return true;
        XGetInputFocus(_display, out var focus, out _);
        if (focus <= 1) return false;
        // Checked XCB replies avoid Xlib's fatal default error handler when a
        // focused application destroys its window while these requests run.
        nint connection = XGetXcbConnection(_display);
        nint geometry = XcbGeometryReply(connection, XcbGeometry(connection, (uint)focus), out nint error);
        if (error != 0) FreeReply(error);
        if (geometry == 0) return false;
        var bounds = Marshal.PtrToStructure<XcbBounds>(geometry);
        FreeReply(geometry);
        nint coordinates = XcbTranslateReply(connection, XcbTranslate(connection, (uint)focus, bounds.Root,
            (short)(bounds.Width / 2), (short)(bounds.Height / 2)), out error);
        if (error != 0) FreeReply(error);
        if (coordinates == 0) return false;
        var position = Marshal.PtrToStructure<XcbPosition>(coordinates);
        FreeReply(coordinates);
        return position.SameScreen != 0 && position.X >= area.X && position.X < area.X + area.Width &&
            position.Y >= area.Y && position.Y < area.Y + area.Height;
    }

    private void Type(int codepoint)
    {
        nuint symbol = (nuint)(codepoint <= 0xff ? codepoint : codepoint | 0x01000000);
        byte key = XKeysymToKeycode(_display, symbol);
        if (key == 0) return;
        if (XkbKeycodeToKeysym(_display, key, 0, 0) == symbol) { TapKey(key); return; }
        if (XkbKeycodeToKeysym(_display, key, 0, 1) != symbol) return;
        uint shift = Evdev(42);
        bool held = _heldKeys.Contains(shift);
        if (!held) XTestFakeKeyEvent(_display, shift, 1, 0);
        TapKey(key);
        if (!held) XTestFakeKeyEvent(_display, shift, 0, 0);
    }

    private void TapKey(uint key)
    {
        XTestFakeKeyEvent(_display, key, 1, 0);
        XTestFakeKeyEvent(_display, key, 0, 0);
    }

    private static uint Evdev(ushort scan)
    {
        uint code = scan switch
        {
            <= 88 => scan,
            0xe01c => 96, 0xe01d => 97, 0xe035 => 98, 0xe037 => 99, 0xe038 => 100,
            0xe047 => 102, 0xe048 => 103, 0xe049 => 104, 0xe04b => 105, 0xe04d => 106,
            0xe04f => 107, 0xe050 => 108, 0xe051 => 109, 0xe052 => 110, 0xe053 => 111,
            0xe05b => 125, 0xe05c => 126, 0xe05d => 127,
            >= 0x64 and <= 0x6e => (uint)(183 + scan - 0x64), 0x76 => 194,
            _ => 0
        };
        return code == 0 ? 0 : code + 8;
    }

    public void Release()
    {
        if (_display == 0) return;
        foreach (var key in _heldKeys) XTestFakeKeyEvent(_display, key, 0, 0);
        foreach (var button in _heldButtons) XTestFakeButtonEvent(_display, button, 0, 0);
        _heldKeys.Clear();
        _heldButtons.Clear();
        XFlush(_display);
    }

    public bool StopRequested
    {
        get
        {
            if (_display == 0) return false;
            var keys = new byte[32];
            XQueryKeymap(_display, keys);
            bool Held(uint keycode) => (keys[keycode / 8] & (1 << (int)(keycode % 8))) != 0;
            return Held(Evdev(88)) && (Held(Evdev(29)) || Held(Evdev(0xe01d))) &&
                (Held(Evdev(56)) || Held(Evdev(0xe038)));
        }
    }

    public void Dispose()
    {
        Release();
        if (_display != 0) XCloseDisplay(_display);
    }

    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    {
        public nuint Name;
        public int Primary, Automatic, Outputs, X, Y, Width, Height, MmWidth, MmHeight;
        public nint Output;
    }
    [StructLayout(LayoutKind.Sequential)] private struct XcbBounds
    {
        public byte Response, Depth;
        public ushort Sequence;
        public uint Length, Root;
        public short X, Y;
        public ushort Width, Height, Border;
    }
    [StructLayout(LayoutKind.Sequential)] private struct XcbPosition
    {
        public byte Response, SameScreen;
        public ushort Sequence;
        public uint Length, Child;
        public short X, Y;
    }
    internal static void InitializeThreads() { if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) try { XInitThreads(); } catch (DllNotFoundException) { } }
    [DllImport("libX11.so.6")] private static extern int XInitThreads();
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern int XDefaultScreen(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern int XDisplayWidth(nint display, int screen);
    [DllImport("libX11.so.6")] private static extern int XDisplayHeight(nint display, int screen);
    [DllImport("libX11.so.6")] private static extern nint XGetAtomName(nint display, nuint atom);
    [DllImport("libX11.so.6")] private static extern int XFree(nint data);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint display);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(nint display, nuint symbol);
    [DllImport("libX11.so.6")] private static extern nuint XkbKeycodeToKeysym(nint display, byte keycode, int group, int level);
    [DllImport("libX11.so.6")] private static extern int XGetInputFocus(nint display, out nuint focus, out int revert);
    [DllImport("libX11.so.6")] private static extern int XQueryKeymap(nint display, [Out] byte[] keys);
    [DllImport("libX11-xcb.so.1", EntryPoint = "XGetXCBConnection")] private static extern nint XGetXcbConnection(nint display);
    [DllImport("libxcb.so.1", EntryPoint = "xcb_get_geometry")] private static extern uint XcbGeometry(nint connection, uint window);
    [DllImport("libxcb.so.1", EntryPoint = "xcb_get_geometry_reply")] private static extern nint XcbGeometryReply(nint connection, uint cookie, out nint error);
    [DllImport("libxcb.so.1", EntryPoint = "xcb_translate_coordinates")] private static extern uint XcbTranslate(nint connection, uint from, uint to, short x, short y);
    [DllImport("libxcb.so.1", EntryPoint = "xcb_translate_coordinates_reply")] private static extern nint XcbTranslateReply(nint connection, uint cookie, out nint error);
    [DllImport("libc.so.6", EntryPoint = "free")] private static extern void FreeReply(nint pointer);
    [DllImport("libXrandr.so.2")] private static extern nint XRRGetMonitors(nint display, nuint window, int active, out int count);
    [DllImport("libXrandr.so.2")] private static extern int XRRQueryVersion(nint display, out int major, out int minor);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeMonitors(nint monitors);
    [DllImport("libXtst.so.6")] private static extern int XTestQueryExtension(nint display, out int events, out int errors, out int major, out int minor);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeButtonEvent(nint display, uint button, int down, nuint delay);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeKeyEvent(nint display, uint key, int down, nuint delay);
}
