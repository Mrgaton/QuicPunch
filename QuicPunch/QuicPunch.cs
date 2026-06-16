
using QuicPunch;
using QuicPunch.PacketHandler;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        private static bool DebugMode = Debugger.IsAttached;
        private static void WriteLine(string m)
        {
            if (DebugMode)
                Console.WriteLine(m);
        }

        private HttpClient client = new HttpClient();
        
        public UdpClient? udp = null;

        public int LocalDiscoveryPort { get; private set; } //Random.Shared.Next(1, 1024);

        public static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuicPunchV16");

        public PeerInfo CurrentPeer { get; private set; }

        private IPEndPoint _IPEndpoint;
        
        private IPEndPoint[] _StunServerEndpoints;
        
        private SimpleStunClient _StunClient;


        private int MostUsedPort;
        private int GetMostUsedPort()
        {
            return _StunClient.StunResponseEndpointHits
            .GroupBy(x => x.Key.Port)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x => x.Value)
            ).OrderByDescending(e => e.Value).FirstOrDefault().Key;
        }

        private (int minPort, int maxPort) StunPortRange;              

        internal CertManager CertManager { get; } = new CertManager(AppDataPath);

        private int CertPublicKey { get; set; }

        //TODO: implement auto connect and password that must use hmac to make proof of ownership of the password and not just as a shared secret for encrypting the connection (which tbh is not that bad but still) and also add some way to manually add peers for first time connections without needing to capture the token from the interogation packets
        public QuicPunch(CancellationTokenSource cts, byte[]? discoveryId, byte[]? connectionPassword, bool autoAcceptConnections, ushort discoveryPort = 443)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            {
                throw new NotSupportedException("QUIC is not supported on this machine.");
            }

            var urls = new string[]
            {
                "https://raw.githubusercontent.com/pradt2/always-online-stun/refs/heads/master/valid_nat_testing_hosts.txt",
                "https://raw.githubusercontent.com/pradt2/always-online-stun/refs/heads/master/valid_nat_testing_ipv4s.txt",
                "https://raw.githubusercontent.com/pradt2/always-online-stun/refs/heads/master/candidates.txt",
                "https://raw.githubusercontent.com/pradt2/always-online-stun/refs/heads/master/valid_ipv4s.txt",
                "https://raw.githubusercontent.com/pradt2/always-online-stun/refs/heads/master/valid_hosts.txt",

                "https://gist.githubusercontent.com/mondain/b0ec1cf5f60ae726202e/raw/2d2b96b4508a38d342e0228d46eab84dad2398a3/public-stun-list.txt",
                "https://gist.githubusercontent.com/zziuni/3741933/raw/212e4b6316110dc5c128d08f65ff8f174d7ae383/stuns",
            };

            var parsedEndpoints = new List<string>();

            foreach (var url in urls) 
            {
                var data =  client.GetStringAsync(url).Result;
            
                foreach(var line in data.Split("\n").Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#")))
                {
                    parsedEndpoints.Add(line);
                }
            }

            var uniqueList = parsedEndpoints
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var servers = new ConcurrentBag<IPEndPoint>();

            Parallel.ForEach(uniqueList,new ParallelOptions() { MaxDegreeOfParallelism = 64 * 5}, line =>
            {
                var ep = Helpers.ResolveEndpoint(line);

                if (ep is not null)
                {
                    foreach (var e in ep)
                    {
                        servers.Add(e);
                    }
                }
            });

            udp = new UdpClient();

            if (OperatingSystem.IsWindows())
                udp.Client.IOControl(-1744830452, [0], null);

            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            udp.Client.DontFragment = true;

            LocalDiscoveryPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            
            _StunServerEndpoints = servers.ToArray();
            _StunClient = new SimpleStunClient(udp, _StunServerEndpoints);

            
            _StunClient.MappedAddressResolved += (sender, args) =>
            {

            };

            CurrentPeer = new PeerInfo()
            {
                Name = Environment.UserName + '\\' + Environment.UserDomainName,
                CertHash = CertManager.CertPublicHash,
            };

            _CancelationSource = cts ?? new CancellationTokenSource();

            _ = ReceiveLoopAsync();

            StunRequest().GetAwaiter().GetResult();

            if (discoveryId != null)
            {
                PoolId = discoveryId.Length == 20 ? discoveryId : SHA1.HashData(discoveryId);

                TrackerScanner = new TrackerScanner(PoolId, LocalDiscoveryPort);
                _ = TrackerScanner.Start();
            }

            if (connectionPassword != null)
            {
                PasswordHash = Rfc2898DeriveBytes.Pbkdf2(connectionPassword, PoolId, 100_000, HashAlgorithmName.SHA3_512, 64);
            }

            AutoAcceptConnections = autoAcceptConnections;

            CertPublicKey = CertManager.PeerCertificate!.GetPublicKey().Length;

            PeerStore = new PeerStore(Path.Combine(AppDataPath, "peers.db"));

            foreach (var speer in PeerStore.GetAll())
            {
                AddCertToAddress(speer.Addresses, speer.CertHash);
                
                _ = PeerInterogation(new PeerInfo()
                {
                    CertHash = speer.CertHash,
                    Addresses = speer.Addresses,
                    MaxPort =  speer.MaxPort,
                    MinPort = speer.MinPort
                }, cts);
            }

            PeerStore.PeerAdded += (PeerStore.SavedPeer speer, bool external) =>
            {
                AddCertToAddress(speer.Addresses, speer.CertHash);

                _ = PeerInterogation(new PeerInfo()
                {
                    CertHash = speer.CertHash,
                    Addresses = speer.Addresses,
                    MaxPort =  speer.MaxPort,
                    MinPort = speer.MinPort
                }, cts);
            };

            Task.Run(StartStunRequest);
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
                    TrackerScanner = new TrackerScanner(value, LocalDiscoveryPort);
                    TrackerScanner.Start();
                }
            }
        }
        internal byte[] PasswordHash { get; set; }
        public bool AutoAcceptConnections { get; set; }
        public bool SharePeers { get; set; }
        public bool AcceptSharedPeers { get; set; }

        public TrackerScanner TrackerScanner { get; private set; }
        public CancellationTokenSource _CancelationSource { get; private set; }

        private string LastToken;
        public async Task StartStunRequest()
        {
            while (!_CancelationSource.IsCancellationRequested)
            {
                try
                {
                    await StunRequest();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.ToString());
                }

                await Task.Delay(5000);
            }
        }

        private async Task StunRequest()
        {
            await Task.Delay(100);

            await _StunClient.SendRequest(_CancelationSource.Token);

            var type = Helpers.GetNetworkType(_StunClient.StunResponseEndpointHits);

            MostUsedPort = GetMostUsedPort();

            var portOrder = _StunClient.StunResponseEndpointHits.OrderByDescending(k => k.Key.Port);

            StunPortRange = ((portOrder.Last().Key.Port / 255) * 255, ((portOrder.First().Key.Port + (255 - 1)) / 255) * 255);
            CurrentPeer.Addresses = _StunClient.StunResponseEndpointHits.Select(k => k.Key.Address).Distinct().ToArray();

            var newToken = GetToken();

            if (newToken != LastToken)
            {
                LastToken = newToken;

                Console.WriteLine($"New token generated: {newToken}");
            }

            _StunClient.StunResponseEndpointHits.Clear();
        }

        public const int PunchIntervalMiliseconds = 2500 / 2;

        public static byte[] MagicHeader = Encoding.UTF8.GetBytes("PuNch");

        public ConcurrentDictionary<IPEndPoint, PeerInfo> AvilablePeers = new ConcurrentDictionary<IPEndPoint, PeerInfo>();
        public ConcurrentDictionary<IPAddress, byte[][]> ExpectedPeerCerts = new ConcurrentDictionary<IPAddress, byte[][]>();

        public void AddCertToAddress(IPAddress[] addresses, byte[] cert)
        {
            foreach(var a in addresses)
            {
                AddCertToAddress(a, cert);
            }
        }
        public void AddCertToAddress(IPAddress address, byte[] cert)
        {
            if (ExpectedPeerCerts.TryGetValue(address, out var peerCerts))
            {
                ExpectedPeerCerts[address] = peerCerts.Append(cert).ToArray();
            }
            else
            {
                ExpectedPeerCerts.TryAdd(address, [ cert ]);
            }
        }
        
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

            var connectionGuid = Guid.NewGuid();

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(MagicHeader);
                w.Write((byte)MessageType.Handshake);
                w.Write((byte)HandShakeType.Request);
                w.Write(localPort);
                w.Write(protocolHandler.ToByteArray());
                w.Write(connectionGuid.ToByteArray());

                payload = ms.ToArray();
                var signature = CertManager.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Resize(ref payload, payload.Length + signature.Length);
                Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
            }

            udp.BigSendAsync(payload, peer);

            var decision = await Manager.WaitForDecisionAsync(new HandshakeRequest(connectionGuid, protocolHandler, peer.ActiveEndPoint), TimeSpan.FromSeconds(30), false, mainCts.Token);

            if (!decision.Accepted)
                throw new Exception("Handshake declined by peer.");

            WriteLine("Peer accepted :D");

            return decision;
        }
        public async Task<(bool Success, UdpClient Client, IPEndPoint remoteEndpoint)> InitUdpConnection(Guid protocolHandler, PeerInfo peer, ushort localPort, CancellationTokenSource mainCts)
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

            var decision = await NegociateConnection(protocolHandler, peer, localPort, mainCts);

            return await QuicPunchConnection.OpenPortCore(nudp, peer, (ushort)decision.Port, mainCts.Token);
        }
        public async Task InitQuicConnection(Guid protocolHandler, PeerInfo peer, ushort localPort, CancellationTokenSource mainCts)
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

            var decision = await NegociateConnection(protocolHandler, peer, localPort, mainCts);

            var connection = await QuicPunchConnection.InitQuicConnectionCore(new IPEndPoint(CurrentPeer.Addresses[0], CurrentPeer.MinPort), nudp, peer, (ushort)decision.Port, CertManager.PeerCertificate!, handler.CompressionOptions, mainCts.Token);

            if (connection.Connection == null || connection.Stream == null)
            {
                await handler.DeniedAsync(peer, mainCts.Token);
            }
            else
            {
                await handler.HandleAsync(connection.Connection, connection.Stream, peer, mainCts.Token);
            }
        }

        //TODO: make peer database for long term storage of peers and their info and add some way to manually add peers to it for first time connections

        public async Task PeerInterogation(string token, CancellationTokenSource mainCts)
        {
            var p = DecodeEndpointToken(token);

            foreach (var address in p.Addresses)
            {
                AddCertToAddress(address, p.CertHash);
            }

            await PeerInterogation(p, mainCts);
        }
       public async Task PeerInterogation(PeerInfo peer, CancellationTokenSource cts)
        {
            if (cts == null)
                cts = new CancellationTokenSource();

            var lcts = CancellationTokenSource.CreateLinkedTokenSource(cts!.Token);


            WriteLine($"Starting interogation for {string.Join(", ",peer.Addresses)}...");

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendLoopAsync(udp!, peer, lcts.Token);
                }
                finally
                {
                    lcts.Dispose();
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
        private async Task ReceiveLoopAsync()
        {
            while (!_CancelationSource.IsCancellationRequested)
            {
                try
                {
                skipPacket:

                    var result = await udp.ReceiveAsync(_CancelationSource.Token);

                    if (!_rateLimiter.IsAllowed(IpToUint(result.RemoteEndPoint.Address)))
                        goto skipPacket;
                    
                    if (_StunClient.TryProcessIncoming(result.Buffer, result.RemoteEndPoint))
                       continue;
                    
                    //Console.WriteLine("Recived: " + Encoding.UTF8.GetString(result.Buffer));


                    if (result.Buffer.Length > 1464 || result.Buffer.Length < MagicHeader.Length + (256 / 8))
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
                                WriteLine($"Received unknown message type {(char)messageType} from {result.RemoteEndPoint}");
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
                    WriteLine($"Error in ReceiveLoopAsync: {ex.Message}");

                    if (_CancelationSource.IsCancellationRequested)
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
                        w.Write((byte)peer.NetworkType);
                        w.Write((byte)peer.Addresses.Length);
                        foreach (var e in peer.Addresses)
                        {
                            w.Write(e.GetAddressBytes());
                        }
                        
                        w.Write((ushort)peer.MinPort);
                        w.Write((ushort)peer.MaxPort);
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

                    var pop = HMACSHA3_256.HashData(Helpers.Combine(BitConverter.GetBytes(ticks), nonce, CurrentPeer.Addresses[0].GetAddressBytes(), BitConverter.GetBytes((ushort)StunPortRange.minPort)), PasswordHash);

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
        private async Task SendLoopAsync(UdpClient udp, PeerInfo peer, CancellationToken token)
        {
            double maxIntervalTicks = TimeSpan.FromSeconds(20).Ticks;
            long intervalTicks = (long)Math.Min(TimeSpan.FromMilliseconds(PunchIntervalMiliseconds * 3).Ticks, maxIntervalTicks);
            int tries = 0;

            var helloPayload = GenerateHelloPayload(MessageType.Hello, false);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    KeyValuePair<IPEndPoint,PeerInfo>? avilablePeer = AvilablePeers.Where(k => EqualityComparer<PeerInfo>.Default.Equals( k.Value, peer )).FirstOrDefault();
                    bool peerResponded = avilablePeer.Value.Key != null;
                    
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
                            await udp.SendAsync(helloPayload, avilablePeer.Value.Key);
                        }
                        else
                        {
                            var payload = GenerateHelloPayload(MessageType.Interogation, true);
                            await udp.BigSendAsync(payload, peer);
                            await Task.Delay(250, token);
                        }
                    }

                    if (peerResponded && tries % 2 == 0)
                    {
                        await udp.SendAsync(BuildPingPacket(PreciseTime.GetCorrectTime().Ticks), avilablePeer.Value.Key);
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
                PackedFlags pf = new PackedFlags()
                {
                    NetworkType = p.NetworkType
                };

                w.Write((byte)pf.RawValue);

                if (p.NetworkType == NetworkType.DynamicAddress || p.NetworkType == NetworkType.DynamicPortAndAddress)
                {
                    w.Write((byte)p.Addresses.Length);

                    for (int i = 0; i < p.Addresses.Length; i++)
                    {
                        w.Write(p.Addresses[i].GetAddressBytes());
                    }
                }
                else
                {
                    w.Write(p.Addresses[0].GetAddressBytes());
                }

                if (pf.NetworkType == NetworkType.DynamicPort || pf.NetworkType == NetworkType.DynamicPortAndAddress)
                {
                    w.Write((short)p.MinPort);
                    w.Write((short)p.MaxPort);
                }
                else
                {
                    w.Write((ushort)p.MinPort);
                }

                //w.Write((byte)p.CertHash.Length);
                w.Write(p.CertHash);
                return Base64Url.EncodeToString(ms.ToArray());
            }
        }
        public static PeerInfo DecodeEndpointToken(string t)
        {
            var peer = new PeerInfo();
            
            using (var ms = new MemoryStream(Base64Url.DecodeFromChars(t)))
            using (var r = new BinaryReader(ms))
            {
                //var version = r.ReadByte();
                // if (version != TokenVersionByte) 
                //    throw new Exception("Invalid token version");

                PackedFlags pf = new PackedFlags(r.ReadByte());

                if (pf.NetworkType == NetworkType.DynamicAddress || pf.NetworkType == NetworkType.DynamicPortAndAddress)
                {
                   var addressesLength = r.ReadByte();

                   IPAddress[] addresses = new IPAddress[addressesLength];
                   
                    for (int i = 0; i < addressesLength; i++)
                    {
                        addresses[i] = new IPAddress(r.ReadBytes(4));
                    }

                    peer.Addresses = addresses;
                }
                else
                {
                    peer.Addresses = [new IPAddress(r.ReadBytes(4))];
                }

                if (pf.NetworkType == NetworkType.DynamicPort || pf.NetworkType == NetworkType.DynamicPortAndAddress)
                {
                    peer.MinPort = r.ReadUInt16();
                    peer.MaxPort = r.ReadUInt16();
                }
                else
                {
                    peer.MinPort =  r.ReadUInt16();
                }
                
                var certHash = r.ReadBytes(384 / 8);
                peer.CertHash = certHash;
                return peer;
            }
        }
        public void Dispose()
        {
            _CancelationSource?.Cancel();
            try { _CancelationSource?.Dispose(); } catch { }
            try { TrackerScanner?.Dispose(); } catch { }
            try { udp?.Dispose(); } catch { }
        }
        
        public enum NetworkType:byte
        {
            Static  = 0,
            DynamicPort = 1,
            DynamicAddress = 2,
            DynamicPortAndAddress = 3
        }
    }
}
