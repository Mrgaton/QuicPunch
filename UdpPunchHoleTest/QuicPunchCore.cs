
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
        public UdpClient? udp = null;
        public const int LocalPort = 3000;//Random.Shared.Next(1, 1024);

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

                return _IPv4Address = Helpers.GetPublicIP().Result; 
            }
        }
        private static IPAddress _IPv4Address;

        public QuicPunchCore (CancellationTokenSource cts, byte[] poolId)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            {
                throw new NotSupportedException("QUIC is not supported on this machine.");
            }

            udp = new UdpClient();

            if (OperatingSystem.IsWindows())
                udp.Client.IOControl(-1744830452, [0], null);

            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, LocalPort));

            CancelationSource = cts;

            PoolId = poolId.Length == 20 ? poolId : SHA1.HashData(poolId);

            TrackerScanner = new TrackerScanner(PoolId, LocalPort);
            TrackerScanner.Start();
        }

        private byte[] _poolId = [];
        public byte[] PoolId
        {
            get => _poolId;
            set 
            {
                if (value.Length != 20 ) throw new ArgumentException("InfoHash must be 20 bytes long.");

                _poolId = value;

                if (TrackerScanner != null)
                {
                    TrackerScanner.Stop();
                    TrackerScanner = new TrackerScanner(value, LocalPort);
                    TrackerScanner.Start();
                }
            } 
        }


        public TrackerScanner TrackerScanner { get; private set; }
        public CancellationTokenSource CancelationSource { get; private set; }

        public void StartInterogation()
        {
            InterogationsListenerTask = ListenLoop(udp, CancelationSource);
        }
        public Task InterogationsListenerTask { get; private set; }



        public const int PunchIntervalMiliseconds = 2500;

        public static byte[] MagicHeader = Encoding.UTF8.GetBytes("PuNch");

        public Dictionary<IPEndPoint, PeerInfo> AvilablePeers = new Dictionary<IPEndPoint, PeerInfo>();

        public HandshakeManager Manager = new HandshakeManager();

        public readonly ConcurrentDictionary<Guid, IProtocolHandler> ProtocolHandlers = new();
        public interface IProtocolHandler
        {
            public Guid ProtocolId { get; }
            Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct);
        }
        public bool RemoveProtocol(IProtocolHandler handler) => ProtocolHandlers.TryRemove(handler.ProtocolId, out _);
        public void RegisterProtocol(IProtocolHandler handler) => ProtocolHandlers[handler.ProtocolId] = handler;
        
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
            Decline = (byte)('D'),
            Unsuported = (byte)('U') //Peer doesnt support the requested protocol
        }
    
        public event Action<PeerInfo>? OnPeerAvilable;
        public async Task InitPeerConection(Guid protocolHandler, PeerInfo peer, ushort localPort, CancellationTokenSource mainCts)
        {
            if (!ProtocolHandlers.TryGetValue(protocolHandler, out var handler))
            {
                throw  new KeyNotFoundException("Handler not found for protocol: " + nameof(protocolHandler));
            }

            byte[] payload;

            var conectionGuid = Guid.NewGuid();

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(MagicHeader);
                w.Write((byte)MessageType.Handshake);
                w.Write((byte)HandShakeType.Request);
                w.Write(localPort);
                w.Write(protocolHandler.ToByteArray());
                w.Write(conectionGuid.ToByteArray());
                payload = ms.ToArray();
            }

            await udp.SendAsync(payload, payload.Length, peer.EndPoint);

            var decision = await Manager.WaitForDecisionAsync(new HandshakeRequest(conectionGuid, protocolHandler, peer.EndPoint), TimeSpan.FromSeconds(30), false, mainCts.Token);

            if (!decision.Accepted)
                throw new Exception("Handshake declined by peer.");

            Console.WriteLine("Peer acepted :D");

            var conection = await QuicConectionCore.InitConnectionCore(localPort, new IPEndPoint(peer.EndPoint.Address, (ushort)decision.Port), IPv4Address, mainCts.Token);

            await handler.HandleAsync(conection.Item1, conection.Item2, peer, mainCts.Token);
        }


        public async Task ListenLoop(UdpClient udp, CancellationTokenSource mainCts)
        {
           await ReceiveLoopAsync(udp, mainCts.Token);

            Console.WriteLine("Shutting down listener...");
        }
        public async Task PeerInterogation(IPEndPoint endpoint, CancellationTokenSource mainCts)
        {
            var punchSuccessful = new TaskCompletionSource<bool>();
            using var udpCts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token);

            Console.WriteLine($"Starting interogation for {endpoint}...");

            var sendTask = SendLoopAsync(udp, endpoint, udpCts.Token);

            await punchSuccessful.Task.WaitAsync(mainCts.Token);
            
            udpCts.Cancel();
            udp.Dispose();
        }
        private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
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
                                var remotePort = r.ReadUInt16();

                                var connectionType = new Guid(r.ReadBytes(16));
                                var guid = new Guid(r.ReadBytes(16));

                                switch (handShakeType)
                                {
                                    case HandShakeType.Request:
                                        Console.WriteLine($"Received handshake request from {result.RemoteEndPoint}");

                                        Task.Factory.StartNew(async () =>
                                        {
                                            HandShakeType decidedResponse = HandShakeType.Unsuported;
                                            ushort decidedPort = 0;
                                            CancellationToken ct = CancellationToken.None;

                                            if (ProtocolHandlers.TryGetValue(connectionType, out var handler))
                                            {
                                                var decision = await Manager.WaitForDecisionAsync(new HandshakeRequest(guid, connectionType, result.RemoteEndPoint), TimeSpan.FromSeconds(30), true, CancellationToken.None);

                                                if (decision.Port == null || decision.Port == 0)
                                                    throw new Exception("Invalid port in handshake decision.");

                                                decidedResponse = decision.Accepted ? HandShakeType.Accept : HandShakeType.Decline;
                                                decidedPort = (ushort)decision.Port;
                                                ct = decision.Ct ?? CancellationToken.None;
                                            }

                                            byte[] payload;

                                            using (MemoryStream ms = new MemoryStream())
                                            using (BinaryWriter w = new BinaryWriter(ms))
                                            {
                                                w.Write(MagicHeader);
                                                w.Write((byte)MessageType.Handshake);
                                                w.Write((byte)(decidedResponse));
                                                w.Write(decidedPort);
                                                w.Write(connectionType.ToByteArray());
                                                w.Write(guid.ToByteArray());
                                                payload = ms.ToArray();
                                            }

                                            await udp.SendAsync(payload, payload.Length, result.RemoteEndPoint);


                                            if (decidedResponse == HandShakeType.Accept)
                                            {
                                                var connection = await QuicConectionCore.InitConnectionCore(decidedPort, new IPEndPoint(result.RemoteEndPoint.Address, remotePort),IPv4Address, ct);

                                                handler.HandleAsync(connection.Item1, connection.Item2, AvilablePeers[result.RemoteEndPoint], ct);
                                            }
                                        });
                                        break;

                                    case HandShakeType.Accept:
                                        Console.WriteLine($"Received handshake ACCEPT from {result.RemoteEndPoint}");
                                        Manager.Approve(guid, remotePort, null);
                                        break;

                                    case HandShakeType.Decline or HandShakeType.Unsuported:
                                        Console.WriteLine($"Handshake canceled from {result.RemoteEndPoint}");
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

        private async Task SendLoopAsync(UdpClient udp, IPEndPoint peer, CancellationToken token)
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

        public async Task<string> GetToken()
        {
            if (!string.IsNullOrEmpty(IPv4))
                return Helpers.EncodeEndpointToken(IPAddress.Parse(IPv4), LocalPort);

            await Helpers.GetPublicIP();

            if (string.IsNullOrEmpty(IPv4))
                throw new Exception("Failed to obtain public IP address.");

            return await GetToken();
        }
      
    }
}
