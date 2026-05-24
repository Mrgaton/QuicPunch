using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Wintun
{
    public class WintunAdapter : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        public IntPtr Handle => _handle;

        private WintunAdapter(IntPtr handle)
        {
            _handle = handle;
        }

        /// <summary>
        /// Gets the version of the running Wintun driver.
        /// </summary>
        public static uint GetRunningDriverVersion()
        {
            uint version = WintunApi.WintunGetRunningDriverVersion();
            if (version == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return version;
        }

        /// <summary>
        /// Deletes the Wintun driver if no adapters are in use.
        /// </summary>
        public static void DeleteDriver()
        {
            if (WintunApi.WintunDeleteDriver() == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>
        /// Creates a new Wintun adapter.
        /// </summary>
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

        /// <summary>
        /// Opens an existing Wintun adapter by name.
        /// </summary>
        public static WintunAdapter Open(string name)
        {
            IntPtr handle = WintunApi.WintunOpenAdapter(name);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Wintun adapter.");

            return new WintunAdapter(handle);
        }

        /// <summary>
        /// Gets the LUID of the adapter.
        /// </summary>
        public ulong Luid
        {
            get
            {
                WintunApi.WintunGetAdapterLUID(_handle, out ulong luid);
                return luid;
            }
        }

        /// <summary>
        /// Starts a Wintun session for reading and writing packets.
        /// </summary>
        /// <param name="capacity">Capacity of the ring buffer. Must be between 128kiB and 64MiB and a power of two. Default is 4MiB.</param>
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
