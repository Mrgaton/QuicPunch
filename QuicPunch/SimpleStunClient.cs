using System.Buffers.Binary;
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
        private readonly List<TxId> _expiredKeysBuffer = new(); // Evita allocations al limpiar
        private readonly object _lock = new();

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

                // Lanza las peticiones en paralelo. Si un DNS falla o bloquea, no detiene a los demás.
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
                // 1. Filtrado ultra-rápido. Si no parece STUN, descartar inmediatamente.
                if (buffer.Length < 20) return false;
                if ((buffer[0] & 0xC0) != 0) return false; // STUN siempre empieza con bits 00

                ushort msgType = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));
                if (msgType != BindingSuccessResponse) return false;

                uint cookie = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4));
                if (cookie != MagicCookie) return false;

                // 2. Extraer el Transaction ID sin asignar memoria (usando el struct TxId)
                ulong p1 = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(8, 8));
                uint p2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(16, 4));
                var txId = new TxId(p1, p2);

                PendingRequest req;
                lock (_lock)
                {
                    if (!_pending.Remove(txId, out req)) return false; // No es nuestra o expiró
                }

                var mapped = ParseMappedAddress(buffer);
                if (mapped is null)
                    return false;

                // Ensure IPv4 before inspecting bytes
                if (mapped.AddressFamily != AddressFamily.InterNetwork)
                    return false;

                var bytes = mapped.Address.GetAddressBytes();

                // Filter RFC1918 private ranges (10.0.0.0/8, 172.16/12, 192.168/16)
                bool isPrivate =
                    bytes[0] == 10 ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168);

                if (isPrivate)
                    return false;

                var rtt = TimeSpan.FromMilliseconds(Environment.TickCount64 - req.SentTicks);

                MappedAddressResolved?.Invoke(
                    this,
                    new StunResultEventArgs(req.Remote, mapped, rtt)
                );

                return true;

                return false;
            }
            catch
            {
                // Si el paquete está corrupto o mal formado, no crasheamos el bucle del usuario
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

                // Generar ID de transacción directamente en el buffer
                RandomNumberGenerator.Fill(reqBytes.AsSpan(8, 12));

                // Leer el ID generado para guardarlo en el diccionario sin strings
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
                // Ignoramos fallos individuales (DNS caído, red no disponible) 
                // para que los demás servidores sigan operando sin problema.
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
                offset = valOffset + ((attrLen + 3) & ~3); // Avanzar saltando el Padding
            }
            return null;
        }

        // Struct de 12 bytes que evita tener que generar strings de Base64
        private readonly record struct TxId(ulong Part1, uint Part2);

        // Usamos Ticks en vez de DateTimeOffset para ser inmunes a cambios de hora del Windows
        private readonly record struct PendingRequest(IPEndPoint Remote, long SentTicks);
    }
}