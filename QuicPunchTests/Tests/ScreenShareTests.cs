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

public static class ScreenShareTests
{
    private static X509Certificate2 GenerateTestCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=ScreenShareNode", ecdsa, HashAlgorithmName.SHA256);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("     SCREEN SHARING PROTOCOL VERIFICATION TESTS    ");
        Console.WriteLine("==================================================");

        await Test_ScreenShare_FramingAndStreamingAsync();

        Console.WriteLine("==================================================");
        Console.WriteLine("     ALL SCREEN SHARING TESTS PASSED!             ");
        Console.WriteLine("==================================================");
    }

    private static async Task Test_ScreenShare_FramingAndStreamingAsync()
    {
        Console.Write("[TEST] VoiceCallHandler screen sharing framing and frame delivery... ");

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

        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = serverEndPoint,
            ApplicationProtocols = new List<SslApplicationProtocol> { alpn },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions)
        });

        var clientOptions = new QuicClientConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            MaxInboundBidirectionalStreams = 512,
            MaxInboundUnidirectionalStreams = 512,
            RemoteEndPoint = listener.LocalEndPoint,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { alpn },
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        var serverAcceptTask = listener.AcceptConnectionAsync();
        var clientNative = await NativeQuicConnection.ConnectAsync(clientOptions);
        var serverNative = await serverAcceptTask;

        var serverQuicConn = QuicConnection.Wrap(serverNative);
        var clientQuicConn = QuicConnection.Wrap(clientNative);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var dummyQcc = new QuicPunch.QuicPunch(cts.Token);

        using var handlerServer = new VoiceCallHandler(dummyQcc);
        using var handlerClient = new VoiceCallHandler(dummyQcc);

        var serverPeer = new PeerInfo(serverCert, null) { Name = "Bob (Viewer)" };
        var clientPeer = new PeerInfo(clientCert, null) { Name = "Alice (Screen Sharer)" };

        var serverMux = new QuicPeerMultiplexer(
            serverQuicConn,
            clientPeer,
            isServer: true,
            protocolLookup: id => id == handlerServer.ProtocolId ? handlerServer : null,
            accessCheck: _ => true
        );

        var clientMux = new QuicPeerMultiplexer(
            clientQuicConn,
            serverPeer,
            isServer: false,
            protocolLookup: id => id == handlerClient.ProtocolId ? handlerClient : null,
            accessCheck: _ => true
        );

        await serverMux.StartAsync();
        await clientMux.StartAsync();

        var tcsStarted = new TaskCompletionSource<(int width, int height, byte codecId, byte fps)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsFrame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsAudio = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        handlerServer.OnScreenShareStarted += (peer, w, h, c, f) => tcsStarted.TrySetResult((w, h, c, f));
        handlerServer.OnScreenFrameReceived += (peer, codecId, data) => tcsFrame.TrySetResult(data);
        handlerServer.OnScreenAudioReceived += (peer, data) => tcsAudio.TrySetResult(data);
        handlerServer.OnScreenShareStopped += (peer) => tcsStopped.TrySetResult(true);

        // Open virtual session for VoiceCallHandler from client to server
        var clientSessionConn = await clientMux.OpenVirtualSessionAsync(handlerClient.ProtocolId);
        var clientAppStream = await clientSessionConn.OpenOutboundStreamAsync(QuicPunch.QuicStreamType.Bidirectional);

        using var clientHandlerCts = new CancellationTokenSource();
        _ = Task.Run(() => handlerClient.HandleAsync(clientSessionConn, clientAppStream, serverPeer, clientHandlerCts.Token));

        // Wait for both sides to register active call session
        for (int i = 0; i < 50 && (!VoiceCallHandler.ActiveCalls.ContainsKey(serverPeer.Id) || !VoiceCallHandler.ActiveCalls.ContainsKey(clientPeer.Id)); i++)
        {
            await Task.Delay(50);
        }

        // 1. Trigger Screen Share Start from Alice (client) with H.264 (codecId=1) at 60 FPS
        await VoiceCallHandler.StartScreenShareAsync(1920, 1080, codecId: 1, fps: 60);
        var (recvW, recvH, recvCodec, recvFps) = await tcsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (recvW != 1920 || recvH != 1080 || recvCodec != 1 || recvFps != 60)
            throw new Exception($"Expected 1920x1080 codec 1 at 60fps, got {recvW}x{recvH} codec {recvCodec} at {recvFps}fps");

        // 2. Send dummy JPEG video frame
        byte[] dummyJpeg = new byte[1024];
        dummyJpeg[0] = 0xFF; dummyJpeg[1] = 0xD8; // JPEG SOI marker
        dummyJpeg[^2] = 0xFF; dummyJpeg[^1] = 0xD9; // JPEG EOI marker
        Random.Shared.NextBytes(dummyJpeg.AsSpan(2, 1020));

        await VoiceCallHandler.SendScreenFrameAsync(dummyJpeg);
        byte[] recvFrame = await tcsFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (recvFrame.Length != dummyJpeg.Length || recvFrame[0] != 0xFF || recvFrame[1] != 0xD8)
            throw new Exception("Received frame corrupted or size mismatch!");

        // 3. Send independent screen audio frame (PCM)
        byte[] dummyPcm = new byte[960 * 2]; // 20ms @ 48kHz mono Int16
        Random.Shared.NextBytes(dummyPcm);
        await VoiceCallHandler.SendScreenAudioAsync(dummyPcm);
        byte[] recvAudio = await tcsAudio.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (recvAudio.Length != dummyPcm.Length || !recvAudio.SequenceEqual(dummyPcm))
            throw new Exception("Received screen audio frame corrupted or size mismatch!");

        // 4. Trigger Screen Share Stop
        await VoiceCallHandler.StopScreenShareAsync();
        bool stopped = await tcsStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!stopped)
            throw new Exception("Did not receive ScreenShareStopped!");

        // Cleanup
        handlerServer.Hangup(clientPeer.Id);
        handlerClient.Hangup(serverPeer.Id);
        clientHandlerCts.Cancel();

        try { await serverMux.DisposeAsync(); } catch { }
        try { await clientMux.DisposeAsync(); } catch { }

        Console.WriteLine("PASSED");
    }
}
