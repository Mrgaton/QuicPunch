using System;
using System.Net;
using System.Threading.Tasks;

namespace QuicPunch.Vpn;

/// <summary>
/// Represents a virtual Layer-3 network adapter on the host operating system (Windows or Linux).
/// </summary>
public interface IVpnAdapter : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Operating system interface name (e.g. "QuicPunchAdapter" on Windows, "qp-tun0" on Linux).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether the adapter has been created and is active.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// The currently assigned local virtual IPv4 address.
    /// </summary>
    IPAddress? LocalIp { get; }

    /// <summary>
    /// The currently assigned subnet mask string (e.g. "255.255.255.0").
    /// </summary>
    string? SubnetMask { get; }

    /// <summary>
    /// The current Maximum Transmission Unit (MTU) of the adapter.
    /// </summary>
    int Mtu { get; }

    /// <summary>
    /// Configures the virtual IP address and subnet mask on the network interface.
    /// </summary>
    void SetIpAddress(IPAddress ip, string subnetMask);

    /// <summary>
    /// Sets the Maximum Transmission Unit (MTU) on the network interface.
    /// </summary>
    void SetMtu(int mtu);

    /// <summary>
    /// Starts a packet I/O session for reading and writing Layer-3 packets.
    /// </summary>
    /// <param name="bufferCapacity">Optional buffer capacity hint (used by ring buffers on Windows).</param>
    IVpnSession StartSession(uint bufferCapacity = 0x400000 * 4);

    /// <summary>
    /// Closes and cleans up the virtual adapter interface.
    /// </summary>
    void Close();
}
