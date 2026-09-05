using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch.Vpn.Windows;

public sealed class WindowsVpnSession : IVpnSession
{
    private readonly WintunSession _session;
    private bool _disposed;

    internal WindowsVpnSession(WintunSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public bool WaitForPacket(int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cancellationToken.IsCancellationRequested)
            return false;

        if (cancellationToken.CanBeCanceled)
        {
            int index = WaitHandle.WaitAny(new[] { _session.ReadWaitEvent, cancellationToken.WaitHandle }, timeoutMilliseconds);
            return index == 0;
        }

        return _session.ReadWaitEvent.WaitOne(timeoutMilliseconds);
    }

    public unsafe int ReceivePacket(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte* packetPointer = WintunApi.WintunReceivePacket(_session.Handle, out uint packetSize);
        if (packetPointer == null)
        {
            int error = Marshal.GetLastWin32Error();
            const int ERROR_NO_MORE_ITEMS = 259;
            if (error == ERROR_NO_MORE_ITEMS)
                return 0;

            throw new Win32Exception(error, "Failed to receive Wintun packet.");
        }

        try
        {
            int size = (int)packetSize;
            if (destination.Length < size)
                return 0;

            new ReadOnlySpan<byte>(packetPointer, size).CopyTo(destination);
            return size;
        }
        finally
        {
            WintunApi.WintunReleaseReceivePacket(_session.Handle, packetPointer);
        }
    }

    public void SendPacket(ReadOnlySpan<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _session.SendPacket(packet);
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        if (!_disposed)
        {
            _session.Dispose();
            _disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
