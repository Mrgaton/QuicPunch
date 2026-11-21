using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch.Helpers;

namespace QuicPunch.Discovery
{
    public sealed class NatPinCoordinator : IDisposable
    {
        public const int DefaultBurstBatchSize = 32;
        public const int HistoryCapacity = 3;
        public static readonly TimeSpan DefaultBurstInterval = TimeSpan.FromMilliseconds(1500);
        public static readonly TimeSpan DefaultResponseCollectionDelay = TimeSpan.FromMilliseconds(700);
        internal static readonly TimeSpan FastSamplingInterval = TimeSpan.FromMilliseconds(750);
        internal static readonly TimeSpan IdleSamplingInterval = TimeSpan.FromSeconds(8);
        internal static readonly TimeSpan ObservationLifetime = TimeSpan.FromSeconds(90);
        private const int MaxObservations = 128;

        private readonly object _lock = new();
        private readonly object _historyLock = new();

        private IPEndPoint[] _allServers = Array.Empty<IPEndPoint>();
        private IPEndPoint[] _shuffledServers = Array.Empty<IPEndPoint>();
        private int _serverCursor = 0;

        private readonly LinkedList<StunBurstSample> _burstHistory = new();
        private readonly Dictionary<(IPEndPoint Server, IPEndPoint Mapped), long> _observations = new();
        private readonly Dictionary<IPEndPoint, IPEndPoint> _lastMappingByServer = new();
        private readonly SemaphoreSlim _firstObservation = new(0, 1);
        private readonly SemaphoreSlim _samplingWake = new(0, 1);
        private IPEndPoint? _referenceServer;
        private bool _checkReferenceNext;
        private bool _rescanRequested;
        private long _lastBurst;
        private long _lastSampleStarted;
        private long _fastSamplingUntil;
        private long _responseCount;

        private UdpClient? _udp;
        private volatile SimpleStunClient? _stunClient;
        private Task? _burstLoopTask;
        private CancellationTokenSource? _loopCts;
        private int _consecutiveFailures = 0;
        private readonly SemaphoreSlim _burstGate = new(1, 1);

        public int BurstBatchSize { get; set; } = DefaultBurstBatchSize;
        public TimeSpan BurstInterval { get; set; } = DefaultBurstInterval;
        public TimeSpan ResponseCollectionDelay { get; set; } = DefaultResponseCollectionDelay;

        public IReadOnlyList<IPEndPoint> Servers
        {
            get
            {
                lock (_lock) return _allServers;
            }
        }

        public SimpleStunClient? StunClient => _stunClient;
        public NatMappingResult CurrentMapping { get; private set; } = new();

        public event EventHandler<NatMappingResult>? MappingChanged;
        public Func<bool, CancellationToken, Task<bool>>? OnStunRefreshRequested;
        internal Func<IPEndPoint, IPEndPoint>? TranslateTarget { get; set; }

        public NatPinCoordinator(IEnumerable<IPEndPoint>? initialServers = null)
        {
            if (initialServers != null)
            {
                SetServerEndpoints(initialServers);
            }
        }

        public void SetServerEndpoints(IEnumerable<IPEndPoint> endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            lock (_lock)
            {
                _allServers = endpoints.Distinct().ToArray();
                _shuffledServers = _allServers.ToArray();
                Random.Shared.Shuffle(_shuffledServers);
                _serverCursor = 0;

                if (_udp != null && _allServers.Length > 0)
                {
                    RebuildStunClient(_udp);
                }
            }
        }

        public void AddServerEndpoints(IEnumerable<IPEndPoint> endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            lock (_lock)
            {
                var existingSet = new HashSet<IPEndPoint>(_allServers);
                var toAdd = endpoints.Where(ep => existingSet.Add(ep)).ToArray();
                if (toAdd.Length == 0) return;

                _allServers = _allServers.Concat(toAdd).ToArray();

                var shuffledToAdd = toAdd.ToArray();
                Random.Shared.Shuffle(shuffledToAdd);
                _shuffledServers = _shuffledServers.Concat(shuffledToAdd).ToArray();

                if (_stunClient != null)
                {
                    _stunClient.AddServers(toAdd);
                }
                else if (_udp != null && _allServers.Length > 0)
                {
                    RebuildStunClient(_udp);
                }
            }
        }

        public void SetUdpClient(UdpClient udp)
        {
            ArgumentNullException.ThrowIfNull(udp);
            lock (_lock)
            {
                _udp = udp;
                RebuildStunClient(udp);
            }
        }

