using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using QuicNet.Protocols;
using QuicPunch;

namespace QuicNet.Vpn;

/// <summary>Reconciles desired VPN state against discovery and live protocol sessions; owns all background work.</summary>
internal sealed class VpnController : IVpnControl, IAsyncDisposable
{
    private sealed class Attempt
    {
        public Task? Task;
        public CancellationTokenSource? Cancellation;
        public int Failures;
        public long RetryAt;
        public string State = "Waiting for discovery";
        public string? Error;
    }
    private readonly QuicPunch.QuicPunch _node;
    private readonly VirtualLanHandler _handler;
    private readonly VpnConfigurationStore _store;
    private readonly CancellationTokenSource _stop;
    private readonly ConcurrentDictionary<Guid, Attempt> _attempts = new();
    private readonly Task _worker;
    private long _appliedRevision = -1;
    private long _adapterRetryAt;
    private volatile string? _error;
    private readonly object _reconcile = new();
    private int _disposed;
    private string[] _transportEndpoints = [];
    public VpnConfiguration Configuration => _store.Snapshot();
    public string ConfigurationPath => _store.Path;
    public string? ConfigurationError => _store.LastError ?? _error;
    public object PeerDefaults => System.Text.Json.JsonSerializer.SerializeToElement(
        (_node.PeerStore?.SavedPeers.Select(p => p.CertHash) ?? [])
        .Concat(_node.AvailablePeers.Values.Where(_node.IsTrustedPeer).Select(p => p.CertHash))
        .Select(Convert.ToHexString).Distinct().Select(hash => new QuicNet.Serialization.VpnPeerDefaultResponse(
            hash, VirtualLanHandler.ComputeAutomaticIp(Convert.FromHexString(hash), _node.PoolId).ToString(),
            _node.PeerStore?.HasAutomaticPermission(Convert.FromHexString(hash), "vpn") == true,
            _node.PeerStore?.SavedPeers.Any(p => Convert.ToHexString(p.CertHash) == hash && p.AutoConnect) == true &&
                _node.PeerStore.HasAutomaticPermission(Convert.FromHexString(hash), "vpn"),
            [_handler.LocalIp + "/32"])).ToArray(), QuicNet.Serialization.NativeUiJsonContext.Default.VpnPeerDefaultResponseArray);
    public object OrchestrationStatus
    {
        get { lock (_reconcile) return System.Text.Json.JsonSerializer.SerializeToElement(
            new QuicNet.Serialization.VpnOrchestrationResponse(_appliedRevision,
            !Configuration.Enabled ? "Stopped" : _handler.IsActive ? "Running" : !_handler.CanStartSession ? "Waiting for permissions" :
                _error != null ? "Error" : "Waiting for adapter",
            _attempts.Select(p => new QuicNet.Serialization.VpnConnectionAttemptResponse(p.Key, p.Value.State, p.Value.Failures, p.Value.Error)).ToArray()),
            QuicNet.Serialization.NativeUiJsonContext.Default.VpnOrchestrationResponse); }
    }
    public VpnController(QuicPunch.QuicPunch node, VirtualLanHandler handler, VpnConfigurationStore store, CancellationToken token)
    {
        _node = node;
        _handler = handler;
        _store = store;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        handler.AttachControl(this);
        lock (_reconcile)
        {
            var initial = store.Snapshot();
            if (initial.AutoAssign) initial.SubnetMask = "255.255.255.0";
            try { _handler.ApplyConfiguration(initial, Compile(initial)); _appliedRevision = initial.Revision; }
            catch (InvalidDataException ex)
            {
                _error = ex.Message;
                initial.Enabled = false;
                uint local = Ipv4Prefix.Address(initial.AutoAssign ? VirtualLanHandler.ComputeAutomaticIp(node.CertManager.CertPublicHash, node.PoolId).ToString() : initial.LocalAddress);
                _handler.ApplyConfiguration(initial, new VpnPolicy(local, []));
            }
        }
        _worker = Task.Run(RunAsync);
    }

