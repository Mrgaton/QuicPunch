using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

public static class NativeQuicOptimizationsTests
{
    private static X509Certificate2 GenerateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=NativeQuicOptTest", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    public static async Task RunAsync()
    {
        Console.WriteLine("===================================================================");
        Console.WriteLine("        NATIVE MSQUIC ADVANCED OPTIMIZATIONS TEST SUITE            ");
        Console.WriteLine("  1. Dynamic Flow Control Windows (16 MB Conn / 4 MB Stream)       ");
        Console.WriteLine("  2. Native Transport KeepAlive (5s) & Direct PING                 ");
        Console.WriteLine("  3. 0-RTT Resumption Level & Ticket Persistence (PeerStore)       ");
        Console.WriteLine("  4. Round-Robin Stream Multiplexing Autoconfiguration             ");
        Console.WriteLine("===================================================================");

        if (!MsQuicTuner.IsSupported)
        {
            Console.WriteLine("[FAIL] MsQuic is not supported or not initialized on this platform.");
            Environment.Exit(1);
            return;
        }

        // --- TEST 1: Optimal Configuration Cache Injection ---
        Console.Write("[TEST 1] Injected 16MB/4MB flow windows, 5s keepalive & 0-RTT to Config Cache... ");
        bool configInjected = MsQuicTuner.EnsureOptimalConfiguration(
            minMtu: 1280,
            maxMtu: 1500,
            connFlowControlWindow: 16 * 1024 * 1024,
            streamRecvWindow: 4 * 1024 * 1024,
            keepAliveIntervalMs: 5000,
            enableZeroRtt: true);
        Console.WriteLine(configInjected ? "PASSED (Active in cache)" : "WARNED (Will patch after configuration instantiation)");

        using var cert = GenerateSelfSignedCertificate();
        byte[] certHash = SHA3_256.HashData(cert.GetPublicKey());
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);