        private void RebuildStunClient(UdpClient udp)
        {
            if (_stunClient != null)
            {
                _stunClient.MappedAddressResolved -= OnStunMappedAddressResolved;
            }

            var servers = _allServers.Length > 0 ? _allServers : Array.Empty<IPEndPoint>();
            if (servers.Length > 0)
            {
                _stunClient = new SimpleStunClient(udp, servers);
                _stunClient.MappedAddressResolved += OnStunMappedAddressResolved;
            }
            else
            {
                _stunClient = null;
            }
        }

        private void OnStunMappedAddressResolved(object? sender, StunResultEventArgs e)
        {
            if (e.ServerEndpoint is not IPEndPoint server || SimpleStunClient.IsBogonOrLocalhost(e.MappedEndPoint.Address))
                return;

            lock (_historyLock)
            {
                if (!ReferenceEquals(sender, _stunClient)) return;
                long now = Environment.TickCount64;
                var key = (server, e.MappedEndPoint);
                bool newCandidate = !_observations.Keys.Any(x => x.Mapped.Equals(e.MappedEndPoint));
                if (_observations.Count >= MaxObservations && !_observations.ContainsKey(key))
                    _observations.Remove(_observations.MinBy(entry => entry.Value).Key);
                _observations[key] = now;
                _referenceServer ??= server;
                if (newCandidate && _lastMappingByServer.TryGetValue(server, out var previous) && !previous.Equals(e.MappedEndPoint))
                {
                    _rescanRequested = true;
                    _fastSamplingUntil = now + 8000;
                }
                _lastMappingByServer[server] = e.MappedEndPoint;
                if (_lastMappingByServer.Count > MaxObservations)
                    _lastMappingByServer.Remove(_lastMappingByServer.Keys.First());
                _responseCount++;
                if (_firstObservation.CurrentCount == 0) _firstObservation.Release();
            }
        }

        public bool TryProcessIncoming(ReadOnlySpan<byte> buffer, IPEndPoint remoteEndPoint)
        {
            var client = _stunClient;
            return client != null && client.TryProcessIncoming(buffer, remoteEndPoint);
        }

        public bool TryProcessIncoming(byte[] buffer, IPEndPoint remoteEndPoint) =>
            TryProcessIncoming(buffer.AsSpan(), remoteEndPoint);

        public IReadOnlyList<IPEndPoint> GetNextBurstBatch(int batchSize)
        {
            lock (_lock)
            {
                if (_shuffledServers.Length == 0)
                    return Array.Empty<IPEndPoint>();

                if (_shuffledServers.Length <= batchSize)
                    return _shuffledServers;

                var batch = new IPEndPoint[batchSize];
                for (int i = 0; i < batchSize; i++)
                {
                    batch[i] = _shuffledServers[(_serverCursor + i) % _shuffledServers.Length];
                }

                _serverCursor = (_serverCursor + batchSize) % _shuffledServers.Length;
                if (_serverCursor == 0)
                {
                    Random.Shared.Shuffle(_shuffledServers);
                }

                return batch;
            }
        }

