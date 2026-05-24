using System;
using System.Runtime.InteropServices;

namespace Wintun
{
    public enum WintunLoggerLevel : int
    {
        Info = 0,
        Warn = 1,
        Err = 2
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void WintunLoggerCallback(WintunLoggerLevel level, ulong timestamp, string message);

    public static unsafe class WintunApi
    {
        private const string DllName = "wintun";

        [DllImport(DllName, ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr WintunCreateAdapter(string name, string tunnelType, Guid* requestedGuid);

        [DllImport(DllName, ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr WintunOpenAdapter(string name);

        [DllImport(DllName, ExactSpelling = true, SetLastError = false)]
        public static extern void WintunCloseAdapter(IntPtr adapter);

        [DllImport(DllName, ExactSpelling = true, SetLastError = true)]
        public static extern int WintunDeleteDriver();

        [DllImport(DllName, ExactSpelling = true, SetLastError = false)]
        public static extern void WintunGetAdapterLUID(IntPtr adapter, out ulong luid);

        [DllImport(DllName, ExactSpelling = true, SetLastError = true)]
        public static extern uint WintunGetRunningDriverVersion();

        [DllImport(DllName, ExactSpelling = true, SetLastError = false)]
        public static extern void WintunSetLogger(WintunLoggerCallback? newLogger);

        [DllImport(DllName, ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

        [DllImport(DllName, ExactSpelling = true, SetLastError = false)]
        public static extern void WintunEndSession(IntPtr session);

        [DllImport(DllName, ExactSpelling = true, SetLastError = false)]
        public static extern IntPtr WintunGetReadWaitEvent(IntPtr session);

        [DllImport(DllName, ExactSpelling = true, SetLastError = true)]
        public static extern byte* WintunReceivePacket(IntPtr session, out uint packetSize);

        [DllImport(DllName, ExactSpelling = true, SetLastError = false)]
        public static extern void WintunReleaseReceivePacket(IntPtr session, byte* packet);

        [DllImport(DllName, ExactSpelling = true, SetLastError = true)]
        public static extern byte* WintunAllocateSendPacket(IntPtr session, uint packetSize);

        [DllImport(DllName, ExactSpelling = true, SetLastError = false)]
        public static extern void WintunSendPacket(IntPtr session, byte* packet);
    }
}
