
using QuicPunch;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static QuicPunch.QuicPunchCore;

namespace QuicPunch
{
    public class QuicPunchCore : IDisposable
    {
        public UdpClient? udp = null;

        public int DiscoveryPort { get; private set; } //Random.Shared.Next(1, 1024);

        public static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"QuicPunchV0");

        public PeerInfo CurrentPeer {  get; private set; }
        public void ClearIPEndpointCache()
        {
            _IPEndpoint = null;
            _IPEndpointStr = null;
        }

        public string IPEndpointStr
        {
            get
            {
                if (_IPEndpointStr != null)
                    return _IPEndpointStr;

                return _IPEndpointStr = IPEndpoint.ToString();
            }
        }
        private string _IPEndpointStr;

        public IPEndPoint IPEndpoint
        {
            get
            {
                if (_IPEndpoint != null)
                    return _IPEndpoint;

                if (udp == null)
                    throw new InvalidOperationException("UDP client is not initialized.");

                return _IPEndpoint = Helpers.GetPublicEndPoint(udp).Result;
            }
        }
        private IPEndPoint _IPEndpoint;

        private CertManager CertManager { get; } = new CertManager(AppDataPath);

        private int CurvePublicKeySize { get; set; }

        private byte[] HelloPayload;
        private byte[] InterogationPayload;
        public QuicPunchCore(CancellationTokenSource cts, byte[] poolId, ushort discoveryPort = 443)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            {
                throw new NotSupportedException("QUIC is not supported on this machine.");
            }

            DiscoveryPort = discoveryPort;

            udp = new UdpClient();

            if (OperatingSystem.IsWindows())
                udp.Client.IOControl(-1744830452, [0], null);

            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            udp.Client.DontFragment = true;

            if (DiscoveryPort == 0)
            {
                DiscoveryPort = this.IPEndpoint.Port;
            }

            CancelationSource = cts;

            PoolId = poolId.Length == 20 ? poolId : SHA1.HashData(poolId);

            TrackerScanner = new TrackerScanner(PoolId, DiscoveryPort);
            TrackerScanner.Start();

            CurrentPeer = new PeerInfo()
            {
                Name = Environment.UserName,
                EndPoint = new IPEndPoint(Helpers.GetPublicIP().Result!, DiscoveryPort),
                CertHash = CertManager.CertPublicHash,
            };

            CurvePublicKeySize = CertManager.PeerCertificate!.GetPublicKey().Length;

            HelloPayload = GenerateHelloPayload(MessageType.Hello);
            InterogationPayload = GenerateHelloPayload(MessageType.Interogation);