    internal VpnPolicy Compile(VpnConfiguration config)
    {
        string local = config.AutoAssign ? VirtualLanHandler.ComputeAutomaticIp(_node.CertManager.CertPublicHash, _node.PoolId).ToString() : config.LocalAddress;
        uint localAddress = Ipv4Prefix.Address(local);
        var entries = config.Peers.ToDictionary(p => p.CertHash, StringComparer.OrdinalIgnoreCase);
        foreach (var peer in _node.PeerStore?.SavedPeers ?? [])
        {
            string hash = Convert.ToHexString(peer.CertHash);
            if (!entries.ContainsKey(hash))
            {
                bool automatic = _node.PeerStore!.HasAutomaticPermission(peer.CertHash, "vpn");
                entries[hash] = new() { CertHash = hash, AutoConnect = peer.AutoConnect && automatic, AutoAccept = automatic,
                    AccessibleDestinations = [local + "/32"] };
            }
        }
        foreach (var peer in _node.AvailablePeers.Values.Where(_node.IsTrustedPeer))
        {
            string hash = Convert.ToHexString(peer.CertHash);
            entries.TryAdd(hash, new() { CertHash = hash, AutoConnect = false, AccessibleDestinations = [local + "/32"] });
        }
        return new(localAddress, entries.Values.Where(p => p.Enabled && _node.IsTrustedPeer(Convert.FromHexString(p.CertHash)))
            .Select(p => new VpnPeerPolicy(p, VirtualLanHandler.ComputeAutomaticIp(Convert.FromHexString(p.CertHash), _node.PoolId).ToString(),
                localAddress, p.UsesAutomaticLan(config.Mode), p.Mode != null || config.AutoConnect)));
    }

    public void SaveConfiguration(VpnConfiguration configuration, long expectedRevision)
    {
        lock (_reconcile)
        {
            VpnConfigurationStore.Validate(configuration);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (configuration.Enabled) _ = Compile(configuration);
            _store.Save(configuration, expectedRevision);
            Reconcile();
        }
    }

