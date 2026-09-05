using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch.Helpers;

namespace QuicPunch.Discovery.PortMapping;

/// <summary>
/// Composite port-mapping coordinator that cascades through available gateway protocols:
/// 1. PCP (Port Control Protocol - RFC 6887) - Sub-millisecond binary protocol with IPv4/IPv6 & CGNAT awareness.
/// 2. NAT-PMP (RFC 6886) - Sub-millisecond binary protocol supported by Apple, OpenWrt & pfSense routers.
/// 3. UPnP IGD (SSDP + SOAP) - Universal Plug and Play fallback for standard consumer routers.
/// </summary>
public sealed class PortMappingCoordinator : IPortMapper
{
    private readonly PcpPortMapper _pcp;
    private readonly NatPmpPortMapper _natPmp;
    private readonly UpnpPortMapper _upnp;

    private IPortMapper? _activeMapper;
    private PortMappingResult? _lastResult;
    private int _disposed;

    public string ProtocolName => _activeMapper?.ProtocolName ?? "Composite (PCP/NAT-PMP/UPnP)";
    public IPortMapper? ActiveMapper => _activeMapper;
    public PortMappingResult? LastResult => _lastResult;
    public UpnpPortMapper UpnpMapper => _upnp;

    public PortMappingCoordinator(IPAddress? gatewayAddress = null)
    {
        _pcp = new PcpPortMapper(gatewayAddress);
        _natPmp = new NatPmpPortMapper(gatewayAddress);
        _upnp = new UpnpPortMapper();
    }

    public async Task<PortMappingResult> TryMapPortAsync(
        int internalPort,
        int requestedExternalPort,
        ProtocolType protocol = ProtocolType.Udp,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return PortMappingResult.Failed(ProtocolName, "Coordinator is disposed.");

        // 1. Try PCP (RFC 6887) - Fastest, modern standard
        try
        {
            using var pcpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pcpCts.CancelAfter(TimeSpan.FromMilliseconds(500));

            var pcpResult = await _pcp.TryMapPortAsync(internalPort, requestedExternalPort, protocol, lifetime, pcpCts.Token).ConfigureAwait(false);
            if (pcpResult.Success)
            {
                _activeMapper = _pcp;
                _lastResult = pcpResult;
                QuicPunchLog.Info($"[PORT-MAP] Successfully mapped port via {_pcp.ProtocolName}: external {pcpResult.ExternalPort} -> internal {internalPort}");
                return pcpResult;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            QuicPunchLog.Info($"[PORT-MAP] PCP attempt notice: {ex.Message}");
        }

        // 2. Try NAT-PMP (RFC 6886) - Fast binary protocol on port 5351
        try
        {
            using var natPmpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            natPmpCts.CancelAfter(TimeSpan.FromMilliseconds(500));

            var natPmpResult = await _natPmp.TryMapPortAsync(internalPort, requestedExternalPort, protocol, lifetime, natPmpCts.Token).ConfigureAwait(false);
            if (natPmpResult.Success)
            {
                _activeMapper = _natPmp;
                _lastResult = natPmpResult;
                QuicPunchLog.Info($"[PORT-MAP] Successfully mapped port via {_natPmp.ProtocolName}: external {natPmpResult.ExternalPort} -> internal {internalPort}");
                return natPmpResult;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            QuicPunchLog.Info($"[PORT-MAP] NAT-PMP attempt notice: {ex.Message}");
        }

        // 3. Fall back to UPnP IGD (SSDP discovery + XML/SOAP control)
        try
        {
            var upnpResult = await _upnp.TryMapPortAsync(internalPort, requestedExternalPort, protocol, lifetime, ct).ConfigureAwait(false);
            if (upnpResult.Success)
            {
                _activeMapper = _upnp;
                _lastResult = upnpResult;
                QuicPunchLog.Info($"[PORT-MAP] Successfully mapped port via {_upnp.ProtocolName}: external {upnpResult.ExternalPort} -> internal {internalPort}");
                return upnpResult;
            }

            _lastResult = upnpResult;
            return upnpResult;
        }
        catch (Exception ex)
        {
            var failedResult = PortMappingResult.Failed(ProtocolName, $"All port mapping providers failed. Last UPnP error: {ex.Message}");
            _lastResult = failedResult;
            return failedResult;
        }
    }

    public async Task<bool> TryUnmapPortAsync(
        int externalPort,
        ProtocolType protocol = ProtocolType.Udp,
        CancellationToken ct = default)
    {
        if (_activeMapper != null)
        {
            try
            {
                return await _activeMapper.TryUnmapPortAsync(externalPort, protocol, ct).ConfigureAwait(false);
            }
            catch { }
        }

        // Attempt best-effort unmapping across all providers if no single active mapper was tracked
        bool pcpOk = false, natPmpOk = false, upnpOk = false;
        try { pcpOk = await _pcp.TryUnmapPortAsync(externalPort, protocol, ct).ConfigureAwait(false); } catch { }
        try { natPmpOk = await _natPmp.TryUnmapPortAsync(externalPort, protocol, ct).ConfigureAwait(false); } catch { }
        try { upnpOk = await _upnp.TryUnmapPortAsync(externalPort, protocol, ct).ConfigureAwait(false); } catch { }

        return pcpOk || natPmpOk || upnpOk;
    }

    public async Task<IPAddress?> GetExternalIpAddressAsync(CancellationToken ct = default)
    {
        if (_activeMapper != null)
        {
            var ip = await _activeMapper.GetExternalIpAddressAsync(ct).ConfigureAwait(false);
            if (ip != null) return ip;
        }

        // Try fast binary queries first
        var pcpIp = await _pcp.GetExternalIpAddressAsync(ct).ConfigureAwait(false);
        if (pcpIp != null) return pcpIp;

        var natPmpIp = await _natPmp.GetExternalIpAddressAsync(ct).ConfigureAwait(false);
        if (natPmpIp != null) return natPmpIp;

        return await _upnp.GetExternalIpAddressAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pcp.Dispose();
        _natPmp.Dispose();
        _upnp.Dispose();
    }
}