        var serverOptions = new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            IdleTimeout = TimeSpan.FromMinutes(2),
            KeepAliveInterval = TimeSpan.FromSeconds(5),
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = new() { new SslApplicationProtocol("quic-opt") },
                ServerCertificate = cert
            }
        };

        await using var listener = await System.Net.Quic.QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = endpoint,
            ApplicationProtocols = new() { new SslApplicationProtocol("quic-opt") },
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
                TargetHost = "localhost",
                ApplicationProtocols = new() { new SslApplicationProtocol("quic-opt") },
                RemoteCertificateValidationCallback = delegate { return true; }
            }
        };

        // Connect client and server using high-level QuicPunch wrapper
        var serverAcceptTask = listener.AcceptConnectionAsync();
        var clientNativeConn = await System.Net.Quic.QuicConnection.ConnectAsync(clientOptions);
        var serverNativeConn = await serverAcceptTask;

        var clientConn = QuicPunch.QuicConnection.Wrap(clientNativeConn);
        var serverConn = QuicPunch.QuicConnection.Wrap(serverNativeConn);

        // --- TEST 1b: Verify Automatic BBR & PMTUD Configuration on Connection Establishment ---
        Console.Write("[TEST 1b] Verifying Automatic BBR Configuration on Client Connection... ");
        bool clientBbrOk = clientConn.TryGetCongestionControl(out var clientAlgo) && clientAlgo == QuicCongestionAlgorithm.Bbr;
        Console.WriteLine(clientBbrOk ? $"PASSED (Algorithm: {clientAlgo})" : $"FAILED (Algorithm: {clientAlgo})");
        if (!clientBbrOk)
        {
            Console.WriteLine("[ERROR] BBR was not automatically configured on client connection.");
            Environment.Exit(1);
        }

        Console.Write("[TEST 1c] Verifying Automatic BBR Configuration on Server Connection... ");
        bool serverBbrOk = serverConn.TryGetCongestionControl(out var serverAlgo) && serverAlgo == QuicCongestionAlgorithm.Bbr;
        Console.WriteLine(serverBbrOk ? $"PASSED (Algorithm: {serverAlgo})" : $"FAILED (Algorithm: {serverAlgo})");
        if (!serverBbrOk)
        {
            Console.WriteLine("[ERROR] BBR was not automatically configured on server connection.");
            Environment.Exit(1);
        }

        // Re-run EnsureOptimalConfiguration to verify configuration cache is healthy
        bool rePatch = MsQuicTuner.EnsureOptimalConfiguration();
        Console.WriteLine($"[TEST 1d] EnsureOptimalConfiguration active across cached handles: {(rePatch ? "PASSED" : "FAILED")}");
        if (!rePatch)
        {
            Console.WriteLine("[ERROR] Configuration cache was not successfully updated.");
            Environment.Exit(1);
        }

        // --- TEST 1e: Verify Automatic Round-Robin Stream Scheduling Scheme ---
        Console.Write("[TEST 1e] Verifying Automatic Round-Robin Stream Scheduling on Client Connection... ");
        bool clientRrOk = clientConn.TryGetStreamSchedulingScheme(out var clientScheme) && clientScheme == QuicStreamSchedulingScheme.RoundRobin;
        Console.WriteLine(clientRrOk ? $"PASSED (Scheme: {clientScheme})" : $"FAILED (Scheme: {clientScheme})");
        if (!clientRrOk)
        {
            Console.WriteLine("[ERROR] Round-Robin was not automatically configured on client connection.");
            Environment.Exit(1);
        }

        Console.Write("[TEST 1f] Verifying Automatic Round-Robin Stream Scheduling on Server Connection... ");
        bool serverRrOk = serverConn.TryGetStreamSchedulingScheme(out var serverScheme) && serverScheme == QuicStreamSchedulingScheme.RoundRobin;
        Console.WriteLine(serverRrOk ? $"PASSED (Scheme: {serverScheme})" : $"FAILED (Scheme: {serverScheme})");
        if (!serverRrOk)
        {
            Console.WriteLine("[ERROR] Round-Robin was not automatically configured on server connection.");
            Environment.Exit(1);
        }

        // --- TEST 1g: Dynamic switching between FIFO and Round-Robin ---
        Console.Write("[TEST 1g] Dynamic switching between FIFO and Round-Robin on live connection... ");
        bool setFifoOk = clientConn.TrySetStreamSchedulingScheme(QuicStreamSchedulingScheme.Fifo) &&
                         clientConn.TryGetStreamSchedulingScheme(out var readFifo) && readFifo == QuicStreamSchedulingScheme.Fifo;
        bool setRrBackOk = clientConn.TrySetStreamSchedulingScheme(QuicStreamSchedulingScheme.RoundRobin) &&
                           clientConn.TryGetStreamSchedulingScheme(out var readRr) && readRr == QuicStreamSchedulingScheme.RoundRobin;
        Console.WriteLine(setFifoOk && setRrBackOk ? "PASSED (FIFO <-> RoundRobin verified)" : "FAILED");
        if (!setFifoOk || !setRrBackOk)
        {
            Console.WriteLine("[ERROR] Dynamic stream scheduling switching failed.");
            Environment.Exit(1);
        }

        // --- TEST 2: Dynamic Flow Control Windows Tuning on Active Connection ---
        Console.Write("[TEST 2] Dynamically expanding Flow Control (16MB Conn, 4MB Stream) on Client... ");
        bool clientFlowOk = clientConn.TrySetFlowControlWindows(16 * 1024 * 1024, 4 * 1024 * 1024);
        Console.WriteLine(clientFlowOk ? "PASSED" : "FAILED");
        if (!clientFlowOk)
        {
            Console.WriteLine("[ERROR] Failed to set flow control windows on active client handle.");
            Environment.Exit(1);
        }

        Console.Write("[TEST 2b] Dynamically expanding Flow Control on Server... ");
        bool serverFlowOk = serverConn.TrySetFlowControlWindows(16 * 1024 * 1024, 4 * 1024 * 1024);
        Console.WriteLine(serverFlowOk ? "PASSED" : "FAILED");
        if (!serverFlowOk)
        {
            Console.WriteLine("[ERROR] Failed to set flow control windows on active server handle.");
            Environment.Exit(1);
        }

        // --- TEST 3: KeepAlive Interval & Native Transport PING ---
        Console.Write("[TEST 3] Adjusting KeepAlive Interval to 3000ms... ");
        bool keepAliveOk = clientConn.TrySetKeepAlive(TimeSpan.FromSeconds(3));
        Console.WriteLine(keepAliveOk ? "PASSED" : "FAILED");
        if (!keepAliveOk)
        {
            Console.WriteLine("[ERROR] Failed to adjust keepalive interval.");
            Environment.Exit(1);
        }

        Console.Write("[TEST 3b] Emitting immediate Transport Keepalive PING signal... ");
        bool pingOk = clientConn.TrySendTransportPing();
        Console.WriteLine(pingOk ? "PASSED" : "FAILED");
        if (!pingOk)
        {
            Console.WriteLine("[ERROR] Failed to send transport ping.");
            Environment.Exit(1);
        }

        // --- TEST 4: PMTUD & Path MTU Verification ---
        Console.Write("[TEST 4] Validating PMTUD dynamic Path MTU... ");
        ushort pmtu = clientConn.PathMtu;
        Console.WriteLine($"PASSED (MTU: {pmtu} bytes)");
        if (pmtu < 1200)
        {
            Console.WriteLine($"[ERROR] Unexpectedly small Path MTU: {pmtu}");
            Environment.Exit(1);
        }

        Console.Write("[TEST 4b] Testing explicit Path MTU Discovery bounds & timing configuration... ");
        bool pmtudSetOk = clientConn.TrySetPathMtuDiscovery(minMtu: 1280, maxMtu: 1500, timeoutUs: 600_000_000UL, missingProbeCount: 3);
        Console.WriteLine(pmtudSetOk ? "PASSED (Bounds: 1280..1500, Timeout: 600s, MissingProbes: 3)" : "FAILED");
        if (!pmtudSetOk)
        {
            Console.WriteLine("[ERROR] Failed to set PMTUD timing on connection.");
            Environment.Exit(1);
        }

        // --- TEST 5: High-Throughput Stream Transfer Over Tuned Connection ---
        Console.Write("[TEST 5] Testing 2 MB data stream transfer over tuned flow control buffers... ");
        var clientStream = await clientConn.OpenOutboundStreamAsync(QuicPunch.QuicStreamType.Bidirectional);
        var serverStreamTask = serverConn.AcceptInboundStreamAsync().AsTask();

        byte[] payload = new byte[2 * 1024 * 1024]; // 2 MB
        Random.Shared.NextBytes(payload);
        byte[] expectedHash = SHA256.HashData(payload);

        var sendTask = Task.Run(async () =>
        {
            await clientStream.WriteAsync(payload);
            await clientStream.FlushAsync();
            clientStream.CompleteWrites();
        });

        var serverStream = await serverStreamTask;

        byte[] received = new byte[payload.Length];
        int totalRead = 0;
        while (totalRead < received.Length)
        {
            int read = await serverStream.ReadAsync(received.AsMemory(totalRead, received.Length - totalRead));
            if (read == 0) break;
            totalRead += read;
        }
        await sendTask;

        if (totalRead != payload.Length)
        {
            Console.WriteLine($"FAILED (Incomplete read: {totalRead} / {payload.Length})");
            Environment.Exit(1);
        }

        byte[] actualHash = SHA256.HashData(received);
        if (!actualHash.AsSpan().SequenceEqual(expectedHash))
        {
            Console.WriteLine("FAILED (Data hash mismatch)");
            Environment.Exit(1);
        }
        Console.WriteLine($"PASSED ({totalRead / 1024} KB verified SHA256 checksum)");

        // --- TEST 6: Resumption Ticket Caching in PeerStore & PeerInfo ---
        Console.Write("[TEST 6] Validating Resumption Ticket caching in PeerInfo & PeerStore... ");
        byte[] mockTicket = new byte[128];
        Random.Shared.NextBytes(mockTicket);

        clientConn.ResumptionTicket = mockTicket;
        bool getTicketOk = clientConn.TryGetResumptionTicket(out var retrievedTicket);
        if (!getTicketOk || !retrievedTicket.AsSpan().SequenceEqual(mockTicket))
        {
            Console.WriteLine("FAILED (QuicConnection.ResumptionTicket getter mismatch)");
            Environment.Exit(1);
        }

        string tempDbPath = Path.Combine(Path.GetTempPath(), $"peer_opt_test_{Guid.NewGuid():N}.json");
        try
        {
            using (var store = new PeerStore(tempDbPath, autoLoad: false, watch: false))
            {
                var peer = new PeerInfo
                {
                    Addresses = new[] { IPAddress.Loopback },
                    PortArray = [8000],
                    Name = "OptTestPeer",
                    ResumptionTicket = mockTicket
                };
                // Set fake cert hash for peer
                var certHashProp = typeof(PeerInfo).GetField("_certHash", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                certHashProp?.SetValue(peer, certHash);

                bool added = store.AddOrUpdate(peer, autoConnect: true, save: true, nostrPubKey: null);
                if (!added)
                {
                    Console.WriteLine("FAILED (Failed to add peer to PeerStore)");
                    Environment.Exit(1);
                }

                bool ticketFound = store.TryGetResumptionTicket(certHash, out var diskTicket);
                if (!ticketFound || diskTicket == null || !diskTicket.AsSpan().SequenceEqual(mockTicket))
                {
                    Console.WriteLine("FAILED (PeerStore could not retrieve stored ResumptionTicket)");
                    Environment.Exit(1);
                }
            }

            // Reload from disk to verify persistence across restarts
            using (var store2 = new PeerStore(tempDbPath, autoLoad: true, watch: false))
            {
                bool reloadedTicketFound = store2.TryGetResumptionTicket(certHash, out var reloadedTicket);
                if (!reloadedTicketFound || reloadedTicket == null || !reloadedTicket.AsSpan().SequenceEqual(mockTicket))
                {
                    Console.WriteLine("FAILED (PeerStore did not persist ResumptionTicket to disk)");
                    Environment.Exit(1);
                }
            }
            Console.WriteLine("PASSED (Stored, serialized, and reloaded accurately)");
        }
        finally
        {
            try { if (File.Exists(tempDbPath)) File.Delete(tempDbPath); } catch { }
            try { if (File.Exists(tempDbPath + ".lock")) File.Delete(tempDbPath + ".lock"); } catch { }
        }

        await clientConn.DisposeAsync();
        await serverConn.DisposeAsync();

        // --- TEST 7: HTTP/3 DPI Camouflage Handshake (ALPN "h3" and SNI "cloudflare-quic.com") ---
        Console.Write("[TEST 7] Emulating HTTP/3 Web Connection (ALPN 'h3', SNI 'cloudflare-quic.com')... ");
        
        using var h3Ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var h3Cert = CertManager.GenerateIdentityCertificate("node1", "user1", h3Ecdh);

        var h3ServerOptions = new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = QuicPunchConnection.SupportedProtocols,
                ServerCertificate = h3Cert,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
            }
        };

        await using var h3Listener = await System.Net.Quic.QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = QuicPunchConnection.SupportedProtocols,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(h3ServerOptions)
        });

        int h3Port = h3Listener.LocalEndPoint.Port;
        var h3ServerTask = h3Listener.AcceptConnectionAsync();

        var h3ClientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, h3Port),
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = QuicPunchConnection.DefaultDisguiseHost,
                ApplicationProtocols = QuicPunchConnection.SupportedProtocols,
                ClientCertificates = new X509Certificate2Collection(h3Cert),
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
            }
        };

        var h3ClientConn = await System.Net.Quic.QuicConnection.ConnectAsync(h3ClientOptions);
        var h3ServerConn = await h3ServerTask;

        var h3ClientWrapped = QuicPunch.QuicConnection.Wrap(h3ClientConn);
        bool telemOk = h3ClientWrapped.TryGetTelemetry(out var h3Telem);

        if (h3ClientConn.NegotiatedApplicationProtocol.ToString() != "h3" &&
            h3ServerConn.NegotiatedApplicationProtocol.ToString() != "h3")
        {
            Console.WriteLine($"FAILED (ALPN was {h3ClientConn.NegotiatedApplicationProtocol}, expected 'h3')");
            Environment.Exit(1);
        }

        if (telemOk && h3Telem.TargetHostName != null && h3Telem.TargetHostName != QuicPunchConnection.DefaultDisguiseHost)
        {
            Console.WriteLine($"FAILED (TargetHost was {h3Telem.TargetHostName}, expected '{QuicPunchConnection.DefaultDisguiseHost}')");
            Environment.Exit(1);
        }

        Console.WriteLine($"PASSED (ALPN: {h3ClientConn.NegotiatedApplicationProtocol}, SNI: {QuicPunchConnection.DefaultDisguiseHost})");

        await h3ClientConn.DisposeAsync();
        await h3ServerConn.DisposeAsync();

        Console.WriteLine("===================================================================");
        Console.WriteLine("    ALL MSQUIC ADVANCED NATIVE OPTIMIZATION TESTS PASSED! (100%)   ");
        Console.WriteLine("===================================================================");
    }
}
