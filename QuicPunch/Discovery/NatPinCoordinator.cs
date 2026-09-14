using System;
using System.Collections.Concurrent;
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

        private readonly object _lock = new();
        private readonly object _historyLock = new();

        private IPEndPoint[] _allServers = Array.Empty<IPEndPoint>();
        private IPEndPoint[] _shuffledServers = Array.Empty<IPEndPoint>();
        private int _serverCursor = 0;

        private readonly LinkedList<StunBurstSample> _burstHistory = new();
        private readonly ConcurrentDictionary<IPEndPoint, int> _currentBurstHits = new();

        private UdpClient? _udp;
        private SimpleStunClient? _stunClient;
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
            if (SimpleStunClient.IsBogonOrLocalhost(e.MappedEndPoint.Address))
                return;

            _currentBurstHits.AddOrUpdate(e.MappedEndPoint, 1, (_, count) => count + 1);
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

        private async Task BurstLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(BurstInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    await ExecuteBurstAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        QuicPunchLog.Error("[NAT PIN] Error in NAT discovery burst cycle", ex);
                    }
                }
            }
        }

        public async Task<NatMappingResult> ExecuteBurstAsync(CancellationToken token = default)
        {
            if (!await _burstGate.WaitAsync(0, token).ConfigureAwait(false))
            {
                return CurrentMapping;
            }

            try
            {
                var stunClient = _stunClient;
                if (stunClient == null || _allServers.Length == 0)
                {
                    if (OnStunRefreshRequested != null)
                    {
                        try
                        {
                            await OnStunRefreshRequested(false, token).ConfigureAwait(false);
                            stunClient = _stunClient;
                        }
                        catch { }
                    }

                    if (stunClient == null || _allServers.Length == 0)
                    {
                        return CurrentMapping;
                    }
                }

                // 1. Select 16 rotating servers from the shuffled catalog
                var targetBatch = GetNextBurstBatch(BurstBatchSize);
                if (targetBatch.Count == 0)
                    return CurrentMapping;

                // 2. Clear current burst transient buffer and fire simultaneous requests
                _currentBurstHits.Clear();
                await stunClient.SendRequest(token, targets: targetBatch).ConfigureAwait(false);

                // 3. Wait collection window to gather UDP responses
                try
                {
                    await Task.Delay(ResponseCollectionDelay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return CurrentMapping;
                }

                // 4. Record sample into 3-burst history buffer
                var sampleHits = new Dictionary<IPEndPoint, int>(_currentBurstHits);
                var sample = new StunBurstSample(sampleHits, targetBatch.Count);

                lock (_historyLock)
                {
                    if (_burstHistory.Count >= HistoryCapacity)
                    {
                        _burstHistory.RemoveFirst();
                    }
                    _burstHistory.AddLast(sample);
                }

                // 5. Handle failure detection and auto-recovery
                if (sample.ResponsesReceived == 0)
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= 3 && OnStunRefreshRequested != null)
                    {
                        QuicPunchLog.Info($"[NAT PIN RESILIENCE] {_consecutiveFailures} consecutive STUN bursts received 0 responses. Requesting endpoint refresh...");
                        try
                        {
                            await OnStunRefreshRequested(true, token).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
                else
                {
                    _consecutiveFailures = 0;
                }

                // 6. Aggregate 3-burst history and compute consolidated mapping
                var newMapping = ComputeAggregatedMapping();
                bool changed = HasMappingChanged(CurrentMapping, newMapping);
                CurrentMapping = newMapping;

                if (changed)
                {
                    MappingChanged?.Invoke(this, newMapping);
                }

                return newMapping;
            }
            finally
            {
                _burstGate.Release();
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

            if (aggregatedHits.Count == 0)
            {
                return new NatMappingResult
                {
                    HistorySamplesCount = samplesCount
                };
            }

            int localPort = _udp?.Client.LocalEndPoint is IPEndPoint localEndpoint ? localEndpoint.Port : 0;
            var connectionFlags = Utilities.GetConnectionFlags(aggregatedHits.Keys, localPort);
            int mostUsedPort = Utilities.GetMostUsedPort(aggregatedHits);

            var ports = aggregatedHits.Keys.Select(k => k.Port).ToList();
            int minObservedPort = ports.Count > 0 ? ports.Min() : mostUsedPort;
            int maxObservedPort = ports.Count > 0 ? ports.Max() : mostUsedPort;

            int minPort = minObservedPort == maxObservedPort || ports.All(p => p == mostUsedPort)
                ? mostUsedPort
                : Math.Clamp(minObservedPort, 1, 65535);

            int maxPort = minObservedPort == maxObservedPort || ports.All(p => p == mostUsedPort)
                ? mostUsedPort
                : Math.Clamp(maxObservedPort, 1, 65535);

            var discoveredAddresses = aggregatedHits.Keys
                .Where(ep => ep.Port is >= 1 and <= 65535 && !SimpleStunClient.IsBogonOrLocalhost(ep.Address))
                .Select(ep => ep.Address)
                .Distinct()
                .OrderBy(Utilities.IpToUint)
                .ToArray();

            ushort[] portArray = connectionFlags.PortMode switch
            {
                PortMode.Single => [(ushort)mostUsedPort],
                PortMode.Multiple => ports.Distinct().Select(p => (ushort)p).OrderBy(p => p).ToArray(),
                PortMode.Range => Enumerable.Range(minPort, maxPort - minPort + 1).Select(p => (ushort)p).ToArray(),
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
                _currentBurstHits.Clear();
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
            if (result.Count == 0 && _stunClient != null)
            {
                return _stunClient.GetActiveHitsSnapshot();
            }
            return result;
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
            _burstGate.Dispose();
        }
    }

    public sealed class NatMappingResult
    {
        public IPAddress[] DiscoveredAddresses { get; init; } = Array.Empty<IPAddress>();
        public ushort[] PortArray { get; init; } = [];
        public int MostUsedPort { get; init; }
        public ConnectionFlags ConnectionFlags { get; init; } = new();
        public QuicPunch.NetworkType NetworkType => ConnectionFlags.IsTor
            ? QuicPunch.NetworkType.Tor
            : (ConnectionFlags.MultipleIps
                ? (ConnectionFlags.IsPortRange ? QuicPunch.NetworkType.DynamicPortAndAddress : QuicPunch.NetworkType.DynamicAddress)
                : (ConnectionFlags.IsPortRange ? QuicPunch.NetworkType.DynamicPort : QuicPunch.NetworkType.Static));
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
