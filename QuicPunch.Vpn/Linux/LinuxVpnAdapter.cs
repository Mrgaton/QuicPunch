using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QuicPunch.Vpn.Linux;

public sealed class LinuxVpnAdapter : IVpnAdapter
{
    public static bool HasNetworkAdministration
    {
        get
        {
            if (!OperatingSystem.IsLinux()) return false;
            try
            {
                string? effective = File.ReadLines("/proc/self/status").FirstOrDefault(line => line.StartsWith("CapEff:", StringComparison.Ordinal));
                return effective != null && ulong.TryParse(effective.AsSpan(7).Trim(), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out ulong capabilities) && (capabilities & (1UL << 12)) != 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    private readonly SafeFileHandle _tun;
    public string Name { get; }
    public bool IsActive => !_tun.IsClosed && !_tun.IsInvalid;
    public IPAddress? LocalIp { get; private set; }
    public string? SubnetMask { get; private set; }
    public int Mtu { get; private set; } = 1420;

    public LinuxVpnAdapter(string name = "qp-tun0")
    {
        ValidateName(name);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux TUN requires Linux.");
        int fd = LinuxTunApi.Open("/dev/net/tun", LinuxTunApi.O_RDWR | LinuxTunApi.O_NONBLOCK | LinuxTunApi.O_CLOEXEC);
        if (fd < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot open /dev/net/tun. Install/load the tun kernel module and allow access to the device.");
        _tun = new SafeFileHandle((nint)fd, ownsHandle: true);
        try
        {
            unsafe
            {
                IfReq ifr = default;
                for (int i = 0; i < name.Length; i++) ifr.IfName[i] = (byte)name[i];
                // Refuse to attach to an existing persistent device owned by another session.
                ifr.IfFlags = (short)(LinuxTunApi.IFF_TUN | LinuxTunApi.IFF_NO_PI | LinuxTunApi.IFF_TUN_EXCL);
                if (LinuxTunApi.Ioctl(fd, LinuxTunApi.TUNSETIFF, &ifr) < 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot create Linux TUN '{name}'. CAP_NET_ADMIN (or the VPN broker) is required; the name must be unused.");
            }
            Name = name;
        }
        catch { _tun.Dispose(); throw; }
    }

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 15 || name is "." or ".." || name.Any(c => c <= ' ' || c >= 127 || c is '/' or ':' or '%'))
            throw new ArgumentException("TUN names must contain 1-15 ASCII characters without whitespace, '/', ':' or '%'.", nameof(name));
    }

    internal static int PrefixLength(IPAddress ip, string subnetMask)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The VPN currently supports IPv4 only.", nameof(ip));
        if (!IPAddress.TryParse(subnetMask, out var mask) || mask.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("An IPv4 subnet mask is required.", nameof(subnetMask));
        uint value = BinaryPrimitives.ReadUInt32BigEndian(mask.GetAddressBytes());
        uint inverse = ~value;
        if ((inverse & (inverse + 1)) != 0)
            throw new ArgumentException("Subnet mask must be contiguous.", nameof(subnetMask));
        return BitOperations.PopCount(value);
    }

    public void SetIpAddress(IPAddress ip, string subnetMask)
    {
        ObjectDisposedException.ThrowIf(_tun.IsClosed, this);
        int cidr = PrefixLength(ip, subnetMask);
        // Linux does not infer the directed broadcast unless explicitly requested.
        VpnCommand.Run("ip", "-4", "address", "replace", $"{ip}/{cidr}", "broadcast", "+", "dev", Name);
        if (LocalIp != null && (!LocalIp.Equals(ip) || SubnetMask != subnetMask))
            VpnCommand.Run("ip", "-4", "address", "del", $"{LocalIp}/{PrefixLength(LocalIp, SubnetMask!)}", "dev", Name);
        LocalIp = ip;
        SubnetMask = subnetMask;
    }

    public void SetMtu(int mtu)
    {
        if (mtu is < 1200 or > 9000)
            throw new ArgumentOutOfRangeException(nameof(mtu), "MTU must be between 1200 and 9000.");
        ObjectDisposedException.ThrowIf(_tun.IsClosed, this);
        VpnCommand.Run("ip", "link", "set", "dev", Name, "mtu", mtu.ToString(System.Globalization.CultureInfo.InvariantCulture), "up");
        Mtu = mtu;
    }

    public IVpnSession StartSession(uint bufferCapacity = 0x400000 * 4)
    {
        ObjectDisposedException.ThrowIf(_tun.IsClosed, this);
        int fd = LinuxTunApi.Duplicate(_tun, LinuxTunApi.F_DUPFD_CLOEXEC, 0);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot duplicate the TUN descriptor.");
        return new LinuxVpnSession(fd);
    }

    public void Close() => Dispose();
    // Nonpersistent TUN and its connected routes disappear when the last session closes.
    public void Dispose() => _tun.Dispose();
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
