using System.Collections.Concurrent;
using System.Net;
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

        /// <summary>
        /// Fast path used by node startup. Fresh/stale cache and built-in servers
        /// are sufficient to become operational; remote catalog downloads are only
        /// performed by an explicit/periodic force refresh.
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

        private static ConcurrentBag<IPEndPoint> ResolveBuiltIns(CancellationToken ct)
        {
            var result = new ConcurrentBag<IPEndPoint>();
            foreach (string host in BuiltInStunHosts)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var endpoints = Utilities.ResolveEndpoint(host);
                    if (endpoints == null) continue;
                    foreach (var endpoint in endpoints)
                        result.Add(endpoint);
                }
                catch { }
            }
            return Deduplicate(result);
        }

        private static async Task<ConcurrentBag<IPEndPoint>> ResolveRemoteCatalogsAsync(CancellationToken ct)
        {
            var parsed = new ConcurrentBag<string>();
            try
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
                await Parallel.ForEachAsync(RemoteStunUrls, options, async (url, token) =>
                {
                    try
                    {
                        string data = await Utilities.client.GetStringAsync(url, token).ConfigureAwait(false);
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

            var result = new ConcurrentBag<IPEndPoint>();
            foreach (string item in parsed.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var endpoints = Utilities.ResolveEndpoint(item);
                    if (endpoints == null) continue;
                    foreach (var endpoint in endpoints)
                        result.Add(endpoint);
                }
                catch { }
            }
            return Deduplicate(result);
        }

        private static ConcurrentBag<IPEndPoint> ToBag(IEnumerable<IPEndPoint> endpoints) =>
            new(endpoints.Distinct().ToArray());

        private static ConcurrentBag<IPEndPoint> Deduplicate(IEnumerable<IPEndPoint> endpoints) =>
            new(endpoints.Distinct().ToArray());
    }
}
