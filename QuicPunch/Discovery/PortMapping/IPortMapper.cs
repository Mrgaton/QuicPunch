using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch.Discovery.PortMapping;

/// <summary>
/// Unified contract for gateway port mapping mechanisms (UPnP IGD, NAT-PMP, PCP).
/// </summary>
public interface IPortMapper : IDisposable
{
    /// <summary>
    /// Friendly protocol identifier (e.g. "PCP", "NAT-PMP", "UPnP IGD").
    /// </summary>
    string ProtocolName { get; }

    /// <summary>
    /// Attempts to open a port mapping on the gateway router.
    /// </summary>
    Task<PortMappingResult> TryMapPortAsync(
        int internalPort,
        int requestedExternalPort,
        ProtocolType protocol = ProtocolType.Udp,
        TimeSpan? lifetime = null,
        CancellationToken ct = default);

    /// <summary>
    /// Attempts to delete an existing port mapping from the gateway router.
    /// </summary>
    Task<bool> TryUnmapPortAsync(
        int externalPort,
        ProtocolType protocol = ProtocolType.Udp,
        CancellationToken ct = default);

    /// <summary>
    /// Queries the external/public IP address of the gateway router.
    /// </summary>
    Task<IPAddress?> GetExternalIpAddressAsync(CancellationToken ct = default);
}
