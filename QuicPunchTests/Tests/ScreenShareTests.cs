using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuicPunch;
using QuicPunch.Multiplexer;
using QuicNet.Protocols;
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
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    public static async Task RunAsync()
    {
        if (!VoiceCallHandler.ActiveCalls.IsEmpty) throw new Exception("Screen test requires isolated call state.");
        try
        {
            await VoiceCallHandler.StartScreenShareAsync(kind: "camera");
            throw new Exception("Video started without a call.");
        }
        catch (InvalidOperationException) { }
        if (VoiceCallHandler.IsLocalScreenSharing) throw new Exception("Rejected video start changed sharing state.");
        // Initialize the application's OpenSSL MsQuic before System.Net.Quic loads its backend.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(QuicPunchConnection).TypeHandle);
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
                TargetHost = QuicPunchConnection.DefaultDisguiseHost,
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var dummyQcc = new QuicPunch.QuicPunch(cts.Token) { PeerTransport = new() { AutoConnectDiscoveredPeers = false } };

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

        await TestRemoteScreenControlsAsync(dummyQcc, handlerServer, handlerClient, serverPeer, clientPeer);

        // 4. Trigger Screen Share Stop
        var cameraStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        handlerServer.OnScreenShareStarted += (peer, _, _, _, _) => cameraStarted.TrySetResult(VoiceCallHandler.ActiveCalls[peer.Id].RemoteVideoKind);
        await VoiceCallHandler.StartScreenShareAsync(640, 480, 1, 30, kind: "camera");
        if (await cameraStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)) != "camera") throw new Exception("Camera source kind was lost across QUIC.");
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

    private static async Task TestRemoteScreenControlsAsync(QuicPunch.QuicPunch qcc, VoiceCallHandler emitter,
        VoiceCallHandler viewer, PeerInfo emitterPeer, PeerInfo viewerPeer)
    {
        string directory = Path.Combine(Path.GetTempPath(), "qp_screen_control_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var store = new QuicPunch.Helpers.PeerStore(Path.Combine(directory, "peers.db"), watch: false);
        typeof(QuicPunch.QuicPunch).GetProperty(nameof(qcc.PeerStore))!.SetValue(qcc, store);
        try
        {
            var hash = viewerPeer.CertHash;
            store.AddOrUpdate([IPAddress.Loopback], [4333], hash, save: true);
            emitter.ReportBrowserScreenSettings(new(), ["h264", "vp9"]);
            var caps = await viewer.RequestScreenControlAsync(emitterPeer.Id, "capabilities");
            if (!caps.Success || caps.Capabilities?.Backend != "browser" || !caps.Capabilities.Codecs.Contains("vp9") || caps.Capabilities.CanConfigure)
                throw new Exception("Emitter did not report its own capabilities and permission state.");
            var denied = await viewer.RequestScreenControlAsync(emitterPeer.Id, "configure", caps.Capabilities.Revision, new());
            if (denied.Code != "Denied") throw new Exception("Friend changed remote screen settings without permission.");
            store.SetProfile(hash, QuicPunch.Helpers.PeerProfile.Administrator);
            caps = await viewer.RequestScreenControlAsync(emitterPeer.Id, "capabilities");
            if (!caps.Capabilities!.CanConfigure) throw new Exception("Administrator cannot configure remote transmission.");
            var settings = new ScreenTransmissionSettings("vp9", 1280, 720, 30, 4_000_000);
            var conflict = await viewer.RequestScreenControlAsync(emitterPeer.Id, "configure", caps.Capabilities.Revision - 1, settings);
            if (conflict.Code != "Conflict") throw new Exception("Stale configuration overwrote the emitter's settings.");
            var unsupported = await viewer.RequestScreenControlAsync(emitterPeer.Id, "configure", caps.Capabilities.Revision,
                settings with { Codec = "hevc" });
            if (unsupported.Code != "Unsupported") throw new Exception("Unavailable codec was accepted.");
            bool acceptBrowserUpdate = true;
            emitter.OnBrowserScreenSettingsRequested += (senderId, requestId, requested) =>
            {
                _ = Task.Run(async () =>
                {
                    if (acceptBrowserUpdate)
                    {
                        await VoiceCallHandler.StartScreenShareAsync(requested.Width, requested.Height,
                            requested.Codec == "vp9" ? (byte)3 : (byte)1, (byte)requested.Fps);
                        emitter.ReportBrowserScreenSettings(requested, ["h264", "vp9"]);
                    }
                    emitter.CompleteBrowserScreenUpdate(requestId, acceptBrowserUpdate);
                });
            };
            var updated = await viewer.RequestScreenControlAsync(emitterPeer.Id, "configure", caps.Capabilities.Revision, settings);
            if (!updated.Success || updated.Capabilities!.Current != settings || updated.Capabilities.Revision <= caps.Capabilities.Revision)
                throw new Exception("Remote configuration was not acknowledged with the applied settings.");
            acceptBrowserUpdate = false;
            var failed = await viewer.RequestScreenControlAsync(emitterPeer.Id, "configure", updated.Capabilities.Revision, settings with { Fps = 60 });
            if (failed.Success || failed.Code != "Failed") throw new Exception("A failed browser update was reported as applied.");
            // Admin session approval is also reusable for applications outside QuicNet's built-in GUID map.
            if (!await qcc.AuthorizeIncomingSessionAsync(viewerPeer, Guid.NewGuid(), QuicPunch.QuicPunch.TransportType.Wan, CancellationToken.None))
                throw new Exception("Administrator required interactive application approval.");
            store.SetProfile(hash, QuicPunch.Helpers.PeerProfile.Friend);
            denied = await viewer.RequestScreenControlAsync(emitterPeer.Id, "configure", updated.Capabilities.Revision, settings);
            if (denied.Code != "Denied") throw new Exception("Existing call retained revoked administrator rights.");
            await VoiceCallHandler.StopScreenShareAsync();
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            emitter.OnScreenShareRequested += _ => asked.TrySetResult();
            var request = await viewer.RequestScreenControlAsync(emitterPeer.Id, "start");
            if (request.Code != "ConsentRequired" || !asked.Task.IsCompleted)
                throw new Exception("A friend's screen request did not require local consent.");
            if (VoiceCallHandler.IsLocalScreenSharing) throw new Exception("Friend started capture without local approval.");
        }
        finally
        {
            typeof(QuicPunch.QuicPunch).GetProperty(nameof(qcc.PeerStore))!.SetValue(qcc, null);
            store.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
