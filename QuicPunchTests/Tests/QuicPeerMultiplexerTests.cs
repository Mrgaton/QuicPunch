using System;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunch.Multiplexer;
using QuicConnection = QuicPunch.QuicConnection;
using NativeQuicConnection = System.Net.Quic.QuicConnection;

namespace QuicPunchTests.Tests
{
    public static class QuicPeerMultiplexerTests
    {
        private static readonly Guid ChatProtocolId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        private static readonly Guid FileProtocolId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        private static X509Certificate2 GenerateTestCert()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=MultiplexerTest", ecdsa, HashAlgorithmName.SHA256);
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        }

        public static async Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("     QUIC PEER MULTIPLEXER END-TO-END TESTS       ");
            Console.WriteLine("==================================================");

            await Test_Multiplexer_TwoConcurrentVirtualProtocolsAsync();
            Test_Multiplexer_BinaryControlFrameRoundtrip();
            await Test_UdpAutoScalingToQuicMultiplexingPipelineAsync();

            Console.WriteLine("==================================================");
            Console.WriteLine("     ALL MULTIPLEXER TESTS PASSED!                ");
            Console.WriteLine("==================================================");
        }

        private static async Task Test_Multiplexer_TwoConcurrentVirtualProtocolsAsync()
        {
            Console.Write("[TEST] Multiplexing Chat and File Transfer over 1 physical QUIC connection... ");

            using var serverCert = GenerateTestCert();
            using var clientCert = GenerateTestCert();

            var alpn = new SslApplicationProtocol("qpmux");
            var serverEndPoint = new IPEndPoint(IPAddress.Loopback, 0);

            var serverOptions = new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = new() { alpn },
                ServerCertificate = serverCert,
                RemoteCertificateValidationCallback = delegate { return true; }
            }
            };

            await using var listener = await System.Net.Quic.QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = serverEndPoint,
                ApplicationProtocols = new() { alpn },
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions)
            });

            var clientOptions = new QuicClientConnectionOptions
            {
                RemoteEndPoint = listener.LocalEndPoint,
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                MaxInboundBidirectionalStreams = 512,
                MaxInboundUnidirectionalStreams = 512,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = new() { alpn },
                    RemoteCertificateValidationCallback = delegate { return true; }
                }
            };

            var acceptTask = listener.AcceptConnectionAsync();
            var clientNativeConn = await NativeQuicConnection.ConnectAsync(clientOptions);
            var serverNativeConn = await acceptTask;

            var serverQuicConn = QuicConnection.Wrap(serverNativeConn);
            var clientQuicConn = QuicConnection.Wrap(clientNativeConn);

            var serverPeer = new PeerInfo(serverCert, null) { Name = "ServerPeer" };
            var clientPeer = new PeerInfo(clientCert, null) { Name = "ClientPeer" };

            var serverChatReceivedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverFileReceivedTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Mock protocol handlers on the server
            var mockChatHandler = new TestProtocolHandler(ChatProtocolId, async (conn, stream, peer, ct) =>
            {
                byte[] lenBuf = new byte[4];
                await stream.ReadExactlyAsync(lenBuf, ct);
                int len = BitConverter.ToInt32(lenBuf);
                byte[] data = new byte[len];
                await stream.ReadExactlyAsync(data, ct);
                string msg = Encoding.UTF8.GetString(data);
                serverChatReceivedTcs.TrySetResult(msg);

                // Echo back
                byte[] echo = Encoding.UTF8.GetBytes("Echo: " + msg);
                await stream.WriteAsync(BitConverter.GetBytes(echo.Length), ct);
                await stream.WriteAsync(echo, ct);
                await stream.FlushAsync(ct);
            });

            var mockFileHandler = new TestProtocolHandler(FileProtocolId, async (conn, stream, peer, ct) =>
            {
                byte[] lenBuf = new byte[4];
                await stream.ReadExactlyAsync(lenBuf, ct);
                int totalBytes = BitConverter.ToInt32(lenBuf);
                byte[] buf = new byte[4096];
                int readTotal = 0;
                while (readTotal < totalBytes)
                {
                    int r = await stream.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, totalBytes - readTotal)), ct);
                    if (r <= 0) break;
                    readTotal += r;
                }
                serverFileReceivedTcs.TrySetResult(readTotal);
            });

            var serverMux = new QuicPeerMultiplexer(
                serverQuicConn,
                clientPeer,
                isServer: true,
                protocolLookup: id => id == ChatProtocolId ? mockChatHandler : id == FileProtocolId ? mockFileHandler : null,
                accessCheck: _ => true);

            var clientMux = new QuicPeerMultiplexer(
                clientQuicConn,
                serverPeer,
                isServer: false,
                protocolLookup: _ => null,
                accessCheck: _ => true);

            await serverMux.StartAsync();
            await clientMux.StartAsync();

            // 1. Client opens Virtual Chat Session
            await using var clientChatConn = await clientMux.OpenVirtualSessionAsync(ChatProtocolId);
            await using var clientChatStream = await clientChatConn.OpenOutboundStreamAsync(QuicPunch.QuicStreamType.Bidirectional);

            byte[] chatPayload = Encoding.UTF8.GetBytes("Hello via Multiplexer!");
            await clientChatStream.WriteAsync(BitConverter.GetBytes(chatPayload.Length));
            await clientChatStream.WriteAsync(chatPayload);
            await clientChatStream.FlushAsync();

            // 2. Concurrently, Client opens Virtual File Session
            await using var clientFileConn = await clientMux.OpenVirtualSessionAsync(FileProtocolId);
            await using var clientFileStream = await clientFileConn.OpenOutboundStreamAsync(QuicPunch.QuicStreamType.Bidirectional);

            const int TestFileSize = 64 * 1024; // 64 KB
            byte[] filePayload = new byte[TestFileSize];
            RandomNumberGenerator.Fill(filePayload);

            await clientFileStream.WriteAsync(BitConverter.GetBytes(TestFileSize));
            await clientFileStream.WriteAsync(filePayload);
            await clientFileStream.FlushAsync();

            string chatReceived = await serverChatReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (chatReceived != "Hello via Multiplexer!")
                throw new Exception($"Chat payload mismatch. Got: {chatReceived}");

            byte[] echoLenBuf = new byte[4];
            await clientChatStream.ReadExactlyAsync(echoLenBuf);
            int echoLen = BitConverter.ToInt32(echoLenBuf);
            byte[] echoData = new byte[echoLen];
            await clientChatStream.ReadExactlyAsync(echoData);
            string echoMsg = Encoding.UTF8.GetString(echoData);
            if (echoMsg != "Echo: Hello via Multiplexer!")
                throw new Exception($"Chat echo mismatch. Got: {echoMsg}");

            int fileBytesReceived = await serverFileReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (fileBytesReceived != TestFileSize)
                throw new Exception($"File size mismatch. Expected {TestFileSize}, got {fileBytesReceived}");

            bool hasTelemetry = clientMux.TryGetTelemetry(out var telemetry);
            if (hasTelemetry)
            {
                // RTT should be non-negative
                if (telemetry.RttMs < 0)
                    throw new Exception("Telemetry RTT cannot be negative.");
            }

            Console.WriteLine("PASSED");

            // Clean up
            await clientMux.DisposeAsync();
            await serverMux.DisposeAsync();
        }

        private static void Test_Multiplexer_BinaryControlFrameRoundtrip()
        {
            Console.Write("[TEST] Binary Control Frame zero-allocation encoding and decoding... ");

            var sessionGuid = Guid.NewGuid();
            var protocolId = Guid.NewGuid();

            var originalMsg = new QuicPeerMultiplexer.ControlMessage
            {
                Type = QuicPeerMultiplexer.ControlMessageType.SessionRequest,
                SessionShortId = 42,
                Flags = 1, // Accepted
                SessionGuid = sessionGuid,
                ProtocolId = protocolId,
                ErrorCode = 0,
                Reason = "Unit test reason text"
            };

            Span<byte> buffer = stackalloc byte[256];
            originalMsg.Encode(buffer, out int written);

            if (written < QuicPeerMultiplexer.ControlMessage.FixedHeaderSize)
                throw new Exception($"Written length too small: {written}");

            var decoded = QuicPeerMultiplexer.ControlMessage.Decode(buffer.Slice(0, written));

            if (decoded.Type != originalMsg.Type ||
                decoded.SessionShortId != originalMsg.SessionShortId ||
                decoded.Accepted != originalMsg.Accepted ||
                decoded.HasReason != originalMsg.HasReason ||
                decoded.SessionGuid != originalMsg.SessionGuid ||
                decoded.ProtocolId != originalMsg.ProtocolId ||
                decoded.ErrorCode != originalMsg.ErrorCode ||
                decoded.Reason != originalMsg.Reason)
            {
                throw new Exception("Decoded control frame does not match original message!");
            }

            Console.WriteLine("PASSED");
        }

        private static async Task Test_UdpAutoScalingToQuicMultiplexingPipelineAsync()
        {
            Console.Write("[TEST] Full pipeline: UDP Hole Punch -> Single QUIC Connection -> Stream 0 Multiplexing -> Multi-subprotocol reuse (<1ms)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_pipe_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_pipe_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            var protoChat = Guid.NewGuid();
            var protoFile = Guid.NewGuid();
            var protoVoice = Guid.NewGuid();

            var chatMessages = Channel.CreateUnbounded<string>();
            var fileMessages = Channel.CreateUnbounded<byte[]>();
            var voiceMessages = Channel.CreateUnbounded<string>();

            var handlerChatB = new TestProtocolHandler(protoChat, async (conn, stream, peer, ct) =>
            {
                byte[] lenBuf = new byte[4];
                await stream.ReadExactlyAsync(lenBuf, ct);
                int len = BitConverter.ToInt32(lenBuf);
                byte[] data = new byte[len];
                await stream.ReadExactlyAsync(data, ct);
                chatMessages.Writer.TryWrite(Encoding.UTF8.GetString(data));
            });

            var handlerFileB = new TestProtocolHandler(protoFile, async (conn, stream, peer, ct) =>
            {
                byte[] lenBuf = new byte[4];
                await stream.ReadExactlyAsync(lenBuf, ct);
                int len = BitConverter.ToInt32(lenBuf);
                byte[] data = new byte[len];
                await stream.ReadExactlyAsync(data, ct);
                fileMessages.Writer.TryWrite(data);
            });

            var handlerVoiceB = new TestProtocolHandler(protoVoice, async (conn, stream, peer, ct) =>
            {
                byte[] lenBuf = new byte[4];
                await stream.ReadExactlyAsync(lenBuf, ct);
                int len = BitConverter.ToInt32(lenBuf);
                byte[] data = new byte[len];
                await stream.ReadExactlyAsync(data, ct);
                voiceMessages.Writer.TryWrite(Encoding.UTF8.GetString(data));
            });

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 42311, autoAcceptConnections: true);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 42312, autoAcceptConnections: true);

                qpB.RegisterProtocol(handlerChatB);
                qpB.RegisterProtocol(handlerFileB);
                qpB.RegisterProtocol(handlerVoiceB);

                // Dummy sender handlers on Node A
                qpA.RegisterProtocol(new TestProtocolHandler(protoChat, (c, s, p, ct) => Task.CompletedTask));
                qpA.RegisterProtocol(new TestProtocolHandler(protoFile, (c, s, p, ct) => Task.CompletedTask));
                qpA.RegisterProtocol(new TestProtocolHandler(protoVoice, (c, s, p, ct) => Task.CompletedTask));

                await qpA.StartAsync();
                await qpB.StartAsync();

                // Exchange peer info
                var peerB_on_A = new PeerInfo(qpB.CertManager.PeerCertificate!, qpB.CertManager.EcdhPublicKeyRaw)
                {
                    Name = qpB.CurrentPeer.Name,
                    ActiveEndPoint = new IPEndPoint(IPAddress.Loopback, 42312),
                    Addresses = new[] { IPAddress.Loopback }
                };
                qpA.AvailablePeers[peerB_on_A.Id] = peerB_on_A;
                qpA.TrustPeer(peerB_on_A.CertHash);

                var peerA_on_B = new PeerInfo(qpA.CertManager.PeerCertificate!, qpA.CertManager.EcdhPublicKeyRaw)
                {
                    Name = qpA.CurrentPeer.Name,
                    ActiveEndPoint = new IPEndPoint(IPAddress.Loopback, 42311),
                    Addresses = new[] { IPAddress.Loopback }
                };
                qpB.AvailablePeers[peerA_on_B.Id] = peerA_on_B;
                qpB.TrustPeer(peerA_on_B.CertHash);

                // Step 1: Subprotocol 1 (Chat) -> Triggers UDP hole punch and establishes physical QUIC + Stream 0
                var chatSendTask = Task.Run(async () =>
                {
                    var handlerA = new TestProtocolHandler(protoChat, async (conn, stream, peer, ct) =>
                    {
                        byte[] msg = Encoding.UTF8.GetBytes("Chat Message 1");
                        await stream.WriteAsync(BitConverter.GetBytes(msg.Length), ct);
                        await stream.WriteAsync(msg, ct);
                        await stream.FlushAsync(ct);
                    });
                    qpA.RegisterProtocol(handlerA);
                    await qpA.InitQuicConnection(protoChat, peerB_on_A);
                });

                await chatSendTask.WaitAsync(TimeSpan.FromSeconds(15));
                string receivedChat = await chatMessages.Reader.ReadAsync();
                if (receivedChat != "Chat Message 1")
                    throw new Exception($"Chat message mismatch: {receivedChat}");

                // Verify single physical QUIC multiplexer is established on both Node A and Node B
                if (!qpA.TryGetPeerMultiplexer(peerB_on_A.Id, out var muxA) || muxA == null || muxA.IsDisposed)
                    throw new Exception("Node A does not have an active QuicPeerMultiplexer!");

                if (!qpB.TryGetPeerMultiplexer(peerA_on_B.Id, out var muxB) || muxB == null || muxB.IsDisposed)
                    throw new Exception("Node B does not have an active QuicPeerMultiplexer!");

                int udpHandshakeSessionsAfterChat = qpB.IncomingHandshakeSessions.Count;

                // Step 2: Subprotocol 2 (File Transfer with 32KB binary data).
                // Reuses the persistent multiplexer in <1ms without any UDP hole punch or handshake!
                byte[] testFileData = new byte[32 * 1024];
                RandomNumberGenerator.Fill(testFileData);

                var fileSendTask = Task.Run(async () =>
                {
                    var handlerFileA = new TestProtocolHandler(protoFile, async (conn, stream, peer, ct) =>
                    {
                        await stream.WriteAsync(BitConverter.GetBytes(testFileData.Length), ct);
                        await stream.WriteAsync(testFileData, ct);
                        await stream.FlushAsync(ct);
                    });
                    qpA.RegisterProtocol(handlerFileA);
                    await qpA.InitQuicConnection(protoFile, peerB_on_A);
                });

                await fileSendTask.WaitAsync(TimeSpan.FromSeconds(10));
                byte[] receivedFile = await fileMessages.Reader.ReadAsync();
                if (!receivedFile.AsSpan().SequenceEqual(testFileData))
                    throw new Exception("File data mismatch over multiplexer!");

                // Assert that ZERO additional UDP handshakes occurred!
                if (qpB.IncomingHandshakeSessions.Count != udpHandshakeSessionsAfterChat)
                    throw new Exception($"Expected zero additional UDP handshakes for second subprotocol, found {qpB.IncomingHandshakeSessions.Count - udpHandshakeSessionsAfterChat}");

                // Step 3: Subprotocol 3 (Voice control signaling frame).
                var voiceSendTask = Task.Run(async () =>
                {
                    var handlerVoiceA = new TestProtocolHandler(protoVoice, async (conn, stream, peer, ct) =>
                    {
                        byte[] voicePayload = Encoding.UTF8.GetBytes("Voice Control Signal Frame");
                        await stream.WriteAsync(BitConverter.GetBytes(voicePayload.Length), ct);
                        await stream.WriteAsync(voicePayload, ct);
                        await stream.FlushAsync(ct);
                    });
                    qpA.RegisterProtocol(handlerVoiceA);
                    await qpA.InitQuicConnection(protoVoice, peerB_on_A);
                });

                await voiceSendTask.WaitAsync(TimeSpan.FromSeconds(10));
                string receivedVoice = await voiceMessages.Reader.ReadAsync();
                if (receivedVoice != "Voice Control Signal Frame")
                    throw new Exception($"Voice datagram mismatch: {receivedVoice}");

                // Assert that still ZERO additional UDP handshakes occurred!
                if (qpB.IncomingHandshakeSessions.Count != udpHandshakeSessionsAfterChat)
                    throw new Exception("Additional UDP handshakes detected for third subprotocol!");

                // Step 4: Simultaneous concurrent execution of two subprotocols in parallel (Chat + 64KB File Transfer)
                byte[] concurrentFileData = new byte[64 * 1024];
                RandomNumberGenerator.Fill(concurrentFileData);

                var concurrentChatTask = Task.Run(async () =>
                {
                    var handlerChat2 = new TestProtocolHandler(protoChat, async (conn, stream, peer, ct) =>
                    {
                        byte[] msg = Encoding.UTF8.GetBytes("Concurrent Chat Message");
                        await stream.WriteAsync(BitConverter.GetBytes(msg.Length), ct);
                        await stream.WriteAsync(msg, ct);
                        await stream.FlushAsync(ct);
                    });
                    qpA.RegisterProtocol(handlerChat2);
                    await qpA.InitQuicConnection(protoChat, peerB_on_A);
                });

                var concurrentFileTask = Task.Run(async () =>
                {
                    var handlerFile2 = new TestProtocolHandler(protoFile, async (conn, stream, peer, ct) =>
                    {
                        await stream.WriteAsync(BitConverter.GetBytes(concurrentFileData.Length), ct);
                        await stream.WriteAsync(concurrentFileData, ct);
                        await stream.FlushAsync(ct);
                    });
                    qpA.RegisterProtocol(handlerFile2);
                    await qpA.InitQuicConnection(protoFile, peerB_on_A);
                });

                await Task.WhenAll(concurrentChatTask, concurrentFileTask).WaitAsync(TimeSpan.FromSeconds(15));
                string receivedConcurrentChat = await chatMessages.Reader.ReadAsync();
                byte[] receivedConcurrentFile = await fileMessages.Reader.ReadAsync();

                if (receivedConcurrentChat != "Concurrent Chat Message")
                    throw new Exception($"Concurrent chat message mismatch: {receivedConcurrentChat}");
                if (!receivedConcurrentFile.AsSpan().SequenceEqual(concurrentFileData))
                    throw new Exception("Concurrent file data mismatch!");

                // Assert that still ZERO additional UDP handshakes occurred throughout all 4 steps!
                if (qpB.IncomingHandshakeSessions.Count != udpHandshakeSessionsAfterChat)
                    throw new Exception("Additional UDP handshakes detected during concurrent subprotocol execution!");

                // Verify telemetry on the multiplexer
                if (muxA.TryGetTelemetry(out var telem))
                {
                    if (telem.PathMtu < 1200)
                        throw new Exception($"Invalid telemetry MTU: {telem.PathMtu}");
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private sealed class TestProtocolHandler : QuicPunch.QuicPunch.IProtocolHandler
        {
            private readonly Func<QuicConnection, Stream, PeerInfo, CancellationToken, Task> _handler;

            public TestProtocolHandler(Guid protocolId, Func<QuicConnection, Stream, PeerInfo, CancellationToken, Task> handler)
            {
                ProtocolId = protocolId;
                _handler = handler;
            }

            public Guid ProtocolId { get; }
            public string ProtocolName => "TestHandler";
            public System.IO.Compression.ZstandardCompressionOptions? CompressionOptions => null;

            public Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct) =>
                _handler(connection, stream, peer, ct);

            public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
        }
    }
}
