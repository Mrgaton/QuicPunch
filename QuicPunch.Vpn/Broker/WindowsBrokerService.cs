using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QuicPunch.Vpn.Broker;

public static class WindowsBrokerService
{
    public const string ServiceName = "QuicNetVpnBroker";
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessStatus { public Status Status; public uint Pid, Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TableEntry { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public MainCallback? Main; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void MainCallback(uint argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ControlCallback(uint control);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcherW([In] TableEntry[] entries);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandlerW(string name, ControlCallback callback);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(IntPtr handle, ref Status status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ProcessStatus status, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    private static MainCallback? _main;
    private static ControlCallback? _control;
    public static uint RunningProcessId
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return 0;
            IntPtr manager = OpenSCManagerW(null, null, 1);
            if (manager == IntPtr.Zero) return 0;
            try
            {
                IntPtr service = OpenServiceW(manager, ServiceName, 4);
                if (service == IntPtr.Zero) return 0;
                try { return QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ProcessStatus>(), out _) && status.Status.State == 4 ? status.Pid : 0; }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }
    }
    public static bool IsRunning => RunningProcessId != 0;
    public static void Run(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _main = (_, _) =>
        {
            using var stop = new CancellationTokenSource();
            IntPtr handle = IntPtr.Zero;
            Status status = new() { Type = 16, State = 2, WaitHint = 15000 };
            _control = command =>
            {
                if (command is 1 or 5) { status.State = 3; status.Controls = 0; SetServiceStatus(handle, ref status); stop.Cancel(); }
                else SetServiceStatus(handle, ref status);
            };
            handle = RegisterServiceCtrlHandlerW(ServiceName, _control);
            if (handle == IntPtr.Zero) return;
            SetServiceStatus(handle, ref status);
            try
            {
                status.State = 4; status.Controls = 5; status.WaitHint = 0; SetServiceStatus(handle, ref status);
                VpnBrokerHost.RunAsync(path, stop.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { Console.Error.WriteLine(ex); status.Win32Exit = 1; }
            finally { status.State = 1; status.Controls = 0; SetServiceStatus(handle, ref status); }
        };
        if (!StartServiceCtrlDispatcherW([new() { Name = ServiceName, Main = _main }, new()])) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
