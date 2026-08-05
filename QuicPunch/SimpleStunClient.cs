using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace QuicPunch
{
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

    public sealed class SimpleStunClient
    {
        private const uint MagicCookie = 0x2112A442;
        private const ushort BindingRequest = 0x0001;
        private const ushort BindingSuccessResponse = 0x0101;
        private const ushort MappedAddress = 0x0001;
        private const ushort XorMappedAddress = 0x0020;

        private readonly UdpClient _udp;
        private readonly IReadOnlyList<IPEndPoint> _servers;
        private readonly Dictionary<TxId, PendingRequest> _pending = new();
        private readonly List<TxId> _expiredKeysBuffer = new();
        private readonly object _lock = new();

        public ConcurrentDictionary<IPEndPoint, int> StunResponseEndpointHits = new ConcurrentDictionary<IPEndPoint, int>();

        public event EventHandler<StunResultEventArgs>? MappedAddressResolved;

        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

        public SimpleStunClient(UdpClient udp, IReadOnlyList<IPEndPoint> servers)
        {
            if (udp.Client.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("UdpClient must be IPv4 (InterNetwork) for this STUN implementation.", nameof(udp));

            _udp = udp;
            _servers = servers;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                CleanupTimeouts();

                var sendTasks = _servers.Select(server => SendRequestSafeAsync(server, cancellationToken));
                
                await Task.WhenAll(sendTasks);

                try
                {
                    await Task.Delay(Interval, cancellationToken);
                }
                catch (TaskCanceledException) { break; }
            }
        }  
        
        public async Task SendRequest(CancellationToken cancellationToken)
        {
            CleanupTimeouts();

            await Task.WhenAll(_servers.Select(server => SendRequestSafeAsync(server, cancellationToken)));
        }

        public bool TryProcessIncoming(byte[] buffer, IPEndPoint remoteEndPoint)
        {
            try
            {
                if (buffer.Length < 20) return false;
                if ((buffer[0] & 0xC0) != 0) return false;

                ushort msgType = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));
                if (msgType != BindingSuccessResponse) return false;

                uint cookie = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4));
                if (cookie != MagicCookie) return false;

                ulong p1 = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(8, 8));
                uint p2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(16, 4));
                var txId = new TxId(p1, p2);

                PendingRequest req;
                lock (_lock)
                {
                    if (!_pending.Remove(txId, out req))
                        return false;
                }

                var mapped = ParseMappedAddress(buffer);
                if (mapped is null)
                    return false;

                if (IsBogonOrLocalhost(mapped.Address))
                    return false;

                var rtt = TimeSpan.FromMilliseconds(Environment.TickCount64 - req.SentTicks);

                StunResponseEndpointHits.AddOrUpdate(mapped, 1, (_, count) => count + 1);

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
                    _pending[txId] = new PendingRequest(remote, Environment.TickCount64);
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

            while (offset + 4 <= end && offset + 4 <= buffer.Length)
            {
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
                ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
                int valOffset = offset + 4;

                if (valOffset + attrLen > buffer.Length) break;

                if (type == XorMappedAddress || type == MappedAddress)
                {
                    var val = buffer.Slice(valOffset, attrLen);
                    if (val.Length >= 8 && val[1] == 0x01) // 0x01 = IPv4
                    {
                        ushort port = BinaryPrimitives.ReadUInt16BigEndian(val.Slice(2, 2));
                        byte[] ipBytes = val.Slice(4, 4).ToArray();

                        if (type == XorMappedAddress)
                        {
                            port ^= (ushort)(MagicCookie >> 16);
                            Span<byte> cookieBytes = stackalloc byte[4];
                            BinaryPrimitives.WriteUInt32BigEndian(cookieBytes, MagicCookie);
                            for (int i = 0; i < 4; i++) ipBytes[i] ^= cookieBytes[i];
                        }
                        return new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                }
                offset = valOffset + ((attrLen + 3) & ~3);
            }
            return null;
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
                foreach (var localIp in Helpers.GetValidLocalIPAddresses())
                {
                    if (localIp.Equals(address)) return true;
                }
            }
            catch { }
            return false;
        }

        private readonly record struct TxId(ulong Part1, uint Part2);

        private readonly record struct PendingRequest(IPEndPoint Remote, long SentTicks);
    }
}