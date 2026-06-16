using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QuicPunch
{
    internal class QuicConection
    {
        //TODO: also implement stun to retrieve external port?
        public static async Task<(bool Sucess, UdpClient client, IPEndPoint remoteEndpoint)> OpenPortCore(
            UdpClient nudp, PeerInfo remotePeer, ushort peerPort, CancellationToken mainCt)
        {
            try
            {
                var punchSuccessful = new TaskCompletionSource<bool>();
                using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt);
                _ = SendLoopAsync(nudp, remotePeer, peerPort, punchCts.Token);

                var remoteEndpoint = await ReceiveHoleLoopAsync(nudp, punchSuccessful, punchCts.Token);
                await punchSuccessful.Task.WaitAsync(punchCts.Token);
                punchCts.Cancel();
                return (true, nudp, remoteEndpoint);
            }
            catch (OperationCanceledException)
            {
                return (false, null, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OpenPortCore: {ex.Message}");
                return (false, null, null);
            }
        }

        public static async Task<(QuicConnection Conection, Stream Stream)> InitQuicConnectionCore(
            IPEndPoint ownPublicEndpoint, UdpClient nudp, PeerInfo remotePeer, ushort peerPort,
            X509Certificate2 ownCertificate, ZstandardCompressionOptions? compressionOptions, CancellationToken mainCt)
        {
            using var openPortCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var openPortLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt, openPortCts.Token);

            var udpResult = await OpenPortCore(nudp, remotePeer, peerPort, openPortLinkedCts.Token)
                .WaitAsync(openPortLinkedCts.Token);

            if (!udpResult.Sucess)
            {
                return (null, null);
            }

            //IPEndPoint remoteNewEndpoint = new IPEndPoint(remotePeer.EndPoint.Address, peerPort);

            var localPort = ((IPEndPoint)nudp.Client.LocalEndPoint!).Port;
            nudp.Dispose();
            openPortLinkedCts.Dispose();

            bool isServer = AmIServer(ownPublicEndpoint.Address, ownPublicEndpoint.Port, remotePeer.Addresses[0],
                remotePeer.MinPort);

            QuicConnection connection = null;
            QuicStream stream = null;

            await Task.Delay(500);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                await PreciseTime.StartSyncedLoggerAsync(500);

                Console.WriteLine($"\n--- ATTEMPT {attempt}/2: Acting as {(isServer ? "SERVER" : "CLIENT")} ---");

                using var attemptCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt, attemptCts.Token);

                try
                {
                    if (isServer)
                    {
                        (connection, stream) = await TryRunServer(localPort, ownCertificate, remotePeer.CertHash,
                            linkedCts.Token);
                    }
                    else
                    {
                        (connection, stream) = await TryRunClient(udpResult.remoteEndpoint, ownCertificate,
                            remotePeer.CertHash, localPort, linkedCts.Token);
                    }

                    if (connection != null && stream != null)
                    {
                        Console.WriteLine("\n[SUCCESS] QUIC Connection Established!");
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[QUIC] {(isServer ? "Listening" : "Connecting")} timed out.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[QUIC] Failure: {ex.ToString()}");
                }

                if (connection == null)
                {
                    Console.WriteLine("[!] Role failed. Waiting to swap roles...");
                    try
                    {
                        await PreciseTime.StartSyncedLoggerAsync(10 * 1000);
                    }
                    catch
                    {
                    }

                    isServer = !isServer;
                }
            }

            if (connection == null || stream == null)
            {
                if (connection != null)
                    await connection.DisposeAsync();

                if (stream != null)
                    await stream.DisposeAsync();

                Console.WriteLine("\n[FAILED] Both roles failed to connect.");
                return (null, null);
            }

            if (compressionOptions != null)
            {
                return (connection, new CompressedTransparentStream(stream, compressionOptions));
            }

            return (connection, stream);
        }

        public static async Task<IPEndPoint> ReceiveHoleLoopAsync(UdpClient udp, TaskCompletionSource<bool> tcs,
            CancellationToken token)
        {
            var ACKPacket = Helpers.Combine(QuicPunch.MagicHeader, [(byte)QuicPunchStructures.MessageType.Ack]);

            while (!token.IsCancellationRequested && !tcs.Task.IsCompleted)
            {
                try
                {

                    skipPacket:

                    var result = await udp.ReceiveAsync(token);

                    Console.WriteLine("Recived: " + Encoding.UTF8.GetString(result.Buffer));

                    //if (!result.RemoteEndPoint.Address.Equals(targetPeer.Address))
                    //    continue;

                    if (result.Buffer.Length > 512 || result.Buffer.Length < QuicPunch.MagicHeader.Length)
                        goto skipPacket;

                    for (int i = 0; i < QuicPunch.MagicHeader.Length; i++)
                    {
                        if (result.Buffer[i] != QuicPunch.MagicHeader[i])
                            goto skipPacket;
                    }

                    using (MemoryStream ms = new MemoryStream(result.Buffer))
                    using (BinaryReader r = new BinaryReader(ms))
                    {
                        _ = r.ReadBytes(QuicPunch.MagicHeader.Length);

                        var messageType = (QuicPunchStructures.MessageType)r.ReadByte();

                        if (messageType == QuicPunchStructures.MessageType.FinalHandshake)
                        {
                            udp.Send(ACKPacket, result.RemoteEndPoint);
                        }
                        else if (messageType == QuicPunchStructures.MessageType.Ack)
                        {
                            for (int i = 0; i < 3; i++)
                            {
                                udp.Send(ACKPacket, result.RemoteEndPoint);
                                await Task.Delay(150);
                            }

                            return result.RemoteEndPoint;
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted
                                                     or SocketError.Interrupted)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in ReceiveLoopAsync: {ex.Message}");
                    if (token.IsCancellationRequested || tcs.Task.IsCompleted)
                        break;
                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            
                return null;
        }
    

    public static async Task SendLoopAsync(UdpClient udp, PeerInfo peer,ushort askedPort, CancellationToken token)
        {
            byte[] payload;

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(QuicPunch.MagicHeader);
                w.Write((byte)QuicPunchStructures.MessageType.FinalHandshake);
                payload = ms.ToArray();
            }

            long intervalTicks = TimeSpan.FromMilliseconds(QuicPunch.PunchIntervalMiliseconds).Ticks;
            int tries = 0;

            while (tries < 32)
            {
                tries++;

                DateTime now = PreciseTime.GetCorrectTime();
                long nextTicks = now.Ticks - (now.Ticks % intervalTicks) + intervalTicks;
                DateTime nextBoundary = new DateTime(nextTicks, DateTimeKind.Utc);
                TimeSpan delay = nextBoundary - PreciseTime.GetCorrectTime();

                if (delay.TotalMilliseconds > 1)
                {
                    await Task.Delay((int)delay.TotalMilliseconds, token);
                }

                Console.WriteLine($"Send hello packet to {peer} at {PreciseTime.GetCorrectTime():HH:mm:ss.fff} time til next {TimeSpan.FromTicks(intervalTicks).Seconds}");

                for (int i = 0; i < 2; i++)
                {
                    if (token.IsCancellationRequested)
                        break;

                    if (peer.NetworkType == QuicPunch.NetworkType.Static || peer.NetworkType == QuicPunch.NetworkType.DynamicAddress)
                    {
                        foreach (var address in peer.Addresses)
                        {                        
                            await udp.SendAsync(payload, new IPEndPoint(address, askedPort));
                        }
                    }
                    await udp.BigSendAsync(payload, peer);
                    await Task.Delay(250, token);
                }

                tries++;
            }
        }

        public static readonly List<SslApplicationProtocol> SupportedProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("quic-punch") };
        public static async Task<(QuicConnection, QuicStream)> TryRunServer(int localPort, X509Certificate2 ownCertificate, byte[] peerCertificate, CancellationToken token)
        {
            var options = new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                ApplicationProtocols = SupportedProtocols,
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ApplicationProtocols = SupportedProtocols,
                        ServerCertificate = ownCertificate,
                        ClientCertificateRequired = true,

                          RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                          {
                              if (certificate == null)
                                  return false;

                              byte[] clientPublicKey = certificate.GetPublicKey();
                              byte[] clientHash = SHA3_384.HashData(clientPublicKey);

                              var valid = clientHash.SequenceEqual(peerCertificate);

                              Console.WriteLine("Client cert hash: " + Convert.ToHexString(clientHash) + " valid: " + valid);

                              return valid;
                          }
                    },

                    MaxInboundBidirectionalStreams = 512,
                    MaxInboundUnidirectionalStreams = 512,
                    IdleTimeout = TimeSpan.FromMinutes(10),
                    KeepAliveInterval = TimeSpan.FromSeconds(19)
                }),
            };

            await using var listener = await QuicListener.ListenAsync(options, token);
            Console.WriteLine("[SERVER] Bound to port. Waiting for peer...");

            var connection = await listener.AcceptConnectionAsync(token);

            try
            {
                var stream = await connection.AcceptInboundStreamAsync(token);

                return (connection, stream);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public static async Task<(QuicConnection, QuicStream)> TryRunClient(IPEndPoint targetPeer, X509Certificate2 ownCertificate, byte[] peerCertificate, int localPort, CancellationToken token)
        {
            var options = new QuicClientConnectionOptions
            {
                RemoteEndPoint = targetPeer,
                LocalEndPoint = new IPEndPoint(IPAddress.Any, localPort), // CRITICAL: Bind to the punched port
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = targetPeer.Address.ToString(),
                    ApplicationProtocols = SupportedProtocols,
                    ClientCertificates = new X509Certificate2Collection(ownCertificate),

                    RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        if (certificate == null)
                            return false;

                        byte[] serverPublicKey = certificate.GetPublicKey();
                        byte[] serverHash = SHA3_384.HashData(serverPublicKey);

                        var valid = serverHash.SequenceEqual(peerCertificate);

                        Console.WriteLine("Server cert hash: " + Convert.ToHexString(serverHash) + " valid: " + valid);

                        return valid;
                    }
                },

                MaxInboundBidirectionalStreams = 512,
                MaxInboundUnidirectionalStreams = 512,
                IdleTimeout = TimeSpan.FromMinutes(10),
                KeepAliveInterval = TimeSpan.FromSeconds(19),

                HandshakeTimeout = TimeSpan.FromSeconds(9),
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

        public static bool AmIServer(IPAddress myPublicIp, int myPort, IPAddress peerPublicIp, int peerPort)
        {
            byte[] m = myPublicIp.GetAddressBytes(), p = peerPublicIp.GetAddressBytes();
            for (int i = 0; i < m.Length; i++)
            {
                if (m[i] > p[i]) return true;
                if (m[i] < p[i]) return false;
            }
            return myPort > peerPort;
        }
    }
}
