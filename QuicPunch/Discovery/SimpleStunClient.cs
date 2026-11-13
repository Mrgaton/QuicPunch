using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using QuicPunch.Helpers;

namespace QuicPunch
{
    public sealed class SimpleStunClient
    {
        private const uint MagicCookie = 0x2112A442;
        private const ushort BindingRequest = 0x0001;
        private const ushort BindingSuccessResponse = 0x0101;
        private const ushort MappedAddress = 0x0001;
        private const ushort XorMappedAddress = 0x0020;

        private readonly UdpClient _udp;
        private IReadOnlyList<IPEndPoint> _servers;
        private IPEndPoint[] _shuffledServers;
        private int _serverCursor = 0;
        private readonly Dictionary<TxId, PendingRequest> _pending = new();
        private readonly List<TxId> _expiredKeysBuffer = new();
        private readonly object _lock = new();

        public ConcurrentDictionary<IPEndPoint, StunEndpointHit> StunResponseEndpointHits = new();

        public void PruneExpiredHits(TimeSpan maxAge)
        {
            long cutoff = Environment.TickCount64 - (long)maxAge.TotalMilliseconds;
            foreach (var kv in StunResponseEndpointHits)
            {
                if (kv.Value.LastSeenTicks < cutoff)
                {
                    StunResponseEndpointHits.TryRemove(kv.Key, out _);
                }
            }
        }

        public Dictionary<IPEndPoint, int> GetActiveHitsSnapshot(TimeSpan? maxAge = null)
        {
            long cutoff = maxAge.HasValue ? Environment.TickCount64 - (long)maxAge.Value.TotalMilliseconds : long.MinValue;
            var result = new Dictionary<IPEndPoint, int>();
            foreach (var kv in StunResponseEndpointHits)
            {
                if (kv.Value.LastSeenTicks >= cutoff)
                {
                    result[kv.Key] = kv.Value.Count;
                }
            }
            return result;
        }

        public event EventHandler<StunResultEventArgs>? MappedAddressResolved;

        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
        public int DefaultBatchSize { get; set; } = 50;

        public SimpleStunClient(UdpClient udp, IReadOnlyList<IPEndPoint> servers)
        {
            if (udp.Client.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                throw new ArgumentException("UdpClient must support IP.", nameof(udp));

            _udp = udp;
            _servers = servers;
            _shuffledServers = servers.OrderBy(_ => Random.Shared.Next()).ToArray();
        }

        private IReadOnlyList<IPEndPoint> GetNextBatch(int batchSize)
        {
            if (_shuffledServers.Length == 0) return Array.Empty<IPEndPoint>();
            if (_shuffledServers.Length <= batchSize) return _shuffledServers;

            lock (_lock)
            {
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

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SendRequest(cancellationToken);

                try
                {
                    await Task.Delay(Interval, cancellationToken);
                }
                catch (TaskCanceledException) { break; }
            }
        }  

        public void AddServers(IEnumerable<IPEndPoint> newServers)
        {
            ArgumentNullException.ThrowIfNull(newServers);
            lock (_lock)
            {
                var existingSet = new HashSet<IPEndPoint>(_servers);
                var toAdd = newServers.Where(s => existingSet.Add(s)).ToArray();
                if (toAdd.Length == 0) return;

                _servers = _servers.Concat(toAdd).Distinct().ToArray();
                var shuffledToAdd = toAdd.OrderBy(_ => Random.Shared.Next()).ToArray();
                _shuffledServers = _shuffledServers.Concat(shuffledToAdd).ToArray();
            }
        }

        public void UpdateServers(IReadOnlyList<IPEndPoint> servers)
        {
            ArgumentNullException.ThrowIfNull(servers);
            lock (_lock)
            {
                _servers = servers;
                _shuffledServers = servers.OrderBy(_ => Random.Shared.Next()).ToArray();
                _serverCursor = 0;
            }
        }

        public async Task SendRequest(CancellationToken cancellationToken, int? batchSize = null, IReadOnlyList<IPEndPoint>? targets = null)
        {
            CleanupTimeouts();

            var toSend = targets ?? GetNextBatch(batchSize ?? DefaultBatchSize);
            await Task.WhenAll(toSend.Select(server => SendRequestSafeAsync(server, cancellationToken))).ConfigureAwait(false);
        }

        public bool TryProcessIncoming(byte[] buffer, IPEndPoint remoteEndPoint) =>
            TryProcessIncoming(buffer.AsSpan(), remoteEndPoint);

        public bool TryProcessIncoming(ReadOnlySpan<byte> buffer, IPEndPoint remoteEndPoint)
        {
            try
            {
                if (buffer.Length < 20) return false;
                if ((buffer[0] & 0xC0) != 0) return false;

                ushort msgType = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(0, 2));
                if (msgType != BindingSuccessResponse) return false;

                uint cookie = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
                if (cookie != MagicCookie) return false;

                ulong p1 = BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(8, 8));
                uint p2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(16, 4));
                var txId = new TxId(p1, p2);

