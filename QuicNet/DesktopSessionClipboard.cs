using System.Runtime.InteropServices;
using Photino.NET;
using QuicNet.Video;

namespace QuicNet;

internal sealed class DesktopSessionClipboard(PhotinoWindow window, nint owner) : IRemoteSessionClipboard
{
    public Task<string?> ReadAsync() => OnWindowAsync(() => OperatingSystem.IsWindows() ? ReadWindows() : ReadGtk());

    public Task<bool> WriteAsync(string text, Func<bool> permitted) => OnWindowAsync(() =>
        permitted() && (OperatingSystem.IsWindows() ? WriteWindows(text) : WriteGtk(text)));

    private Task<T> OnWindowAsync<T>(Func<T> work) => Task.Run(() =>
    {
        T result = default!;
        window.Invoke(() => result = work());
        return result;
    });

    private string? ReadWindows()
    {
        if (!OpenClipboard(owner)) return null;
        try
        {
            var handle = GetClipboardData(13); // CF_UNICODETEXT
            if (handle == 0) return null;
            var data = GlobalLock(handle);
            if (data == 0) return null;
            try
            {
                int length = (int)Math.Min(GlobalSize(handle) / 2, (nuint)VoiceCallHandler.RemoteClipboardLimit + 1);
                var chars = new char[length];
                Marshal.Copy(data, chars, 0, length);
                int end = Array.IndexOf(chars, '\0');
                return end >= 0 ? new string(chars, 0, end) : null;
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    private bool WriteWindows(string text)
    {
        if (!OpenClipboard(owner)) return false;
        nint handle = 0;
        try
        {
            var chars = (text + '\0').ToCharArray();
            handle = GlobalAlloc(2, (nuint)chars.Length * 2);
            if (handle == 0) return false;
            var data = GlobalLock(handle);
            if (data == 0) return false;
            try { Marshal.Copy(chars, 0, data, chars.Length); }
            finally { GlobalUnlock(handle); }
            if (!EmptyClipboard() || SetClipboardData(13, handle) == 0) return false;
            handle = 0; // Ownership transferred to Windows.
            return true;
        }
        finally { if (handle != 0) GlobalFree(handle); CloseClipboard(); }
    }

    private static string? ReadGtk()
    {
        var clipboard = GtkClipboardGet(GdkAtomIntern("CLIPBOARD", false));
        var text = GtkClipboardWaitForText(clipboard);
        if (text == 0) return null;
        try
        {
            // Bound the managed copy even when the local clipboard contains a large document.
            int maxBytes = VoiceCallHandler.RemoteClipboardLimit * 4;
            int count = 0;
            while (count < maxBytes && Marshal.ReadByte(text, count) != 0) count++;
            return count < maxBytes ? Marshal.PtrToStringUTF8(text, count) : null;
        }
        finally { GFree(text); }
    }

    private static bool WriteGtk(string text)
    {
        var clipboard = GtkClipboardGet(GdkAtomIntern("CLIPBOARD", false));
        if (clipboard == 0) return false;
        GtkClipboardSetText(clipboard, text, -1);
        return true;
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint data);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint count);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint data);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint data);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint data);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint data);
    [DllImport("libgdk-3.so.0", EntryPoint = "gdk_atom_intern")] private static extern nint GdkAtomIntern([MarshalAs(UnmanagedType.LPUTF8Str)] string atom, bool onlyIfExists);
    [DllImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_get")] private static extern nint GtkClipboardGet(nint atom);
    [DllImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_wait_for_text")] private static extern nint GtkClipboardWaitForText(nint clipboard);
    [DllImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_set_text")] private static extern void GtkClipboardSetText(nint clipboard, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int length);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_free")] private static extern void GFree(nint data);
}
