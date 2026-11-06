using System.Runtime.InteropServices;

namespace QuicPunch.Helpers;

/// <summary>Keeps a native resource alive for the full duration of a call using its raw handle.</summary>
internal sealed class NativeHandleLease : IDisposable
{
    private SafeHandle? _handle;
    public IntPtr Handle { get; }

    public NativeHandleLease(SafeHandle handle)
    {
        bool held = false;
        try
        {
            handle.DangerousAddRef(ref held);
            Handle = handle.DangerousGetHandle();
            _handle = handle;
        }
        catch
        {
            if (held) handle.DangerousRelease();
            throw;
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.DangerousRelease();
}