                remoteEndPoint = EndpointAddressCodec.Normalize(remoteEndPoint);
                PendingRequest req;
                lock (_lock)
                {
                    if (!_pending.TryGetValue(txId, out req) || !req.Remote.Equals(remoteEndPoint))
                        return false;
                    if (Environment.TickCount64 - req.SentTicks >= Timeout.TotalMilliseconds)
                    {
                        _pending.Remove(txId);
                        return false;
                    }
                }

                var mapped = ParseMappedAddress(buffer);
                if (mapped is null || mapped.Port == 0)
                    return false;

                if (IsBogonOrLocalhost(mapped.Address))
                    return false;

                // Invalid or mismatched packets must not consume a live transaction.
                // Remove only after validation, and atomically reject duplicate replies.
                lock (_lock)
                {
                    if (!_pending.Remove(txId, out req) ||
                        Environment.TickCount64 - req.SentTicks >= Timeout.TotalMilliseconds)
                        return false;
                }

                var rtt = TimeSpan.FromMilliseconds(Environment.TickCount64 - req.SentTicks);

                long now = Environment.TickCount64;
                StunResponseEndpointHits.AddOrUpdate(
                    mapped,
                    _ => new StunEndpointHit(1, now),
                    (_, existing) =>
                    {
                        existing.Count++;
                        existing.LastSeenTicks = now;
                        return existing;
                    });

                MappedAddressResolved?.Invoke(
                    this,
                    new StunResultEventArgs(req.Remote, mapped, rtt)
                );

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task SendRequestSafeAsync(IPEndPoint remote, CancellationToken ct)
        {
            try
            {
                byte[] reqBytes = new byte[20];

                BinaryPrimitives.WriteUInt16BigEndian(reqBytes.AsSpan(0, 2), BindingRequest);
                BinaryPrimitives.WriteUInt16BigEndian(reqBytes.AsSpan(2, 2), 0);
                BinaryPrimitives.WriteUInt32BigEndian(reqBytes.AsSpan(4, 4), MagicCookie);

                RandomNumberGenerator.Fill(reqBytes.AsSpan(8, 12));

                ulong p1 = BinaryPrimitives.ReadUInt64BigEndian(reqBytes.AsSpan(8, 8));
                uint p2 = BinaryPrimitives.ReadUInt32BigEndian(reqBytes.AsSpan(16, 4));
                var txId = new TxId(p1, p2);

                lock (_lock)
                {
                    _pending[txId] = new PendingRequest(EndpointAddressCodec.Normalize(remote), Environment.TickCount64);
                }

                await _udp.SendAsync(reqBytes, remote, ct);
            }
            catch
            {

            }
        }

