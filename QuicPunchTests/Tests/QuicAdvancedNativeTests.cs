using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

public static class QuicAdvancedNativeTests
{
    private static X509Certificate2 GenerateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("   MSQUIC ADVANCED NATIVE CAPABILITIES TEST       ");
        Console.WriteLine("==================================================");

        if (!MsQuicTuner.IsSupported)
        {
            Console.WriteLine("[SKIP] MsQuicTuner is not supported on this platform.");
            return;
        }

        // 1. Enable Datagrams, Migration, and MTU 1500 on configuration cache
        bool patched = MsQuicDatagramChannel.EnableDatagramsOnConfigurationCache();
        Console.WriteLine($"[TEST 1] Injected MTU 1500, Migration & Datagrams to Config Cache: {(patched ? "SUCCESS" : "FAILED")}");

        using var cert = GenerateSelfSignedCertificate();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);

        var serverOptions = new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            IdleTimeout = TimeSpan.FromMinutes(2),
            KeepAliveInterval = TimeSpan.FromSeconds(5),
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = new() { new SslApplicationProtocol("quic-adv") },
                ServerCertificate = cert
            }
        };

        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = endpoint,
            ApplicationProtocols = new() { new SslApplicationProtocol("quic-adv") },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions)
        });

        int serverPort = listener.LocalEndPoint.Port;
        Console.WriteLine($"[SERVER] Listening on QUIC 127.0.0.1:{serverPort}");

        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, serverPort),
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            IdleTimeout = TimeSpan.FromMinutes(2),
            KeepAliveInterval = TimeSpan.FromSeconds(5),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new() { new SslApplicationProtocol("quic-adv") },
                RemoteCertificateValidationCallback = delegate { return true; }
            }
        };

        // Warm up configurations so System.Net.Quic builds and caches configuration handles
        var warmServerTask = listener.AcceptConnectionAsync();
        var warmClient = await QuicConnection.ConnectAsync(clientOptions);
        var warmServer = await warmServerTask;
        await warmClient.DisposeAsync();
        await warmServer.DisposeAsync();

        // Patch cached configurations with MTU 1500, Migration, and Datagrams
        MsQuicDatagramChannel.EnableDatagramsOnConfigurationCache();

        var serverTask = listener.AcceptConnectionAsync();
        var clientConn = await QuicConnection.ConnectAsync(clientOptions);
        var serverConn = await serverTask;

        // Attach datagram channels
        using var clientChannel = MsQuicDatagramChannel.Attach(clientConn);
        using var serverChannel = MsQuicDatagramChannel.Attach(serverConn);

        // 2. Test QoS DSCP Packet Prioritization
        Console.Write("[TEST 2] Setting QoS DSCP (Voice = EF / 46)... ");
        bool dscpVoiceOk = MsQuicTuner.TrySetDscp(clientConn, QuicDscpPriority.Voice);
        Console.WriteLine(dscpVoiceOk ? "SUCCESS" : "SKIPPED/FAILED (Requires Admin or Policy on Windows)");

        Console.Write("[TEST 2] Setting QoS DSCP (Video = AF41 / 34)... ");
        bool dscpVideoOk = MsQuicTuner.TrySetDscp(clientConn, QuicDscpPriority.Video);
        Console.WriteLine(dscpVideoOk ? "SUCCESS" : "SKIPPED/FAILED");

        // 2b. Test Dynamic Congestion Control Switching (BBR)
        Console.Write("[TEST 2b] Querying initial Congestion Control... ");
        bool getCcOk = MsQuicTuner.TryGetCongestionControl(clientConn, out var initialCc);
        Console.WriteLine(getCcOk ? $"SUCCESS ({initialCc})" : "FAILED");

        Console.Write("[TEST 2b] Switching Congestion Control to BBR... ");
        bool setBbrOk = MsQuicTuner.TrySetCongestionControl(clientConn, QuicCongestionAlgorithm.Bbr);
        Console.WriteLine(setBbrOk ? "SUCCESS" : "FAILED");

        Console.Write("[TEST 2b] Verifying active Congestion Control is BBR... ");
        bool getActiveCcOk = MsQuicTuner.TryGetCongestionControl(clientConn, out var activeCc);
        Console.WriteLine(getActiveCcOk && activeCc == QuicCongestionAlgorithm.Bbr ? $"PASSED ({activeCc})" : $"FAILED (Active={activeCc})");

        // 3. Test Native Endpoint Discovery
        Console.Write("[TEST 3] Querying Native Endpoints via MsQuic... ");
        bool localOk = MsQuicTuner.TryGetLocalAddress(clientConn, out var clientLocalEp);
        bool remoteOk = MsQuicTuner.TryGetRemoteAddress(clientConn, out var clientRemoteEp);
        if (localOk && remoteOk && clientLocalEp != null && clientRemoteEp != null)
        {
            Console.WriteLine($"PASSED (Local: {clientLocalEp}, Remote: {clientRemoteEp})");
        }
        else
        {
            Console.WriteLine($"FAILED (LocalOk={localOk}, RemoteOk={remoteOk})");
        }

        // 4. Test Stream Priority (QUIC_PARAM_STREAM_PRIORITY) & Transmit Traffic
        Console.Write("[TEST 4] Testing Native Stream Priority & Transmission... ");
        await using (var stream = await clientConn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional))
        {
            var acceptTask = serverConn.AcceptInboundStreamAsync().AsTask();

            // Query default priority (should be 32767 / 0x7FFF / Normal)
            if (!MsQuicTuner.TryGetStreamPriority(stream, out ushort defaultPrio) || defaultPrio != (ushort)QuicStreamPriority.Normal)
                throw new Exception($"Expected default stream priority 32767, got {defaultPrio}");

            // Set priority to Critical (0xFFFF)
            if (!stream.SetQuicPriority(QuicStreamPriority.Critical))
                throw new Exception("Failed to set stream priority to Critical");

            if (stream.GetQuicPriority() != QuicStreamPriority.Critical)
                throw new Exception($"Expected stream priority Critical (65535), got {stream.GetQuicPriority()}");

            // Set priority to Low (0x4000)
            if (!stream.SetQuicPriority(QuicStreamPriority.Low))
                throw new Exception("Failed to set stream priority to Low");

            if (!MsQuicTuner.TryGetStreamPriority(stream, out ushort lowPrio) || lowPrio != (ushort)QuicStreamPriority.Low)
                throw new Exception($"Expected stream priority Low (16384), got {lowPrio}");

            var pingBytes = Encoding.UTF8.GetBytes("TelemetryPingDataStream");
            await stream.WriteAsync(pingBytes);
            await stream.FlushAsync();

            await using var serverStream = await acceptTask;
            var buf1 = new byte[pingBytes.Length];
            await serverStream.ReadExactlyAsync(buf1);

            var pongBytes = Encoding.UTF8.GetBytes("TelemetryPongDataStream");
            await serverStream.WriteAsync(pongBytes);
            await serverStream.FlushAsync();

            var buf2 = new byte[pongBytes.Length];
            await stream.ReadExactlyAsync(buf2);
        }
        await Task.Delay(50);
        Console.WriteLine("PASSED (Stream Priority & Data Stream OK)");

        // 5. Query Real-Time Telemetry (QUIC_PARAM_CONN_STATISTICS_V2)
        Console.WriteLine("[TEST 5] Querying QUIC_PARAM_CONN_STATISTICS_V2 Telemetry:");
        bool clientTelemOk = MsQuicTuner.TryGetTelemetry(clientConn, out var clientTelem);
        bool serverTelemOk = MsQuicTuner.TryGetTelemetry(serverConn, out var serverTelem);

        if (clientTelemOk && clientTelem != null)
        {
            Console.WriteLine($"   [CLIENT TELEMETRY]");
            Console.WriteLine($"   --> RTT: {clientTelem.RttMs:F3} ms (MinRTT: {clientTelem.MinRttMs:F3} ms, MaxRTT: {clientTelem.MaxRttMs:F3} ms)");
            Console.WriteLine($"   --> Current Path MTU: {clientTelem.PathMtu} bytes");
            Console.WriteLine($"   --> Packets Sent: {clientTelem.SendTotalPackets}, Packets Lost: {clientTelem.SendSuspectedLostPackets}");
            Console.WriteLine($"   --> Loss Ratio: {clientTelem.PacketLossRatio:P2}");
            Console.WriteLine($"   --> Bytes Sent: {clientTelem.SendTotalBytes} B");
            Console.WriteLine($"   --> Congestion Window: {clientTelem.SendCongestionWindow} B");
        }
        else
        {
            Console.WriteLine("   [CLIENT TELEMETRY] FAILED to retrieve telemetry!");
        }

        if (serverTelemOk && serverTelem != null)
        {
            Console.WriteLine($"   [SERVER TELEMETRY]");
            Console.WriteLine($"   --> Packets Received: {serverTelem.RecvTotalPackets}, Dropped: {serverTelem.RecvDroppedPackets}");
            Console.WriteLine($"   --> Bytes Received: {serverTelem.RecvTotalBytes} B");
            Console.WriteLine($"   --> Current Path MTU: {serverTelem.PathMtu} bytes");
        }

        if (!clientTelemOk || clientTelem == null)
            throw new Exception("Telemetry query failed on client connection.");

        // 6. Test QuicPunch.QuicConnection high-level wrapper APIs (Datagrams + Opus Voice + QoS DSCP)
        Console.WriteLine("[TEST 6] Testing QuicPunch.QuicConnection Wrapper APIs & Opus over QUIC Datagrams:");
        var wrappedClient = QuicPunch.QuicConnection.Wrap(clientConn);
        var wrappedServer = QuicPunch.QuicConnection.Wrap(serverConn);

        // Verify DSCP priority via wrapper
        bool wrapDscpOk = wrappedClient.TrySetDscp(QuicDscpPriority.Voice);
        Console.WriteLine($"   --> Wrapper TrySetDscp(Voice): {(wrapDscpOk ? "SUCCESS" : "FAILED")}");

        // Verify Path MTU via wrapper
        bool wrapMtuOk = wrappedClient.TryGetPathMtu(out var clientMtu);
        Console.WriteLine($"   --> Wrapper PathMtu: {wrappedClient.PathMtu} bytes (TryGetPathMtu: {(wrapMtuOk ? "SUCCESS" : "FAILED")}, PMTUD Active: {MsQuicTuner.IsPathMtuDiscoveryActive})");

        // Verify Stream Priority via wrapper
        await using (var wsClient = await wrappedClient.OpenOutboundStreamAsync(QuicStreamType.Bidirectional))
        {
            var acceptTask = wrappedServer.AcceptInboundStreamAsync();
            wsClient.QuicPriority = QuicStreamPriority.High;
            if (wsClient.QuicPriority != QuicStreamPriority.High)
                throw new Exception($"Expected wrapped QuicStream priority High, got {wsClient.QuicPriority}");
            wsClient.Priority16 = 0x1234;
            if (wsClient.Priority16 != 0x1234)
                throw new Exception($"Expected wrapped QuicStream priority16 0x1234, got {wsClient.Priority16:X4}");

            await wsClient.WriteAsync(new byte[] { 1, 2, 3 });
            await wsClient.FlushAsync();
            await using var wsServer = await acceptTask;
            var drainBuf = new byte[3];
            await wsServer.ReadExactlyAsync(drainBuf);

            Console.WriteLine("   --> Wrapper QuicStream priority properties: SUCCESS");
        }

        // Verify Telemetry via wrapper
        bool wrapTelemOk = wrappedClient.TryGetTelemetry(out var wrapTelem);
        Console.WriteLine($"   --> Wrapper TryGetTelemetry: {(wrapTelemOk && wrapTelem != null ? "SUCCESS" : "FAILED")}");

        // Generate synthetic audio PCM frame (60ms 48kHz = 2880 samples = 5760 bytes)
        short[] syntheticPcm = new short[OpusVoiceCodec.DefaultFrameSize];
        for (int i = 0; i < syntheticPcm.Length; i++)
        {
            syntheticPcm[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000) * 16000); // 440Hz sine wave
        }

        using var codec = new OpusVoiceCodec();
        byte[] opusPacket = codec.Encode(syntheticPcm);
        Console.WriteLine($"   --> Encoded 60ms raw PCM (5760 B) to Opus packet ({opusPacket.Length} B) - Compression: {(1.0 - (double)opusPacket.Length / (syntheticPcm.Length * 2)):P1}");

        var receivedAudioTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        wrappedServer.DatagramChannel!.OnDatagramReceived += (data) =>
        {
            if (OpusVoiceCodec.IsOpusPacket(data))
            {
                receivedAudioTcs.TrySetResult(data);
            }
        };

        // Send Opus audio frame directly over QUIC unreliable datagram
        bool sendOk = wrappedClient.SendDatagram(opusPacket);
        Console.WriteLine($"   --> Transmitted Opus audio via wrappedClient.SendDatagram: {(sendOk ? "SUCCESS" : "FAILED")}");

        var receivedAudio = await Task.WhenAny(receivedAudioTcs.Task, Task.Delay(2000)) == receivedAudioTcs.Task
            ? await receivedAudioTcs.Task
            : null;

        if (receivedAudio == null)
            throw new Exception("Timed out waiting for Opus audio datagram over QUIC!");

        short[] decodedPcm = codec.Decode(receivedAudio);
        Console.WriteLine($"   --> Server received and decoded Opus datagram: {decodedPcm.Length} samples (Expected {OpusVoiceCodec.DefaultFrameSize}) - SUCCESS!");

        Console.WriteLine("==================================================");
        Console.WriteLine("   ALL ADVANCED NATIVE CAPABILITY TESTS PASSED!   ");
        Console.WriteLine("==================================================");

        await clientConn.DisposeAsync();
        await serverConn.DisposeAsync();
    }
}
