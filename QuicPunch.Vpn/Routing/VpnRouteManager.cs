using System.Net.NetworkInformation;
using System.Text.Json;
using System.Net;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QuicPunch.Vpn.Routing;

/// <summary>Owns only routes successfully added by this instance; never replaces or deletes someone else's routes.</summary>
public sealed class VpnRouteManager(string adapterName, CancellationToken cancellationToken = default) : IDisposable
{
    // MIB_IPFORWARDROW: all fourteen fields are DWORDs; IPv4 DWORDs retain network byte order in memory.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRoute
    {
        public uint Destination, Mask, Policy, NextHop, InterfaceIndex, Type, Protocol, Age, NextHopAS;
        public uint Metric1, Metric2, Metric3, Metric4, Metric5;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetBestRoute(uint destination, uint source, out NativeRoute route);
    internal sealed record PhysicalRoute(uint InterfaceIndex, string InterfaceName, string Gateway, bool Exact);
    internal static PhysicalRoute InspectWindowsRoute(IPAddress address)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        uint destination = BitConverter.ToUInt32(address.GetAddressBytes());
        uint error = GetBestRoute(destination, 0, out var row);
        if (error != 0) throw new Win32Exception((int)error);
        string name = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Supports(NetworkInterfaceComponent.IPv4) &&
            n.GetIPProperties().GetIPv4Properties()?.Index == row.InterfaceIndex)?.Name ?? "";
        return new(row.InterfaceIndex, name, new IPAddress(BitConverter.GetBytes(row.NextHop)).ToString(), row.Mask == uint.MaxValue && row.Destination == destination);
    }
    private sealed record Route(string Prefix, string Interface, string Gateway);
    private readonly List<Route> _installed = [];
    public void ProtectTransportEndpoints(IEnumerable<string> addresses)
    {
        foreach (string value in addresses.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
            string ip = address.ToString();
            if (OperatingSystem.IsWindows())
            {
                var physical = InspectWindowsRoute(address);
                if (physical.InterfaceName == adapterName) throw new IOException("Transport endpoint already routes through the VPN.");
                if (physical.Exact) continue;
                var route = new Route(ip + "/32", physical.InterfaceIndex.ToString(), physical.Gateway);
                Run(true, route); _installed.Add(route);
            }
            else
            {
                using var exact = JsonDocument.Parse(VpnCommand.Run("ip", "-4", "-j", "route", "show", "exact", ip + "/32"));
                if (exact.RootElement.GetArrayLength() != 0)
                {
                    if (exact.RootElement.EnumerateArray().Any(r => r.TryGetProperty("dev", out var dev) && dev.GetString() == adapterName))
                        throw new IOException("Transport endpoint already routes through the VPN.");
                    continue;
                }
                using var json = JsonDocument.Parse(VpnCommand.Run("ip", "-4", "-j", "route", "get", ip));
                var row = json.RootElement[0];
                string network = row.GetProperty("dev").GetString()!;
                if (network == adapterName) throw new IOException("Transport endpoint already routes through the VPN.");
                var route = new Route(ip + "/32", network, row.TryGetProperty("gateway", out var gw) ? gw.GetString()! : "0.0.0.0");
                Run(true, route); _installed.Add(route);
            }
        }
    }
    public void Install(IEnumerable<string> prefixes)
    {
        try
        {
            foreach (string prefix in prefixes.SelectMany(p => p == "0.0.0.0/0" ? new[] { "0.0.0.0/1", "128.0.0.0/1" } : new[] { p }).Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var route = new Route(prefix, adapterName, "0.0.0.0");
                Run(true, route);
                _installed.Add(route);
            }
        }
        catch { Dispose(); throw; }
    }
    private void Run(bool add, Route route)
    {
        string executable;
        string[] arguments;
        if (OperatingSystem.IsWindows())
        {
            executable = "netsh";
            arguments = ["interface", "ipv4", add ? "add" : "delete", "route", "prefix=" + route.Prefix,
                "interface=" + route.Interface, "nexthop=" + route.Gateway, "store=active"];
        }
        else if (OperatingSystem.IsLinux())
        {
            executable = "ip";
            arguments = ["-4", "route", add ? "add" : "del", route.Prefix, "dev", route.Interface, "proto", "186",
                .. route.Gateway == "0.0.0.0" ? Array.Empty<string>() : ["via", route.Gateway]];
        }
        else throw new PlatformNotSupportedException();
        VpnCommand.Run(executable, arguments);
    }
    public void Dispose()
    {
        foreach (var route in _installed.AsEnumerable().Reverse()) { try { Run(false, route); } catch { } }
        _installed.Clear();
    }
}
