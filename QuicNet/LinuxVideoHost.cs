using System.Runtime.InteropServices;
using QuicNet.Video;

namespace QuicNet;

// Each X11 surface owns its connection, so decoding never calls GTK.
internal sealed class LinuxVideoHost(nint parent) : INativeVideoHost, IDisposable
{
    private readonly Dictionary<string, nint> _surfaces = [];
    private readonly object _gate = new();
    private bool _disposed;

    internal static LinuxVideoHost? FindForWindow(string title)
    {
        if (!X11RemoteInput.IsSession) return null;
        var windows = GtkWindowList();
        try
        {
            for (nint node = windows; node != 0; node = Marshal.ReadIntPtr(node, nint.Size))
            {
                nint widget = Marshal.ReadIntPtr(node);
                if (!string.Equals(Marshal.PtrToStringUTF8(GtkWindowTitle(widget)), title, StringComparison.Ordinal)) continue;
                nint window = GtkWidgetWindow(widget);
                if (window == 0) continue;
                nuint xid = X11WindowId(window);
                if (xid != 0) return new((nint)xid);
            }
            return null;
        }
        finally { if (windows != 0) FreeList(windows); }
    }

    public Task<nint> SurfaceAsync(string peerId, bool probe = false)
    {
        if (probe) return Task.FromResult((nint)0);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_surfaces.TryGetValue(peerId, out var existing)) return Task.FromResult(existing);
            nint surface = NativeMedia.qpv_surface_create(parent);
            if (surface == 0) throw new InvalidOperationException("No se pudo crear la superficie de vídeo X11.");
            _surfaces.Add(peerId, surface);
            return Task.FromResult(surface);
        }
    }

    public Task LayoutAsync(NativeVideoLayout layout)
    {
        lock (_gate)
        {
            if (_surfaces.TryGetValue(layout.peerId, out var surface))
            {
                if (!layout.visible)
                    NativeMedia.qpv_x11_surface_layout(surface, 0, 0, 1, 1, 0, 0, 0, 0, 0);
                else
                {
                    var area = layout.Viewport();
                    NativeMedia.qpv_desktop_surface_crop(surface, area.X, area.Y, area.Width, area.Height, 1,
                        area.Left, area.Top, area.Right, area.Bottom);
                }
            }
            return Task.CompletedTask;
        }
    }

    public Task ReleaseAsync(string peerId)
    {
        lock (_gate) if (_surfaces.Remove(peerId, out var surface)) NativeMedia.qpv_surface_destroy(surface);
        return Task.CompletedTask;
    }

    public string? HardwareComponent(int codecId, int width, int height) => null;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var surface in _surfaces.Values) NativeMedia.qpv_surface_destroy(surface);
            _surfaces.Clear();
        }
    }

    [DllImport("libgtk-3.so.0", EntryPoint = "gtk_window_list_toplevels")] private static extern nint GtkWindowList();
    [DllImport("libgtk-3.so.0", EntryPoint = "gtk_window_get_title")] private static extern nint GtkWindowTitle(nint widget);
    [DllImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_window")] private static extern nint GtkWidgetWindow(nint widget);
    [DllImport("libgdk-3.so.0", EntryPoint = "gdk_x11_window_get_xid")] private static extern nuint X11WindowId(nint window);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_list_free")] private static extern void FreeList(nint list);
}