        private void CleanupTimeouts()
        {
            var now = Environment.TickCount64;
            var timeoutMs = Timeout.TotalMilliseconds;

            lock (_lock)
            {
                _expiredKeysBuffer.Clear();
                foreach (var kvp in _pending)
                {
                    if (now - kvp.Value.SentTicks > timeoutMs)
                        _expiredKeysBuffer.Add(kvp.Key);
                }
                foreach (var key in _expiredKeysBuffer)
                {
                    _pending.Remove(key);
                }
            }
        }

        private static IPEndPoint? ParseMappedAddress(ReadOnlySpan<byte> buffer)
        {
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
            int offset = 20;
            int end = 20 + length;
            if ((length & 3) != 0 || end != buffer.Length) return null;
            IPEndPoint? mapped = null;
            IPEndPoint? xorMapped = null;

            while (offset < end)
            {
                if (offset + 4 > end) return null;
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
                ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
                int valOffset = offset + 4;

                int nextOffset = valOffset + ((attrLen + 3) & ~3);
                if (nextOffset > end) return null;

                if (type == XorMappedAddress || type == MappedAddress)
                {
                    var val = buffer.Slice(valOffset, attrLen);
                    if ((val.Length == 8 && val[1] == 1) || (val.Length == 20 && val[1] == 2))
                    {
                        ushort port = BinaryPrimitives.ReadUInt16BigEndian(val.Slice(2, 2));
                        byte[] ipBytes = val.Slice(4).ToArray();

                        if (type == XorMappedAddress)
                        {
                            port ^= (ushort)(MagicCookie >> 16);
                            // IPv6 XOR mask is cookie + the request's 96-bit transaction ID.
                            for (int i = 0; i < ipBytes.Length; i++) ipBytes[i] ^= buffer[4 + i];
                        }
                        var endpoint = new IPEndPoint(new IPAddress(ipBytes), port);
                        if (type == XorMappedAddress) xorMapped ??= endpoint;
                        else mapped ??= endpoint;
                    }
                }
                offset = nextOffset;
            }
            return xorMapped ?? mapped;
        }

        public static bool IsBogonOrLocalhost(IPAddress address)
        {
            if (address is null) return true;
            if (IPAddress.IsLoopback(address)) return true;

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();

                // 0.0.0.0/8 (This host / network)
                if (bytes[0] == 0) return true;

                // 10.0.0.0/8 (Private RFC 1918)
                if (bytes[0] == 10) return true;

                // 100.64.0.0/10 (Carrier-Grade NAT RFC 6598)
                if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) return true;

                // 127.0.0.0/8 (Loopback RFC 1122)
                if (bytes[0] == 127) return true;

                // 169.254.0.0/16 (Link-Local / APIPA RFC 3927)
                if (bytes[0] == 169 && bytes[1] == 254) return true;

                // 172.16.0.0/12 (Private RFC 1918)
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

                // 192.0.0.0/24 (IETF Protocol Assignments)
                if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) return true;

