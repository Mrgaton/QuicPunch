using System.Runtime.InteropServices;
using QuicNet.Video;

namespace QuicPunchTests.Tests;

internal static class NativeWindowCaptureTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A dedicated off-screen test window: never capture a user's window or desktop.
        using var stop = new ManualResetEventSlim();
        var ready = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            IntPtr hwnd = IntPtr.Zero;
            try
            {
                SetThreadDpiAwarenessContext(new IntPtr(-4));
                hwnd = CreateWindowEx(0x08000080, "STATIC", "QuicNet synthetic capture verification",
                    0x90000000, -20000, -20000, 640, 360, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                ready.TrySetResult(hwnd);
                while (!stop.Wait(10))
                    while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
                    { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
            catch (Exception ex) { ready.TrySetException(ex); }
            finally { if (hwnd != IntPtr.Zero) DestroyWindow(hwnd); released.TrySetResult(); }
        }) { IsBackground = true, Name = "Synthetic capture window" };
        thread.Start();
        try
        {
            var hwnd = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string source = $"hwnd:{hwnd.ToInt64()}";
            var window = FfmpegScreenEncoder.Sources().Single(s => s.Id == source);
            if (window.Kind != "window" || window.Width != 640 || window.Height != 360)
                throw new Exception($"Native picker lost the synthetic window or its client bounds: {window.Kind} {window.Width}x{window.Height}.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            byte[] preview = await FfmpegScreenEncoder.PreviewAsync(source, timeout.Token);
            if (preview.Length < 4 || preview[0] != 0xff || preview[1] != 0xd8)
                throw new Exception("Native window preview did not produce JPEG.");
            using var encoder = new FfmpegScreenEncoder();
            var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var profile = await encoder.StartAsync(new() { Source = source, Encoder = "libx264", Width = 640, Height = 360, Fps = 15 },
                _ => Task.FromResult(true), (_, _) => { frame.TrySetResult(); return Task.CompletedTask; }, timeout.Token);
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (profile.Width != 640 || profile.Height != 360)
                throw new Exception("Native window capture changed the client rectangle.");
            await encoder.StopAsync();
            stop.Set(); await released.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try { FfmpegScreenEncoder.CaptureArguments(new() {Source = source}, "libx264"); throw new Exception("Closed window was accepted."); }
            catch (ArgumentException) { }
            Console.WriteLine("[WINDOW] Real HWND inventory, JPEG preview, FFmpeg capture and closed-window rejection passed.");
        }
        finally { stop.Set(); await released.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref Message message);
}
