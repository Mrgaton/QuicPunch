using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch.Vpn.Linux;

public sealed class LinuxVpnSession : IVpnSession
{
    private int _fd;
    private bool _disposed;

    internal LinuxVpnSession(int fd)
    {
        if (fd < 0)
            throw new ArgumentOutOfRangeException(nameof(fd), "Invalid file descriptor.");
        _fd = fd;
    }

    public unsafe bool WaitForPacket(int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cancellationToken.IsCancellationRequested)
            return false;

        PollFd pfd = new PollFd
        {
            Fd = _fd,
            Events = LinuxTunApi.POLLIN,
            Revents = 0
        };

        int remaining = timeoutMilliseconds;
        const int sliceMs = 50;

        while (!cancellationToken.IsCancellationRequested)
        {
            int currentWait = timeoutMilliseconds < 0 ? sliceMs : Math.Min(remaining, sliceMs);
            int ret = LinuxTunApi.Poll(&pfd, 1, currentWait);

            if (ret > 0)
            {
                if ((pfd.Revents & (LinuxTunApi.POLLERR | LinuxTunApi.POLLHUP)) != 0)
                    return false;
                return (pfd.Revents & LinuxTunApi.POLLIN) != 0;
            }

            if (ret < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                const int EINTR = 4;
                if (errno == EINTR)
                    continue;
                return false;
            }

            if (timeoutMilliseconds >= 0)
            {
                remaining -= currentWait;
                if (remaining <= 0)
                    return false;
            }
        }

        return false;
    }

    public unsafe int ReceivePacket(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.IsEmpty)
            return 0;

        fixed (byte* ptr = destination)
        {
            nint bytesRead = LinuxTunApi.Read(_fd, ptr, (nuint)destination.Length);
            if (bytesRead < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                const int EAGAIN = 11;
                const int EINTR = 4;
                if (errno == EAGAIN || errno == EINTR)
                    return 0;

                throw new Win32Exception(errno, $"Failed to read from Linux TUN device (errno {errno}).");
            }

            return (int)bytesRead;
        }
    }

    public unsafe void SendPacket(ReadOnlySpan<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packet.IsEmpty)
            return;
        if (packet.Length > 0xFFFF)
            throw new ArgumentException("Packet size exceeds maximum IP packet size (65535 bytes).", nameof(packet));

        fixed (byte* ptr = packet)
        {
            nint written = LinuxTunApi.Write(_fd, ptr, (nuint)packet.Length);
            if (written < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new Win32Exception(errno, $"Failed to write to Linux TUN device (errno {errno}).");
            }
        }
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        if (!_disposed)
        {
            int fd = Interlocked.Exchange(ref _fd, -1);
            if (fd >= 0)
            {
                LinuxTunApi.Close(fd);
            }
            _disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
