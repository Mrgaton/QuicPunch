using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using QuicPunch.Helpers;

namespace QuicPunch
{
    public static class StunGatherer
    {
        public static string StunEndpointsCachePath { get; set; } = Path.Combine(Path.GetTempPath(), "stunServersCache.epl");
        public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

        public static readonly string[] BuiltInStunHosts = new[]
        {
            "stun.l.google.com:19302",
            "stun1.l.google.com:19302",
            "stun2.l.google.com:19302",
            "stun3.l.google.com:19302",
            "stun4.l.google.com:19302",
            "stun.cloudflare.com:3478",
            "stun.syncthing.net:3478",
            "stun.nextcloud.com:443",
            "stun.sipgate.net:3478",
            "stun.voip.blackberry.com:3478"
        };

        public static readonly string[] RemoteStunUrls = new[]
        {
            "https://raw.githubusercontent.com/pradt2/always-online-stun/refs/heads/master/valid_nat_testing_hosts.txt",
            "https://raw.githubusercontent.com/pradt2/always-online-stun/refs/heads/master/valid_nat_testing_ipv4s.txt",
            "https://raw.githubusercontent.com/pradt2/always-online-stun/refs/heads/master/candidates.txt",
            "https://raw.githubusercontent.com/pradt2/always-online-stun/refs/heads/master/valid_ipv4s.txt",
            "https://raw.githubusercontent.com/pradt2/always-online-stun/refs/heads/master/valid_hosts.txt"
        };

        private static int _backgroundFetchActive = 0;

        /// <summary>
        /// Fast initial resolution for instant node startup (&lt; 50ms).
        /// Reads disk cache if populated (&gt; 10 endpoints) and fresh,
        /// otherwise returns the built-in seed endpoints.
        /// </summary>
        public static async Task<ConcurrentBag<IPEndPoint>> GatherInitialEndpointsAsync(CancellationToken ct = default)
        {
            if (EndpointCache.TryRead(StunEndpointsCachePath, CacheTtl, out var fresh, checkTtl: true) && fresh.Count > 10)
            {
                return ToBag(fresh);
            }

            var builtIns = ResolveBuiltIns(ct);
            if (!builtIns.IsEmpty)
            {
                if (!File.Exists(StunEndpointsCachePath))
                {
                    EndpointCache.Save(StunEndpointsCachePath, builtIns);
                }
                return builtIns;
            }

            if (EndpointCache.TryRead(StunEndpointsCachePath, CacheTtl, out var fallback, checkTtl: false) && fallback.Count > 0)
            {
                return ToBag(fallback);
            }

            return new ConcurrentBag<IPEndPoint>();
        }

        /// <summary>
        /// Initiates non-blocking background fetching and resolution of external STUN catalogs.
        /// Dispatches discovered endpoints in streaming batches to <paramref name="onBatchDiscovered"/>
        /// so active components like NatPinCoordinator can immediately rotate through new servers.
        /// </summary>
        public static Task StartBackgroundCatalogFetchAsync(Action<IReadOnlyList<IPEndPoint>> onBatchDiscovered, CancellationToken ct = default)
        {
            return Task.Run(async () =>
            {
                if (Interlocked.CompareExchange(ref _backgroundFetchActive, 1, 0) != 0)
                {
                    // Already running
                    return;
                }

                try
                {
                    await ResolveRemoteCatalogsAsync(ct, onBatchDiscovered).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[STUN] Background catalog fetch notice: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _backgroundFetchActive, 0);
                }
            }, ct);
        }

