using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Datagrams;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests
{
    public static class QuicDatagramCategorizationTests
    {
        public static async Task RunAllTestsAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  QUIC DATAGRAM CATEGORIZATION & FRAMING TESTS    ");
            Console.WriteLine("==================================================");

            Test_ZeroAllocEnvelopeSerializationAndParsing();
            Test_CategorizedDatagramDispatchAndPeerIdentification();
            Test_VideoFrameFragmentationAndReassembly();
            Test_FrameDropToleranceAndStaleCleanup();
            await Test_EndToEndMultiplexedProtocolWithVideoAndAudioDatagramsAsync();

            Console.WriteLine("==================================================");
            Console.WriteLine("  ALL DATAGRAM CATEGORIZATION TESTS PASSED!       ");
            Console.WriteLine("==================================================");
        }

        private static void Test_ZeroAllocEnvelopeSerializationAndParsing()
        {
            Console.Write("[TEST 1] Zero-alloc envelope serialization and parsing... ");

            // 1. Unfragmented envelope
            byte category = QuicDatagramCategory.Audio;
            var flags = QuicDatagramFlags.None;
            uint seq = 42;
            byte[] payload = Encoding.UTF8.GetBytes("Opus Audio Sample 20ms");

            Span<byte> buffer = stackalloc byte[QuicDatagramEnvelope.UnfragmentedHeaderSize + payload.Length];
            int written = QuicDatagramEnvelope.WriteEnvelope(buffer, category, flags, seq, payload);
            if (written != buffer.Length)
                throw new Exception($"Written length mismatch: {written} vs {buffer.Length}");

            if (!QuicDatagramEnvelope.TryParse(buffer, out var env))
                throw new Exception("Failed to parse unfragmented envelope");

            if (env.Category != category)
                throw new Exception($"Category mismatch: {env.Category} vs {category}");
            if (env.SequenceNumber != seq)
                throw new Exception($"Sequence mismatch: {env.SequenceNumber} vs {seq}");
            if (env.IsFragmented)
                throw new Exception("Expected IsFragmented to be false");
            if (!env.Payload.SequenceEqual(payload))
                throw new Exception("Payload mismatch in unfragmented envelope");

            // 2. Fragmented envelope
            byte vidCat = QuicDatagramCategory.VideoKeyFrame;
            var vidFlags = QuicDatagramFlags.IsKeyFrame;
            uint vidSeq = 100;
            ushort frameId = 7;
            ushort fragIdx = 3;
            ushort totalFrags = 10;
            byte[] chunkData = new byte[800];
            RandomNumberGenerator.Fill(chunkData);

            Span<byte> fragBuffer = stackalloc byte[QuicDatagramEnvelope.FragmentedHeaderSize + chunkData.Length];
            int fragWritten = QuicDatagramEnvelope.WriteFragmentEnvelope(fragBuffer, vidCat, vidFlags, vidSeq, frameId, fragIdx, totalFrags, chunkData);
            if (fragWritten != fragBuffer.Length)
                throw new Exception($"Frag written length mismatch: {fragWritten} vs {fragBuffer.Length}");

            if (!QuicDatagramEnvelope.TryParse(fragBuffer, out var fragEnv))
                throw new Exception("Failed to parse fragmented envelope");

            if (fragEnv.Category != vidCat)
                throw new Exception($"Frag category mismatch: {fragEnv.Category}");
            if (!fragEnv.IsFragmented)
                throw new Exception("Expected IsFragmented to be true");
            if (!fragEnv.IsKeyFrame)
                throw new Exception("Expected IsKeyFrame to be true");
            if (fragEnv.FrameId != frameId)
                throw new Exception($"FrameId mismatch: {fragEnv.FrameId}");
            if (fragEnv.FragmentIndex != fragIdx)
                throw new Exception($"FragmentIndex mismatch: {fragEnv.FragmentIndex}");
            if (fragEnv.TotalFragments != totalFrags)
                throw new Exception($"TotalFragments mismatch: {fragEnv.TotalFragments}");
            if (!fragEnv.Payload.SequenceEqual(chunkData))
                throw new Exception("Chunk payload mismatch");

            // 3. Raw datagram fallback
            byte[] rawBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            if (!QuicDatagramEnvelope.TryParse(rawBytes, out var rawEnv))
                throw new Exception("Failed to parse raw legacy datagram");

            if (rawEnv.IsFramed)
                throw new Exception("Raw datagram should not be marked IsFramed");
            if (rawEnv.Category != QuicDatagramCategory.Generic)
                throw new Exception($"Raw datagram should default to Generic category, got {rawEnv.Category}");
            if (!rawEnv.Payload.SequenceEqual(rawBytes))
                throw new Exception("Raw datagram payload mismatch");

            Console.WriteLine("PASSED");
        }

        private static void Test_CategorizedDatagramDispatchAndPeerIdentification()
        {
            Console.Write("[TEST 2] Category dispatch and peer identification... ");

            var dummyPeer = new PeerInfo(GenerateTestCert(), Array.Empty<byte>())
            {
                Name = "PeerStreamer",
                ActiveEndPoint = new IPEndPoint(IPAddress.Loopback, 5000)
            };

            var reassembler = new DatagramFrameReassembler();
            bool audioReceived = false;
            bool cursorReceived = false;

            // Simulate incoming audio datagram
            byte[] audioPayload = Encoding.UTF8.GetBytes("AudioOpusPacket48kHz");
            Span<byte> audioBuf = stackalloc byte[QuicDatagramEnvelope.UnfragmentedHeaderSize + audioPayload.Length];
            QuicDatagramEnvelope.WriteEnvelope(audioBuf, QuicDatagramCategory.Audio, QuicDatagramFlags.None, 1, audioPayload);

            if (QuicDatagramEnvelope.TryParse(audioBuf, out var audioEnv))
            {
                var msg = new QuicDatagramMessage(dummyPeer, audioEnv.Category, audioEnv.Flags, audioEnv.SequenceNumber, audioBuf.ToArray().AsMemory(QuicDatagramEnvelope.UnfragmentedHeaderSize));
                if (msg.Category == QuicDatagramCategory.Audio && msg.Peer.Name == "PeerStreamer")
                {
                    audioReceived = true;
                }
            }

            // Simulate incoming cursor position datagram (Category = CursorInput)
            byte[] cursorPayload = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(cursorPayload.AsSpan(0, 4), 1920); // X coord
            BinaryPrimitives.WriteInt32LittleEndian(cursorPayload.AsSpan(4, 4), 1080); // Y coord
            Span<byte> cursorBuf = stackalloc byte[QuicDatagramEnvelope.UnfragmentedHeaderSize + cursorPayload.Length];
            QuicDatagramEnvelope.WriteEnvelope(cursorBuf, QuicDatagramCategory.CursorInput, QuicDatagramFlags.None, 2, cursorPayload);

            if (QuicDatagramEnvelope.TryParse(cursorBuf, out var cursorEnv))
            {
                var msg = new QuicDatagramMessage(dummyPeer, cursorEnv.Category, cursorEnv.Flags, cursorEnv.SequenceNumber, cursorBuf.ToArray().AsMemory(QuicDatagramEnvelope.UnfragmentedHeaderSize));
                if (msg.Category == QuicDatagramCategory.CursorInput && msg.Peer.Id == dummyPeer.Id)
                {
                    int x = BinaryPrimitives.ReadInt32LittleEndian(msg.Payload.Span.Slice(0, 4));
                    int y = BinaryPrimitives.ReadInt32LittleEndian(msg.Payload.Span.Slice(4, 4));
                    if (x == 1920 && y == 1080)
                    {
                        cursorReceived = true;
                    }
                }
            }

            if (!audioReceived || !cursorReceived)
                throw new Exception($"Category dispatch verification failed: Audio={audioReceived}, Cursor={cursorReceived}");

            Console.WriteLine("PASSED");
        }

        private static void Test_VideoFrameFragmentationAndReassembly()
        {
            Console.Write("[TEST 3] Video frame fragmentation and automatic reassembly... ");

            var reassembler = new DatagramFrameReassembler(TimeSpan.FromSeconds(2));

            // Generate 64 KB uncompressed video/screen slice frame
            byte[] originalFrame = new byte[64 * 1024];
            RandomNumberGenerator.Fill(originalFrame);

            int maxChunkSize = 1200;
            int totalFragments = (originalFrame.Length + maxChunkSize - 1) / maxChunkSize;
            ushort frameId = 42;

            bool frameCompleted = false;
            ReadOnlyMemory<byte> reassembledFrame = default;
            byte completedCategory = 0;
            bool completedKeyFrame = false;

            // Transmit fragments in simulated network order
            Span<byte> fragBuf = stackalloc byte[QuicDatagramEnvelope.FragmentedHeaderSize + maxChunkSize];
            for (int i = 0; i < totalFragments; i++)
            {
                int offset = i * maxChunkSize;
                int len = Math.Min(maxChunkSize, originalFrame.Length - offset);
                var chunk = originalFrame.AsSpan(offset, len);

                var flags = QuicDatagramFlags.IsFragmented | QuicDatagramFlags.IsKeyFrame;
                if (i == totalFragments - 1) flags |= QuicDatagramFlags.IsLastFragment;

                int written = QuicDatagramEnvelope.WriteFragmentEnvelope(fragBuf, QuicDatagramCategory.VideoKeyFrame, flags, (uint)(100 + i), frameId, (ushort)i, (ushort)totalFragments, chunk);

                if (QuicDatagramEnvelope.TryParse(fragBuf.Slice(0, written), out var env))
                {
                    if (reassembler.TryProcessFragment(env, out var cat, out var fullPayload, out var isKey))
                    {
                        frameCompleted = true;
                        reassembledFrame = fullPayload;
                        completedCategory = cat;
                        completedKeyFrame = isKey;
                    }
                }
            }

            if (!frameCompleted)
                throw new Exception("Frame failed to reassemble after all fragments arrived!");

            if (completedCategory != QuicDatagramCategory.VideoKeyFrame)
                throw new Exception($"Reassembled category mismatch: {completedCategory}");

            if (!completedKeyFrame)
                throw new Exception("Reassembled frame should be marked as keyframe");

            if (!reassembledFrame.Span.SequenceEqual(originalFrame))
                throw new Exception($"Reassembled frame size or content mismatch: {reassembledFrame.Length} vs {originalFrame.Length}");

            Console.WriteLine("PASSED");
        }

        private static void Test_FrameDropToleranceAndStaleCleanup()
        {
            Console.Write("[TEST 4] Frame drop tolerance and real-time stale cleanup... ");

            var reassembler = new DatagramFrameReassembler(TimeSpan.FromMilliseconds(50));

            ushort droppedFrameId = 99;
            int totalFragments = 4;
            Span<byte> dropFragBuf = stackalloc byte[QuicDatagramEnvelope.FragmentedHeaderSize + 500];

            // Deliver fragments 0, 1, 2, but DROP fragment 3
            for (int i = 0; i < 3; i++)
            {
                byte[] chunk = new byte[500];
                var flags = QuicDatagramFlags.IsFragmented;

                int written = QuicDatagramEnvelope.WriteFragmentEnvelope(dropFragBuf, QuicDatagramCategory.VideoDeltaFrame, flags, (uint)(200 + i), droppedFrameId, (ushort)i, (ushort)totalFragments, chunk);

                if (QuicDatagramEnvelope.TryParse(dropFragBuf.Slice(0, written), out var env))
                {
                    bool completed = reassembler.TryProcessFragment(env, out _, out _, out _);
                    if (completed)
                        throw new Exception("Frame should not complete with missing fragment!");
                }
            }

            // Wait past the 50ms TTL so stale fragments expire
            Thread.Sleep(80);

            // Now send a fresh, intact frame with FrameId = 100
            ushort intactFrameId = 100;
            byte[] freshPayload = Encoding.UTF8.GetBytes("IntactNextVideoDeltaFrame");
            Span<byte> freshBuf = stackalloc byte[QuicDatagramEnvelope.FragmentedHeaderSize + freshPayload.Length];
            QuicDatagramEnvelope.WriteFragmentEnvelope(freshBuf, QuicDatagramCategory.VideoDeltaFrame, QuicDatagramFlags.IsFragmented, 301, intactFrameId, 0, 1, freshPayload);

            if (QuicDatagramEnvelope.TryParse(freshBuf, out var freshEnv))
            {
                bool freshCompleted = reassembler.TryProcessFragment(freshEnv, out var cat, out var payload, out _);
                if (!freshCompleted)
                    throw new Exception("Fresh frame failed to assemble after stale frame drop!");

                if (Encoding.UTF8.GetString(payload.Span) != "IntactNextVideoDeltaFrame")
                    throw new Exception("Payload mismatch on fresh frame");
            }

            Console.WriteLine("PASSED");
        }

        private static async Task Test_EndToEndMultiplexedProtocolWithVideoAndAudioDatagramsAsync()
        {
            Console.Write("[TEST 5] End-to-end multiplexed video and audio datagram streaming... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_dg_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_dg_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            var protoStream = Guid.NewGuid();

            var audioReceivedChannel = Channel.CreateUnbounded<string>();
            var cursorReceivedChannel = Channel.CreateUnbounded<(int X, int Y)>();
            var videoFrameReceivedChannel = Channel.CreateUnbounded<byte[]>();
            var sessionDoneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Protocol handler on Node B implementing IProtocolHandler with automatic datagram categorization
            var handlerStreamingB = new StreamingProtocolHandler(
                protoStream,
                onStream: async (conn, stream, peer, ct) =>
                {
                    // Control stream handshake
                    byte[] ack = Encoding.UTF8.GetBytes("STREAM_READY");
                    await stream.WriteAsync(ack, ct);
                    await stream.FlushAsync(ct);

                    // Keep session open while datagrams are streaming
                    await sessionDoneTcs.Task.WaitAsync(ct);
                },
                onDatagram: (conn, peer, dgram) =>
                {
                    QuicPunch.QuicPunchLog.Info($"[TEST 5 RECEIVER] Received datagram category {dgram.Category}, payload {dgram.Payload.Length} bytes");
                    if (dgram.Category == QuicDatagramCategory.Audio)
                    {
                        audioReceivedChannel.Writer.TryWrite(Encoding.UTF8.GetString(dgram.Payload.Span));
                    }
                    else if (dgram.Category == QuicDatagramCategory.CursorInput)
                    {
                        int x = BinaryPrimitives.ReadInt32LittleEndian(dgram.Payload.Span.Slice(0, 4));
                        int y = BinaryPrimitives.ReadInt32LittleEndian(dgram.Payload.Span.Slice(4, 4));
                        cursorReceivedChannel.Writer.TryWrite((x, y));
                    }
                },
                onFrame: (conn, peer, category, frame, isKey) =>
                {
                    QuicPunch.QuicPunchLog.Info($"[TEST 5 RECEIVER] Received frame category {category}, length {frame.Length} bytes, isKey={isKey}");
                    if (category == QuicDatagramCategory.VideoKeyFrame)
                    {
                        videoFrameReceivedChannel.Writer.TryWrite(frame.ToArray());
                    }
                });

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 43311, autoAcceptConnections: true);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 43312, autoAcceptConnections: true);

                qpB.RegisterProtocol(handlerStreamingB);
                qpA.RegisterProtocol(new StreamingProtocolHandler(protoStream, (c, s, p, ct) => Task.CompletedTask));

                await qpA.StartAsync();
                await qpB.StartAsync();

                var peerB_on_A = new PeerInfo(qpB.CertManager.PeerCertificate!, qpB.CertManager.EcdhPublicKeyRaw)
                {
                    Name = qpB.CurrentPeer.Name,
                    ActiveEndPoint = new IPEndPoint(IPAddress.Loopback, 43312),
                    Addresses = new[] { IPAddress.Loopback }
                };
                qpA.AvailablePeers[peerB_on_A.Id] = peerB_on_A;
                qpA.TrustPeer(peerB_on_A.CertHash);

                var peerA_on_B = new PeerInfo(qpA.CertManager.PeerCertificate!, qpA.CertManager.EcdhPublicKeyRaw)
                {
                    Name = qpA.CurrentPeer.Name,
                    ActiveEndPoint = new IPEndPoint(IPAddress.Loopback, 43311),
                    Addresses = new[] { IPAddress.Loopback }
                };
                qpB.AvailablePeers[peerA_on_B.Id] = peerA_on_B;
                qpB.TrustPeer(peerA_on_B.CertHash);

                // Open session and transmit streaming data from Node A
                byte[] testVideoFrame = new byte[32 * 1024]; // 32 KB compressed video frame
                RandomNumberGenerator.Fill(testVideoFrame);

                var streamingTask = Task.Run(async () =>
                {
                    var senderHandler = new StreamingProtocolHandler(protoStream, async (conn, stream, peer, ct) =>
                    {
                        byte[] buf = new byte[12];
                        await stream.ReadExactlyAsync(buf, ct); // Wait for STREAM_READY

                        // 1. Send Audio Datagram
                        byte[] audio = Encoding.UTF8.GetBytes("OpusAudioLiveStream");
                        bool aSent = conn.SendCategorizedDatagram(QuicDatagramCategory.Audio, audio);
                        QuicPunch.QuicPunchLog.Info($"[TEST 5 SENDER] Audio sent: {aSent}");

                        // 2. Send Cursor Position Datagram
                        byte[] cursor = new byte[8];
                        BinaryPrimitives.WriteInt32LittleEndian(cursor.AsSpan(0, 4), 2560);
                        BinaryPrimitives.WriteInt32LittleEndian(cursor.AsSpan(4, 4), 1440);
                        bool cSent = conn.SendCategorizedDatagram(QuicDatagramCategory.CursorInput, cursor);
                        QuicPunch.QuicPunchLog.Info($"[TEST 5 SENDER] Cursor sent: {cSent}");

                        // 3. Send 32 KB Video KeyFrame (automatic fragmentation across datagrams)
                        bool vSent = conn.SendFrame(QuicDatagramCategory.VideoKeyFrame, testVideoFrame, isKeyFrame: true, maxFragmentSize: 1200);
                        QuicPunch.QuicPunchLog.Info($"[TEST 5 SENDER] Video sent: {vSent}");

                        // Keep sender session open while receiver reads
                        await sessionDoneTcs.Task.WaitAsync(ct);
                    });

                    qpA.RegisterProtocol(senderHandler);
                    await qpA.InitQuicConnection(protoStream, peerB_on_A);
                });

                // Verify that Node B received and automatically categorized all datagrams
                string receivedAudio = await audioReceivedChannel.Reader.ReadAsync();
                if (receivedAudio != "OpusAudioLiveStream")
                    throw new Exception($"Audio payload mismatch: {receivedAudio}");

                var (curX, curY) = await cursorReceivedChannel.Reader.ReadAsync();
                if (curX != 2560 || curY != 1440)
                    throw new Exception($"Cursor position mismatch: ({curX}, {curY})");

                byte[] receivedVideo = await videoFrameReceivedChannel.Reader.ReadAsync();
                if (!receivedVideo.AsSpan().SequenceEqual(testVideoFrame))
                    throw new Exception($"Reassembled video frame mismatch over multiplexer! Length: {receivedVideo.Length} vs {testVideoFrame.Length}");

                sessionDoneTcs.TrySetResult();
                await streamingTask.WaitAsync(TimeSpan.FromSeconds(5));

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static X509Certificate2 GenerateTestCert()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=DatagramTest", ecdsa, HashAlgorithmName.SHA256);
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        }

        private sealed class StreamingProtocolHandler : QuicPunch.QuicPunch.IProtocolHandler
        {
            private readonly Func<QuicConnection, Stream, PeerInfo, CancellationToken, Task> _onStream;
            private readonly Action<QuicConnection, PeerInfo, QuicDatagramMessage>? _onDatagram;
            private readonly Action<QuicConnection, PeerInfo, byte, ReadOnlyMemory<byte>, bool>? _onFrame;

            public StreamingProtocolHandler(
                Guid protocolId,
                Func<QuicConnection, Stream, PeerInfo, CancellationToken, Task> onStream,
                Action<QuicConnection, PeerInfo, QuicDatagramMessage>? onDatagram = null,
                Action<QuicConnection, PeerInfo, byte, ReadOnlyMemory<byte>, bool>? onFrame = null)
            {
                ProtocolId = protocolId;
                _onStream = onStream;
                _onDatagram = onDatagram;
                _onFrame = onFrame;
            }

            public Guid ProtocolId { get; }
            public string ProtocolName => "StreamingProtocol";
            public System.IO.Compression.ZstandardCompressionOptions? CompressionOptions => null;

            public Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct) =>
                _onStream(connection, stream, peer, ct);

            public void OnDatagramReceived(QuicConnection connection, PeerInfo peer, QuicDatagramMessage datagram) =>
                _onDatagram?.Invoke(connection, peer, datagram);

            public void OnFrameReceived(QuicConnection connection, PeerInfo peer, byte category, ReadOnlyMemory<byte> framePayload, bool isKeyFrame) =>
                _onFrame?.Invoke(connection, peer, category, framePayload, isKeyFrame);

            public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
        }
    }
}
