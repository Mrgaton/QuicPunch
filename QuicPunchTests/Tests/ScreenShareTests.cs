using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuicPunch;
using QuicPunch.Multiplexer;
using QuicNet.Protocols;
using QuicNet.Video;
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
        TestDecoderProbeContracts();
        TestRemoteDesktopInputValidation();
        await ScreenPreviewTransportTests.RunAsync();
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

    private static void TestDecoderProbeContracts()
    {
        var request = Guid.NewGuid();
        var reply = new QuicNet.Serialization.ScreenDecoderProbeReplyMessage(request, true,
            new("hvc1.1.6.L120.90", 1920, 1080, "prefer-software", "GPU rechazada; CPU aceptada."));
        var json = System.Text.Json.JsonSerializer.Serialize(reply, QuicNet.Serialization.SharedJson.Options);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<QuicNet.Serialization.ScreenDecoderProbeReplyMessage>(json, QuicNet.Serialization.SharedJson.Options);
        if (parsed != reply) throw new Exception("Decoder diagnostics did not survive the generated JSON contract.");
        // Old receivers send only the two original fields. Their replies remain valid.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<QuicNet.Serialization.ScreenDecoderProbeReplyMessage>(
            "{\"requestId\":\"" + request + "\",\"supported\":true}", QuicNet.Serialization.SharedJson.Options);
        if (legacy?.requestId != request || legacy.supported != true || legacy.diagnostic != null)
            throw new Exception("Legacy decoder replies must not require diagnostics.");
    }

    private static void TestRemoteDesktopInputValidation()
    {
        var click = new RemoteDesktopInput("down", .25, .75, 0);
        var text = new RemoteDesktopInput("text", text: "móvil 🌍");
        if (!WindowsRemoteInput.IsValid(click) || !WindowsRemoteInput.IsValid(text) ||
            !WindowsRemoteInput.IsValid(new("keydown", key: "KeyA")) ||
            !WindowsRemoteInput.IsValid(new("keyup", key: "ControlLeft")) ||
            WindowsRemoteInput.IsValid(click with { x = double.NaN }) ||
            WindowsRemoteInput.IsValid(click with { y = -0.1 }) ||
            WindowsRemoteInput.IsValid(click with { button = 3 }) ||
            WindowsRemoteInput.IsValid(new("text", text: "line\nbreak")) ||
            WindowsRemoteInput.IsValid(new("key", key: "Ctrl+Alt+F12")) ||
            WindowsRemoteInput.IsValid(new("keydown", key: "F25")))
            throw new Exception("Remote input validation did not enforce the input shape.");
    }

    private static async Task Test_ScreenShare_FramingAndStreamingAsync()
    {
        Console.Write("[TEST] VoiceCallHandler screen sharing framing and frame delivery... ");

        using var serverCert = GenerateTestCert();
        using var clientCert = GenerateTestCert();

        using var setupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var nativePair = await Fixtures.NativeBridgePair.CreateAsync(serverCert, clientCert, setupDeadline.Token);
        var serverQuicConn = nativePair.Server;
        var clientQuicConn = nativePair.Client;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var dummyQcc = new QuicPunch.QuicPunch(cts.Token) { PeerTransport = new() { AutoConnectDiscoveredPeers = false } };

        using var handlerServer = new VoiceCallHandler(dummyQcc, new QuicPunch.Audio.NullAudioBackend());
        using var handlerClient = new VoiceCallHandler(dummyQcc, new QuicPunch.Audio.NullAudioBackend());

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

        // The optional tail preserves the legacy masks/control byte and bounds GPU hints to supported codecs.
        await VoiceCallHandler.SetLocalScreenCodecCapabilitiesAsync(2, 2, 34);
        for (int i = 0; i < 50 && VoiceCallHandler.ActiveCalls.Values.Any(s => s.RemoteScreenDecoderHardwarePreferenceMask != 2); i++) await Task.Delay(20);
        if (VoiceCallHandler.ActiveCalls.Values.Any(s => !s.RemoteScreenCapabilitiesReceived || s.RemoteScreenDecoderHardwarePreferenceMask != 2 || s.RemoteScreenControlVersion != VoiceCallHandler.RemoteInputVersion))
            throw new Exception("Receiver GPU preferences did not survive the capability frame.");
        await VoiceCallHandler.SetLocalScreenCodecCapabilitiesAsync(2, 0);
        for (int i = 0; i < 50 && VoiceCallHandler.ActiveCalls.Values.Any(s => s.RemoteScreenDecoderHardwarePreferenceMask != null || s.RemoteScreenDecoderCodecMask != 0); i++) await Task.Delay(20);
        if (VoiceCallHandler.ActiveCalls.Values.Any(s => !s.RemoteScreenCapabilitiesReceived || s.RemoteScreenDecoderHardwarePreferenceMask != null || s.RemoteScreenDecoderCodecMask != 0))
            throw new Exception("Legacy/no-decoder report retained stale GPU support or became unknown.");

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

        // A receiver veto must not prevent the sender from announcing/encoding.
        // Both sessions reject video first, then one becomes compatible.
        foreach (var call in VoiceCallHandler.ActiveCalls.Values) call.IsScreenDecoderApproved = false;
        var unsupportedStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compatibleFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var incompatibleAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool incompatibleVideoReceived = false;
        handlerServer.OnScreenShareStarted += (_, _, _, codec, _) => { if (codec == 5) unsupportedStart.TrySetResult(); };
        handlerClient.OnScreenFrameReceived += (_, _, _) => compatibleFrame.TrySetResult();
        handlerServer.OnScreenFrameReceived += (_, _, _) => incompatibleVideoReceived = true;
        handlerServer.OnScreenAudioReceived += (_, _) => incompatibleAudio.TrySetResult();
        await VoiceCallHandler.StartScreenShareAsync(1920, 1080, codecId: 5, fps: 60);
        await unsupportedStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!VoiceCallHandler.IsLocalScreenSharing || VoiceCallHandler.LocalScreenCodecId != 5)
            throw new Exception("An incompatible receiver stopped or changed the selected codec.");
        VoiceCallHandler.ActiveCalls[clientPeer.Id].IsScreenDecoderApproved = true;
        await VoiceCallHandler.SendScreenFrameAsync(dummyJpeg);
        await VoiceCallHandler.SendScreenAudioAsync(dummyPcm);
        await compatibleFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await incompatibleAudio.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (incompatibleVideoReceived) throw new Exception("Undecodable video was sent to the incompatible receiver.");
        foreach (var call in VoiceCallHandler.ActiveCalls.Values) call.IsScreenDecoderApproved = true;

        await TestRemoteScreenControlsAsync(dummyQcc, handlerServer, handlerClient, serverPeer, clientPeer);

        // 4. Trigger Screen Share Stop
        var cameraStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        handlerServer.OnScreenShareStarted += (peer, _, _, _, _) => cameraStarted.TrySetResult(VoiceCallHandler.ActiveCalls[peer.Id].RemoteVideoKind);
        await VoiceCallHandler.StartScreenShareAsync(640, 480, 1, 30, kind: "camera");
        if (await cameraStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)) != "camera") throw new Exception("Camera source kind was lost across QUIC.");
        var decoderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDecoder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int decoderCalls = 0;
        Task SlowDecoder(PeerInfo _, byte codec, byte[] data)
        { Interlocked.Increment(ref decoderCalls); decoderEntered.TrySetResult(); return releaseDecoder.Task; }
        handlerServer.OnScreenFrameReceivedAsync += SlowDecoder;
        try
        {
            await VoiceCallHandler.SendScreenFrameAsync(dummyJpeg);
            await decoderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(350);
            for (int i = 0; i < 16; i++) await VoiceCallHandler.SendScreenFrameAsync(dummyJpeg);
            await Task.Delay(300);
            if (decoderCalls != 1) throw new Exception("A stalled decoder accumulated media callbacks after expiry.");
            await VoiceCallHandler.StopScreenShareAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await tcsStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { releaseDecoder.TrySetResult(); handlerServer.OnScreenFrameReceivedAsync -= SlowDecoder; }
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
            store.AddOrUpdate([IPAddress.Loopback], [4334], emitterPeer.CertHash, save: true);
            emitter.ReportBrowserScreenSettings(new(), ["h264", "vp9"]);
            var caps = await viewer.RequestScreenControlAsync(emitterPeer.Id, "capabilities");
            if (!caps.Success || caps.Capabilities?.Backend != "browser" || !caps.Capabilities.Codecs.Contains("vp9") || caps.Capabilities.CanConfigure)
                throw new Exception("Emitter did not report its own capabilities and permission state.");
            var denied = await viewer.RequestScreenControlAsync(emitterPeer.Id, "configure", caps.Capabilities.Revision, new());
            if (denied.Code != "Denied") throw new Exception("Friend changed remote screen settings without permission.");
            store.SetProfile(hash, QuicPunch.Helpers.PeerProfile.Administrator);
            var refusedInput = new TaskCompletionSource<RemoteInputStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.RemoteInputChanged += status =>
            {
                if (status.message != null) refusedInput.TrySetResult(status);
            };
            await viewer.RequestRemoteInputAsync(emitterPeer.Id);
            var refusal = await refusedInput.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (refusal.session != null || emitter.GetRemoteInputStatus().session != null)
                throw new Exception("Administrator obtained a remote-input session for a browser capture.");
            try
            {
                await viewer.ForwardRemoteInputAsync(Guid.NewGuid(), [new("text", text: "test")]);
                throw new Exception("Input was forwarded without an active grant.");
            }
            catch (ArgumentException) { }
            try
            {
                await viewer.SendRemoteClipboardAsync(Guid.NewGuid(), "text");
                throw new Exception("Clipboard text was sent without an active grant and opt-in.");
            }
            catch (ArgumentException) { }
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
            emitter.OnScreenShareRequested += (_, _) => asked.TrySetResult();
            emitter.GetPlatformScreenCapture = () => new TestPlatformCapture();
            int platformRequests = 0;
            emitter.StartRemotePlatformScreen = (peer, forControl, ct) =>
            {
                if (peer.Id != viewerPeer.Id || forControl || !ct.CanBeCanceled)
                    throw new Exception("Platform request lost the caller identity or call lifetime.");
                platformRequests++;
                return Task.FromException(new UnauthorizedAccessException("Android rechazó la captura."));
            };
            store.SetAutomaticPermission(hash, "voice", true);
            var request = await viewer.RequestScreenControlAsync(emitterPeer.Id, "start");
            if (request.Code != "ConsentRequired" || !asked.Task.IsCompleted || platformRequests != 0)
                throw new Exception("Automatic call approval granted automatic screen sharing.");
            store.SetProfile(hash, QuicPunch.Helpers.PeerProfile.Administrator);
            request = await viewer.RequestScreenControlAsync(emitterPeer.Id, "start");
            if (request.Code != "Denied" || platformRequests != 1 || VoiceCallHandler.IsLocalScreenSharing)
                throw new Exception("Administrator did not reach platform capture or platform denial was ignored.");
            emitter.StartRemotePlatformScreen = (_, _, _) =>
                VoiceCallHandler.StartScreenShareAsync(720, 1280, 1, 30, viewerPeer.Id);
            request = await viewer.RequestScreenControlAsync(emitterPeer.Id, "start");
            if (!request.Success || VoiceCallHandler.LocalScreenTargetPeerId != viewerPeer.Id)
                throw new Exception("Authorized platform capture did not start a targeted transmission.");
            var platformStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.OnScreenShareStopped += peer =>
            {
                if (peer.Id == emitterPeer.Id) platformStopped.TrySetResult();
            };
            await VoiceCallHandler.StopScreenShareAsync();
            await platformStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await TestRemoteInputConsentAsync(emitter, viewer, emitterPeer, viewerPeer, store);
        }
        finally
        {
            emitter.GetPlatformScreenCapture = null;
            emitter.StartRemotePlatformScreen = null;
            typeof(QuicPunch.QuicPunch).GetProperty(nameof(qcc.PeerStore))!.SetValue(qcc, null);
            store.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task TestRemoteInputConsentAsync(VoiceCallHandler emitter, VoiceCallHandler viewer,
        PeerInfo emitterPeer, PeerInfo viewerPeer, QuicPunch.Helpers.PeerStore store)
    {
        var platform = new TestPlatformCapture();
        emitter.StartRemotePlatformScreen = async (peer, forControl, _) =>
        {
            if (!forControl || peer.Id != viewerPeer.Id)
                throw new Exception("A remote-control request did not request targeted full-screen capture.");
            await VoiceCallHandler.StartScreenShareAsync(720, 1280, 1, 30, peer.Id);
            emitter.SetPlatformRemoteInput(platform, 1);
        };

        var active = new TaskCompletionSource<RemoteInputView>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.RemoteInputChanged += status =>
        {
            if (status.session is { active: true } session) active.TrySetResult(session);
        };
        bool neededPrompt = false;
        emitter.RemoteInputChanged += status => neededPrompt |= status.session is { incoming: true, active: false };
        try
        {
            await viewer.RequestRemoteInputWithCaptureAsync(emitterPeer.Id);
            var session = await active.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (neededPrompt || session.clipboardAllowed ||
                emitter.GetRemoteInputStatus().session is not { active: true, administrator: true, clipboardAllowed: false })
                throw new Exception("Administrator was not granted input automatically with clipboard disabled.");
            await viewer.ForwardRemoteInputAsync(session.id, [new("key", key: "Enter")]);
            await platform.Input.Task.WaitAsync(TimeSpan.FromSeconds(5));

            store.SetProfile(viewerPeer.CertHash, QuicPunch.Helpers.PeerProfile.Friend);
            await platform.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (emitter.GetRemoteInputStatus().session != null)
                throw new Exception("An administrator retained automatic control after demotion.");

            // The host intentionally rate-limits new requests.
            await Task.Delay(5000);
            var pending = new TaskCompletionSource<RemoteInputView>(TaskCreationOptions.RunContinuationsAsynchronously);
            emitter.RemoteInputChanged += status =>
            {
                if (status.session is { incoming: true, active: false } waiting) pending.TrySetResult(waiting);
            };
            await viewer.RequestRemoteInputAsync(emitterPeer.Id);
            var manual = await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await emitter.RespondRemoteInputAsync(manual.id, true, confirmed: false);
                throw new Exception("A friend received control without explicit confirmation.");
            }
            catch (ArgumentException) { }
            await emitter.RespondRemoteInputAsync(manual.id, true, confirmed: true);
            if (emitter.GetRemoteInputStatus().session is not { active: true, administrator: false })
                throw new Exception("An explicitly confirmed friend was denied control.");
            emitter.StopRemoteInput(manual.id);
        }
        finally
        {
            if (emitter.GetRemoteInputStatus().session is { } session) emitter.StopRemoteInput(session.id);
            emitter.SetPlatformRemoteInput(null, 0);
            await VoiceCallHandler.StopScreenShareAsync();
        }
    }

    private sealed class TestPlatformCapture : IPlatformScreenCapture, IPlatformRemoteInput
    {
        public TaskCompletionSource Input { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RemoteInputArea? RemoteInputArea => new(0, 0, 720, 1280);
        public IRemoteInputDevice CreateRemoteInputDevice() => new TestInputDevice(this);
        public Task<NativeScreenProfile> StartAsync(ScreenEncoderOptions options, Func<byte[], Task> output,
            Action<string?> stopped, CancellationToken ct = default) => throw new NotSupportedException();
        public void Resume() { }
        public Task StopAsync() => Task.CompletedTask;

        private sealed class TestInputDevice(TestPlatformCapture platform) : IRemoteInputDevice
        {
            public void Apply(RemoteInputArea area, RemoteDesktopInput input) => platform.Input.TrySetResult();
            public void Release() { }
            public void Dispose() => platform.Disposed.TrySetResult();
        }
    }
}
