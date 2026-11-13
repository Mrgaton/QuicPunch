using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using QuicPunch.Discovery.PortMapping;

namespace QuicPunch;

public partial class QuicPunch
{
    private readonly SemaphoreSlim _mappingGate = new(1, 1);
    private ManagedPortMapping? _controlMapping;
    private PortMappingResult? _advertisedMapping;
    private readonly ConcurrentDictionary<int, ManagedPortMapping> _tunnelMappings = new();
    private readonly ConcurrentDictionary<int, Task> _mappingMonitors = new();

    private async Task StartControlMappingAsync(UdpClient expectedSocket, CancellationToken token)
    {
        await _mappingGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long networkGeneration = Volatile.Read(ref _networkGeneration);
            if (_controlMapping != null) await _controlMapping.DisposeAsync().ConfigureAwait(false);
            _controlMapping = null;
            if (!EnableUpnp || !ReferenceEquals(udp, expectedSocket)) return;
            int port = ((IPEndPoint)expectedSocket.Client.LocalEndPoint!).Port;
            _controlMapping = await ManagedPortMapping.StartAsync(port, result =>
            {
                if (!ReferenceEquals(udp, expectedSocket) || token.IsCancellationRequested ||
                    networkGeneration != Volatile.Read(ref _networkGeneration)) return;
                _advertisedMapping = result;
                IsUpnpMapped = result != null;
                ActivePortMappingProtocol = result?.ProtocolName;
                OnNatMappingChanged(this, NatCoordinator.CurrentMapping);
                InvalidateTokenCache();
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _mappingGate.Release(); }
    }

    private async Task<PortMappingResult?> MapTunnelAsync(int port, CancellationToken token)
    {
        // Control mapping already discovers whether this network has a mapper.
        // Waiting behind that discovery and then repeating it per data socket adds
        // two timeouts to every connection when UPnP/PCP/NAT-PMP cannot respond.
        // Direct/STUN candidates can proceed while control discovery keeps retrying.
        if (!EnableUpnp || _advertisedMapping is not { Success: true }) return null;
        await _mappingGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_tunnelMappings.TryGetValue(port, out var existing)) return existing.Result;
            var lease = await ManagedPortMapping.StartAsync(port, _ => { }, LifecycleToken, token).ConfigureAwait(false);
            _tunnelMappings[port] = lease;
            _mappingMonitors[port] = MonitorTunnelMappingAsync(port, lease, LifecycleToken);
            return lease.Result;
        }
        finally { _mappingGate.Release(); }
    }

    private async Task MonitorTunnelMappingAsync(int port, ManagedPortMapping lease, CancellationToken token)
    {
        long started = Environment.TickCount64;
        bool connected = false;
        try
        {
            while (!token.IsCancellationRequested && !lease.IsDisposed)
            {
                bool active = _peerMultiplexers.Values.Any(m => !m.IsDisposed && m.LocalEndPoint is IPEndPoint ep && ep.Port == port);
                if (active) connected = true;
                else if (connected || Environment.TickCount64 - started > 90000) break;
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _tunnelMappings.TryRemove(new KeyValuePair<int, ManagedPortMapping>(port, lease));
            await lease.DisposeAsync().ConfigureAwait(false);
            _mappingMonitors.TryRemove(port, out _);
        }
    }

    private async Task RefreshPortMappingsForNetworkChangeAsync(CancellationToken token)
    {
        await StopPortMappingsAsync().ConfigureAwait(false);
        foreach (var entry in _tunnelMappings.ToArray())
            if (_tunnelMappings.TryRemove(entry)) await entry.Value.DisposeAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (udp is { } socket && IsWanStarted)
            await StartControlMappingAsync(socket, _wanLoopCts?.Token ?? LifecycleToken).WaitAsync(token).ConfigureAwait(false);
    }

    private async Task StopPortMappingsAsync()
    {
        await _mappingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_controlMapping != null) await _controlMapping.DisposeAsync().ConfigureAwait(false);
            _controlMapping = null;
            _advertisedMapping = null;
            IsUpnpMapped = false;
            ActivePortMappingProtocol = null;
        }
        finally { _mappingGate.Release(); }
    }
}