                // 192.0.2.0/24 (TEST-NET-1)
                if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) return true;

                // 192.88.99.0/24 (6to4 Relay Anycast)
                if (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) return true;

                // 192.168.0.0/16 (Private RFC 1918)
                if (bytes[0] == 192 && bytes[1] == 168) return true;

                // 198.18.0.0/15 (Benchmarking RFC 2544)
                if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;

                // 198.51.100.0/24 (TEST-NET-2)
                if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true;

                // 203.0.113.0/24 (TEST-NET-3)
                if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true;

                // 224.0.0.0/4 (Multicast)
                if (bytes[0] >= 224 && bytes[0] <= 239) return true;

                // 240.0.0.0/4 (Reserved / Class E / Broadcast)
                if (bytes[0] >= 240) return true;
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.IsIPv6SiteLocal || address.IsIPv6LinkLocal || address.IsIPv6Multicast) return true;
                var bytes = address.GetAddressBytes();
                // fc00::/7 (Unique Local Address RFC 4193)
                if ((bytes[0] & 0xFE) == 0xFC) return true;
            }

            if (IsLocalInterfaceAddress(address)) return true;

            return false;
        }

        private static bool IsLocalInterfaceAddress(IPAddress address)
        {
            try
            {
                foreach (var localIp in Utilities.GetValidLocalIPAddresses())
                {
                    if (localIp.Equals(address)) return true;
                }
            }
            catch { }
            return false;
        }



        public static async Task<List<CandidateEndpoint>> GatherCandidatesAsync(
            UdpClient udp, ushort localPort, IReadOnlyList<IPEndPoint>? servers = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var candidates = new List<CandidateEndpoint>();
            var seen = new HashSet<IPEndPoint>();

            try
            {
                var localIps = Utilities.GetValidLocalIPAddresses();
                foreach (var ip in localIps)
                {
                    if (EndpointAddressCodec.CanAdvertise(ip) && !IPAddress.IsLoopback(ip))
                    {
                        var hostEp = new IPEndPoint(ip, localPort);
                        if (seen.Add(hostEp))
                        {
                            candidates.Add(new CandidateEndpoint(hostEp, CandidateType.Host, 2130706431));
                        }
                    }
                }
            }
            catch { }

            try
            {
                var stunServers = (servers != null && servers.Count > 0)
                    ? servers
                    : (IReadOnlyList<IPEndPoint>)StunGatherer.BuiltInStunHosts
                        .SelectMany(h => Utilities.ResolveEndpoint(h) ?? Enumerable.Empty<IPEndPoint>())
                        .Distinct()
                        .ToList();
                if (stunServers != null && stunServers.Count > 0)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(timeout ?? TimeSpan.FromMilliseconds(1000));

                    int count = Math.Min(stunServers.Count, 32);

                    var client = new SimpleStunClient(udp, stunServers);
                    int resolved = 0;
                    client.MappedAddressResolved += (_, result) =>
                    {
                        if (seen.Add(result.MappedEndPoint))
                        {
                            candidates.Add(new CandidateEndpoint(result.MappedEndPoint, CandidateType.ServerReflexive, 1694498815));
                            Interlocked.Increment(ref resolved);
                        }
                    };
                    await client.SendRequest(cts.Token, count).ConfigureAwait(false);

                    // Retransmission watchdog: if no ServerReflexive candidate arrives after 300ms, re-burst
                    var retransmit = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(300, cts.Token).ConfigureAwait(false);
                            if (Volatile.Read(ref resolved) == 0)
                            {
                                await client.SendRequest(cts.Token, count).ConfigureAwait(false);
                            }
                        }
                        catch { }
                    }, cts.Token);

                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            try
                            {
                                var res = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                                client.TryProcessIncoming(res.Buffer, res.RemoteEndPoint);
                            }
                            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                            catch (SocketException) { break; }
                        }
                    }
                    finally
                    {
                        cts.Cancel();
                        try { await retransmit.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                }
            }
            catch { }

            return candidates;
        }

        private readonly record struct TxId(ulong Part1, uint Part2);

        private readonly record struct PendingRequest(IPEndPoint Remote, long SentTicks);
    }

    public sealed class StunEndpointHit
    {
        public int Count { get; set; }
        public long LastSeenTicks { get; set; }

        public StunEndpointHit(int count, long lastSeenTicks)
        {
            Count = count;
            LastSeenTicks = lastSeenTicks;
        }
    }

    public sealed class StunResultEventArgs : EventArgs
    {
        public EndPoint ServerEndpoint { get; }
        public IPEndPoint MappedEndPoint { get; }
        public TimeSpan RoundTripTime { get; }

        public StunResultEventArgs(IPEndPoint remote, IPEndPoint mapped, TimeSpan rtt)
        {
            ServerEndpoint = remote;
            MappedEndPoint = mapped;
            RoundTripTime = rtt;
        }
    }
}
