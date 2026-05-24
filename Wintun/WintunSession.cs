using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Wintun
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

            // The wait handle is owned by the Wintun session and should not be closed.
            var safeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(waitEventHandle, ownsHandle: false);
            _readWaitEvent = new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = safeWaitHandle };
        }

        public IntPtr Handle => _handle;

        /// <summary>
        /// Gets the wait handle that gets signaled when there's available data to read.
        /// </summary>
        public WaitHandle ReadWaitEvent => _readWaitEvent;

        /// <summary>
        /// Retrieves the next available packet. Returns null if no packets are available.
        /// </summary>
        public unsafe byte[]? ReceivePacket()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            byte* packetPointer = WintunApi.WintunReceivePacket(_handle, out uint packetSize);
            if (packetPointer == null)
            {
                int error = Marshal.GetLastWin32Error();
                const int ERROR_NO_MORE_ITEMS = 259; // Windows error code 259
                
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

        /// <summary>
        /// Allocates a buffer and sends the packet.
        /// </summary>
        public unsafe void SendPacket(byte[] packet)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (packet.Length > 0xFFFF) throw new ArgumentException("Packet size exceeds maximum IP packet size (65535 bytes).", nameof(packet));

            byte* sendBuffer = WintunApi.WintunAllocateSendPacket(_handle, (uint)packet.Length);
            if (sendBuffer == null)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to allocate send packet buffer.");
            }

            Marshal.Copy(packet, 0, (IntPtr)sendBuffer, packet.Length);
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