        public void Start(UdpClient udp, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                Stop();

                SetUdpClient(udp);
                _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = _loopCts.Token;

                Interlocked.Exchange(ref _lastSampleStarted, Environment.TickCount64);
                _burstLoopTask = Task.Run(() => BurstLoopAsync(token), token);
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_loopCts != null)
                {
                    try { _loopCts.Cancel(); } catch { }
                    _loopCts.Dispose();
                    _loopCts = null;
                }
                _burstLoopTask = null;
            }
        }

        public async Task StopAsync()
        {
            Task? worker;
            lock (_lock) { worker = _burstLoopTask; Stop(); }
            if (worker != null) { try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            await _burstGate.WaitAsync().ConfigureAwait(false);
            _burstGate.Release();
        }

        private async Task BurstLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    long now = Environment.TickCount64;
                    bool fast;
                    lock (_historyLock)
                        fast = now < _fastSamplingUntil || _rescanRequested;
                    var interval = fast ? FastSamplingInterval : IdleSamplingInterval;
                    var pause = interval - TimeSpan.FromMilliseconds(now - Interlocked.Read(ref _lastSampleStarted));
                    if (pause > TimeSpan.Zero && await _samplingWake.WaitAsync(pause, token).ConfigureAwait(false))
                        continue;

                    bool burst;
                    lock (_historyLock)
                        burst = (_rescanRequested || !CurrentMapping.HasMapping) &&
                            Environment.TickCount64 - _lastBurst >= Math.Max(5000, BurstInterval.TotalMilliseconds);
                    await SampleAsync(burst, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Error("[NAT PIN] STUN sampling failed", ex);
                }
            }
        }

        public Task<NatMappingResult> ExecuteBurstAsync(CancellationToken token = default) =>
            SampleAsync(burst: true, token);

        internal void RequestFastSampling()
        {
            lock (_historyLock)
            {
                long now = Environment.TickCount64;
                if (now < _fastSamplingUntil) return;
                _fastSamplingUntil = now + 8000;
                if (_samplingWake.CurrentCount == 0) _samplingWake.Release();
            }
        }

        private IReadOnlyList<IPEndPoint> SelectTargets(bool burst)
        {
            if (burst)
            {
                lock (_historyLock)
                {
                    _rescanRequested = false;
                    _lastBurst = Environment.TickCount64;
                }
                // A catalog can contain hundreds of unreliable servers. Spread coverage
                // across rounds, starting with the server that last answered us.
                var candidates = GetNextBurstBatch(BurstBatchSize);
                if (_referenceServer is { } reference)
                    return candidates.Prepend(reference).Distinct().Take(BurstBatchSize).ToArray();
                return candidates;
            }
            _checkReferenceNext = !_checkReferenceNext;
            if (_checkReferenceNext && _referenceServer is { } stable)
                return [stable];
            return GetNextBurstBatch(1);
        }

        private Task? _catalogRefreshTask;

        private async Task RequestCatalogRefreshAsync(bool force, CancellationToken token)
        {
            try
            {
                if (OnStunRefreshRequested is { } refresh)
                    await refresh(force, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { QuicPunchLog.Info($"[NAT PIN] STUN catalog refresh failed: {ex.Message}"); }
        }

        private async Task<NatMappingResult> SampleAsync(bool burst, CancellationToken token)
        {
            await _burstGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                long started = Environment.TickCount64;
                Interlocked.Exchange(ref _lastSampleStarted, started);
                var stunClient = _stunClient;
                if (stunClient == null)
                {
                    if (_catalogRefreshTask is not { IsCompleted: false })
                        _catalogRefreshTask = RequestCatalogRefreshAsync(false, token);
                    return CurrentMapping;
                }

                long before;
                bool hadMapping;
                lock (_historyLock)
                {
                    before = _responseCount;
                    hadMapping = CurrentMapping.HasMapping;
                    _firstObservation.Wait(0);
                }

                var targets = SelectTargets(burst);
                if (TranslateTarget is { } translate)
                    targets = targets.Select(translate).Distinct().ToArray();
                if (targets.Count == 0) return CurrentMapping;
                await stunClient.SendRequest(token, targets: targets).ConfigureAwait(false);

                if (!hadMapping || !burst)
                {
                    await _firstObservation.WaitAsync(ResponseCollectionDelay, token).ConfigureAwait(false);
                    ApplyObservedMapping(stunClient, token);
                }

                if (burst)
                {
                    var remaining = ResponseCollectionDelay - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
                }

                if (!ReferenceEquals(stunClient, _stunClient)) return CurrentMapping;
                if (Interlocked.Read(ref _responseCount) == before)
                {
                    if (++_consecutiveFailures >= 3)
                    {
                        _consecutiveFailures = 0;
                        lock (_historyLock) { _referenceServer = null; _rescanRequested = true; }
                        if (_catalogRefreshTask is not { IsCompleted: false })
                            _catalogRefreshTask = RequestCatalogRefreshAsync(true, token);
                    }
                }
                else
                    _consecutiveFailures = 0;

                return ApplyObservedMapping(stunClient, token);
            }
            finally
            {
                _burstGate.Release();
            }
        }

        private NatMappingResult ApplyObservedMapping(SimpleStunClient client, CancellationToken token)
        {
            lock (_historyLock)
            {
                if (token.IsCancellationRequested || !ReferenceEquals(client, _stunClient)) return CurrentMapping;
                long now = Environment.TickCount64;
                foreach (var stale in _observations.Where(x => now - x.Value >= ObservationLifetime.TotalMilliseconds).Select(x => x.Key).ToArray())
                    _observations.Remove(stale);

                var hits = _observations.Keys.GroupBy(x => x.Mapped).ToDictionary(x => x.Key, x => x.Count());
                var mapping = ComputeMapping(hits, hits.Count == 0 ? 0 : 1);
                bool changed = HasMappingChanged(CurrentMapping, mapping);
                CurrentMapping = mapping;
                if (changed)
                    MappingChanged?.Invoke(this, mapping);
                return mapping;
            }
        }

        public NatMappingResult ComputeAggregatedMapping()
        {
            var aggregatedHits = new Dictionary<IPEndPoint, int>();
            int samplesCount;

            lock (_historyLock)
            {
                samplesCount = _burstHistory.Count;
                foreach (var sample in _burstHistory)
                {
                    foreach (var (endpoint, count) in sample.EndpointHits)
                    {
                        aggregatedHits[endpoint] = aggregatedHits.GetValueOrDefault(endpoint, 0) + count;
                    }
                }
            }

            return ComputeMapping(aggregatedHits, samplesCount);
        }

        private NatMappingResult ComputeMapping(Dictionary<IPEndPoint, int> aggregatedHits, int samplesCount)
        {
            if (aggregatedHits.Count == 0)
            {
                return new NatMappingResult
                {
                    HistorySamplesCount = samplesCount
                };
            }

            int localPort = _udp?.Client.LocalEndPoint is IPEndPoint localEndpoint ? localEndpoint.Port : 0;
            // Native IPv6 ports must not widen an IPv4 NAT's predicted port range.
            var portHits = aggregatedHits.Where(p => p.Key.Address.AddressFamily == AddressFamily.InterNetwork).ToDictionary();
            if (portHits.Count == 0) portHits = aggregatedHits;
            var connectionFlags = Utilities.GetConnectionFlags(portHits, localPort);
            int mostUsedPort = Utilities.GetMostUsedPort(portHits);

            var ports = portHits.Keys.Select(static endpoint => endpoint.Port).ToList();
            int minObservedPort = ports.Count > 0 ? ports.Min() : mostUsedPort;
            int maxObservedPort = ports.Count > 0 ? ports.Max() : mostUsedPort;

            int minPort = minObservedPort == maxObservedPort || ports.All(p => p == mostUsedPort)
                ? mostUsedPort
                : Math.Clamp(minObservedPort, 1, 65535);

            int maxPort = minObservedPort == maxObservedPort || ports.All(p => p == mostUsedPort)
                ? mostUsedPort
                : Math.Clamp(maxObservedPort, 1, 65535);

            var addressHits = aggregatedHits
                .Where(static pair => pair.Key.Port is >= 1 and <= 65535)
                .Where(static pair => !SimpleStunClient.IsBogonOrLocalhost(pair.Key.Address))
                .GroupBy(static pair => pair.Key.Address)
                .Select(static group => new { Address = group.Key, Hits = group.Sum(pair => pair.Value) })
                .OrderByDescending(static entry => entry.Hits)
                .ThenBy(static entry => entry.Address.ToString(), StringComparer.Ordinal)
                .ToArray();

            var discoveredAddresses = addressHits
                .GroupBy(entry => entry.Address.AddressFamily)
                .SelectMany(group => group)
                .Take(32)
                .Select(entry => entry.Address)
                .OrderBy(static address => address.ToString(), StringComparer.Ordinal)
                .ToArray();
            connectionFlags.MultipleIps = discoveredAddresses.Length > 1;

            ushort[] portArray = connectionFlags.PortMode switch
            {
                PortMode.Single => [(ushort)mostUsedPort],
                PortMode.Multiple => ports.Distinct().Select(static port => (ushort)port).OrderBy(static port => port).ToArray(),
                PortMode.Range => [(ushort)minPort, (ushort)maxPort],
                _ => [(ushort)mostUsedPort]
            };

            return new NatMappingResult
            {
                DiscoveredAddresses = discoveredAddresses,
                PortArray = portArray,
                MostUsedPort = mostUsedPort,
                ConnectionFlags = connectionFlags,
                AggregatedHits = aggregatedHits,
                HistorySamplesCount = samplesCount
            };
        }

        public void ResetMapping()
        {
            lock (_historyLock)
            {
                _burstHistory.Clear();
                _observations.Clear();
                _lastMappingByServer.Clear();
                _referenceServer = null;
                _checkReferenceNext = false;
                _rescanRequested = false;
                _lastBurst = 0;
                _fastSamplingUntil = Environment.TickCount64 + 8000;
            }

            var client = _stunClient;
            if (client != null)
            {
                client.StunResponseEndpointHits.Clear();
            }

            var previous = CurrentMapping;
            CurrentMapping = new NatMappingResult
            {
                HistorySamplesCount = 0
            };

            if (previous.HasMapping)
            {
                MappingChanged?.Invoke(this, CurrentMapping);
            }
        }

        internal void InvalidateNetworkPath()
        {
            lock (_lock)
            lock (_historyLock)
            {
                if (_udp != null) RebuildStunClient(_udp);
                ResetMapping();
            }
        }

        public Task RefreshForNetworkChangeAsync(CancellationToken token = default)
        {
            InvalidateNetworkPath();
            return ExecuteBurstAsync(token);
        }

        public Dictionary<IPEndPoint, int> GetStunHitsSnapshot()
        {
            var result = new Dictionary<IPEndPoint, int>();
            lock (_historyLock)
            {
                foreach (var sample in _burstHistory)
                {
                    foreach (var (ep, count) in sample.EndpointHits)
                    {
                        result[ep] = result.GetValueOrDefault(ep, 0) + count;
                    }
                }
            }
            return result.Count != 0 ? result : new Dictionary<IPEndPoint, int>(CurrentMapping.AggregatedHits);
        }

        public void AddManualSample(IPEndPoint mappedEndpoint, int count = 1)
        {
            ArgumentNullException.ThrowIfNull(mappedEndpoint);
            var dict = new Dictionary<IPEndPoint, int> { [mappedEndpoint] = count };
            var sample = new StunBurstSample(dict, 16);

            lock (_historyLock)
            {
                if (_burstHistory.Count >= HistoryCapacity)
                    _burstHistory.RemoveFirst();

                _burstHistory.AddLast(sample);
            }

            CurrentMapping = ComputeAggregatedMapping();
        }

        private static bool HasMappingChanged(NatMappingResult oldMap, NatMappingResult newMap)
        {
            if (oldMap.ConnectionFlags.RawValue != newMap.ConnectionFlags.RawValue) return true;
            if (oldMap.MostUsedPort != newMap.MostUsedPort) return true;
            if (!oldMap.PortArray.SequenceEqual(newMap.PortArray)) return true;
            if (oldMap.AggregatedHits.Count != newMap.AggregatedHits.Count ||
                oldMap.AggregatedHits.Keys.Any(endpoint => !newMap.AggregatedHits.ContainsKey(endpoint))) return true;

            if (oldMap.DiscoveredAddresses.Length != newMap.DiscoveredAddresses.Length) return true;
            for (int i = 0; i < oldMap.DiscoveredAddresses.Length; i++)
            {
                if (!oldMap.DiscoveredAddresses[i].Equals(newMap.DiscoveredAddresses[i]))
                    return true;
            }

            return false;
        }

        public void Dispose()
        {
            Stop();
            if (_stunClient != null)
            {
                _stunClient.MappedAddressResolved -= OnStunMappedAddressResolved;
                _stunClient = null;
            }
            // An in-flight caller may still release this managed gate; GC reclaims it safely.
        }
    }

    public sealed class NatMappingResult
    {
        public IPAddress[] DiscoveredAddresses { get; init; } = Array.Empty<IPAddress>();
        public ushort[] PortArray { get; init; } = [];
        public int MostUsedPort { get; init; }
        public ConnectionFlags ConnectionFlags { get; init; } = new();
        public QuicPunch.NetworkType NetworkType => Utilities.GetNetworkType(ConnectionFlags);
        public IReadOnlyDictionary<IPEndPoint, int> AggregatedHits { get; init; } = new Dictionary<IPEndPoint, int>();
        public int HistorySamplesCount { get; init; }
        public bool HasMapping => DiscoveredAddresses.Length > 0 && MostUsedPort > 0;
    }

    public sealed class StunBurstSample
    {
        public long TimestampTicks { get; }
        public IReadOnlyDictionary<IPEndPoint, int> EndpointHits { get; }
        public int ServersContacted { get; }
        public int ResponsesReceived { get; }

        public StunBurstSample(IReadOnlyDictionary<IPEndPoint, int> hits, int serversContacted)
        {
            TimestampTicks = Environment.TickCount64;
            EndpointHits = new Dictionary<IPEndPoint, int>(hits);
            ServersContacted = serversContacted;
            ResponsesReceived = hits.Values.Sum();
        }
    }
}
