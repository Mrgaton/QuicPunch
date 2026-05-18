using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using UdpPunchHoleTest;

namespace QuicPunch
{
    public class QuicConectionCore
    {
        public static async Task<(QuicConnection,Stream)> InitConnectionCore(ushort localPort,IPEndPoint remoteEndpoint, CancellationTokenSource mainCts)
        {
            var nudp = new UdpClient();

            if (OperatingSystem.IsWindows())
                nudp.Client.IOControl(-1744830452, [0], null);

            nudp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            nudp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));

            var punchSuccessful = new TaskCompletionSource<bool>();
            using var udpCts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token);

            _ = SendLoopAsync(nudp, remoteEndpoint, udpCts.Token);

            await ReceiveHoleLoopAsync(nudp, localPort, punchSuccessful, udpCts.Token);
            await punchSuccessful.Task.WaitAsync(mainCts.Token);

            udpCts.Dispose();
            nudp.Dispose();

            bool isServer = !AmIServer(QuicPunchCore.IPv4Address, localPort, remoteEndpoint.Address, remoteEndpoint.Port);

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
                        (connection, stream) = await TryRunServer(localPort, linkedCts.Token);
                    }
                    else
                    {
                        (connection, stream) = await TryRunClient(remoteEndpoint, localPort, linkedCts.Token);
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
            }

            if (connection != null)
                await connection.DisposeAsync();

            if (stream != null)
                await stream.DisposeAsync();

            return (connection, new BrotliTransparentStream(stream));
        }
        public static async Task ReceiveHoleLoopAsync(UdpClient udp, ushort port, TaskCompletionSource<bool> tcs, CancellationToken token)
        {
            var ACKPacket = Helpers.Combine(QuicPunchCore.MagicHeader, [(byte)QuicPunchCore.MessageType.ACK]);

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
                            udp.SendAsync(ACKPacket, ACKPacket.Length, result.RemoteEndPoint);
                        }
                        else if (messageType == QuicPunchCore.MessageType.ACK)
                        {
                            await Task.Delay(250);
                            udp.SendAsync(ACKPacket, ACKPacket.Length, result.RemoteEndPoint);
                            tcs.SetResult(true);
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
        public static async Task<(QuicConnection, QuicStream)> TryRunServer(int localPort, CancellationToken token)
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

        public static async Task<(QuicConnection, QuicStream)> TryRunClient(IPEndPoint targetPeer, int localPort, CancellationToken token)
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
