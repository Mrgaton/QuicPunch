namespace QuicPunch.Vpn.Routing;

/// <summary>Enables routing only when explicitly requested and authorized by the administrator.</summary>
public sealed class VpnForwardingManager : IDisposable
{
    private readonly string _adapter;
    private bool _changedLinux;
    private bool _windows;
    public VpnForwardingManager(string adapter, bool enabled)
    {
        _adapter = adapter;
        if (OperatingSystem.IsWindows())
        {
            Run("netsh", ["interface", "ipv4", "set", "interface", adapter, enabled ? "forwarding=enabled" : "forwarding=disabled", "weakhostreceive=enabled", "weakhostsend=enabled"]);
            _windows = true;
        }
        else if (enabled && OperatingSystem.IsLinux())
        {
            if (File.ReadAllText("/proc/sys/net/ipv4/ip_forward").Trim() != "1")
            { Run("sysctl", ["-w", "net.ipv4.ip_forward=1"]); _changedLinux = true; }
        }
    }
    private static void Run(string executable, string[] arguments)
    {
        VpnCommand.Run(executable, arguments);
    }
    public void Dispose()
    {
        if (_windows) { try { Run("netsh", ["interface", "ipv4", "set", "interface", _adapter, "forwarding=disabled", "weakhostreceive=disabled", "weakhostsend=disabled"]); } catch { } _windows = false; }
        if (_changedLinux) { try { Run("sysctl", ["-w", "net.ipv4.ip_forward=0"]); } catch { } _changedLinux = false; }
    }
}
