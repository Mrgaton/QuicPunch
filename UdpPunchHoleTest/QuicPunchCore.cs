
using QuicPunch;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace UdpPunchHoleTest
{
    internal class QuicPunchCore
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

        private const int PunchIntervalMiliseconds = 2500;

        private static byte[] MagicHeader = Encoding.UTF8.GetBytes("PuNcH");

        private enum MessageType : byte
        {
            Hello = (byte)('H'),
            ACK = (byte)('K'),
            Handshake = (byte)('S')
        }

        public static Dictionary<IPEndPoint, PeerInfo> AvilablePeers = new Dictionary<IPEndPoint, PeerInfo>();

        public static event Action<PeerInfo>? OnPeerAvilable;
        public static async Task<(QuicConnection, Stream)> InitPeerConection(PeerInfo peer, CancellationTokenSource mainCts)
        {
            using var udp = new UdpClient(LocalPort);

            throw new NotImplementedException();
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

            if (OperatingSystem.IsWindows())
                udp.Client.IOControl(-1744830452, [ 0 ], null);

            Console.WriteLine($"Starting interogation for {endpoint}...");

            var sendTask = SendLoopAsync(udp, endpoint, udpCts.Token);

            await punchSuccessful.Task.WaitAsync(mainCts.Token);
            
            udpCts.Cancel();
            udp.Dispose();

            /*await Task.Delay(100);

            Console.WriteLine("\n[--- UDP HOLE PUNCH COMPLETE ---]");
            Console.WriteLine("Transitioning to QUIC...\n");

            if (IPv4Address == null)
                await GetPublicIP();

            bool isServer = !AmIServer(IPv4Address, LocalPort, endpoint.Address, endpoint.Port);

            QuicConnection connection = null;
            QuicStream stream = null;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Console.WriteLine($"\n--- ATTEMPT {attempt}/2: Acting as {(isServer ? "SERVER" : "CLIENT")} ---");

                using var attemptCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token, attemptCts.Token);

                try
                {
                    if (isServer)
                    {
                        (connection, stream) = await TryRunServer(LocalPort, linkedCts.Token);
                    }
                    else
                    {
                        (connection, stream) = await TryRunClient(endpoint, LocalPort, linkedCts.Token);
                    }

                    if (connection != null && stream != null)
                    {
                        Con+sole.WriteLine("\n[SUCCESS] QUIC Connection Established!");
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[QUIC] {(isServer ? "Listening" : "Connecting")} timed out.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[QUIC] Failure: {ex.Message}");
                }

                // Sync up both sides: wait until the 8-second window is entirely over before starting Attempt 2
                if (connection == null)
                {
                    Console.WriteLine("[!] Role failed. Waiting to swap roles...");
                    try
                    {
                        await Task.Delay(-1, attemptCts.Token);
                    }
                    catch { }

                    isServer = !isServer; // SWAP!
                }
            }

            if (connection == null || stream == null)
            {
                Console.WriteLine("\n[FAILED] Both roles failed to connect. A strict firewall is blocking both ends.");

                await connection!.DisposeAsync();
                await stream!.DisposeAsync();

                ConnectToPeer(endpoint, mainCts);
            }

            return (connection, new BrotliTransparentStream(stream));*/
        }
        private static async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
        {
            var ACKPacket = Helpers.Combine(MagicHeader, [(byte)MessageType.ACK ]);

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
                                var certHash = r.ReadBytes(Program.CurrentPeer.CertHash.Length);
                                var curvePuiblicKey = r.ReadBytes(Program.CurrentPeer.CurvePublicKey.Length);
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
                                break;
                            case (byte)MessageType.ACK:
                                Console.WriteLine($"Received ACK from {result.RemoteEndPoint}");
                                continue; // Don't add ACK senders to peer list
                            default:
                                Console.WriteLine($"Received unknown message type {(char)messageType} from {result.RemoteEndPoint}");
                                continue; // Ignore unknown message types
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
                w.Write(Program.CurrentPeer.CertHash);
                w.Write(Program.CurrentPeer.CurvePublicKey);
                w.Write(Program.CurrentPeer.Name);

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

                Console.WriteLine($"Send hello packet to {peer} at {PreciseTime.GetCorrectTime():HH:mm:ss.fff} time til next {TimeSpan.FromTicks(intervalTicks).Seconds}");

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

        private static async Task<(QuicConnection, QuicStream)> TryRunServer(int localPort, CancellationToken token)
        {
            var options = new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("quic-p2p") },
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("quic-p2p") },
                        ServerCertificate = CertManager.PeerCertificate
                    },

                    IdleTimeout = TimeSpan.FromMinutes(10),
                    KeepAliveInterval = TimeSpan.FromSeconds(15)
                }),
            };

            await using var listener = await QuicListener.ListenAsync(options, token);
            Console.WriteLine("[SERVER] Bound to port. Waiting for peer...");

            var connection = await listener.AcceptConnectionAsync(token);
            var stream = await connection.AcceptInboundStreamAsync(token);

            return (connection, stream);
        }

        private static async Task<(QuicConnection, QuicStream)> TryRunClient(IPEndPoint targetPeer, int localPort, CancellationToken token)
        {
            var options = new QuicClientConnectionOptions
            {
                RemoteEndPoint = targetPeer,
                LocalEndPoint = new IPEndPoint(IPAddress.Any, localPort), // CRITICAL: Bind to the punched port
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("quic-p2p") },
                    RemoteCertificateValidationCallback = delegate { return true; } // Accept self-signed
                },

                IdleTimeout = TimeSpan.FromMinutes(10),
                KeepAliveInterval = TimeSpan.FromSeconds(15)
            };

            QuicConnection connection = null;

            // Keep trying to punch outbound until the 8 second token cancels
            while (!token.IsCancellationRequested)
            {
                try
                {
                    connection = await QuicConnection.ConnectAsync(options, token);
                    Console.WriteLine("[CLIENT] Connected successfully!");
                    break;
                }
                catch
                {
                    await Task.Delay(500, token); // Small backoff, then try again
                }
            }

            if (connection == null)
                return (null, null);

            var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);

            //await stream.WriteAsync(new byte[] { 0x00 }, token); // Trigger server's AcceptInboundStreamAsync

            return (connection, stream);
        }
      
        private static bool AmIServer(IPAddress myPublicIp, int myPort, IPAddress peerPublicIp, int peerPort)
        {
            byte[] m = myPublicIp.GetAddressBytes(), p = peerPublicIp.GetAddressBytes();
            for (int i = 0; i < m.Length; i++)
            { 
                if (m[i] > p[i]) return true;
                if (m[i] < p[i]) return false;
            }
            return myPort > peerPort;
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