        /// <summary>
        /// Fast path used by node startup and backward compatibility.
        /// </summary>
        public static async Task<ConcurrentBag<IPEndPoint>> GatherStunEndpoints(bool forceRefresh = false, CancellationToken ct = default)
        {
            if (!forceRefresh && EndpointCache.TryRead(StunEndpointsCachePath, CacheTtl, out var fresh, checkTtl: true) && fresh.Count > 0)
                return ToBag(fresh);

            if (!forceRefresh)
            {
                if (EndpointCache.TryRead(StunEndpointsCachePath, CacheTtl, out var stale, checkTtl: false) && stale.Count > 0)
                    return ToBag(stale);

                var builtIns = ResolveBuiltIns(ct);
                if (!builtIns.IsEmpty)
                    EndpointCache.Save(StunEndpointsCachePath, builtIns);
                return builtIns;
            }

            var remote = await ResolveRemoteCatalogsAsync(ct).ConfigureAwait(false);
            if (!remote.IsEmpty)
            {
                EndpointCache.Save(StunEndpointsCachePath, remote);
                return remote;
            }

            if (EndpointCache.TryRead(StunEndpointsCachePath, CacheTtl, out var fallbackCache, checkTtl: false) && fallbackCache.Count > 0)
                return ToBag(fallbackCache);

            var fallback = ResolveBuiltIns(ct);
            if (!fallback.IsEmpty)
                EndpointCache.Save(StunEndpointsCachePath, fallback);
            return fallback;
        }

