
using QuicPunch;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace UdpPunchHoleTest
{
    public class QuicPunchCore
    {
        public static int LocalPort = 3000;//Random.Shared.Next(1, 1024);

        public static void ClearIPv4Cache()
        {
            _IPv4 = null;
            _IPv4Address = null;
        }

        public static string IPv4  {
            get 
            { 
                if (_IPv4 != null) 
                    return _IPv4; 

                return _IPv4 = IPv4Address.ToString(); 
            }
        }
        private static string _IPv4;

        public static IPAddress IPv4Address {
            get 
            {
                if (_IPv4Address != null)
                    return _IPv4Address;

                return _IPv4Address = GetPublicIP().Result; 
            }
        }

        private static IPAddress _IPv4Address;

        public const int PunchIntervalMiliseconds = 2500;

        public static byte[] MagicHeader = Encoding.UTF8.GetBytes("PuNch");

        public static Dictionary<IPEndPoint, PeerInfo> AvilablePeers = new Dictionary<IPEndPoint, PeerInfo>();

        public static HandshakeManager Manager = new HandshakeManager();

        public enum MessageType : byte
        {
            Hello = (byte)('H'),
            ACK = (byte)('K'),
            Handshake = (byte)('S'),
            FinalHandshake = (byte)('F')
        }
        public enum HandShakeType : byte
        {
            Request = (byte)('R'),
            Accept = (byte)('A'),
            Decline = (byte)('D')
        }
    
        public static event Action<PeerInfo>? OnPeerAvilable;
        public static async Task<(QuicConnection, Stream)> InitPeerConection(UdpClient udp, Guid connectionType, PeerInfo peer, ushort localPort, CancellationTokenSource mainCts)
        {
            byte[] payload;

            var guid = Guid.NewGuid();

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(MagicHeader);
                w.Write((byte)MessageType.Handshake);
                w.Write((byte)HandShakeType.Request);
                w.Write(localPort);
                w.Write(connectionType.ToByteArray());
                w.Write(guid.ToByteArray());
                payload = ms.ToArray();
            }

            await udp.SendAsync(payload, payload.Length, peer.EndPoint);

            var decision = await Manager.WaitForDecisionAsync(new HandshakeRequest(guid, connectionType, peer.EndPoint), TimeSpan.FromSeconds(30), false, mainCts.Token);

            if (!decision.Accepted)
                throw new Exception("Handshake declined by peer.");

            Console.WriteLine("Peer acepted :D");

            return await QuicConectionCore.InitConnectionCore(localPort, peer.EndPoint, mainCts);
        }


        public static async Task ListenLoop(UdpClient udp, CancellationTokenSource mainCts)
        {
           await ReceiveLoopAsync(udp, mainCts.Token);

            Console.WriteLine("Shutting down listener...");
        }
        public static async Task PeerInterogation(IPEndPoint endpoint, UdpClient udp, CancellationTokenSource mainCts)
        {
            var punchSuccessful = new TaskCompletionSource<bool>();
            using var udpCts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token);

            Console.WriteLine($"Starting interogation for {endpoint}...");

            var sendTask = SendLoopAsync(udp, endpoint, udpCts.Token);

            await punchSuccessful.Task.WaitAsync(mainCts.Token);
            
            udpCts.Cancel();
            udp.Dispose();
        }
        private static async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
        {
            var ACKPacket = Helpers.Combine(MagicHeader, [ (byte)MessageType.ACK ]);

            while (!token.IsCancellationRequested)
            {
                try
                {

                skipPacket:

                    var result = await udp.ReceiveAsync(token);

                    //Console.WriteLine("Recived: " + Encoding.UTF8.GetString(result.Buffer));

                    //if (!result.RemoteEndPoint.Address.Equals(targetPeer.Address))
                    //    continue;

                    if (result.Buffer.Length > 512)
                        goto skipPacket;

                    for (int i = 0; i < MagicHeader.Length; i++)
                    {
                        if (result.Buffer[i] != MagicHeader[i])
                            goto skipPacket;
                    }

                    using (MemoryStream ms = new MemoryStream(result.Buffer))
                    using (BinaryReader r = new BinaryReader(ms))
                    {
                        _ = r.ReadBytes(MagicHeader.Length);

                        byte messageType = r.ReadByte();

                        switch(messageType)
                        {
                            case (byte)MessageType.Hello:
                                var certHash = r.ReadBytes(Helpers.CurrentPeer.CertHash.Length);
                                var curvePuiblicKey = r.ReadBytes(Helpers.CurrentPeer.CurvePublicKey.Length);
                                var name = r.ReadString();

                                var peerInfo = new PeerInfo
                                {
                                    EndPoint = result.RemoteEndPoint,
                                    CertHash = certHash,
                                    CurvePublicKey = curvePuiblicKey,
                                    Name = name,
                                    LastSeen = PreciseTime.GetCorrectTime()
                                };

                                if (!AvilablePeers.ContainsKey(peerInfo.EndPoint))
                                {
                                    AvilablePeers[peerInfo.EndPoint] = peerInfo;
                                    OnPeerAvilable?.Invoke(peerInfo);
                                }

                                await udp.SendAsync(ACKPacket, ACKPacket.Length, result.RemoteEndPoint);
                                continue;

                            case (byte)MessageType.ACK:
                                //Console.WriteLine($"Received ACK from {result.RemoteEndPoint}");
                                continue;

                            case (byte)MessageType.Handshake:
                                var handShakeType = (HandShakeType)r.ReadByte();
                                var port = r.ReadUInt16();

                                var connectionType = new Guid(r.ReadBytes(16));
                                var guid = new Guid(r.ReadBytes(16));

                                switch (handShakeType)
                                {
                                    case HandShakeType.Request:
                                        Console.WriteLine($"Received handshake request from {result.RemoteEndPoint}");

                                        Task.Factory.StartNew(async () =>
                                        {
                                            var decision = await Manager.WaitForDecisionAsync(new HandshakeRequest(guid, connectionType, result.RemoteEndPoint), TimeSpan.FromSeconds(30), true, CancellationToken.None);

                                            byte[] payload;
                                            ushort connectionPort = (ushort)Random.Shared.Next(1024, 65536);


                                            using (MemoryStream ms = new MemoryStream())
                                            using (BinaryWriter w = new BinaryWriter(ms))
                                            {
                                                w.Write(MagicHeader);
                                                w.Write((byte)MessageType.Handshake);
                                                w.Write((byte)(decision.Accepted ? HandShakeType.Accept : HandShakeType.Decline));
                                                w.Write(connectionPort);
                                                w.Write(connectionType.ToByteArray());
                                                w.Write(guid.ToByteArray());
                                                payload = ms.ToArray();
                                            }

                                            await udp.SendAsync(payload, payload.Length, result.RemoteEndPoint);


                                            if (decision.Accepted)
                                            {
                                                //TODO: DO FINAL CONECTION
                                            }
                                        });
                                        break;

                                    case HandShakeType.Accept:
                                        Console.WriteLine($"Received handshake ACCEPT from {result.RemoteEndPoint}");
                                        Manager.Approve(guid, port);
                                        break;

                                    case HandShakeType Decline:
                                        Console.WriteLine($"Received handshake DECLINE from {result.RemoteEndPoint}");
                                        Manager.Reject(guid);
                                        break;
                                }
                                continue;

                            default:
                                Console.WriteLine($"Received unknown message type {(char)messageType} from {result.RemoteEndPoint}");
                                continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in ReceiveLoopAsync: {ex.Message}");
                    Thread.Sleep(3000);
                }
            }
        }

        private static async Task SendLoopAsync(UdpClient udp, IPEndPoint peer, CancellationToken token)
        {
            byte[] payload;

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(MagicHeader);
                w.Write((byte)MessageType.Hello);
                w.Write(Helpers.CurrentPeer.CertHash);
                w.Write(Helpers.CurrentPeer.CurvePublicKey);
                w.Write(Helpers.CurrentPeer.Name);

                payload = ms.ToArray();
            }

            double maxIntervalTicks = TimeSpan.FromSeconds(20).Ticks;
            long intervalTicks = TimeSpan.FromMilliseconds(PunchIntervalMiliseconds).Ticks;
            int tries = 0;

            while (true)
            {
                bool peerResponded = AvilablePeers.ContainsKey(peer);

                if (tries > 0)
                {
                    bool isPowerOfTwo = (tries & (tries - 1)) == 0;

                    if (isPowerOfTwo || peerResponded)
                    {
                        intervalTicks = (long)Math.Min(intervalTicks * 2, maxIntervalTicks);
                    }
                }

                tries++;

                DateTime now = PreciseTime.GetCorrectTime();
                long nextTicks = now.Ticks - (now.Ticks % intervalTicks) + intervalTicks;
                DateTime nextBoundary = new DateTime(nextTicks, DateTimeKind.Utc);
                TimeSpan delay = nextBoundary - PreciseTime.GetCorrectTime();

                if (delay.TotalMilliseconds > 15)
                {
                    await Task.Delay((int)delay.TotalMilliseconds, token);

                    //while (PreciseTime.GetCorrectTime() < nextBoundary)
                    //{
                    //    Thread.SpinWait(10);
                    //}
                }

                //Console.WriteLine($"Send hello packet to {peer} at {PreciseTime.GetCorrectTime():HH:mm:ss.fff} time til next {TimeSpan.FromTicks(intervalTicks).Seconds}");

                for (int i = 0; i < (peerResponded ? 1 : 2); i++)
                {
                    if (token.IsCancellationRequested)
                        break;

                    await udp.SendAsync(payload, payload.Length, peer);
                    await Task.Delay(250, token);
                }

                tries ++;
            }
        }

     

        public static async Task<IPAddress?> GetPublicIP()
        {
            (string Host, int Port)[] servers =
            [
                ("stun.l.google.com",   19302),
                ("stun1.l.google.com",  19302),
                ("stun.cloudflare.com", 3478),
            ];

            foreach (var (host, port) in servers)
            {
                try
                {
                    using var udp = new UdpClient();

                    byte[] request = new byte[20];
                    BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), 0x0001);
                    Random.Shared.NextBytes(request.AsSpan(4, 16));

                    await udp.SendAsync(request.AsMemory(), host, port);
                    var result = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    var buffer = result.Buffer;

                    for (int i = 20; i < buffer.Length - 8; i++)
                    {
                        if (buffer[i] == 0x00 && buffer[i + 1] == 0x01)

                        {
                            var address = new IPAddress(buffer.AsSpan(i + 8, 4)); ;

                            return address; 
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        public static async Task<string> GetToken()
        {
            if (!string.IsNullOrEmpty(IPv4))
                return EncodeEndpointToken(IPAddress.Parse(IPv4), LocalPort);

            await GetPublicIP();

            if (string.IsNullOrEmpty(IPv4))
                throw new Exception("Failed to obtain public IP address.");

            return await GetToken();
        }
        public static string EncodeEndpointToken(IPAddress ip, int port) => EncodeEndpointToken(new IPEndPoint(ip, port));
        public static string EncodeEndpointToken(IPEndPoint ep)
        {
            byte[] r = new byte[6];
            Buffer.BlockCopy(ep.Address.GetAddressBytes(), 0, r, 0, 4);
            r[4] = (byte)(ep.Port >> 8);
            r[5] = (byte)ep.Port; return Convert.ToBase64String(r);
        }
        public static IPEndPoint DecodeEndpointToken(string t) { byte[] r = Convert.FromBase64String(t); return new IPEndPoint(new IPAddress(r.Take(4).ToArray()), (r[4] << 8) | r[5]); }

    }
}
