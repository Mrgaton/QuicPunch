using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace QuicPunch.Vpn.Windows
{
    public class WintunSession : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;
        private WaitHandle _readWaitEvent;

        internal WintunSession(IntPtr handle)
        {
            _handle = handle;
            IntPtr waitEventHandle = WintunApi.WintunGetReadWaitEvent(_handle);
            if (waitEventHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to get read wait event.");
            }

            var safeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(waitEventHandle, ownsHandle: false);
            _readWaitEvent = new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = safeWaitHandle };
        }

        public IntPtr Handle => _handle;
        public WaitHandle ReadWaitEvent => _readWaitEvent;

        public unsafe byte[]? ReceivePacket()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            byte* packetPointer = WintunApi.WintunReceivePacket(_handle, out uint packetSize);
            if (packetPointer == null)
            {
                int error = Marshal.GetLastWin32Error();
                const int ERROR_NO_MORE_ITEMS = 259;
                
                if (error == ERROR_NO_MORE_ITEMS) 
                    return null;
                
                throw new Win32Exception(error, "Failed to receive Wintun packet.");
            }

            try
            {
                byte[] packet = new byte[packetSize];
                Marshal.Copy((IntPtr)packetPointer, packet, 0, (int)packetSize);
                return packet;
            }
            finally
            {
                WintunApi.WintunReleaseReceivePacket(_handle, packetPointer);
            }
        }

        public unsafe void SendPacket(byte[] packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            SendPacket(packet.AsSpan());
        }

        public unsafe void SendPacket(ReadOnlySpan<byte> packet)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (packet.Length > 0xFFFF) throw new ArgumentException("Packet size exceeds maximum IP packet size (65535 bytes).", nameof(packet));

            byte* sendBuffer = WintunApi.WintunAllocateSendPacket(_handle, (uint)packet.Length);
            if (sendBuffer == null)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to allocate send packet buffer.");
            }

            packet.CopyTo(new Span<byte>(sendBuffer, packet.Length));
            WintunApi.WintunSendPacket(_handle, sendBuffer);
        }

        public void Close()
        {
            Dispose();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _readWaitEvent?.Dispose();
                }

                if (_handle != IntPtr.Zero)
                {
                    WintunApi.WintunEndSession(_handle);
                    _handle = IntPtr.Zero;
                }
                _disposed = true;
            }
        }

        ~WintunSession()
        {
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