        public static ConcurrentBag<IPEndPoint> ResolveBuiltIns(CancellationToken ct = default)
        {
            var result = new ConcurrentBag<IPEndPoint>();
            foreach (string host in BuiltInStunHosts)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (!TryParseStunEndpoint(host, out var h, out var port)) continue;
                    if (IPAddress.TryParse(h, out var ip))
                    {
                        if (ip.AddressFamily == AddressFamily.InterNetwork && !SimpleStunClient.IsBogonOrLocalhost(ip))
                            result.Add(new IPEndPoint(ip, port));
                        continue;
                    }

                    var addresses = Dns.GetHostAddresses(h);
                    foreach (var addr in addresses)
                    {
                        if (addr.AddressFamily == AddressFamily.InterNetwork && !SimpleStunClient.IsBogonOrLocalhost(addr))
                            result.Add(new IPEndPoint(addr, port));
                    }
                }
                catch { }
            }
            return Deduplicate(result);
        }

        public static async Task<ConcurrentBag<IPEndPoint>> ResolveRemoteCatalogsAsync(
            CancellationToken ct = default,
            Action<IReadOnlyList<IPEndPoint>>? onBatchDiscovered = null)
        {
            var parsed = new ConcurrentBag<string>();
            try
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct };
                await Parallel.ForEachAsync(RemoteStunUrls, options, async (url, token) =>
                {
                    try
                    {
                        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        reqCts.CancelAfter(TimeSpan.FromSeconds(6));
                        string data = await Utilities.client.GetStringAsync(url, reqCts.Token).ConfigureAwait(false);
                        foreach (string line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
                                parsed.Add(line);
                        }
                    }
                    catch { }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            var uniqueRaw = parsed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (uniqueRaw.Count == 0)
            {
                var fallbackBuiltIns = ResolveBuiltIns(ct);
                if (!fallbackBuiltIns.IsEmpty && onBatchDiscovered != null)
                {
                    onBatchDiscovered(fallbackBuiltIns.ToList());
                }
                return fallbackBuiltIns;
            }

            var ipEndpoints = new List<IPEndPoint>();
            var hostnamesToResolve = new List<(string host, int port)>();

            foreach (var raw in uniqueRaw)
            {
                if (!TryParseStunEndpoint(raw, out var host, out var port))
                    continue;

                if (IPAddress.TryParse(host, out var ip))
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !SimpleStunClient.IsBogonOrLocalhost(ip))
                    {
                        ipEndpoints.Add(new IPEndPoint(ip, port));
                    }
                }
                else
                {
                    hostnamesToResolve.Add((host, port));
                }
            }

            var result = new ConcurrentBag<IPEndPoint>();

            // 1. Fast path: immediately yield all raw IP endpoints (0 DNS latency!)
            var distinctIps = ipEndpoints.Distinct().ToList();
            if (distinctIps.Count > 0)
            {
                foreach (var ep in distinctIps)
                    result.Add(ep);

                onBatchDiscovered?.Invoke(distinctIps);
                QuicPunchLog.Info($"[STUN] Background catalog fast-path loaded {distinctIps.Count} raw IP STUN endpoints.");
            }

            // 2. Resolve hostnames in parallel with bounded concurrency and timeout
            var uniqueHosts = hostnamesToResolve
                .GroupBy(h => (h.host.ToLowerInvariant(), h.port))
                .Select(g => g.First())
                .ToList();

            const int batchSize = 25;
            var dnsBatchBuffer = new ConcurrentBag<IPEndPoint>();
            var dnsOptions = new ParallelOptions { MaxDegreeOfParallelism = 24, CancellationToken = ct };

            try
            {
                await Parallel.ForEachAsync(uniqueHosts, dnsOptions, async (item, token) =>
                {
                    try
                    {
                        using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        dnsCts.CancelAfter(TimeSpan.FromMilliseconds(1800));

                        var addresses = await Dns.GetHostAddressesAsync(item.host, dnsCts.Token).ConfigureAwait(false);
                        foreach (var addr in addresses)
                        {
                            if (addr.AddressFamily == AddressFamily.InterNetwork && !SimpleStunClient.IsBogonOrLocalhost(addr))
                            {
                                var ep = new IPEndPoint(addr, item.port);
                                result.Add(ep);
                                dnsBatchBuffer.Add(ep);
                            }
                        }

                        if (dnsBatchBuffer.Count >= batchSize && onBatchDiscovered != null)
                        {
                            var flushed = new List<IPEndPoint>();
                            while (dnsBatchBuffer.TryTake(out var ep))
                                flushed.Add(ep);
                            if (flushed.Count > 0)
                            {
                                onBatchDiscovered(flushed);
                            }
                        }
                    }
                    catch { }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }

            // Flush remaining buffer
            if (onBatchDiscovered != null && !dnsBatchBuffer.IsEmpty)
            {
                var remaining = new List<IPEndPoint>();
                while (dnsBatchBuffer.TryTake(out var ep))
                    remaining.Add(ep);
                if (remaining.Count > 0)
                {
                    onBatchDiscovered(remaining);
                }
            }

            // 3. Include built-ins to guarantee reliable seeds are always retained
            var builtIns = ResolveBuiltIns(ct);
            foreach (var b in builtIns)
                result.Add(b);

            var finalCatalog = Deduplicate(result);
            if (finalCatalog.Count > 0)
            {
                EndpointCache.Save(StunEndpointsCachePath, finalCatalog);
                QuicPunchLog.Info($"[STUN] Background catalog discovery complete: {finalCatalog.Count} distinct STUN endpoints saved to cache.");
            }

            return finalCatalog;
        }

        public static bool TryParseStunEndpoint(string line, out string host, out int port)
        {
            host = string.Empty;
            port = 3478;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            line = line.Trim();

            if (line.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
                line = line[5..].Trim();
            else if (line.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
                line = line[5..].Trim();

            if (string.IsNullOrWhiteSpace(line))
                return false;

            if (line.StartsWith('['))
            {
                int closing = line.IndexOf(']');
                if (closing > 1)
                {
                    host = line[1..closing].Trim();
                    if (closing + 1 < line.Length && line[closing + 1] == ':')
                    {
                        if (int.TryParse(line[(closing + 2)..].Trim(), out int parsedPort) && parsedPort is >= 1 and <= 65535)
                        {
                            port = parsedPort;
                            return true;
                        }
                        return false;
                    }
                    port = 3478;
                    return true;
                }
                return false;
            }

            int colon = line.LastIndexOf(':');
            if (colon <= 0 || colon == line.Length - 1)
            {
                if (colon == -1)
                {
                    host = line;
                    port = 3478;
                    return true;
                }
                return false;
            }

            host = line[..colon].Trim();
            if (int.TryParse(line[(colon + 1)..].Trim(), out int p) && p is >= 1 and <= 65535)
            {
                port = p;
                return true;
            }

            return false;
        }

        private static ConcurrentBag<IPEndPoint> ToBag(IEnumerable<IPEndPoint> endpoints) =>
            new(endpoints.Distinct().ToArray());

        private static ConcurrentBag<IPEndPoint> Deduplicate(IEnumerable<IPEndPoint> endpoints) =>
            new(endpoints.Distinct().ToArray());
    }
}
