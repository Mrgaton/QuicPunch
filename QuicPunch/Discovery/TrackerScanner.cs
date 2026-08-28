using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using QuicPunch.Helpers;

namespace QuicPunch
{
    public class TrackerScanner : IDisposable, IAsyncDisposable
    {
        private readonly byte[] _infoHash;
        private readonly byte[] _peerId = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).Take(20).ToArray();
        private volatile int _port;

        private IPAddress PublicIp;
        private readonly HashSet<IPAddress> _publicAddresses = new();
        private readonly object _addressLock = new();

        private readonly ConcurrentDictionary<IPEndPoint, DateTime> _peers = new();
        private readonly ConcurrentBag<IPEndPoint> _trackers = new();
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private CancellationTokenSource? _runCts;
        private Task? _runTask;
        private Task? _resolveTask;
        private bool _isDisposed;

        public event Action<IPEndPoint>? OnPeerFound;
        public IEnumerable<IPEndPoint> ActivePeers => _peers.Keys;
        public int AnnouncedPort => _port;
        public bool IsRunning => _runTask != null && !_runTask.IsCompleted;

        public void SetAnnouncedPort(int port)
        {
            if (port > 0 && port <= 65535)
            {
                _port = port;
            }
        }

        public void SetPublicAddresses(IEnumerable<IPAddress>? addresses)
        {
            if (addresses == null) return;
            lock (_addressLock)
            {
                _publicAddresses.Clear();
                foreach (var addr in addresses)
                {
                    if (addr != null)
                    {
                        _publicAddresses.Add(addr);
                        PublicIp = addr;
                    }
                }
            }
        }

        public void UpdateAnnouncement(IEnumerable<IPAddress>? addresses, int announcedPort)
        {
            SetPublicAddresses(addresses);
            SetAnnouncedPort(announcedPort);
        }

        public TrackerScanner(byte[] infoHash, int port)
        {
            if (infoHash.Length != 20) throw new ArgumentException("Hash must be 20 bytes");
            _infoHash = infoHash;

            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Port must be an ushort");

            _port = port;

            PublicIp = IPAddress.Parse("127.0.0.1");
        }

