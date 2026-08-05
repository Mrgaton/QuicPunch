
using QuicPunch;
using QuicPunch.PacketHandler;
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunch
{
    public class QuicPunch : IDisposable
    {
        public const int SioUdpConnReset = unchecked((int)0x9800000C);
        public const int SioUdpNetReset = unchecked((int)0x9800000F);

        public static void ConfigureUdpSocket(UdpClient client)
        {
            if (OperatingSystem.IsWindows())
            {
                byte[] optionInValue = new byte[] { 0 };
                client.Client.IOControl(SioUdpConnReset, optionInValue, null);
                client.Client.IOControl(SioUdpNetReset, optionInValue, null);
            }
        }

        private static bool DebugMode = Debugger.IsAttached;
        public static void WriteLine(string m)
        {
            if (DebugMode)
                Console.WriteLine(m);
        }

        private HttpClient client = new HttpClient();
        
        public UdpClient? udp = null;

        public int LocalDiscoveryPort { get; private set; } //Random.Shared.Next(1, 1024);

        public bool RebindListenerPort(ushort newPort)
        {
            try
            {
                var oldUdp = udp;
                var newUdp = new UdpClient();
                ConfigureUdpSocket(newUdp);
                newUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                newUdp.Client.Bind(new IPEndPoint(IPAddress.Any, newPort));
                newUdp.Client.DontFragment = true;

                udp = newUdp;
                LocalDiscoveryPort = ((IPEndPoint)newUdp.Client.LocalEndPoint!).Port;

                _StunClient = new SimpleStunClient(newUdp, _StunServerEndpoints);

                if (TrackerScanner != null)
                {
                    TrackerScanner.Stop();
                    TrackerScanner = new TrackerScanner(PoolId, LocalDiscoveryPort);
                    _ = TrackerScanner.Start();
                }

                try { oldUdp?.Close(); oldUdp?.Dispose(); } catch { }

                _ = Task.Run(async () => await StunRequest());
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QuicPunch] Failed to rebind listener port to {newPort}: {ex.Message}");
                return false;
            }
        }

        public static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuicPunchV16");

        public PeerInfo CurrentPeer { get; private set; }

        private IPEndPoint _IPEndpoint;
        
        private IPEndPoint[] _StunServerEndpoints;
        
        private SimpleStunClient _StunClient;

        public PeerStore PeerStore { get; private set; }

        public HandshakeManager Manager = new HandshakeManager();

        private readonly IpRateLimiter _rateLimiter = new IpRateLimiter(500);

        public readonly ConcurrentDictionary<Guid, IProtocolHandler> ProtocolHandlers = new();
        
        private int MostUsedPort;
     
        private (int minPort, int maxPort) StunPortRange;              

        internal CertManager CertManager { get; } = new CertManager(AppDataPath);

        private int CertPublicKey { get; set; }

        public static ushort GetDeterministicPortFromCertHash(byte[] certHash, int minPort = 49152, int maxPort = 65535)
        {
            if (certHash == null || certHash.Length < 4)
                return (ushort)Random.Shared.Next(minPort, maxPort + 1);

            uint val = BinaryPrimitives.ReadUInt32LittleEndian(certHash);
            int range = maxPort - minPort + 1;
            return (ushort)(minPort + (val % range));
        }

        //TODO: implement auto connect and password that must use hmac to make proof of ownership of the password and not just as a shared secret for encrypting the connection (which tbh is not that bad but still) and also add some way to manually add peers for first time connections without needing to capture the token from the interogation packets
        public QuicPunch(CancellationTokenSource cts, byte[]? discoveryId, byte[]? connectionPassword, bool autoAcceptConnections, ushort discoveryPort = 0)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            {
                throw new NotSupportedException("QUIC is not supported on this machine.");
            }

            string stunEndpointsCachePath = Path.Combine(Path.GetTempPath(), "stunServersCache.epl");

            var servers = new ConcurrentBag<IPEndPoint>();
            
            if (File.Exists(stunEndpointsCachePath))
            {
                foreach (var parsedLine in File.ReadAllLines(stunEndpointsCachePath))
                {
                    if (IPEndPoint.TryParse(parsedLine, out var ep))
                    {
                        servers.Add(ep);
                    }
                }
            }
            else
            {
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

                _ = Task.Run(async () =>
                {
                    var parsedEndpoints = new List<string>();

                    foreach (var url in urls) 
                    {
                        try
                        {
                            var data = await client.GetStringAsync(url);
                    
                            foreach(var line in data.Split("\n").Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#")))
                            {
                                parsedEndpoints.Add(line);
                            }
                        }
                        catch { }
                    }

                    var uniqueList = parsedEndpoints
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    
                    Parallel.ForEach(uniqueList, new ParallelOptions() { MaxDegreeOfParallelism = 64 * 5}, line =>
                    {
                        try
                        {
                            var ep = Helpers.ResolveEndpoint(line);

                            if (ep is not null)
                            {
                                foreach (var e in ep)
                                {
                                    servers.Add(e);
                                }
                            }
                        }
                        catch { }
                    });
                    
                    try
                    {
                        File.WriteAllText(stunEndpointsCachePath, string.Join('\n', servers.Select(e => e.ToString())));
                    }
                    catch { }
                });
            }

            if (discoveryPort == 0)
            {
                discoveryPort = GetDeterministicPortFromCertHash(CertManager.CertPublicHash);
            }

            udp = new UdpClient();
            ConfigureUdpSocket(udp);

            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            udp.Client.DontFragment = true;

            LocalDiscoveryPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            
            _StunServerEndpoints = servers.ToArray();
            _StunClient = new SimpleStunClient(udp, _StunServerEndpoints);

            CurrentPeer = new PeerInfo(CertManager.PeerCertificate, CertManager.EcdhPublicKeyRaw)
            {
                Name = $"{Environment.UserName}@{Environment.MachineName}",
                Addresses = Array.Empty<IPAddress>(),
            };

            CancellationSource = cts ?? new CancellationTokenSource();

            if (discoveryId != null)
            {
                PoolId = discoveryId.Length == 20 ? discoveryId : SHA1.HashData(discoveryId);

                TrackerScanner = new TrackerScanner(PoolId, LocalDiscoveryPort);
                TrackerScanner.OnPeerFound += OnTrackerPeerDiscovered;
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
                ExpectedPeerCerts.Add(speer.CertHash);
                
                int minPort = speer.MinPort > 0 ? speer.MinPort : LocalDiscoveryPort;
                int maxPort = speer.MaxPort > 0 ? speer.MaxPort : LocalDiscoveryPort;

                _ = PeerInterrogation(new PeerInfo()
                {
                    Addresses = speer.Addresses,
                    MaxPort = maxPort,
                    MinPort = minPort,
                    EcdhPublicKey = speer.EcdhPublicKey
                }.SetCertificateHash(speer.CertHash), CancellationSource);
            }

            PeerStore.PeerAdded += (PeerStore.SavedPeer speer, bool external) =>
            {                
                ExpectedPeerCerts.Add(speer.CertHash);

                int minPort = speer.MinPort > 0 ? speer.MinPort : LocalDiscoveryPort;
                int maxPort = speer.MaxPort > 0 ? speer.MaxPort : LocalDiscoveryPort;

                _ = PeerInterrogation(new PeerInfo()
                {
                    Addresses = speer.Addresses,
                    MaxPort = maxPort,
                    MinPort = minPort,
                    EcdhPublicKey = speer.EcdhPublicKey
                }.SetCertificateHash(speer.CertHash), CancellationSource);
            };

            _ = ReceiveLoopAsync();

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
                    TrackerScanner.OnPeerFound += OnTrackerPeerDiscovered;
                    TrackerScanner.Start();
                }
            }
        }
        internal byte[] PasswordHash { get; set; }
        public bool AutoAcceptConnections { get; set; }
        public bool SharePeers { get; set; }
        public bool AcceptSharedPeers { get; set; }

        public TrackerScanner TrackerScanner { get; private set; }
        public CancellationTokenSource CancellationSource { get; private set; }

        private string LastToken;
        public async Task StartStunRequest()
        {
            _ = SendLocalLanDiscoveryAsync();
            _ = StartPingLoopAsync();

            while (!CancellationSource.IsCancellationRequested)
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

        public async Task StartPingLoopAsync()
        {
            while (!CancellationSource.IsCancellationRequested)
            {
                try
                {
                    if (!AvailablePeers.IsEmpty)
                    {
                        byte[] pingReq = BuildPingPacket(Stopwatch.GetTimestamp(), false);
                        foreach (var peer in AvailablePeers.Values)
                        {
                            if (peer.ActiveEndPoint != null)
                            {
                                await udp.SendAsync(pingReq, peer.ActiveEndPoint);
                            }
                        }
                    }
                }
                catch { }

                await Task.Delay(2000, CancellationSource.Token);
            }
        }

        public async Task SendLocalLanDiscoveryAsync()
        {
            try
            {
                var payload = GenerateHelloPayload(MessageType.Interrogation, true);
                udp.EnableBroadcast = true;
                await udp.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, LocalDiscoveryPort));
            }
            catch { }
        }

        private void OnTrackerPeerDiscovered(IPEndPoint ep)
        {
            try
            {
                if (IPAddress.IsLoopback(ep.Address)) return;
                if (CurrentPeer.ActiveEndPoint != null && CurrentPeer.ActiveEndPoint.Equals(ep)) return;
                if (ep.Port == LocalDiscoveryPort && CurrentPeer.Addresses != null && CurrentPeer.Addresses.Contains(ep.Address)) return;

                bool alreadyKnown = AvailablePeers.Values.Any(p =>
                    (p.ActiveEndPoint != null && p.ActiveEndPoint.Equals(ep)) ||
                    (p.Addresses != null && p.Addresses.Contains(ep.Address) && (p.MinPort <= ep.Port && p.MaxPort >= ep.Port)));

                if (!alreadyKnown)
                {
                    var peerInfo = new PeerInfo()
                    {
                        Addresses = new[] { ep.Address },
                        MinPort = ep.Port,
                        MaxPort = ep.Port
                    };
                    _ = PeerInterrogation(peerInfo, CancellationSource);
                }
            }
            catch { }
        }

        private async Task StunRequest()
        {
            await _StunClient.SendRequest(CancellationSource.Token);

            await Task.Delay(500);

            CurrentPeer.NetworkType =  Helpers.GetNetworkType(_StunClient.StunResponseEndpointHits);
            MostUsedPort = Helpers.GetMostUsedPort(_StunClient.StunResponseEndpointHits);
            
            var portOrder = _StunClient.StunResponseEndpointHits.OrderByDescending(k => k.Key.Port);

            StunPortRange = portOrder.All(po => po.Key.Port == MostUsedPort) ?  (MostUsedPort, MostUsedPort) : ((portOrder.Last().Key.Port / 255) * 255, ((portOrder.First().Key.Port + (255 - 1)) / 255) * 255);
            CurrentPeer.Addresses = _StunClient.StunResponseEndpointHits.Select(k => k.Key.Address).Where(a => !SimpleStunClient.IsBogonOrLocalhost(a)).Distinct().ToArray();

            CurrentPeer.MinPort = StunPortRange.minPort;
            CurrentPeer.MaxPort = StunPortRange.maxPort;
            
            var newToken = GetToken();
         
            if (newToken != LastToken)
            {
                LastToken = newToken;

                Console.WriteLine($"New token generated: {newToken}");
            }

            _StunClient.StunResponseEndpointHits.Clear();
        }

        public const int PunchIntervalMiliseconds = 2500 / 2;

        public static byte[] MagicHeader = Encoding.UTF8.GetBytes("PNch");
        
        public class ExpectedPeerCertSet
        {
            private readonly ConcurrentDictionary<byte[], byte> _dict = new(Helpers.ByteArrayComparer.Instance);
            public void Add(byte[] certHash) { if (certHash != null) _dict[certHash] = 0; }
            public bool Contains(byte[] certHash) => certHash != null && _dict.ContainsKey(certHash);
        }

        public ConcurrentDictionary<Guid, PeerInfo> AvailablePeers { get; } = new();
        public ExpectedPeerCertSet ExpectedPeerCerts { get; } = new();
        public event Action<Guid, byte[]>? OnDataReceived;
        public event Action<PeerInfo>? OnPeerDisconnected;
        internal void RaisePeerDisconnected(PeerInfo peer) => OnPeerDisconnected?.Invoke(peer);

        internal byte[] BuildDisconnectPacket()
        {
            byte[] packet = new byte[MagicHeader.Length + 1 + CurrentPeer.IdRaw.Length];
            Buffer.BlockCopy(MagicHeader, 0, packet, 0, MagicHeader.Length);
            packet[MagicHeader.Length] = (byte)MessageType.Disconnect;
            Buffer.BlockCopy(CurrentPeer.IdRaw, 0, packet, MagicHeader.Length + 1, CurrentPeer.IdRaw.Length);
            return packet;
        }

        public void DisconnectPeer(Guid peerId)
        {
            if (AvailablePeers.TryRemove(peerId, out var peer))
            {
                if (peer.ActiveEndPoint != null)
                {
                    byte[] packet = BuildDisconnectPacket();
                    _ = udp.SendAsync(packet, peer.ActiveEndPoint);
                }

                if (peer.CertHash != null)
                {
                    var keysToCancel = ActiveInterrogations
                        .Where(kv => kv.Value.Peer.CertHash != null && kv.Value.Peer.CertHash.SequenceEqual(peer.CertHash))
                        .Select(kv => kv.Key).ToList();
                    foreach (var k in keysToCancel) CancelInterrogation(k);
                }

                WriteLine($"Disconnected peer {peer.Name} ({peerId})");
                OnPeerDisconnected?.Invoke(peer);
            }
        }

        private readonly ConcurrentDictionary<ushort, Channel<(Guid Peer, byte[] Payload)>> _packetChannels = new();
        public ChannelReader<(Guid Peer, byte[] Payload)> GetPacketReader(ushort packetType)
        {
            return _packetChannels.GetOrAdd(packetType, _ =>
                Channel.CreateBounded<(Guid, byte[])>(256)).Reader;
        }

        internal void PublishReceivedData(Guid peerId, ushort packetType, byte[] payload)
        {
            if (_packetChannels.TryGetValue(packetType, out var channel))
            {
                channel.Writer.TryWrite((peerId, payload));
            }
        }

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


        public event Action<PeerInfo>? OnPeerAvailable;
        internal void RaisePeerAvailable(PeerInfo peerInfo)
        {
            OnPeerAvailable?.Invoke(peerInfo);
        }

        //TODO: add retries :smile:
        private async Task<HandshakeDecision> NegotiateConnection(Guid protocolHandler, PeerInfo peer, ushort localPort, CancellationTokenSource mainCts)
        {
            byte[] payload;

            var connectionGuid = Guid.NewGuid();

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(MagicHeader);
                w.Write((byte)MessageType.Handshake);
                w.Write(CurrentPeer.IdRaw);
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
            ConfigureUdpSocket(nudp);
            nudp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            nudp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));

            var decision = await NegotiateConnection(protocolHandler, peer, localPort, mainCts);

            return await QuicPunchConnection.OpenPortCore(nudp, peer, (ushort)decision.Port, mainCts.Token);
        }
        public async Task InitQuicConnection(Guid protocolHandler, PeerInfo peer, ushort localPort, CancellationTokenSource mainCts)
        {
            if (!ProtocolHandlers.TryGetValue(protocolHandler, out var handler))
            {
                throw new KeyNotFoundException("Handler not found for protocol: " + nameof(protocolHandler));
            }

            var nudp = new UdpClient();
            ConfigureUdpSocket(nudp);
            nudp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            nudp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));

            var decision = await NegotiateConnection(protocolHandler, peer, localPort, mainCts);

            var connection = await QuicPunchConnection.InitQuicConnectionCore(CurrentPeer, nudp, peer, (ushort)decision.Port, CertManager.PeerCertificate!, handler.CompressionOptions, mainCts.Token);

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

        public class ActiveInterrogationSession
        {
            public string Id { get; } = Guid.NewGuid().ToString();
            public PeerInfo Peer { get; }
            public DateTime StartTime { get; } = DateTime.Now;
            public CancellationTokenSource Cts { get; }

            public ActiveInterrogationSession(PeerInfo peer, CancellationTokenSource cts)
            {
                Peer = peer;
                Cts = cts;
            }
        }

        public ConcurrentDictionary<string, ActiveInterrogationSession> ActiveInterrogations { get; } = new();

        public bool CancelInterrogation(string id)
        {
            if (ActiveInterrogations.TryRemove(id, out var session))
            {
                try { session.Cts.Cancel(); } catch { }
                return true;
            }
            return false;
        }

        public async Task PeerInterrogation(string token, CancellationTokenSource mainCts)
        {
            var p = Helpers.DecodeEndpointToken(token);
            ExpectedPeerCerts.Add(p.CertHash);
            await PeerInterrogation(p, mainCts);
        }

        public async Task PeerInterrogation(PeerInfo peer, CancellationTokenSource cts)
        {
            if (peer.CertHash != null && CurrentPeer.CertHash != null && peer.CertHash.SequenceEqual(CurrentPeer.CertHash))
            {
                return;
            }

            if (peer.CertHash != null)
            {
                ExpectedPeerCerts.Add(peer.CertHash);
            }

            if (cts == null)
                cts = new CancellationTokenSource();

            var lcts = CancellationTokenSource.CreateLinkedTokenSource(cts!.Token);
            var session = new ActiveInterrogationSession(peer, lcts);

            var existingKeys = ActiveInterrogations
                .Where(kv => kv.Value.Peer.CertHash != null && peer.CertHash != null && kv.Value.Peer.CertHash.SequenceEqual(peer.CertHash))
                .Select(kv => kv.Key).ToList();
            foreach (var k in existingKeys)
            {
                CancelInterrogation(k);
            }

            ActiveInterrogations[session.Id] = session;

            WriteLine($"Starting interogation for {string.Join(", ",peer.Addresses)}...");

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendLoopAsync(udp!, peer, lcts.Token);
                }
                finally
                {
                    ActiveInterrogations.TryRemove(session.Id, out _);
                    lcts.Dispose();
                }
            });
        }
        
        private async Task ReceiveLoopAsync()
        {
            while (!CancellationSource.IsCancellationRequested)
            {
                try
                {
                skipPacket:

                    var result = await udp.ReceiveAsync(CancellationSource.Token);

                    if (result.RemoteEndPoint.Address == IPAddress.Parse("79.116.202.89"))
                    {
                        Console.Write("omg");
                    }    

                    if (_StunClient.TryProcessIncoming(result.Buffer, result.RemoteEndPoint))
                    {
                        continue;
                    }

                    if (!_rateLimiter.IsAllowed(Helpers.IpToUint(result.RemoteEndPoint.Address)))
                        goto skipPacket;

                    //Console.WriteLine("Recived: " + Encoding.UTF8.GetString(result.Buffer));
                    //if (result.Buffer.Length > 1464 || result.Buffer.Length < MagicHeader.Length + (128 / 8))
                    //    goto skipPacket;

                    for (int i = 0; i < MagicHeader.Length; i++)
                    {
                        if (result.Buffer[i] != MagicHeader[i])
                            goto skipPacket;
                    }

                    using (MemoryStream ms = new MemoryStream(result.Buffer))
                    using (BinaryReader r = new BinaryReader(ms))
                    {
                        ms.Position = MagicHeader.Length;
                        byte messageType = r.ReadByte();

                        switch (messageType)
                        {
                            case (byte)MessageType.Interrogation:
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

                            case (byte)MessageType.Disconnect:
                                DisconnectHandler.HandleDisconnect(this, r, udp, result);
                                continue;

                            case (byte)MessageType.Data:
                                var span = result.Buffer.AsSpan();

                                const int NonceSize = 12;
                                const int TagSize = 16; 

                                int minimumLength = MagicHeader.Length + sizeof(byte) + sizeof(ushort) + CurrentPeer.IdRaw.Length + NonceSize + TagSize;

                                if (span.Length < minimumLength)
                                {
                                    continue;
                                }

                                int offset = 0;

                                offset += MagicHeader.Length; //Ignore header
                                offset++; //Ignore message type
                                
                                ushort packetType = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, sizeof(ushort)));
                                offset += sizeof(ushort);

                                ReadOnlySpan<byte> senderId = span.Slice(offset, CurrentPeer.IdRaw.Length);
                                
                                offset += CurrentPeer.IdRaw.Length;

                                ReadOnlySpan<byte> nonce = span.Slice(offset, NonceSize);
                                offset += NonceSize;

                                ReadOnlySpan<byte> tag = span.Slice(offset, TagSize);
                                offset += TagSize;

                                ReadOnlySpan<byte> ciphertext = span[offset..];

                                var plaintext = new byte[ciphertext.Length];

                                var peerId = new Guid(senderId);
                                if (!this.AvailablePeers.TryGetValue(peerId, out var peer) || peer.PeerCipher == null)
                                {
                                    continue;
                                }

                                try
                                {
                                    peer.PeerCipher.Decrypt(nonce, ciphertext, tag, plaintext);
                                    PublishReceivedData(peerId, packetType, plaintext);
                                }
                                catch (CryptographicException)
                                {
                                    plaintext = Array.Empty<byte>();
                                    continue;
                                }
                                continue;

                            default:
                                WriteLine($"Received unknown message type {(char)messageType} from {result.RemoteEndPoint}");
                                continue;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    if (CancellationSource.IsCancellationRequested)
                        break;
                }
                catch (SocketException sex)
                {
                    if (CancellationSource.IsCancellationRequested || sex.SocketErrorCode == SocketError.OperationAborted)
                        break;
                }
                catch (Exception ex)
                {
                    WriteLine($"Error in ReceiveLoopAsync: {ex.Message}");
                    if (CancellationSource.IsCancellationRequested)
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
                w.Write(CurrentPeer.IdRaw);

                var peersCopy = AvailablePeers.ToArray();

                w.Write(sharePeers ? (ushort)peersCopy.Length : (ushort)0);

                if (sharePeers)
                {
                    foreach (var peer in peersCopy.Select(p => p.Value))
                    {
                        PackedFlags pf = new PackedFlags()
                        {
                            NetworkType = peer.NetworkType
                        };

                        w.Write((byte)pf.RawValue);

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
                
                PackedFlags pf = new PackedFlags()
                {
                    NetworkType = CurrentPeer.NetworkType
                };
                w.Write((byte)pf.RawValue);

                var addresses = CurrentPeer.Addresses ?? Array.Empty<IPAddress>();
                w.Write((byte)addresses.Length);
                foreach (var address in addresses)
                {
                    w.Write(address.GetAddressBytes());
                }
                
                ushort minPort = CurrentPeer.MinPort > 0 ? (ushort)CurrentPeer.MinPort : (ushort)LocalDiscoveryPort;
                ushort maxPort = CurrentPeer.MaxPort > 0 ? (ushort)CurrentPeer.MaxPort : (ushort)LocalDiscoveryPort;

                w.Write(minPort);
                w.Write(maxPort);

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
                    var nonce = RandomNumberGenerator.GetBytes(24);
                    w.Write(nonce);

                    var pop = HMACSHA3_256.HashData(Helpers.Combine(BitConverter.GetBytes(ticks), nonce), PasswordHash);

                    w.Write(pop);
                }

                payload = ms.ToArray();

                var signature = CertManager.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Resize(ref payload, payload.Length + signature.Length);
                Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
            }

            return payload;
        }

        internal byte[] BuildPingPacket(long timestamp, bool isResponse = false)
        {
            int size = MagicHeader.Length + 1 + 1 + 16 + 8;
            byte[] packet = new byte[size];

            Buffer.BlockCopy(MagicHeader, 0, packet, 0, MagicHeader.Length);
            packet[MagicHeader.Length] = (byte)MessageType.Ping;
            packet[MagicHeader.Length + 1] = (byte)(isResponse ? 1 : 0);
            Buffer.BlockCopy(CurrentPeer.IdRaw, 0, packet, MagicHeader.Length + 2, 16);
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(MagicHeader.Length + 2 + 16, 8), timestamp);

            return packet;
        }
        private async Task SendLoopAsync(UdpClient udp, PeerInfo peer, CancellationToken token)
        {
            int tries = 0;

            bool includePassword = PasswordHash != null;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool peerResponded = AvailablePeers.TryGetValue(peer.Id, out PeerInfo availablePeer);

                    // Generate fresh payload each time (password proof includes timestamp/nonce)
                    var helloPayload = GenerateHelloPayload(MessageType.Hello, includePassword);

                    if (peerResponded)
                    {
                        await udp.SendAsync(helloPayload, availablePeer.ActiveEndPoint);
                    }
                    else
                    {
                        var payload = GenerateHelloPayload(MessageType.Interrogation, true);
                        await udp.BigSendAsync(payload, peer);
                    }

                    tries++;

                    // Fast burst for first 5 attempts (50ms apart = 250ms total burst),
                    // then linear backoff: 1s, 2s, 3s... capped at 20s
                    int delayMs;
                    if (tries <= 5)
                    {
                        delayMs = 50;
                    }
                    else
                    {
                        delayMs = Math.Min((tries - 5) * 1000, 20000);
                    }

                    await Task.Delay(delayMs, token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                    await Task.Delay(250, token);
                }
            }
        }

        public async ValueTask SendPayloadAsync(PeerInfo peer, ushort packetType, ReadOnlyMemory<byte> payload)
        {
            if (peer.PeerCipher == null)
                throw new InvalidOperationException("Peer cipher is not initialized.");

            const int NonceSize = 12;
            const int TagSize = 16;

            int packetLength = MagicHeader.Length + sizeof(byte)  + sizeof(ushort) + CurrentPeer.IdRaw.Length + NonceSize + TagSize + payload.Length;

            byte[] packet = ArrayPool<byte>.Shared.Rent(packetLength);
            try
            {
                Span<byte> span = packet.AsSpan(0, packetLength);
                int offset = 0;

                MagicHeader.CopyTo(span[offset..]);
                offset += MagicHeader.Length;

                span[offset++] = (byte)MessageType.Data;

                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, sizeof(ushort)), packetType);
                offset += sizeof(ushort);

                CurrentPeer.IdRaw.CopyTo(span[offset..]);
                offset += 16;

                Span<byte> nonce = span.Slice(offset, NonceSize);
                offset += NonceSize;

                Span<byte> tag = span.Slice(offset, TagSize);
                offset += TagSize;

                Span<byte> ciphertext = span.Slice(offset);

                RandomNumberGenerator.Fill(nonce);

                peer.PeerCipher.Encrypt(nonce, payload.Span, ciphertext, tag);

                await udp.BigSendAsync(packet.AsMemory(0, packetLength), peer)
                         .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }
        public string GetToken() => 
            Helpers.EncodeEndpointToken(CurrentPeer);

        public void Dispose()
        {
            CancellationSource?.Cancel();
            try { CancellationSource?.Dispose(); } catch { }
            try { TrackerScanner?.Dispose(); } catch { }
            try { udp?.Dispose(); } catch { }
        }
        
        public enum NetworkType : byte
        {
            Unknown = 255,
            Static  = 0,
            DynamicPort = 1,
            DynamicAddress = 2,
            DynamicPortAndAddress = 3
        }
    }
}
