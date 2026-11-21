using System.Runtime.InteropServices;

namespace QuicNet.Video;

internal sealed record RemoteDesktopInput(string kind, double x = 0, double y = 0, int button = 0, int delta = 0, string? text = null, string? key = null);

internal sealed class WindowsRemoteInput : IRemoteInputDevice
{
    private int _heldButtons;
    private readonly HashSet<ushort> _heldKeys = [];
    private static readonly uint[] ButtonDown = [0x0002, 0x0020, 0x0008];
    private static readonly uint[] ButtonUp = [0x0004, 0x0040, 0x0010];
    private static readonly Dictionary<string, ushort> Keys = new(StringComparer.Ordinal)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Escape"] = 0x1B,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["ArrowLeft"] = 0x25, ["ArrowUp"] = 0x26, ["ArrowRight"] = 0x27, ["ArrowDown"] = 0x28,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E
    };

    internal static RemoteInputArea? CaptureArea(string source)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return null;
        if (source == "desktop") return VirtualDesktop();
        if (!source.StartsWith("display:", StringComparison.Ordinal)) return null;
        var display = ScreenCaptureInventory.Displays().FirstOrDefault(d => d.Id == source[8..]);
        return display == null ? null : new(display.X, display.Y, display.Width, display.Height);
    }

    internal static bool IsValid(RemoteDesktopInput? input) => input?.kind switch
    {
        "move" => InBounds(input),
        "down" or "up" => InBounds(input) && input.button is >= 0 and <= 2,
        "scroll" => InBounds(input) && input.delta is >= -480 and <= 480 && input.delta != 0,
        "text" => input.text is { Length: > 0 and <= 64 } text && !text.Any(char.IsControl),
        "key" => input.key != null && Keys.ContainsKey(input.key),
        "keydown" or "keyup" => RemoteKeyboard.TryScan(input.key, out _),
        "release" => true,
        _ => false
    };

    private static bool InBounds(RemoteDesktopInput input) => input.x is >= 0 and <= 1 && input.y is >= 0 and <= 1;

    public void Apply(RemoteInputArea area, RemoteDesktopInput input)
    {
        if (input.kind == "release") { Release(); return; }
        if (input.kind is "keydown" or "keyup")
        {
            if (!RemoteKeyboard.TryScan(input.key, out ushort scan)) return;
            bool down = input.kind == "keydown";
            if (down && !ForegroundOnSharedDisplay(area) || !down && !_heldKeys.Contains(scan)) return;
            if (Send([ScanKey(scan, !down)]))
            {
                if (down) _heldKeys.Add(scan);
                else _heldKeys.Remove(scan);
            }
            return;
        }
        if (input.kind is "key" or "text")
        {
            if (!ForegroundOnSharedDisplay(area)) return;
            if (input.kind == "key")
            {
                ushort key = Keys[input.key!];
                uint extended = key is >= 0x21 and <= 0x2E ? 1u : 0;
                Send([Keyboard(key, 0, extended), Keyboard(key, 0, extended | 2)]);
            }
            else
            {
                var events = new Input[input.text!.Length * 2];
                for (int index = 0; index < input.text.Length; index++)
                {
                    events[index * 2] = Keyboard(0, input.text[index], 4);
                    events[index * 2 + 1] = Keyboard(0, input.text[index], 6);
                }
                Send(events);
            }
            return;
        }

        var desktop = VirtualDesktop();
        if (desktop == null) return;
        int x = area.X + (int)Math.Round(input.x * (area.Width - 1));
        int y = area.Y + (int)Math.Round(input.y * (area.Height - 1));
        if (MonitorFromPoint(new Point(x, y), 0) == 0)
        {
            if (input.kind == "up") Release();
            return;
        }
        var screen = desktop.Value;
        int dx = (int)((((long)x - screen.X) * 65536 + 32768) / screen.Width);
        int dy = (int)((((long)y - screen.Y) * 65536 + 32768) / screen.Height);
        if (dx is < 0 or > 65535 || dy is < 0 or > 65535) { Release(); return; }
        var move = Mouse(0xC001, dx, dy);
        if (input.kind == "move") Send([move]);
        else if (input.kind == "scroll") Send([move, Mouse(0x0800, data: -input.delta)]);
        else
        {
            int mask = 1 << input.button;
            bool down = input.kind == "down";
            if (down == ((_heldButtons & mask) != 0)) return;
            if (Send([move, Mouse(down ? ButtonDown[input.button] : ButtonUp[input.button])]))
                _heldButtons = down ? _heldButtons | mask : _heldButtons & ~mask;
        }
    }

    public void Release()
    {
        foreach (ushort scan in _heldKeys.ToArray())
            if (Send([ScanKey(scan, true)])) _heldKeys.Remove(scan);
        for (int button = 0; button < ButtonUp.Length; button++)
            if ((_heldButtons & (1 << button)) != 0 && Send([Mouse(ButtonUp[button])]))
                _heldButtons &= ~(1 << button);
    }

    public void Dispose() => Release();
    public bool StopRequested => (GetAsyncKeyState(0x11) & GetAsyncKeyState(0x12) & GetAsyncKeyState(0x7B) & 0x8000) != 0;

    private static RemoteInputArea? VirtualDesktop()
    {
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            var area = new RemoteInputArea(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
            return area.Width > 0 && area.Height > 0 ? area : null;
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    private static bool ForegroundOnSharedDisplay(RemoteInputArea area)
    {
        if (area == VirtualDesktop()) return true;
        var monitor = MonitorFromWindow(GetForegroundWindow(), 0);
        return monitor != 0 && monitor == MonitorFromPoint(new Point(area.X + area.Width / 2, area.Y + area.Height / 2), 0);
    }

    private static bool Send(Input[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    private static Input Mouse(uint flags, int x = 0, int y = 0, int data = 0) =>
        new() { Value = new() { Mouse = new() { X = x, Y = y, Data = data, Flags = flags } } };
    private static Input Keyboard(ushort key, ushort scan, uint flags) =>
        new() { Type = 1, Value = new() { Keyboard = new() { Key = key, Scan = scan, Flags = flags } } };
    private static Input ScanKey(ushort scan, bool up) =>
        Keyboard(0, (ushort)(scan & 0xff), 8u | (scan > 0xff ? 1u : 0) | (up ? 2u : 0));

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputValue Value; }
    [StructLayout(LayoutKind.Explicit)] private struct InputValue
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y, Data; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