            StartInterogationListener();
        }

        private byte[] _poolId = [];
        public byte[] PoolId
        {
            get => _poolId;
            set
            {
                if (value.Length != 20) throw new ArgumentException("InfoHash must be 20 bytes long.");

                _poolId = value;

                if (TrackerScanner != null)
                {
                    TrackerScanner.Stop();
                    TrackerScanner = new TrackerScanner(value, DiscoveryPort);
                    TrackerScanner.Start();
                }
            }
        }


        public TrackerScanner TrackerScanner { get; private set; }
        public CancellationTokenSource CancelationSource { get; private set; }

        public void StartInterogationListener()
        {
            CancelationSource = new CancellationTokenSource();
            InterogationsListenerTask = ReceiveLoopAsync(udp, CancelationSource.Token);
        }
        public void StopInterogationListener()
        {
            CancelationSource.Cancel();
        }
        public Task InterogationsListenerTask { get; private set; }



        public const int PunchIntervalMiliseconds = 2500 / 2;

        public static byte[] MagicHeader = Encoding.UTF8.GetBytes("PuNch");

        public ConcurrentDictionary<IPEndPoint, PeerInfo> AvilablePeers = new ConcurrentDictionary<IPEndPoint, PeerInfo>();

        public HandshakeManager Manager = new HandshakeManager();

        private readonly IpRateLimiter _rateLimiter = new IpRateLimiter(5);

        public readonly ConcurrentDictionary<Guid, IProtocolHandler> ProtocolHandlers = new();

        public interface IProtocolHandler
        {
            public Guid ProtocolId { get; }
            public ushort PreferredPort { get; }
            public string ProtocolName { get; }
            public bool Commpression { get; }
            Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct);
        }
        public bool RemoveProtocol(IProtocolHandler handler) => ProtocolHandlers.TryRemove(handler.ProtocolId, out _);
        public void RegisterProtocol(IProtocolHandler handler) => ProtocolHandlers[handler.ProtocolId] = handler;

        public enum MessageType : byte
        {
            Hello = (byte)('H'),
            Ping = (byte)('P'),
            Interogation = (byte)('I'),
            Ack = (byte)('K'),
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

        //TODO: add retries :smile:
        private async Task<HandshakeDecision> NegociateConnection(Guid protocolHandler, PeerInfo peer, ushort localPort, CancellationTokenSource mainCts)
        {
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

            await udp.SendAsync(payload, peer.EndPoint);

            var decision = await Manager.WaitForDecisionAsync(new HandshakeRequest(conectionGuid, protocolHandler, peer.EndPoint), TimeSpan.FromSeconds(30), false, mainCts.Token);

            if (!decision.Accepted)
                throw new Exception("Handshake declined by peer.");

            Console.WriteLine("Peer acepted :D");

            return decision;
        }
        public async Task<(bool Success, UdpClient Client)> InitUdpConection(Guid protocolHandler, PeerInfo peer, ushort localPort, CancellationTokenSource mainCts)
        {
            if (!ProtocolHandlers.TryGetValue(protocolHandler, out var handler))
            {
                throw new KeyNotFoundException("Handler not found for protocol: " + nameof(protocolHandler));
            }

            var decision = await NegociateConnection(protocolHandler, peer, localPort, mainCts);

            return await QuicConectionCore.OpenPortCore(localPort, peer, (ushort)decision.Port, mainCts.Token);
        }
        public async Task InitQuicConection(Guid protocolHandler, PeerInfo peer, ushort localPort, CancellationTokenSource mainCts)
        {
            if (!ProtocolHandlers.TryGetValue(protocolHandler, out var handler))
            {
                throw new KeyNotFoundException("Handler not found for protocol: " + nameof(protocolHandler));
            }

            var decision = await NegociateConnection(protocolHandler, peer, localPort, mainCts);

            var conection = await QuicConectionCore.InitQuicConnectionCore(this.IPEndpoint.Address, localPort, peer, (ushort)decision.Port, CertManager.PeerCertificate!, handler.Commpression, mainCts.Token);
            await handler.HandleAsync(conection.Item1, conection.Item2, peer, mainCts.Token);

        }

        public async Task PeerInterogation(string token, bool replaceIfTampered, CancellationTokenSource mainCts)
        {
            var p = Helpers.DecodeEndpointToken(token);

            if (!AvilablePeers.ContainsKey(p.EndPoint))
            {
                AvilablePeers[p.EndPoint] = p;
            }
            else if (!p.CertHash.SequenceEqual(AvilablePeers[p.EndPoint].CertHash) && replaceIfTampered)
            {
                AvilablePeers[p.EndPoint] = p;
            }
            else
            {
                throw new Exception("Peer info tampering detected.");
            }

            await PeerInterogation(p.EndPoint, mainCts);
        }
        public async Task PeerInterogation(IPEndPoint endpoint, CancellationTokenSource mainCts)
        {
            using var udpCts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token);

            Console.WriteLine($"Starting interogation for {endpoint}...");

            var sendTask = SendLoopAsync(udp!, endpoint, udpCts.Token);

            try
            {
                while (!mainCts.Token.IsCancellationRequested && !AvilablePeers.ContainsKey(endpoint))
                {
                    await Task.Delay(250, mainCts.Token);
                }
            }
            catch (OperationCanceledException) { }

            udpCts.Cancel();
        }
        private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
        {
            var ACKPacket = Helpers.Combine(MagicHeader, [(byte)MessageType.Ack]);

            while (!token.IsCancellationRequested)
            {
                try
                {

                skipPacket:

                    var result = await udp.ReceiveAsync(token);

                    if (!_rateLimiter.IsAllowed(result.RemoteEndPoint.Address))
                        goto skipPacket;

                    //Console.WriteLine("Recived: " + Encoding.UTF8.GetString(result.Buffer));

                    //if (!result.RemoteEndPoint.Address.Equals(targetPeer.Address))
                    //    continue;

                    if (result.Buffer.Length > 512 || result.Buffer.Length < MagicHeader.Length)
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

                        switch (messageType)
                        {
                            case (byte)MessageType.Interogation:
                            case (byte)MessageType.Hello:
                                if (messageType == (byte)MessageType.Interogation)
                                {
                                    udp.SendAsync(HelloPayload, result.RemoteEndPoint);
                                }

                                var certHash = r.ReadBytes(CurrentPeer.CertHash.Length);
                                byte nameSize = r.ReadByte();
                                var nameBytes = r.ReadBytes(nameSize);
                                var publicKeyBytes = r.ReadBytes(CurvePublicKeySize);

                                var signature = r.ReadBytes(256 / 8);

                                if (!AvilablePeers.ContainsKey(result.RemoteEndPoint))
                                {
                                    var ecdsa = ECDsa.Create();

                                    ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

                                    var peerInfo = new PeerInfo
                                    {
                                        EndPoint = result.RemoteEndPoint,
                                        CertHash = certHash,
                                        Name = Encoding.UTF8.GetString(nameBytes),
                                        LastSeen = PreciseTime.GetCorrectTime(),
                                        Curve = ecdsa
                                    };

                                    if (!peerInfo.Curve.VerifyData(result.Buffer.AsSpan(0, result.Buffer.Length - signature.Length), signature, HashAlgorithmName.SHA3_256))
                                    {
                                        Console.WriteLine("Received invalid signature from " + result.RemoteEndPoint);  
                                        continue;
                                    }


                                    AvilablePeers[peerInfo.EndPoint] = peerInfo;
                                    OnPeerAvilable?.Invoke(peerInfo);
                                }
                                else
                                {
                                    var peer = AvilablePeers[result.RemoteEndPoint];

                                    if (!peer.Curve.VerifyData(result.Buffer.AsSpan(0, result.Buffer.Length - signature.Length), signature, HashAlgorithmName.SHA3_256))
                                    {
                                        Console.WriteLine("Received invalid signature from " + result.RemoteEndPoint); 
                                        continue;
                                    }

                                    if (!certHash.SequenceEqual(peer.CertHash))
                                    {
                                        //TODO: IDK what to do enter in panick cause someone is spoofing conections!=!="!"?=)i3?_="!
                                    }
                                    else
                                    {
                                        if (peer.Name.Length != nameBytes.Length || peer.Name != Encoding.UTF8.GetString(nameBytes))
                                        {
                                            peer.Name = Encoding.UTF8.GetString(nameBytes);
                                        }

                                        peer.LastSeen = PreciseTime.GetCorrectTime();
                                    }
                                }

                                await udp.SendAsync(ACKPacket, result.RemoteEndPoint);
                                continue;

                            case (byte)MessageType.Ack:
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

                                        _ = Task.Run(async () =>
                                        {
                                            HandShakeType decidedResponse = HandShakeType.Unsuported;
                                            ushort decidedPort = 0;
                                            CancellationToken ct = CancellationToken.None;

                                            if (ProtocolHandlers.TryGetValue(connectionType, out var handler))
                                            {
                                                var decision = await Manager.WaitForDecisionAsync(new HandshakeRequest(guid, connectionType, result.RemoteEndPoint), TimeSpan.FromSeconds(30), true, CancellationToken.None);

                                                if (decision.Accepted)
                                                {
                                                    if (decision.Port == null || decision.Port == 0)
                                                        throw new Exception("Invalid port in handshake decision.");
                                                    decidedResponse = HandShakeType.Accept;
                                                    decidedPort = (ushort)decision.Port;
                                                    ct = decision.Ct ?? CancellationToken.None;
                                                }
                                                else
                                                {
                                                    decidedResponse = HandShakeType.Decline;
                                                    decidedPort = 0;
                                                }
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

                                            await udp.SendAsync(payload, result.RemoteEndPoint);

                                            if (decidedResponse == HandShakeType.Accept)
                                            {
                                                var connection = await QuicConectionCore.InitQuicConnectionCore(this.IPEndpoint.Address, decidedPort, AvilablePeers[result.RemoteEndPoint], remotePort, CertManager.PeerCertificate!, handler.Commpression, ct);

                                                handler.HandleAsync(connection.Item1, connection.Item2, AvilablePeers[result.RemoteEndPoint], ct);
                                            }
                                        });
                                        continue;

                                    case HandShakeType.Accept:
                                        Console.WriteLine($"Received handshake ACCEPT from {result.RemoteEndPoint}");
                                        Manager.Approve(guid, remotePort, null);
                                        continue;

                                    case HandShakeType.Decline or HandShakeType.Unsuported:
                                        Console.WriteLine($"Handshake canceled from {result.RemoteEndPoint}");
                                        Manager.Reject(guid);
                                        continue;
                                }
                            continue;

                            case (byte)MessageType.Ping:
                                bool secondTimestamp = ms.ReadByte() > 0;
                                long t1 = r.ReadInt64();

                                if (secondTimestamp)
                                {
                                    long t2 = r.ReadInt64();

                                    if (!AvilablePeers.TryGetValue(result.RemoteEndPoint, out PeerInfo peer))
                                        continue;

                                    peer.UpTicks = t2 - t1;
                                    peer.DownTicks = PreciseTime.GetCorrectTime().Ticks - t2;
                                    peer.Ping = TimeSpan.FromTicks(PreciseTime.GetCorrectTime().Ticks - t1);
                                }
                                else
                                {
                                    udp.SendAsync(BuildPingPacket(t1, PreciseTime.GetCorrectTime().Ticks), result.RemoteEndPoint);
                                }
                                continue;

                            default:
                                Console.WriteLine($"Received unknown message type {(char)messageType} from {result.RemoteEndPoint}");
                                continue;
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in ReceiveLoopAsync: {ex.Message}");

                    if (token.IsCancellationRequested)
                        break;
                }
            }
        }


        private byte[] GenerateHelloPayload(MessageType type)
        {
            byte[] payload;

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(MagicHeader);
                w.Write((byte)type);
                w.Write(CurrentPeer.CertHash);

                var nameBytes = Encoding.UTF8.GetBytes(CurrentPeer.Name);
                w.Write((byte)nameBytes.Length);
                w.Write(nameBytes);

                w.Write(CertManager.Curve.ExportSubjectPublicKeyInfo());

                w.Write(new byte[256 / 8]); //Reserved for signature

                payload = ms.ToArray();
                var signature = CertManager.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Copy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
            }

            return payload;
        }

        public byte[] BuildPingPacket(long t1, long? t2 = null)
        {
            int size = MagicHeader.Length + 2 + 8 + (t2.HasValue ? 8 : 0);

            byte[] packet = new byte[size];

            Buffer.BlockCopy(MagicHeader, 0, packet, 0, MagicHeader.Length);

            packet[MagicHeader.Length] = (byte)MessageType.Ping;
            packet[MagicHeader.Length+1] = (byte)(t2.HasValue ? 1 : 0);

            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(MagicHeader.Length + 2, 8), t1);

            if (t2.HasValue)
            {
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(MagicHeader.Length + 2 + 8, 8), t2.Value);
            }

            return packet;
        }
        private async Task SendLoopAsync(UdpClient udp, IPEndPoint peer, CancellationToken token)
        {
            double maxIntervalTicks = TimeSpan.FromSeconds(20).Ticks;
            long intervalTicks = TimeSpan.FromMilliseconds(PunchIntervalMiliseconds).Ticks;
            int tries = 0;

            while (!token.IsCancellationRequested)
            {
                try
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

                        if (peerResponded)
                        {
                            await udp.SendAsync(HelloPayload, peer);
                        }
                        else
                        {
                            await udp.SendAsync(InterogationPayload, peer);
                            await Task.Delay(250, token);
                        }
                    }

                    if (tries % 2 == 0)
                    {
                        await udp.SendAsync(BuildPingPacket(PreciseTime.GetCorrectTime().Ticks), peer);
                    }

                    tries++;
                }
                catch(Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    await Task.Delay(250, token);
                }
            }
        }

        public async Task<string> GetToken()
        {
            if (!string.IsNullOrEmpty(this.IPEndpointStr))
                return Helpers.EncodeEndpointToken(CurrentPeer);

            await Helpers.GetPublicIP();

            if (string.IsNullOrEmpty(IPEndpointStr))
                throw new Exception("Failed to obtain public IP address.");

            return await GetToken();
        }

        public void Dispose()
        {
            CancelationSource?.Cancel();
            try { CancelationSource?.Dispose(); } catch { }
            try { TrackerScanner?.Dispose(); } catch { }
            try { udp?.Dispose(); } catch { }
        }
    }
}
