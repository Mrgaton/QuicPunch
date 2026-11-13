using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using QuicNet.Notifications;

namespace QuicNet;

internal sealed class DesktopAppPlatform : IAppPlatform, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Tick; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput input);
    public TimeSpan? GetUserIdleTime()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount64 - input.Tick)) : null;
    }
    private readonly string _url, _sounds;
    private readonly WindowsNotificationIcon? _icon;
    private int _ringtonePlaying;
    public Action? OpenWindow { get; set; }
    public bool StartupSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    public string Description => OperatingSystem.IsWindows()
        ? "Inicio al iniciar sesión. Al cerrar, la ventana se minimiza si activas segundo plano. Icono de QuicNet: abrir o salir."
        : "Inicio al iniciar sesión. En Linux las notificaciones requieren notify-send; el sonido usa paplay o aplay.";
    public DesktopAppPlatform(string url, string directory, CancellationTokenSource lifetime)
    {
        _url = url; _sounds = Path.Combine(directory, "notification-sounds");
        if (OperatingSystem.IsWindows()) _icon = new(() => { if (OpenWindow != null) OpenWindow(); else Open(_url); }, () => lifetime.Cancel(), Open);
    }
    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    internal static string StartupCommand()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No se encuentra el ejecutable.");
        string command = "\"" + executable + "\"";
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            command += " \"" + Assembly.GetEntryAssembly()!.Location + "\"";
        return command + " --startup";
    }
    public void SetStartWithSystem(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue("QuicNet", StartupCommand()); else key.DeleteValue("QuicNet", false);
        }
        else if (OperatingSystem.IsLinux())
        {
            string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            string path = Path.Combine(config, "autostart", "quicnet.desktop");
            if (!enabled) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "[Desktop Entry]\nType=Application\nName=QuicNet\nExec=" + StartupCommand().Replace("%", "%%") + "\nTerminal=false\n");
        }
        else throw new PlatformNotSupportedException();
    }
    private static async Task<bool> RunAsync(string command, IEnumerable<string> arguments, CancellationToken ct)
    {
        try
        {
            var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(process.StandardError.ReadToEndAsync(deadline.Token), process.StandardOutput.ReadToEndAsync(deadline.Token), process.WaitForExitAsync(deadline.Token)); }
            catch { try { process.Kill(); } catch { } throw; }
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    public async Task NotifyAsync(AppNotice notice, bool desktop, CancellationToken ct)
    {
        if (desktop && notice.Popup)
        {
            if (_icon != null) _icon.Notify(notice.Title, notice.Message, _url.TrimEnd('/') + notice.Url);
            else if (OperatingSystem.IsLinux())
                if (!await RunAsync("notify-send", ["--app-name=QuicNet", "--hint=boolean:suppress-sound:true", "--expire-time=8000", "--", notice.Title, System.Security.SecurityElement.Escape(notice.Message) ?? ""], ct))
                    WebUi.WebUiServer.LogEvent("[NOTIFICATIONS] notify-send no está disponible o el escritorio ha rechazado el aviso.");
        }
        if (!notice.Sound || Volatile.Read(ref _ringtonePlaying) != 0) return;
        Directory.CreateDirectory(_sounds);
        string audio = Path.Combine(_sounds, $"v2-{notice.Category}-{notice.Tone}-{notice.Volume}.wav");
        if (!File.Exists(audio)) await File.WriteAllBytesAsync(audio, NotificationAudio.Wave(notice.Tone, notice.Volume, notice.Category), ct);
        if (OperatingSystem.IsWindows()) PlaySound(audio, 0, 0x00020001);
        else if (OperatingSystem.IsLinux() && !await RunAsync("paplay", [audio], ct)) await RunAsync("aplay", ["-q", audio], ct);
    }
    public async Task PlayRingtoneAsync(int volume, CancellationToken ct)
    {
        Directory.CreateDirectory(_sounds);
        string audio = Path.Combine(_sounds, $"ring-v1-{volume}.wav");
        if (!File.Exists(audio)) await File.WriteAllBytesAsync(audio, NotificationAudio.Ringtone(volume), ct);
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _ringtonePlaying, 1);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // ASYNC | FILENAME | NODEFAULT: never block the notification queue.
                PlaySound(audio, 0, 0x00020003);
                try { await Task.Delay(1300, ct); }
                finally { PlaySound(null, 0, 0); }
            }
            else if (OperatingSystem.IsLinux() && !await RunAsync("paplay", [audio], ct))
                await RunAsync("aplay", ["-q", audio], ct);
        }
        finally { Interlocked.Exchange(ref _ringtonePlaying, 0); }
    }
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern bool PlaySound(string? sound, nint module, uint flags);
    public void Dispose() => _icon?.Dispose();
}

