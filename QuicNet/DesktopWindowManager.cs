using System.Diagnostics;
using System.Text.Json;
using Photino.NET;
using QuicNet.Watch.Services;
using QuicNet.Watch.Web;

namespace QuicNet;

public sealed class PhotinoWatchFilePicker(PhotinoWindow window) : IWatchFilePicker
{
    public async Task<string?> PickVideoFileAsync()
    {
        string[] files = await window.ShowOpenFileAsync(
            "Elegir vídeo",
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            false,
            [("Vídeos", ["*.mp4", "*.m4v", "*.webm", "*.mov", "*.mkv", "*.ogv", "*.ogg"]), ("Todos", ["*.*"])]).ConfigureAwait(false);
        return files.FirstOrDefault();
    }
}

public static class DesktopWindowManager
{
    public static void ShowWindow(string url, string nodeAppDataPath, CancellationTokenSource cts, WatchWebServer? watchWeb = null,
        Func<bool>? keepRunning = null, bool startMinimized = false, Action<Action>? registerOpen = null)
    {
        var thread = new Thread(() =>
        {
            try
            {
                if (OperatingSystem.IsLinux() && QuicPunch.Helpers.NativeAssetBundle.Prepare() is { } native)
                    System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(native, "Photino.Native.so"));
                var window = new PhotinoWindow()
                    .SetTitle("QuicNet")
                    .SetUseOsDefaultSize(false);

                string statePath = Path.Combine(nodeAppDataPath, "window_state.json");
                Directory.CreateDirectory(nodeAppDataPath);
                WindowStateConfig state = LoadWindowState(statePath);
                window.SetSize(state.Width >= 700 ? state.Width : 1320, state.Height >= 500 ? state.Height : 860);
                if (state.Left >= 0 && state.Top >= 0) window.SetLocation(new System.Drawing.Point(state.Left, state.Top));
                else window.Center();
                if (state.IsMaximized && !startMinimized) window.SetMaximized(true);
                if (startMinimized) window.SetMinimized(true);
                registerOpen?.Invoke(() => window.Invoke(() => window.SetMinimized(false)));

                if (watchWeb != null)
                {
                    watchWeb.SetFilePicker(new PhotinoWatchFilePicker(window));
                }

                window.RegisterWindowCreatedHandler((_, _) =>
                {
                    if (OperatingSystem.IsWindows()) QuicNet.Video.NativeVideoRuntime.Instance.Attach(new WindowsVideoHost(window));
                });
                window.Load(url);

                window.WindowClosing += (_, _) =>
                {
                    SaveWindowState(statePath, window);
                    if (!cts.IsCancellationRequested && keepRunning?.Invoke() == true)
                    {
                        window.SetMinimized(true);
                        return true;
                    }
                    try { cts.Cancel(); } catch { }
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000).ConfigureAwait(false);
                        Environment.Exit(0);
                    });
                    return false;
                };
                window.WaitForClose();
                try { cts.Cancel(); } catch { }
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    Environment.Exit(0);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebUI] Native window unavailable ({ex.Message}). Opening browser.");
                try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
            }
        });

        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private static WindowStateConfig LoadWindowState(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), QuicNet.Serialization.NativeUiJsonContext.Default.WindowStateConfig) ?? new WindowStateConfig() : new WindowStateConfig(); }
        catch { return new WindowStateConfig(); }
    }

    private static void SaveWindowState(string path, PhotinoWindow window)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new WindowStateConfig
            {
                Width = window.Width,
                Height = window.Height,
                Left = window.Left,
                Top = window.Top,
                IsMaximized = window.Maximized
            }, new QuicNet.Serialization.NativeUiJsonContext(new JsonSerializerOptions { WriteIndented = true }).WindowStateConfig));
        }
        catch { }
    }
}

public sealed class WindowStateConfig
{
    public int Width { get; set; } = 1320;
    public int Height { get; set; } = 860;
    public int Left { get; set; } = -1;
    public int Top { get; set; } = -1;
    public bool IsMaximized { get; set; }
}
