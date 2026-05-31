
using QuicPunch;
using QuicPunch.PacketHandler;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunch
{
    public class QuicPunch : IDisposable
    {
        public UdpClient? udp = null;

        public int PublicDiscoveryPort { get; private set; } //Random.Shared.Next(1, 1024);
        public int LocalDiscoveryPort { get; private set; } //Random.Shared.Next(1, 1024);

        public static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuicPunchV16");

        public PeerInfo CurrentPeer { get; private set; }
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

        internal CertManager CertManager { get; } = new CertManager(AppDataPath);

        private int CertPublicKey { get; set; }

        //TODO: implement auto connect and password that must use hmac to make proof of ownership of the password and not just as a shared secret for encrypting the connection (which tbh is not that bad but still) and also add some way to manually add peers for first time connections without needing to capture the token from the interogation packets
        public QuicPunch(CancellationTokenSource cts, byte[]? discoveryId, byte[]? connectionPassword, bool autoAcceptConnections, ushort discoveryPort = 443)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            {
                throw new NotSupportedException("QUIC is not supported on this machine.");
            }


            udp = new UdpClient();

            if (OperatingSystem.IsWindows())
                udp.Client.IOControl(-1744830452, [0], null);

            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            udp.Client.DontFragment = true;

            LocalDiscoveryPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            PublicDiscoveryPort = this.IPEndpoint.Port;

            CancelationSource = cts ?? new CancellationTokenSource();

            if (discoveryId != null)
            {
                PoolId = discoveryId.Length == 20 ? discoveryId : SHA1.HashData(discoveryId);

                TrackerScanner = new TrackerScanner(PoolId, PublicDiscoveryPort);
                _ = TrackerScanner.Start();
            }

            if (connectionPassword != null)
            {
                PasswordHash = Rfc2898DeriveBytes.Pbkdf2(connectionPassword, PoolId, 100_000, HashAlgorithmName.SHA3_512, 64);
            }

            AutoAcceptConnections = autoAcceptConnections;

            CurrentPeer = new PeerInfo()
            {
                Name = Environment.UserName + '\\' + Environment.UserDomainName,
                EndPoint = this.IPEndpoint,
                CertHash = CertManager.CertPublicHash,
            };

            CertPublicKey = CertManager.PeerCertificate!.GetPublicKey().Length;

            PeerStore = new PeerStore(Path.Combine(AppDataPath, "peers.db"));

            foreach (var speer in PeerStore.GetAll())
            {
                if (!ExpectedPeerCert.TryGetValue(speer.EndPoint, out _))
                {
                    ExpectedPeerCert.TryAdd(speer.EndPoint, speer.CertHash);
                }

                _ = PeerInterogation(speer.EndPoint, cts);
            }

            PeerStore.PeerAdded += (PeerStore.SavedPeer speer, bool external) =>
            {
                if (!ExpectedPeerCert.TryGetValue(speer.EndPoint, out _))
                {
                    ExpectedPeerCert.TryAdd(speer.EndPoint, speer.CertHash);
                }

                _ = PeerInterogation(speer.EndPoint, cts);
            };
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
                    TrackerScanner = new TrackerScanner(value, PublicDiscoveryPort);
                    TrackerScanner.Start();
                }
            }
        }
        internal byte[] PasswordHash { get; set; }
        public bool AutoAcceptConnections { get; set; }
        public bool SharePeers { get; set; }
        public bool AcceptSharedPeers { get; set; }

        public TrackerScanner TrackerScanner { get; private set; }
        public CancellationTokenSource CancelationSource { get; private set; }

        public void StartInterogationListener()
        {
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
        public ConcurrentDictionary<IPEndPoint, byte[]> ExpectedPeerCert = new ConcurrentDictionary<IPEndPoint, byte[]>();

        public PeerStore PeerStore { get; private set; }

        public HandshakeManager Manager = new HandshakeManager();

        private readonly IpRateLimiter _rateLimiter = new IpRateLimiter(5);

        public readonly ConcurrentDictionary<Guid, IProtocolHandler> ProtocolHandlers = new();

        public interface IProtocolHandler
        {
            public Guid ProtocolId { get; }
            public string ProtocolName { get; }
            public ZstandardCompressionOptions? CompressionOptions { get; }
            Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct);
            Task DeniedAsync(PeerInfo peer, CancellationToken ct);
        }
        public bool RemoveProtocol(IProtocolHandler handler) => ProtocolHandlers.TryRemove(handler.ProtocolId, out _);
        public void RegisterProtocol(IProtocolHandler handler) => ProtocolHandlers[handler.ProtocolId] = handler;


        public event Action<PeerInfo>? OnPeerAvilable;
        internal void RaisePeerAvailable(PeerInfo peerInfo)
        {
            OnPeerAvilable?.Invoke(peerInfo);
        }

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
                var signature = CertManager.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Resize(ref payload, payload.Length + signature.Length);
                Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
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

            var nudp = new UdpClient();
            if (OperatingSystem.IsWindows())
                nudp.Client.IOControl(-1744830452, [0], null);
            nudp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            nudp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
            var publicEndPoint = await Helpers.GetPublicEndPoint(nudp);

            var decision = await NegociateConnection(protocolHandler, peer, (ushort)publicEndPoint.Port, mainCts);

            return await QuicConection.OpenPortCore(nudp, peer, (ushort)decision.Port, mainCts.Token);
        }
        public async Task InitQuicConection(Guid protocolHandler, PeerInfo peer, ushort localPort, CancellationTokenSource mainCts)
        {
            if (!ProtocolHandlers.TryGetValue(protocolHandler, out var handler))
            {
                throw new KeyNotFoundException("Handler not found for protocol: " + nameof(protocolHandler));
            }

            var nudp = new UdpClient();
            if (OperatingSystem.IsWindows())
                nudp.Client.IOControl(-1744830452, [0], null);
            nudp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            nudp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
            var publicEndPoint = await Helpers.GetPublicEndPoint(nudp);

            var decision = await NegociateConnection(protocolHandler, peer, (ushort)publicEndPoint.Port, mainCts);

            var conection = await QuicConection.InitQuicConnectionCore(this.IPEndpoint, nudp, peer, (ushort)decision.Port, CertManager.PeerCertificate!, handler.CompressionOptions, mainCts.Token);

            if (conection.Conection == null || conection.Stream == null)
            {
                await handler.DeniedAsync(peer, mainCts.Token);
            }
            else
            {
                await handler.HandleAsync(conection.Conection, conection.Stream, peer, mainCts.Token);
            }
        }

        //TODO: make peer database for long term storage of peers and their info and add some way to manually add peers to it for first time connections

        public async Task PeerInterogation(string token, CancellationTokenSource mainCts)
        {
            var p = DecodeEndpointToken(token);

            if (!ExpectedPeerCert.TryGetValue(p.EndPoint, out var cert))
            {
                ExpectedPeerCert.TryAdd(p.EndPoint, p.CertHash);
            }

            await PeerInterogation(p.EndPoint, mainCts);
        }
        private readonly ConcurrentDictionary<IPEndPoint, CancellationTokenSource> _punchCts = new ConcurrentDictionary<IPEndPoint, CancellationTokenSource>();
        public async Task PeerInterogation(IPEndPoint endpoint, CancellationTokenSource mainCts)
        {
            if (mainCts == null)
                mainCts = new CancellationTokenSource();

            var cts = CancellationTokenSource.CreateLinkedTokenSource(mainCts!.Token);

            if (!_punchCts.TryAdd(endpoint, cts))
            {
                cts.Dispose();
                return;
            }

            Console.WriteLine($"Starting interogation for {endpoint}...");

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendLoopAsync(udp!, endpoint, cts.Token);
                }
                finally
                {
                    _punchCts.TryRemove(endpoint, out _);
                    cts.Dispose();
                }
            });
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint IpToUint(IPAddress ip)
        {
            Span<byte> bytes = stackalloc byte[4];

            if (!ip.TryWriteBytes(bytes, out int written) || written != 4)
                throw new ArgumentException("IPv4 only");

            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }
        private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {

                skipPacket:

                    var result = await udp.ReceiveAsync(token);

                    if (!_rateLimiter.IsAllowed(IpToUint(result.RemoteEndPoint.Address)))
                        goto skipPacket;

                    //Console.WriteLine("Recived: " + Encoding.UTF8.GetString(result.Buffer));


                    if (result.Buffer.Length > 1464 || result.Buffer.Length < MagicHeader.Length)
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
                                HelloHandler.HandleHello(this, r, udp, result, messageType);
                                continue;

                            case (byte)MessageType.Ack:
                                AckHandler.HandleAck(this, r, udp, result);
                                continue;

                            case (byte)MessageType.Handshake:
                                HandshakeHandler.HandleHandshake(this, r, udp, result);
                                continue;

                            case (byte)MessageType.Ping:
                                PingHandler.HandlePing(this, r, udp, result);
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

        internal byte[] GenerateAck(bool sharePeers)
        {
            byte[] payload;

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(MagicHeader);
                w.Write((byte)MessageType.Ack);


                var peersCopy = AvilablePeers.ToArray();

                w.Write(sharePeers ? (ushort)peersCopy.Length : (ushort)0);

                if (sharePeers)
                {
                    foreach (var peer in peersCopy.Select(p => p.Value))
                    {
                        w.Write(peer.EndPoint.Address.GetAddressBytes());
                        w.Write((ushort)peer.EndPoint.Port);
                        w.Write(peer.CertHash);
                    }
                }

                w.Write(PreciseTime.GetCorrectTime().Ticks);

                payload = ms.ToArray();

                var signature = CertManager.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Resize(ref payload, payload.Length + signature.Length);
                Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
            }

            return payload;
        }
        internal byte[] GenerateHelloPayload(MessageType type, bool passwordProof)
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

                var cert = CertManager.PeerCertificate.Export(X509ContentType.Cert);
                var certBytes = cert.Length;
                w.Write((ushort)certBytes);
                w.Write(cert);

                w.Write((byte)(PasswordHash != null && passwordProof ? 255 : 0));

                if (PasswordHash != null && passwordProof)
                {
                    var ticks = PreciseTime.GetCorrectTime().Ticks;
                    w.Write(ticks);
                    var nonce = RandomNumberGenerator.GetBytes(32);
                    w.Write(nonce);

                    var pop = HMACSHA3_256.HashData(Helpers.Combine(BitConverter.GetBytes(ticks), nonce, IPEndpoint.Address.GetAddressBytes(), BitConverter.GetBytes((ushort)IPEndpoint.Port)), PasswordHash);

                    w.Write(pop);
                }

                payload = ms.ToArray();

                var signature = CertManager.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Resize(ref payload, payload.Length + signature.Length);
                Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
            }

            return payload;
        }

        internal byte[] BuildPingPacket(long t1, long? t2 = null)
        {
            int size = MagicHeader.Length + 2 + 8 + (t2.HasValue ? 8 : 0);

            byte[] packet = new byte[size];

            Buffer.BlockCopy(MagicHeader, 0, packet, 0, MagicHeader.Length);

            packet[MagicHeader.Length] = (byte)MessageType.Ping;
            packet[MagicHeader.Length + 1] = (byte)(t2.HasValue ? 1 : 0);

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

            var helloPayload = GenerateHelloPayload(MessageType.Hello, false);

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
                            await udp.SendAsync(helloPayload, peer);
                        }
                        else
                        {
                            await udp.SendAsync(GenerateHelloPayload(MessageType.Interogation, true), peer);
                            await Task.Delay(250, token);
                        }
                    }

                    if (tries % 2 == 0)
                    {
                        await udp.SendAsync(BuildPingPacket(PreciseTime.GetCorrectTime().Ticks), peer);
                    }

                    tries++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    await Task.Delay(250, token);
                }
            }
        }

        public string GetToken() => EncodeEndpointToken(CurrentPeer);

        private const byte TokenVersionByte = 1;
        public static string EncodeEndpointToken(PeerInfo p)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                //w.Write(TokenVersionByte);
                w.Write(p.EndPoint.Address.GetAddressBytes());
                w.Write((ushort)p.EndPoint.Port);

                //w.Write((byte)p.CertHash.Length);
                w.Write(p.CertHash);
                return Base64Url.EncodeToString(ms.ToArray());
            }
        }
        public static PeerInfo DecodeEndpointToken(string t)
        {
            using (var ms = new MemoryStream(Base64Url.DecodeFromChars(t)))
            using (var r = new BinaryReader(ms))
            {
                //var version = r.ReadByte();
                // if (version != TokenVersionByte) 
                //    throw new Exception("Invalid token version");

                var addressBytes = r.ReadBytes(4);
                var port = r.ReadUInt16();

                //var certHashLength = r.ReadByte();
                var certHash = r.ReadBytes(384 / 8);

                return new PeerInfo
                {
                    EndPoint = new IPEndPoint(new IPAddress(addressBytes), port),
                    CertHash = certHash,
                };
            }
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