/// <summary>A native tray icon and message loop, without a Windows-only UI framework.</summary>
internal sealed class WindowsNotificationIcon : IDisposable
{
    private const uint Callback = 0x8001;
    private readonly Action _open, _exit;
    private readonly Action<string> _openUrl;
    private readonly WndProc _procedure;
    private nint _window;
    private string? _target;
    private IconData _data;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    internal bool IsReady { get; private set; }
    public WindowsNotificationIcon(Action open, Action exit, Action<string> openUrl)
    {
        _open = open; _exit = exit; _openUrl = openUrl; _procedure = Handle;
        _thread = new Thread(Run) { IsBackground = true, Name = "QuicNet notifications" }; _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }
    private void Run()
    {
        try
        {
            var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), Name = "QuicNet.Notifications", Instance = GetModuleHandle(null) };
            RegisterClassEx(ref cls);
            _window = CreateWindowEx(0, cls.Name, "QuicNet", 0, 0, 0, 0, 0, 0, 0, cls.Instance, 0);
            lock (_gate)
            {
                _data = new() { Size = (uint)Marshal.SizeOf<IconData>(), Window = _window, Id = 1, Flags = 7, Callback = Callback, Icon = LoadIcon(0, 32512), Tip = "QuicNet · doble clic para abrir · clic derecho para salir", Info = "", Title = "" };
                IsReady = _window != 0 && ShellNotify(0, ref _data);
            }
            _ready.Set();
            while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception ex) { WebUi.WebUiServer.LogEvent("[NOTIFICATIONS] Icono de Windows: " + ex.Message); }
        finally { lock (_gate) { if (_window != 0) ShellNotify(2, ref _data); _window = 0; IsReady = false; } _ready.Set(); }
    }
    private nint Handle(nint window, uint message, nuint wparam, nint lparam)
    {
        if (message == Callback)
        {
            try
            {
                if ((uint)lparam == 0x0203) _open();
                if ((uint)lparam == 0x0405 && _target != null) _openUrl(_target); // balloon click
                if ((uint)lparam == 0x0205)
                {
                    var menu = CreatePopupMenu();
                    try
                    {
                        AppendMenu(menu, 0, 1, "Abrir QuicNet"); AppendMenu(menu, 0, 2, "Salir de QuicNet");
                        GetCursorPos(out var point); SetForegroundWindow(window);
                        int selection = TrackPopupMenu(menu, 0x0100, point.X, point.Y, 0, window, 0);
                        if (selection == 1) _open(); else if (selection == 2) _exit();
                    }
                    finally { DestroyMenu(menu); }
                }
            }
            catch { }
        }
        if (message == 0x0010) { DestroyWindow(window); return 0; }
        if (message == 0x0002) { PostQuitMessage(0); return 0; }
        return DefWindowProc(window, message, wparam, lparam);
    }
    public void Notify(string title, string message, string target)
    {
        lock (_gate)
        {
            if (_window == 0) return;
            _target = target; _data.Flags = 0x10; _data.Info = message[..Math.Min(255, message.Length)];
            _data.Title = title[..Math.Min(63, title.Length)]; _data.InfoFlags = 0x11; // information + no OS sound
            ShellNotify(1, ref _data);
        }
    }
    public void Dispose() { if (_window != 0) PostMessage(_window, 0x0010, 0, 0); }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WndProc(nint window, uint message, nuint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu; public string Name; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint Wparam; public nint Lparam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct IconData { public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip; public uint State, StateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info; public uint Timeout; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title; public uint InfoFlags; public Guid Guid; public nint BalloonIcon; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotify(uint action, ref IconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint msg, nuint w, nint l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
