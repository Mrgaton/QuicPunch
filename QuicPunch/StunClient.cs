using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace QuicPunch
{
    internal static class StunClient
    {
        private const uint MagicCookie = 0x2112A442;

        private static readonly (string Host, int Port)[] DefaultServers =
        [
            ("stun.l.google.com", 19302),
            ("stun1.l.google.com", 19302),
            ("stun.cloudflare.com", 3478),
        ];

        public static async Task<IPEndPoint?> GetMappedEndpointAsync(
            UdpClient udp,
            TimeSpan? perServerTimeout = null,
            CancellationToken cancellationToken = default)
        {
            var timeout = perServerTimeout ?? TimeSpan.FromSeconds(2);

            foreach (var (host, port) in DefaultServers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var endpoint = await QueryServerAsync(
                        udp,
                        host,
                        port,
                        timeout,
                        cancellationToken);

                    if (endpoint != null)
                        return endpoint;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Try next STUN server.
                }
            }

            return null;
        }

        private static async Task<IPEndPoint?> QueryServerAsync(
            UdpClient udp,
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            byte[] request = new byte[20];

            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), 0x0001); // STUN Binding
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 0); // Message 
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), MagicCookie); // Magic cookie
            RandomNumberGenerator.Fill(request.AsSpan(8, 12)); // Transaction

            await udp.SendAsync(request, request.Length, host, port);

            var started = Stopwatch.GetTimestamp();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var elapsed = Stopwatch.GetElapsedTime(started);
                var remaining = timeout - elapsed;

                if (remaining <= TimeSpan.Zero)
                    return null;

                UdpReceiveResult result;

                try
                {
                    result = await udp.ReceiveAsync().WaitAsync(remaining, cancellationToken);
                }
                catch (TimeoutException)
                {
                    return null;
                }

                var mapped = TryParseBindingResponse(
                    result.Buffer,
                    request.AsSpan(8, 12));

                if (mapped != null)
                    return mapped;

            }
        }

        private static IPEndPoint? TryParseBindingResponse(
            byte[] buffer,
            ReadOnlySpan<byte> expectedTransactionId)
        {
            if (buffer.Length < 20)
                return null;

            ushort messageType = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));
            ushort messageLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));
            uint magicCookie = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4));

            // Binding Success Response
            if (messageType != 0x0101)
                return null;

            if (magicCookie != MagicCookie)
                return null;

            if (!buffer.AsSpan(8, 12).SequenceEqual(expectedTransactionId))
                return null;

            if (buffer.Length < 20 + messageLength)
                return null;

            int offset = 20;
            int end = 20 + messageLength;

            IPEndPoint? mappedAddress = null;

            while (offset + 4 <= end)
            {
                ushort attributeType = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
                ushort attributeLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 2, 2));

                int valueOffset = offset + 4;
                int valueEnd = valueOffset + attributeLength;

                if (valueEnd > end)
                    return null;

                var value = buffer.AsSpan(valueOffset, attributeLength);

                switch (attributeType)
                {
                    case 0x0020: // XOR-MAPPED-ADDRESS
                    {
                        var endpoint = ParseAddressAttribute(value, xor: true, expectedTransactionId);
                        if (endpoint != null)
                            return endpoint;

                        break;
                    }

                    case 0x0001: // MAPPED-ADDRESS, legacy
                    {
                        mappedAddress = ParseAddressAttribute(value, xor: false, expectedTransactionId);
                        break;
                    }
                }

                int paddedLength = (attributeLength + 3) & ~3;
                offset = valueOffset + paddedLength;
            }

            return mappedAddress;
        }

        private static IPEndPoint? ParseAddressAttribute(
            ReadOnlySpan<byte> value,
            bool xor,
            ReadOnlySpan<byte> transactionId)
        {
            if (value.Length < 8)
                return null;

            byte family = value[1];

            ushort port = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2));

            if (xor)
                port ^= (ushort)(MagicCookie >> 16);

            if (family == 0x01) // IPv4
            {
                if (value.Length < 8)
                    return null;

                byte[] addressBytes = value.Slice(4, 4).ToArray();

                if (xor)
                {
                    Span<byte> cookieBytes = stackalloc byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(cookieBytes, MagicCookie);

                    for (int i = 0; i < 4; i++)
                        addressBytes[i] ^= cookieBytes[i];
                }

                return new IPEndPoint(new IPAddress(addressBytes), port);
            }

            if (family == 0x02) // IPv6
            {
                if (value.Length < 20)
                    return null;

                byte[] addressBytes = value.Slice(4, 16).ToArray();

                if (xor)
                {
                    Span<byte> xorMask = stackalloc byte[16];
                    BinaryPrimitives.WriteUInt32BigEndian(xorMask.Slice(0, 4), MagicCookie);
                    transactionId.CopyTo(xorMask.Slice(4, 12));

                    for (int i = 0; i < 16; i++)
                        addressBytes[i] ^= xorMask[i];
                }

                return new IPEndPoint(new IPAddress(addressBytes), port);
            }

            return null;
        }
    }
}