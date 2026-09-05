using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QuicPunch.Vpn.Windows
{
    public class WintunAdapter : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        public IntPtr Handle => _handle;

        internal WintunAdapter(IntPtr handle)
        {
            _handle = handle;
        }

        public static uint GetRunningDriverVersion()
        {
            uint version = WintunApi.WintunGetRunningDriverVersion();
            if (version == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return version;
        }

        public static void DeleteDriver()
        {
            if (WintunApi.WintunDeleteDriver() == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public static unsafe WintunAdapter Create(string name, string tunnelType, Guid? requestedGuid = null)
        {
            IntPtr handle;
            if (requestedGuid.HasValue)
            {
                Guid guid = requestedGuid.Value;
                handle = WintunApi.WintunCreateAdapter(name, tunnelType, &guid);
            }
            else
            {
                handle = WintunApi.WintunCreateAdapter(name, tunnelType, null);
            }

            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create Wintun adapter.");

            return new WintunAdapter(handle);
        }

        public static WintunAdapter Open(string name)
        {
            IntPtr handle = WintunApi.WintunOpenAdapter(name);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Wintun adapter.");

            return new WintunAdapter(handle);
        }

        public ulong Luid
        {
            get
            {
                WintunApi.WintunGetAdapterLUID(_handle, out ulong luid);
                return luid;
            }
        }

        public WintunSession StartSession(uint capacity = 0x400000 * 4)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IntPtr sessionHandle = WintunApi.WintunStartSession(_handle, capacity);
            if (sessionHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to start Wintun session.");

            return new WintunSession(sessionHandle);
        }

        public void Close()
        {
            Dispose();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (_handle != IntPtr.Zero)
                {
                    WintunApi.WintunCloseAdapter(_handle);
                    _handle = IntPtr.Zero;
                }
                _disposed = true;
            }
        }

        ~WintunAdapter()
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
