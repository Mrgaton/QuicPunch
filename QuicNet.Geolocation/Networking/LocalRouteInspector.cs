using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace QuicNet.Geolocation;

public interface ILocalRouteInspector { RouteAssessment Inspect(string destination); }

/// <summary>Local OS queries only. Does not bind to a physical interface or bypass a tunnel.</summary>
public sealed class LocalRouteInspector : ILocalRouteInspector
{
    public RouteAssessment Inspect(string destination)
    {
        if (!IPAddress.TryParse(destination, out var ip)) throw new ArgumentException("A literal IP is required.");
        try
        {
            string? source;
            int? index = null;
            string method;
            if (OperatingSystem.IsWindows())
            {
                (index, source) = WindowsRoute(ip);
                method = "GetBestRoute2";
            }
            else
            {
                // UDP connect performs a local route lookup; no Send/SendTo and no application datagrams.
                using var socket = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(new IPEndPoint(ip, 9));
                source = (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
                method = "kernel-selected-source (UDP policy may differ from ICMP)";
            }
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var properties = nic.GetIPProperties();
                int? nicIndex = null, mtu = null;
                try
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    { var p = properties.GetIPv4Properties(); nicIndex = p?.Index; mtu = p?.Mtu; }
                    else
                    { var p = properties.GetIPv6Properties(); nicIndex = p?.Index; mtu = p?.Mtu; }
                }
                catch (NetworkInformationException) { }
                if (index.HasValue ? nicIndex != index : !properties.UnicastAddresses.Any(a => a.Address.ToString() == source)) continue;
                string text = (nic.Name + " " + nic.Description).ToLowerInvariant();
                bool tunnel = nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    new[] { "wireguard", "wintun", "tap-windows", "openvpn", "tailscale", "zerotier", "fortinet ssl vpn", "anyconnect" }
                        .Any(text.Contains);
                if (OperatingSystem.IsLinux() && !nic.Name.Contains('/') &&
                    File.Exists("/sys/class/net/" + nic.Name + "/tun_flags")) tunnel = true;
                bool? classification = tunnel ? true :
                    nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? false : null;
                return new(destination, source, nic.Id, nic.Name, nic.Description, nicIndex, mtu,
                    nic.NetworkInterfaceType.ToString(), classification, method, null);
            }
            return Unknown(destination, method, "Selected route could not be mapped to an active interface.") with { SourceAddress = source, InterfaceIndex = index };
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException or PlatformNotSupportedException or
            UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        { return Unknown(destination, "local-route-query", ex.Message); }
    }
    private static RouteAssessment Unknown(string destination, string method, string error) =>
        new(destination, null, null, null, null, null, null, "unknown", null, method, error);

    // SOCKADDR_INET is a 28-byte IPv4/IPv6 union. The first two fields in MIB_IPFORWARD_ROW2
    // are NET_LUID (8 bytes) and NET_IFINDEX (4 bytes). The opaque output buffer is oversized.
    private static (int Index, string Source) WindowsRoute(IPAddress ip)
    {
        nint destination = Marshal.AllocHGlobal(28), bestSource = Marshal.AllocHGlobal(28), route = Marshal.AllocHGlobal(256);
        try
        {
            byte[] bytes = new byte[28];
            bool ipv4 = ip.AddressFamily == AddressFamily.InterNetwork;
            BitConverter.GetBytes((ushort)(ipv4 ? 2 : 23)).CopyTo(bytes, 0);
            ip.GetAddressBytes().CopyTo(bytes, ipv4 ? 4 : 8);
            if (!ipv4) BitConverter.GetBytes((uint)ip.ScopeId).CopyTo(bytes, 24);
            Marshal.Copy(bytes, 0, destination, bytes.Length);
            Marshal.Copy(new byte[28], 0, bestSource, 28);
            Marshal.Copy(new byte[256], 0, route, 256);
            uint error = GetBestRoute2(0, 0, 0, destination, 0, route, bestSource);
            if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
            byte[] result = new byte[28]; Marshal.Copy(bestSource, result, 0, 28);
            var address = ipv4 ? new IPAddress(result.AsSpan(4, 4)) :
                new IPAddress(result.AsSpan(8, 16), BitConverter.ToUInt32(result, 24));
            return (Marshal.ReadInt32(route, 8), address.ToString());
        }
        finally { Marshal.FreeHGlobal(destination); Marshal.FreeHGlobal(bestSource); Marshal.FreeHGlobal(route); }
    }
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestRoute2(nint interfaceLuid, uint interfaceIndex, nint sourceAddress,
        nint destinationAddress, uint addressSortOptions, nint bestRoute, nint bestSourceAddress);
}

public static class TunnelDetector
{
    public static TunnelAssessment Assess(IEnumerable<RouteAssessment> routes, IEnumerable<PathMtuResult>? mtu = null,
        bool hostingExitHint = false)
    {
        var routeArray = routes.DistinctBy(r => r.Destination).ToArray();
        var mtuArray = mtu?.ToArray() ?? [];
        var reasons = new List<string>();
        bool identified = routeArray.Any(r => r.UsesLocalTunnel == true);
        bool reduced = routeArray.Any(r => r.Mtu is > 0 and < 1500) ||
            mtuArray.Any(m => m.UpperBoundBytes is > 0 and < 1500);
        bool complete = routeArray.Length > 0 && routeArray.All(r => r.UsesLocalTunnel.HasValue && r.Error == null);
        if (identified) reasons.Add("A measured destination is routed through an identifiable local tunnel interface.");
        if (reduced) reasons.Add("Reduced MTU: also compatible with PPPoE, mobile access or operator encapsulation.");
        if (hostingExitHint) reasons.Add("Offline metadata classifies the supplied public IP as hosting; this is not proof of VPN.");
        if (routeArray.Any(r => r.Error != null)) reasons.Add("One or more local route lookups were incomplete.");
        reasons.Add("Router-hosted VPNs and application/policy tunnels can escape this inspection. Absence is never guaranteed.");
        reasons.Add("No bypass, remote public-IP lookup, DNS lookup or VPN reputation API is performed.");
        return new(identified ? TunnelState.LocalTunnelIdentified : reduced || hostingExitHint ? TunnelState.PossibleEncapsulation :
            complete ? TunnelState.NoIndicators : TunnelState.Indeterminate, routeArray, mtuArray, reasons.ToArray())
        { MixedRoutes = routeArray.Any(r => r.UsesLocalTunnel == true) && routeArray.Any(r => r.UsesLocalTunnel == false) };
    }
}
