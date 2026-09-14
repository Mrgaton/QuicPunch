using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace QuicPunch.Vpn.Linux;

public sealed class LinuxVpnAdapter : IVpnAdapter
{
    private int _tunFd = -1;
    private bool _disposed;

    public string Name { get; }
    public bool IsActive => _tunFd >= 0 && !_disposed;
    public IPAddress? LocalIp { get; private set; }
    public string? SubnetMask { get; private set; }
    public int Mtu { get; private set; } = 1420;

    public LinuxVpnAdapter(string name = "qp-tun0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        int fd = LinuxTunApi.Open("/dev/net/tun", LinuxTunApi.O_RDWR);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new Win32Exception(errno, $"Failed to open /dev/net/tun (errno {errno}). Ensure the 'tun' kernel module is loaded and you have sufficient permissions.");
        }

        try
        {
            unsafe
            {
                IfReq ifr = default;
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                int copyLen = Math.Min(nameBytes.Length, 15);
                for (int i = 0; i < copyLen; i++)
                {
                    ifr.IfName[i] = nameBytes[i];
                }
                ifr.IfName[copyLen] = 0;

                ifr.IfFlags = (short)(LinuxTunApi.IFF_TUN | LinuxTunApi.IFF_NO_PI);

                int ret = LinuxTunApi.Ioctl(fd, LinuxTunApi.TUNSETIFF, &ifr);
                if (ret < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    throw new Win32Exception(errno, $"Failed to create Linux TUN device '{name}' (errno {errno}). Root or CAP_NET_ADMIN privileges are required.");
                }

                var sb = new StringBuilder();
                for (int i = 0; i < 16 && ifr.IfName[i] != 0; i++)
                {
                    sb.Append((char)ifr.IfName[i]);
                }
                Name = sb.Length > 0 ? sb.ToString() : name;
            }

            _tunFd = fd;
        }
        catch
        {
            LinuxTunApi.Close(fd);
            throw;
        }
    }

    public void SetIpAddress(IPAddress ip, string subnetMask)
    {
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentException.ThrowIfNullOrWhiteSpace(subnetMask);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IPAddress.TryParse(subnetMask, out var maskIp))
            throw new ArgumentException("Invalid subnet mask", nameof(subnetMask));

        uint maskUint = BinaryPrimitives.ReadUInt32BigEndian(maskIp.GetAddressBytes());
        int cidr = BitOperations.PopCount(maskUint);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ip",
            Arguments = $"addr replace {ip}/{cidr} dev {Name}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit();
        if (process is { ExitCode: not 0 })
            throw new InvalidOperationException($"ip command failed while assigning {ip}/{cidr} to {Name} (exit code {process.ExitCode}).");

        LocalIp = ip;
        SubnetMask = subnetMask;
    }

    public void SetMtu(int mtu)
    {
        if (mtu < 1200 || mtu > 9000)
            throw new ArgumentOutOfRangeException(nameof(mtu), "MTU must be between 1200 and 9000.");
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ip",
            Arguments = $"link set dev {Name} mtu {mtu} up",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit();
        if (process is { ExitCode: not 0 })
            throw new InvalidOperationException($"ip command failed while setting MTU {mtu} on {Name} (exit code {process.ExitCode}).");

        Mtu = mtu;
    }

    public IVpnSession StartSession(uint bufferCapacity = 0x400000 * 4)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tunFd < 0)
            throw new InvalidOperationException("TUN adapter is closed.");

        int sessionFd = LinuxTunApi.Dup(_tunFd);
        if (sessionFd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new Win32Exception(errno, $"Failed to duplicate TUN file descriptor (errno {errno}).");
        }

        return new LinuxVpnSession(sessionFd);
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "ip",
                    Arguments = $"link set dev {Name} down",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process?.WaitForExit();
            }
            catch { }

            int fd = Interlocked.Exchange(ref _tunFd, -1);
            if (fd >= 0)
            {
                LinuxTunApi.Close(fd);
            }
            _disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