    public void PausePeer(Guid id)
    {
        lock (_reconcile)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_attempts.TryGetValue(id, out var attempt)) attempt.Cancellation?.Cancel();
            SetAutoConnect(id, false);
            _handler.DisconnectPeer(id);
        }
    }
    public void ResumePeer(Guid id) { lock (_reconcile) SetAutoConnect(id, true); }
    private void SetAutoConnect(Guid id, bool enabled)
    {
        if (!_node.AvailablePeers.TryGetValue(id, out var peer)) return;
        var config = Configuration;
        string hash = Convert.ToHexString(peer.CertHash);
        var entry = config.Peers.FirstOrDefault(p => p.CertHash.Equals(hash, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            entry = new() { CertHash = hash, AutoAccept = _node.PeerStore?.HasAutomaticPermission(peer.CertHash, "vpn") == true,
                AccessibleDestinations = [_handler.LocalIp + "/32"] };
            config.Peers.Add(entry);
        }
        entry.AutoConnect = enabled;
        entry.Paused = !enabled;
        SaveConfiguration(config, config.Revision);
    }

    private async Task RunAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                try { lock (_reconcile) Reconcile(); }
                catch (Exception ex) { _error = ex.Message; }
            } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            lock (_reconcile) foreach (var entry in _attempts.Values) entry.Cancellation?.Cancel();
            try { await Task.WhenAll(_attempts.Values.Select(a => a.Task ?? Task.CompletedTask)).ConfigureAwait(false); } catch { }
            lock (_reconcile)
            {
                foreach (var entry in _attempts.Values) entry.Cancellation?.Dispose();
                _handler.StopTun();
            }
        }
    }

    private void Reconcile()
    {
        foreach (var peer in _node.AvailablePeers.Values.Where(_node.IsTrustedPeer))
            if (_node.TryGetPeerMultiplexer(peer.Id, out var transport) && transport is { IsDisposed: false })
                _ = transport.RefreshRemoteProtocolAvailabilityAsync(_handler.ProtocolId);
        var config = Configuration;
        if (!config.Enabled)
        {
            foreach (var entry in _attempts.Values) { entry.Cancellation?.Cancel(); entry.State = "Disabled"; }
            if (_handler.IsActive) _handler.StopTun();
        }
        // Recompile on discovery/trust changes as well as edits; no peer can acquire an address by announcing it.
        var policy = Compile(config);
        var network = Ipv4Prefix.Parse(Ipv4Prefix.Format(policy.LocalAddress) + "/" + (config.AutoAssign ? 24 : Ipv4Prefix.MaskLength(config.SubnetMask)));
        var prefixes = policy.Peers.SelectMany(p => p.Routes).Append(network).ToArray();
        var endpoints = _node.AvailablePeers.Values.Where(_node.IsTrustedPeer).SelectMany(p => (p.Addresses ?? []).Concat(p.ActiveEndPoint == null ? [] : new[] { p.ActiveEndPoint.Address }))
            .Concat(_node.GetStunHitsSnapshot().Keys.Select(p => p.Address))
            .Where(p => p.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(p))
            .Select(p => p.ToString()).Distinct().Where(p => prefixes.Any(r => r.Contains(Ipv4Prefix.Address(p)))).Order().Take(257).ToArray();
        if (endpoints.Length > 256) throw new InvalidDataException("Too many physical transport endpoints for these VPN routes (maximum 256). Narrow the routed prefixes.");
        bool endpointsChanged = !_transportEndpoints.SequenceEqual(endpoints);
        bool changed = config.Revision != _appliedRevision;
        _transportEndpoints = endpoints;
        if (endpointsChanged) _handler.SetTransportEndpoints(endpoints);
        if (config.AutoAssign) config.SubnetMask = "255.255.255.0";
        // Effective /32 routes may change on discovery or trust updates without a file revision.
        _handler.ApplyConfiguration(config, policy);
        _appliedRevision = config.Revision;
        if (changed || endpointsChanged) _adapterRetryAt = 0;
        _error = _handler.IsActive ? null : _handler.LastError;
        if (!config.Enabled)
        {
            _error = null;
            foreach (var entry in _attempts.Values) { entry.Cancellation?.Cancel(); entry.State = "Disabled"; }
            if (_handler.IsActive) _handler.StopTun();
            return;
        }
        if (!_handler.IsActive && Stopwatch.GetTimestamp() >= _adapterRetryAt)
        {
            try
            {
                if (_handler.CanStartSession) _handler.SetupTun();
                else _error = "Configuración guardada. Esperando permisos de administrador o el servicio VPN.";
            }
            catch (Exception ex) { _error = ex.Message; _adapterRetryAt = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency; }
        }
        var liveIds = _node.AvailablePeers.Keys.ToHashSet();
        foreach (var pair in _attempts.Where(p => !liveIds.Contains(p.Key)).ToArray())
        {
            pair.Value.Cancellation?.Cancel();
            if (pair.Value.Task?.IsCompleted != false && _attempts.TryRemove(pair.Key, out var removed)) removed.Cancellation?.Dispose();
        }
        int starting = _attempts.Count(p => p.Value.Task?.IsCompleted == false && !_handler.HasActivePeer(p.Key));
        foreach (var peer in _node.AvailablePeers.Values)
        {
            if (!_node.IsTrustedPeer(peer) || !peer.HasCertificate || !peer.IsSessionReady) continue;
            var p = policy.Peer(Convert.ToHexString(peer.CertHash));
            var entry = _attempts.GetOrAdd(peer.Id, _ => new());
            if (_handler.HasActivePeer(peer.Id))
            {
                entry.State = "Connected";
                entry.Failures = 0;
                entry.Error = null;
                entry.Cancellation?.CancelAfter(Timeout.InfiniteTimeSpan);
                continue;
            }
            bool selected = p is { AutoConnect: true, Paused: false };
            if (!selected)
            {
                entry.Cancellation?.Cancel();
                entry.State = "Manual";
                continue;
            }
            if (entry.Task != null && entry.Task.IsCompleted)
            {
                entry.Cancellation?.Dispose();
                entry.Cancellation = null;
                entry.Task = null;
                entry.Failures++;
                entry.RetryAt = Stopwatch.GetTimestamp() + (long)(RetryDelay(entry.Failures).TotalSeconds * Stopwatch.Frequency);
            }
            if (!_handler.IsActive) { entry.State = "Waiting for adapter"; continue; }
            if (!_node.TryGetPeerMultiplexer(peer.Id, out var mux) || mux is not { IsDisposed: false })
            { entry.State = "Waiting for transport"; continue; }
            if (mux.GetRemoteProtocolAvailability(_handler.ProtocolId) != true)
            { entry.State = "Waiting for remote adapter capability"; continue; }
            if (entry.Task != null || _node.IsConnectionInFlight(peer.Id, _handler.ProtocolId) || _node.HasActiveProtocolSession(peer.Id, _handler.ProtocolId)) continue;
            if (!peer.IsAvailableAndResponsive()) { entry.State = "Waiting for discovery"; continue; }
            if (Stopwatch.GetTimestamp() < entry.RetryAt) { entry.State = "Retry scheduled"; continue; }
            if (starting >= 4) continue;
            entry.State = "Connecting";
            entry.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            entry.Cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            var token = entry.Cancellation.Token;
            entry.Task = Task.Run(async () => {
                try { await _node.InitQuicConnection(_handler.ProtocolId, peer, 0, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { entry.Error = ex.Message; }
            });
            starting++;
        }
    }
    internal static TimeSpan RetryDelay(int failures) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Clamp(failures, 0, 6))) + Random.Shared.NextDouble());
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; await _stop.CancelAsync(); await _worker.ConfigureAwait(false); _stop.Dispose(); }
}
