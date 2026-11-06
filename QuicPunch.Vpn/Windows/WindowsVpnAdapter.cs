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
    public int Mtu { get; private set; } = 1420;

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

        // Windows NDIS / Wintun may take a brief moment to register the interface name in netsh.
        // Retry up to 5 times with small backoff before giving up.
        int maxRetries = 5;
        int exitCode = -1;
        string lastError = "";

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"interface ip set address name=\"{Name}\" static {ip} {subnetMask}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });
                if (process != null)
                {
                    lastError = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                    if (exitCode == 0)
                        break;
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            if (attempt < maxRetries)
                Thread.Sleep(200 * attempt);
        }

        if (exitCode != 0)
            throw new InvalidOperationException($"netsh failed while assigning IP {ip}/{subnetMask} to {Name} (exit code {exitCode}). Details: {lastError.Trim()}");

        LocalIp = ip;
        SubnetMask = subnetMask;

        // Apply semi-professional optimizations for gaming and local network traffic:
        // 1. Interface Metric: Set low metric (10) so Windows prioritizes this adapter for game broadcast discovery.
        OptimizeInterfaceMetric(10);

        // 2. Network Category: Set to 'Private' so Windows Firewall allows ICMP (ping) and incoming game discovery.
        ConfigurePrivateNetworkProfile();

        // 3. Firewall rule: Ensure inbound ICMP & game traffic is allowed on this virtual adapter.
        ConfigureFirewallRule();
    }

    private void OptimizeInterfaceMetric(int metric = 10)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface ipv4 set interface \"{Name}\" metric={metric}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(2000);
        }
        catch { }
    }

    private void ConfigurePrivateNetworkProfile()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"try {{ Set-NetConnectionProfile -InterfaceAlias '{Name}' -NetworkCategory Private -ErrorAction SilentlyContinue }} catch {{ }}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(3000);
        }
        catch { }
    }

    private void ConfigureFirewallRule()
    {
        try
        {
            // Add rule allowing all inbound traffic on the virtual interface
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall add rule name=\"QuicPunch LAN Adapter\" dir=in action=allow interface=\"{Name}\" enable=yes",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(2000);
        }
        catch { }
    }

    private void RemoveFirewallRule()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall delete rule name=\"QuicPunch LAN Adapter\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(2000);
        }
        catch { }
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
            RemoveFirewallRule();
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
