using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;

namespace QuicPunch.Vpn.Windows;

public sealed class WindowsVpnAdapter : IVpnAdapter
{
    private WintunAdapter? _adapter;
    private bool _disposed;

    public string Name { get; }
    public bool IsActive => _adapter != null && !_disposed;
    public IPAddress? LocalIp { get; private set; }
    public string? SubnetMask { get; private set; }
    public int Mtu { get; private set; } = 1500;

    public WindowsVpnAdapter(string name = "QuicPunchAdapter", string tunnelType = "QuicPunchTunnel", Guid? requestedGuid = null)
    {
        Name = name;
        _adapter = WintunAdapter.Create(name, tunnelType, requestedGuid);
    }

    public void SetIpAddress(IPAddress ip, string subnetMask)
    {
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentException.ThrowIfNullOrWhiteSpace(subnetMask);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"interface ip set address name=\"{Name}\" static {ip} {subnetMask}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit();
        if (process is { ExitCode: not 0 })
            throw new InvalidOperationException($"netsh failed while assigning IP {ip}/{subnetMask} to {Name} (exit code {process.ExitCode}).");

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
            FileName = "netsh",
            Arguments = $"interface ipv4 set subinterface \"{Name}\" mtu={mtu} store=persistent",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit();
        if (process is { ExitCode: not 0 })
            throw new InvalidOperationException($"netsh failed while setting MTU {mtu} on {Name} (exit code {process.ExitCode}).");

        Mtu = mtu;
    }

    public IVpnSession StartSession(uint bufferCapacity = 0x400000 * 4)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_adapter == null)
            throw new InvalidOperationException("Adapter is not open.");

        var wintunSession = _adapter.StartSession(bufferCapacity);
        return new WindowsVpnSession(wintunSession);
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        if (!_disposed)
        {
            _adapter?.Dispose();
            _adapter = null;
            _disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
