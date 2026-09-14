using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuicPunch;
using QuicPunch.Multiplexer;
using QuicPunchTests.Protocols;
using QuicConnection = QuicPunch.QuicConnection;
using NativeQuicConnection = System.Net.Quic.QuicConnection;

namespace QuicPunchTests.Tests;

public static class SpeedTestTests
{
    private static X509Certificate2 GenerateTestCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=SpeedTestNode", ecdsa, HashAlgorithmName.SHA256);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("        SPEED TEST PROTOCOL END-TO-END TESTS      ");
        Console.WriteLine("==================================================");

        await Test_SpeedTest_EndToEndBenchmarkAsync();

        Console.WriteLine("==================================================");
        Console.WriteLine("     ALL SPEED TEST BENCHMARK TESTS PASSED!       ");
        Console.WriteLine("==================================================");
    }

    private static async Task Test_SpeedTest_EndToEndBenchmarkAsync()
    {
        Console.Write("[TEST] SpeedTestHandler benchmark over QuicPeerMultiplexer... ");

        using var serverCert = GenerateTestCert();
        using var clientCert = GenerateTestCert();

        var alpn = new SslApplicationProtocol("qpmux");
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, 0);

        var serverOptions = new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            MaxInboundBidirectionalStreams = 512,
            MaxInboundUnidirectionalStreams = 512,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { alpn },
                ServerCertificate = serverCert,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = serverEndPoint,
            ApplicationProtocols = new List<SslApplicationProtocol> { alpn },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions)
        };

        await using var listener = await QuicListener.ListenAsync(listenerOptions);
        var boundEndPoint = listener.LocalEndPoint;

        var clientOptions = new QuicClientConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            MaxInboundBidirectionalStreams = 512,
            MaxInboundUnidirectionalStreams = 512,
            RemoteEndPoint = boundEndPoint,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { alpn },
                TargetHost = "localhost",
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        var serverAcceptTask = listener.AcceptConnectionAsync();
        var clientNative = await NativeQuicConnection.ConnectAsync(clientOptions);
        var serverNative = await serverAcceptTask;

        var serverQuicConn = QuicConnection.Wrap(serverNative);
        var clientQuicConn = QuicConnection.Wrap(clientNative);

        using var serverHandler = new SpeedTestHandler();
        using var clientHandler = new SpeedTestHandler();

        var serverPeer = new PeerInfo(serverCert, null) { Name = "ServerNode" };
        var clientPeer = new PeerInfo(clientCert, null) { Name = "ClientNode" };

        var serverMux = new QuicPeerMultiplexer(
            serverQuicConn,
            clientPeer,
            isServer: true,
            protocolLookup: id => id == serverHandler.ProtocolId ? serverHandler : null,
            accessCheck: _ => true
        );

        var clientMux = new QuicPeerMultiplexer(
            clientQuicConn,
            serverPeer,
            isServer: false,
            protocolLookup: id => id == clientHandler.ProtocolId ? clientHandler : null,
            accessCheck: _ => true
        );

        await serverMux.StartAsync();
        await clientMux.StartAsync();

        // 1. Client initiates SpeedTest virtual session
        var clientSessionConn = await clientMux.OpenVirtualSessionAsync(clientHandler.ProtocolId);
        var clientAppStream = await clientSessionConn.OpenOutboundStreamAsync(QuicPunch.QuicStreamType.Bidirectional);
        
        using var clientHandlerCts = new CancellationTokenSource();
        _ = Task.Run(() => clientHandler.HandleAsync(clientSessionConn, clientAppStream, serverPeer, clientHandlerCts.Token));

        // Wait briefly for both sides to establish session
        for (int i = 0; i < 40 && (!SpeedTestHandler.ActiveSessions.ContainsKey(serverPeer.Id) || !SpeedTestHandler.ActiveSessions.ContainsKey(clientPeer.Id)); i++)
        {
            await Task.Delay(50);
        }

        // Test 1: Upload mode (Client -> Server)
        {
            var completedTcs = new TaskCompletionSource<SpeedTestHandler.SummaryPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PeerInfo, SpeedTestHandler.SummaryPayload> handler = (_, s) => completedTcs.TrySetResult(s);
            clientHandler.OnCompleted += handler;

            var run = await clientHandler.StartTestAsync(serverPeer, SpeedTestHandler.TestMode.Upload, 2);
            var summary = await Task.WhenAny(completedTcs.Task, Task.Delay(5000)) == completedTcs.Task ? await completedTcs.Task : null;
            clientHandler.OnCompleted -= handler;

            if (summary == null) throw new Exception("Upload test timed out.");
            if (summary.UploadMbps <= 0) throw new Exception($"Expected UploadMbps > 0, got {summary.UploadMbps}");
            Console.WriteLine($"[Upload OK: {summary.UploadMbps} Mbps]");
        }

        // Test 2: Download mode (Server -> Client)
        {
            var completedTcs = new TaskCompletionSource<SpeedTestHandler.SummaryPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PeerInfo, SpeedTestHandler.SummaryPayload> handler = (_, s) => completedTcs.TrySetResult(s);
            clientHandler.OnCompleted += handler;

            var run = await clientHandler.StartTestAsync(serverPeer, SpeedTestHandler.TestMode.Download, 2);
            var summary = await Task.WhenAny(completedTcs.Task, Task.Delay(5000)) == completedTcs.Task ? await completedTcs.Task : null;
            clientHandler.OnCompleted -= handler;

            if (summary == null) throw new Exception("Download test timed out.");
            if (summary.DownloadMbps <= 0) throw new Exception($"Expected DownloadMbps > 0, got {summary.DownloadMbps}");
            Console.WriteLine($"[Download OK: {summary.DownloadMbps} Mbps]");
        }

        // Test 3: Both mode (Bidirectional)
        {
            var completedTcs = new TaskCompletionSource<SpeedTestHandler.SummaryPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PeerInfo, SpeedTestHandler.SummaryPayload> handler = (_, s) => completedTcs.TrySetResult(s);
            clientHandler.OnCompleted += handler;

            var run = await clientHandler.StartTestAsync(serverPeer, SpeedTestHandler.TestMode.Both, 2);
            var summary = await Task.WhenAny(completedTcs.Task, Task.Delay(5000)) == completedTcs.Task ? await completedTcs.Task : null;
            clientHandler.OnCompleted -= handler;

            if (summary == null) throw new Exception("Bidirectional test timed out.");
            if (summary.UploadMbps <= 0 || summary.DownloadMbps <= 0)
                throw new Exception($"Expected UploadMbps > 0 and DownloadMbps > 0, got Up:{summary.UploadMbps} Down:{summary.DownloadMbps}");
            Console.WriteLine($"[Bidirectional OK: Up {summary.UploadMbps} Mbps, Down {summary.DownloadMbps} Mbps]");
        }

        clientHandlerCts.Cancel();
        await clientMux.DisposeAsync();
        await serverMux.DisposeAsync();
    }
}
