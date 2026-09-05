using System;

namespace QuicPunch.Vpn;

public static class VpnAdapterFactory
{
    public static string DefaultAdapterName => OperatingSystem.IsWindows() ? "QuicPunchAdapter" : "qp-tun0";

    public static IVpnAdapter Create(string? name = null, string tunnelType = "QuicPunchTunnel", Guid? requestedGuid = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? DefaultAdapterName : name;

        if (OperatingSystem.IsWindows())
        {
            return new Windows.WindowsVpnAdapter(name, tunnelType, requestedGuid);
        }

        if (OperatingSystem.IsLinux())
        {
            return new Linux.LinuxVpnAdapter(name);
        }

        throw new PlatformNotSupportedException("QuicPunch VPN virtual network adapter is only supported on Windows and Linux.");
    }
}