        public static readonly string[] BuiltInTrackers = new[]
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.tracker.cl:1337/announce",
            "udp://tracker.openbittorrent.com:6969/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://explodie.org:6969/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.coppersurfer.tk:6969/announce"
        };

        public static string TrackerEndpointsCachePath { get; set; } = Path.Combine(Path.GetTempPath(), "trackerServersCache.epl");
        public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

        public async Task<int> LoadAndResolveTrackersAsync(string[]? customTrackers = null, CancellationToken ct = default)
        {
            try
            {
                if (customTrackers == null && EndpointCache.TryRead(TrackerEndpointsCachePath, CacheTtl, out var cachedTrackers, checkTtl: true))
                {
                    foreach (var ep in cachedTrackers)
                    {
                        if (!_trackers.Contains(ep))
                        {
                            _trackers.Add(ep);
                        }
                    }
                    if (_trackers.Count > 0)
                    {
                        QuicPunchLog.Info($"[TRACKER CACHE] Loaded {_trackers.Count} tracker endpoint(s) from cache.");
                        return _trackers.Count;
                    }
                }

                var list = customTrackers ?? await GetPublicTrackers(ct);
                if (list == null || list.Length == 0)
                {
                    list = BuiltInTrackers;
                }

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = ct
                };

                var resolved = new HashSet<IPEndPoint>();

                await Parallel.ForEachAsync(list, parallelOptions, async (url, token) =>
                {
                    try
                    {
                        var uri = url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) ? new Uri(url) : new Uri($"udp://{url}");

                        var addresses = await Dns.GetHostAddressesAsync(uri.Host, token).ConfigureAwait(false);

                        lock (resolved)
                        {
                            foreach (var ip in addresses)
                            {
                                resolved.Add(new IPEndPoint(ip, uri.Port));
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        QuicPunchLog.Info($"Failed to resolve tracker {url}: {ex.Message}");
                    }
                }).ConfigureAwait(false);

                if (resolved.Count > 0)
                {
                    foreach (var ep in resolved)
                    {
                        if (!_trackers.Contains(ep))
                        {
                            _trackers.Add(ep);
                        }
                    }
                    if (customTrackers == null)
                    {
                        EndpointCache.Save(TrackerEndpointsCachePath, resolved);
                    }
                    QuicPunchLog.Info($"[TRACKER RECOVERY] Successfully loaded and resolved {_trackers.Count} tracker endpoint(s).");
                }
                else if (customTrackers == null && EndpointCache.TryRead(TrackerEndpointsCachePath, CacheTtl, out var staleCache, checkTtl: false))
                {
                    foreach (var ep in staleCache)
                    {
                        if (!_trackers.Contains(ep))
                        {
                            _trackers.Add(ep);
                        }
                    }
                    if (_trackers.Count > 0)
                    {
                        EndpointCache.Save(TrackerEndpointsCachePath, _trackers);
                    }
                }

                return _trackers.Count;
            }
            catch (OperationCanceledException) { return 0; }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[TRACKER ERROR] Failed to load trackers: {ex.Message}");
                return 0;
            }
        }

        public Task Start(string[]? customTrackers = null) => StartAsync(customTrackers);

        public async Task StartAsync(string[]? customTrackers = null)
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);

                if (IsRunning)
                {
                    QuicPunchLog.Info("[TRACKER] TrackerScanner is already running. Ignoring duplicate Start call.");
                    return;
                }

                _runCts = new CancellationTokenSource();
                var token = _runCts.Token;

                _resolveTask = Task.Run(async () => await LoadAndResolveTrackersAsync(customTrackers, token).ConfigureAwait(false), token);
                _runTask = Task.Run(() => TrackerLoopAsync(customTrackers, token), token);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private async Task TrackerLoopAsync(string[]? customTrackers, CancellationToken token)
        {
            int retryDelaySec = 5;
            DateTime nextRetryUtc = DateTime.UtcNow.AddSeconds(5);
            DateTime nextPeriodicRefreshUtc = DateTime.UtcNow.AddHours(6);

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    if (_trackers.Count == 0)
                    {
                        if (DateTime.UtcNow >= nextRetryUtc)
                        {
                            QuicPunchLog.Info($"[TRACKER RETRY] No active trackers found. Retrying tracker resolution...");
                            int count = await LoadAndResolveTrackersAsync(customTrackers, token).ConfigureAwait(false);
                            if (count == 0)
                            {
                                retryDelaySec = Math.Min(retryDelaySec * 2, 60);
                                nextRetryUtc = DateTime.UtcNow.AddSeconds(retryDelaySec);
                            }
                            else
                            {
                                retryDelaySec = 5;
                            }
                        }
                        continue;
                    }

                    if (DateTime.UtcNow >= nextPeriodicRefreshUtc)
                    {
                        nextPeriodicRefreshUtc = DateTime.UtcNow.AddHours(6);
                        _ = Task.Run(async () => await LoadAndResolveTrackersAsync(customTrackers, token).ConfigureAwait(false), token);
                    }

                    var tasks = _trackers.OrderBy(_ => Random.Shared.Next()).Take(Math.Max(1, _trackers.Count / 4)).Select(endpoint => ParasiteTracker(endpoint, token));

                    await Task.WhenAll(tasks).ConfigureAwait(false);

                    Cleanup();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"Error in tracker loop: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_runCts == null)
                    return;

                try { _runCts.Cancel(); } catch { }

                if (_runTask != null)
                {
                    try { await _runTask.ConfigureAwait(false); } catch { }
                    _runTask = null;
                }

                if (_resolveTask != null)
                {
                    try { await _resolveTask.ConfigureAwait(false); } catch { }
                    _resolveTask = null;
                }

                try { _runCts.Dispose(); } catch { }
                _runCts = null;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        private async Task ParasiteTracker(IPEndPoint endpoint, CancellationToken ct = default)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.SendTimeout = 3000;
                udp.Client.ReceiveTimeout = 3000;

                int transactionId = Random.Shared.Next();

                byte[] connectReq = new byte[16];
                BinaryPrimitives.WriteInt64BigEndian(connectReq, 0x41727101980L);
                BinaryPrimitives.WriteInt32BigEndian(connectReq.AsSpan(8), 0);
                BinaryPrimitives.WriteInt32BigEndian(connectReq.AsSpan(12), transactionId);

                await udp.SendAsync(connectReq, endpoint).ConfigureAwait(false);
                var res = await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

                if (res.Buffer.Length < 16 || !res.RemoteEndPoint.Equals(endpoint))
                    return;

                int connectAction = BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(0, 4));
                int connectTxId = BinaryPrimitives.ReadInt32BigEndian(res.Buffer.AsSpan(4, 4));

                if (connectAction != 0 || connectTxId != transactionId)
                    return;

                long connectionId = BinaryPrimitives.ReadInt64BigEndian(res.Buffer.AsSpan(8, 8));

                int announceTxId = Random.Shared.Next();
                byte[] announceReq = new byte[98];
                var span = announceReq.AsSpan();
                BinaryPrimitives.WriteInt64BigEndian(span, connectionId);
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(8), 1); // Action: Announce
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(12), announceTxId);
                _infoHash.CopyTo(span.Slice(16));
                _peerId.CopyTo(span.Slice(36));
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(80), 2); // Event: Started
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(92), -1); // NumWant
                BinaryPrimitives.WriteInt16BigEndian(span.Slice(96), (short)_port);

                await udp.SendAsync(announceReq, endpoint).ConfigureAwait(false);
                var annRes = await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

                if (annRes.Buffer.Length < 20 || !annRes.RemoteEndPoint.Equals(endpoint))
                    return;

                int annAction = BinaryPrimitives.ReadInt32BigEndian(annRes.Buffer.AsSpan(0, 4));
                int annRespTxId = BinaryPrimitives.ReadInt32BigEndian(annRes.Buffer.AsSpan(4, 4));

                if (annAction != 1 || annRespTxId != announceTxId)
                    return;

                ProcessPeers(annRes.Buffer);
            }
            catch { }
        }

        private void ProcessPeers(byte[] buffer)
        {
            if (buffer.Length < 20) return;
            var peerData = buffer.AsSpan(20);
            for (int i = 0; i < peerData.Length / 6; i++)
            {
                var p = peerData.Slice(i * 6, 6);
                var ip = new IPAddress(p.Slice(0, 4).ToArray());

                if (SimpleStunClient.IsBogonOrLocalhost(ip))
                    continue;

                var endpoint = new IPEndPoint(ip, BinaryPrimitives.ReadUInt16BigEndian(p.Slice(4)));

                bool isSelf = false;
                lock (_addressLock)
                {
                    if (_publicAddresses.Contains(ip) && endpoint.Port == _port)
                        isSelf = true;
                }

                if (isSelf || (endpoint.Address.Equals(PublicIp) && endpoint.Port == _port)) 
                    continue;

                if (_peers.TryAdd(endpoint, DateTime.UtcNow))
                    OnPeerFound?.Invoke(endpoint);
                else
                    _peers[endpoint] = DateTime.UtcNow;
            }
        }

        private void Cleanup()
        {
            var limit = DateTime.UtcNow.AddMinutes(-1);
            foreach (var (key, value) in _peers)
                if (value < limit) _peers.TryRemove(key, out _);
        }

        private async Task<string[]> GetPublicTrackers(CancellationToken ct = default)
        {
            try
            {
                var data = await Utilities.client.GetStringAsync("https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_all_udp.txt", ct).ConfigureAwait(false);
                return data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch { return Array.Empty<string>(); }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
            _lifecycleLock.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            await StopAsync().ConfigureAwait(false);
            _lifecycleLock.Dispose();
        }
    }
}
