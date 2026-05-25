using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Quic;

namespace QuicPunch
{
    internal class FriendsLanHandler : QuicPunchCore.IProtocolHandler
    {
        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000002");
        public ushort PreferredPort => 0; 
        public string ProtocolName => "FriendsLAN";

        public ZstandardCompressionOptions? CompressionOptions => null; //new ZstandardCompressionOptions() { AppendChecksum = false, EnableLongDistanceMatching = false, Quality = 6};

        private readonly IPAddress _localIp;

        public ConcurrentDictionary<uint, Stream> ActivePeers { get; } = new();

        public FriendsLanHandler(IPAddress localIp)
        {
            _localIp = localIp ?? throw new ArgumentNullException(nameof(localIp));
        }

        public  async Task HandleAsync(
            QuicConnection connection,
            Stream stream,
            PeerInfo peer,
            CancellationToken ct)
        {
            if (connection == null) 
                throw new ArgumentNullException(nameof(connection));

            Console.WriteLine($"\n[FriendsLAN] Secure tunnel established with peer: {peer.Name} ({peer.EndPoint})");

            uint remoteIp;

            byte[] localIpBytes = _localIp.GetAddressBytes();
            await stream.WriteAsync(localIpBytes, ct);
            await stream.FlushAsync(ct);

            byte[] remoteIpBytes = new byte[4];
            await ReadExactlyAsync(stream, remoteIpBytes, 4, ct);
            remoteIp = BinaryPrimitives.ReadUInt32BigEndian(remoteIpBytes);
            Console.WriteLine($"[FriendsLAN] Peer virtual IP: {remoteIp}");

            try
            {
                ActivePeers[remoteIp] = stream;

            
                byte[] lenBuffer = new byte[4];

                while (true) //(!ct.IsCancellationRequested)
                {
                    try
                    {
                        await ReadExactlyAsync(stream, lenBuffer, 4, ct);
                        int len = BitConverter.ToInt32(lenBuffer, 0);

                        if (len == 0)
                            continue;

                        byte[] packet = new byte[len];

                        await ReadExactlyAsync(stream, packet, len, ct);

                        unsafe
                        {
                            fixed (byte* p = packet)
                            {
                                Program.LogPacket(p, (uint)len);
                            }
                        }

                        if (Program._session != null)
                        {
                            Program._session.SendPacket(packet);
                        }
                    }
                    catch (QuicException quicex)
                    {
                        Console.WriteLine($"[FriendsLAN] QUIC error with peer {(remoteIp.ToString() ?? peer.Name)}: {quicex.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FriendsLAN] Tunnel error with peer {(peer.EndPoint.ToString() ?? peer.Name)}: {ex.Message}");
            }
            finally
            {
                if (remoteIp != null)
                {
                    ActivePeers.TryRemove(remoteIp, out _);
                }
                Console.WriteLine($"[FriendsLAN] Secure tunnel closed with peer: {peer.Name}");
            }
        }
        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int totalRead = 0;

            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
                if (read == 0)
                {
                    throw new EndOfStreamException("Connection closed by remote peer while reading packet data.");
                }
                totalRead += read;
            }
        }
    }
}
