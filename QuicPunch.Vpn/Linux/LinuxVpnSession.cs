using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QuicPunch.Vpn.Linux;

public sealed class LinuxVpnSession : IVpnSession
{
    private const int EINTR = 4, EAGAIN = 11, ENOBUFS = 105;
    private readonly SafeFileHandle _tun;
    private int _failed;
    private int _disposed;
    public bool IsHealthy => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _failed) == 0;

    internal LinuxVpnSession(int fd)
    {
        if (fd < 0) throw new ArgumentOutOfRangeException(nameof(fd));
        _tun = new SafeFileHandle((nint)fd, ownsHandle: true);
    }

    public unsafe bool WaitForPacket(int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMilliseconds, -1);
        ThrowIfUnavailable();
        bool held = false;
        try
        {
            // Keep the fd alive during poll: concurrent Dispose must never target a recycled descriptor.
            _tun.DangerousAddRef(ref held);
            PollFd pfd = new() { Fd = _tun.DangerousGetHandle().ToInt32(), Events = LinuxTunApi.POLLIN };
            long started = Environment.TickCount64;
            while (!cancellationToken.IsCancellationRequested)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                int remaining = timeoutMilliseconds < 0 ? -1 : (int)Math.Max(0, timeoutMilliseconds - (Environment.TickCount64 - started));
                // A short slice also lets Dispose interrupt an infinite wait without closing an in-use fd.
                int wait = remaining < 0 ? 50 : Math.Min(remaining, 50);
                int result = LinuxTunApi.Poll(&pfd, 1, wait);
                if (result > 0)
                {
                    if ((pfd.Revents & (LinuxTunApi.POLLERR | LinuxTunApi.POLLHUP | LinuxTunApi.POLLNVAL)) != 0)
                    {
                        Volatile.Write(ref _failed, 1);
                        throw new IOException($"Linux TUN is unavailable (poll events 0x{pfd.Revents:X}).");
                    }
                    return (pfd.Revents & LinuxTunApi.POLLIN) != 0;
                }
                if (result < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno != EINTR) throw Failure("poll", errno);
                }
                if (timeoutMilliseconds >= 0 && Environment.TickCount64 - started >= timeoutMilliseconds) return false;
            }
            return false;
        }
        finally { if (held) _tun.DangerousRelease(); }
    }

    public unsafe int ReceivePacket(Span<byte> destination)
    {
        ThrowIfUnavailable();
        if (destination.IsEmpty) return 0;
        fixed (byte* ptr = destination)
        {
            while (true)
            {
                nint read = LinuxTunApi.Read(_tun, ptr, (nuint)destination.Length);
                if (read > 0) return (int)read;
                if (read == 0) { Volatile.Write(ref _failed, 1); throw new IOException("Linux TUN returned EOF."); }
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR) continue;
                if (errno == EAGAIN) return 0;
                throw Failure("read", errno);
            }
        }
    }

    public unsafe void SendPacket(ReadOnlySpan<byte> packet)
    {
        ThrowIfUnavailable();
        if (packet.IsEmpty) return;
        if (packet.Length > 65535) throw new ArgumentException("IP packets cannot exceed 65535 bytes.", nameof(packet));
        fixed (byte* ptr = packet)
        {
            while (true)
            {
                nint written = LinuxTunApi.Write(_tun, ptr, (nuint)packet.Length);
                if (written == packet.Length) return;
                if (written >= 0)
                {
                    Volatile.Write(ref _failed, 1);
                    throw new IOException("Linux TUN returned a partial packet write.");
                }
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR) continue;
                // Packet-oriented, nonblocking I/O: congestion drops a packet, never tears down the VPN.
                if (errno is EAGAIN or ENOBUFS) return;
                throw Failure("write", errno);
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _failed) != 0) throw new IOException("Linux TUN session has failed.");
    }
    private Win32Exception Failure(string operation, int errno)
    {
        Volatile.Write(ref _failed, 1);
        return new Win32Exception(errno, $"Linux TUN {operation} failed (errno {errno}).");
    }
    public void Close() => Dispose();
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _tun.Dispose(); }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
