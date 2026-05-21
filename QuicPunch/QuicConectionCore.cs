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
    internal class QuicConectionCore
    {
        //TODO: also implement stun to retrieve external port?
        public static async Task<(bool Sucess, UdpClient client)> OpenPortCore(ushort localPort, PeerInfo remotePeer, ushort peerPort, CancellationToken mainCt)
        {
            try
            {
                var nudp = new UdpClient();

                IPEndPoint remotePeerNewPort = new IPEndPoint(remotePeer.EndPoint.Address, peerPort);

                if (OperatingSystem.IsWindows())
                    nudp.Client.IOControl(-1744830452, [0], null);

                nudp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                nudp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));

                var punchSuccessful = new TaskCompletionSource<bool>();

                _ = SendLoopAsync(nudp, remotePeerNewPort, mainCt);

                await ReceiveHoleLoopAsync(nudp, localPort, punchSuccessful, mainCt);
                await punchSuccessful.Task.WaitAsync(mainCt);

                return (true, nudp);
            }
            catch (OperationCanceledException)
            {
                return (false, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OpenPortCore: {ex.Message}");
                return (false, null);
            }
        }
        public static async Task<(QuicConnection Conection, Stream Stream)> InitQuicConnectionCore(IPAddress ownPublicEndpoint, ushort localPort, PeerInfo remotePeer, ushort peerPort, X509Certificate2 ownCertificate, bool compression, CancellationToken mainCt)
        {
            var udpResult = await OpenPortCore(localPort, remotePeer, peerPort, mainCt).WaitAsync(mainCt);

            if (!udpResult.Sucess)
            {
                return (null, null);
            }

            udpResult.client.Dispose();  // Free port for use in quic

            IPEndPoint remotePeerNewPort = new IPEndPoint(remotePeer.EndPoint.Address, peerPort);

            bool isServer = AmIServer(ownPublicEndpoint, localPort, remotePeerNewPort.Address, remotePeerNewPort.Port);

            QuicConnection connection = null;
            QuicStream stream = null;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Console.WriteLine($"\n--- ATTEMPT {attempt}/2: Acting as {(isServer ? "SERVER" : "CLIENT")} ---");

                using var attemptCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt, attemptCts.Token);

                try
                {
                    if (isServer)
                    {
                        (connection, stream) = await TryRunServer(localPort, ownCertificate, remotePeer.CertHash ,linkedCts.Token);
                    }
                    else
                    {
                        (connection, stream) = await TryRunClient(remotePeerNewPort, ownCertificate, remotePeer.CertHash, localPort, linkedCts.Token);
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
                    Console.WriteLine($"[QUIC] Failure: {ex.Message}");
                }

                if (connection == null)
                {
                    Console.WriteLine("[!] Role failed. Waiting to swap roles...");
                    try
                    {
                        await Task.Delay(-1, attemptCts.Token);
                    }
                    catch { }

                    isServer = !isServer;
                }
            }

            if (connection == null || stream == null)
            {
                Console.WriteLine("\n[FAILED] Both roles failed to connect.");
                return (null, null);
            }

            if (compression)
            {
                return (connection, new CompressedTransparentStream(stream, new ZstandardCompressionOptions() { AppendChecksum = false, EnableLongDistanceMatching = false, Quality = 11 }));
            }

            return (connection, stream);
        }

        public static async Task ReceiveHoleLoopAsync(UdpClient udp, ushort port, TaskCompletionSource<bool> tcs, CancellationToken token)
        {
            var ACKPacket = Helpers.Combine(QuicPunchCore.MagicHeader, [(byte)QuicPunchCore.MessageType.Ack]);

            while (!token.IsCancellationRequested && !tcs.Task.IsCompleted)
            {
                try
                {

                skipPacket:

                    var result = await udp.ReceiveAsync(token);

                    Console.WriteLine("Recived: " + Encoding.UTF8.GetString(result.Buffer));

                    //if (!result.RemoteEndPoint.Address.Equals(targetPeer.Address))
                    //    continue;

                    if (result.Buffer.Length > 512 || result.Buffer.Length < QuicPunchCore.MagicHeader.Length)
                        goto skipPacket;

                    for (int i = 0; i < QuicPunchCore.MagicHeader.Length; i++)
                    {
                        if (result.Buffer[i] != QuicPunchCore.MagicHeader[i])
                            goto skipPacket;
                    }

                    using (MemoryStream ms = new MemoryStream(result.Buffer))
                    using (BinaryReader r = new BinaryReader(ms))
                    {
                        _ = r.ReadBytes(QuicPunchCore.MagicHeader.Length);

                        var messageType = (QuicPunchCore.MessageType)r.ReadByte();

                        if (messageType == QuicPunchCore.MessageType.FinalHandshake)
                        {
                            udp.Send(ACKPacket, result.RemoteEndPoint);
                        }
                        else if (messageType == QuicPunchCore.MessageType.Ack)
                        {
                            await Task.Delay(250);
                            udp.Send(ACKPacket, result.RemoteEndPoint);
                            tcs.SetResult(true);
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
                    if (token.IsCancellationRequested || tcs.Task.IsCompleted)
                        break;
                    try { await Task.Delay(1000, token); } catch { break; }
                }
            }
        }

        public static async Task SendLoopAsync(UdpClient udp, IPEndPoint peer, CancellationToken token)
        {
            byte[] payload;

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(QuicPunchCore.MagicHeader);
                w.Write((byte)QuicPunchCore.MessageType.FinalHandshake);
                payload = ms.ToArray();
            }

            long intervalTicks = TimeSpan.FromMilliseconds(QuicPunchCore.PunchIntervalMiliseconds).Ticks;
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

                    await udp.SendAsync(payload, payload.Length, peer);
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

                              return CryptographicOperations.FixedTimeEquals(clientHash, peerCertificate);
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
            var stream = await connection.AcceptInboundStreamAsync(token);

            return (connection, stream);
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
                    ApplicationProtocols = SupportedProtocols,
                    ClientCertificates = new X509Certificate2Collection(ownCertificate),

                    RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        if (certificate == null)
                            return false;

                        byte[] serverPublicKey = certificate.GetPublicKey();
                        byte[] serverHash = SHA3_384.HashData(serverPublicKey);

                        return CryptographicOperations.FixedTimeEquals(serverHash, peerCertificate);
                    }
                },

                MaxInboundBidirectionalStreams = 512,
                MaxInboundUnidirectionalStreams = 512,
                IdleTimeout = TimeSpan.FromMinutes(10),
                KeepAliveInterval = TimeSpan.FromSeconds(19)
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
