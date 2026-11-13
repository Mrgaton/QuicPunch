using QuicPunch.Helpers;
using System.Net;
using System.Text.Json;
using QuicNet.Protocols;
using QuicNet.Settings;
using QuicNet.Updates;
using QuicNet.WebUi;

namespace QuicNet;

internal static class ReleaseSmokeTest
{

    public static async Task RunAsync()
    {
        string data = Path.Combine(Path.GetTempPath(), "QuicNet-release-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            _ = UpdateManifest.ReadPublicKey();
            PeerStore.VerifyAotJsonContract();
            if (OperatingSystem.IsWindows())
            {
                using var probes=typeof(QuicNet.Video.NativeVideoRuntime).Assembly.GetManifestResourceStream("QuicNet.NativeVideo.Probes.json")!;
                var fixtures=JsonSerializer.Deserialize(probes,QuicNet.Video.NativeVideoJsonContext.Default.NativeVideoFixtureArray)!;
                foreach(var fixture in fixtures)
                {
                    var result=QuicNet.Video.NativeVideoRuntime.Probe(fixture,false,0,null);
                    if(!result.Success)throw new InvalidOperationException("Native FFmpeg "+fixture.codec+": "+result.Error);
                }
                Console.WriteLine("Verified native FFmpeg H.264, HEVC, VP9 and AV1 decoding from the isolated release.");
            }
            Console.WriteLine($"Verified {QuicNet.Serialization.AotContractValidation.Run()} generated JSON contracts.");
            if (!MsQuicNativeLoader.IsCustomLibraryLoaded) throw new InvalidOperationException("QUIC no disponible.");
            await VerifyQuicConfigurationAsync();
            // Prove that the actual AOT binary can sanitize a legacy Tor certificate
            // without replacing the signing identity pinned by existing contacts.
            using var legacyTorEcdh = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            using var legacyTorCert = CertManager.GenerateIdentityCertificate("legacy-private-host", "legacy-private-user", legacyTorEcdh);
            File.WriteAllBytes(Path.Combine(data, "tor_peerCert.pfx"), legacyTorCert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
            File.WriteAllBytes(Path.Combine(data, "tor_ecdhKey.key"), legacyTorEcdh.ExportPkcs8PrivateKey());
            await using var node = new QuicPunch.QuicPunch(appDataPath: data) { WanEnabled = false, EnableUpnp = false };
            if (!node.TorCurrentPeer.Name!.StartsWith("tor-") ||
                node.TorCertManager.PeerCertificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.UpnName, false) != "" ||
                !node.TorCertManager.PeerCertificate.GetPublicKey().AsSpan().SequenceEqual(legacyTorCert.GetPublicKey()))
                throw new InvalidOperationException("La migración privada de identidad Tor no conservó las claves o expuso un nombre real.");
            Console.WriteLine("Verified Tor identity metadata migration with unchanged signing key.");
            // Android release disables reflection; Native AOT must exercise the same explicit metadata path.
            node.CurrentPeer.Addresses = [IPAddress.Parse("198.51.100.31")];
            node.CurrentPeer.PortArray = [45678];
            node.InvalidateTokenCache();
            string announcement = node.Discovery.GetWanNostrDiscoveryPayload()
                ?? throw new InvalidOperationException("WAN discovery announcement serialization failed.");
            using (var discoveryJson = JsonDocument.Parse(announcement))
            {
                if (!discoveryJson.RootElement.TryGetProperty("wan", out var endpoint) || string.IsNullOrWhiteSpace(endpoint.GetString()))
                    throw new InvalidOperationException("WAN discovery announcement contains no endpoint token.");
            }
            Console.WriteLine("Verified generated WAN discovery announcement JSON.");
            node.ReputationFilter?.Dispose(); node.ReputationFilter = null;
            if (OperatingSystem.IsWindows()) using (var output = new QuicPunch.Audio.WindowsWaveAudioBackend())
            {
                // Never route release tests to VR headsets or their virtual endpoints.
                var device = output.GetOutputDevices().FirstOrDefault(d => !d.IsDefault &&
                    !new[] { "Quest", "Oculus", "Virtual", "Headset" }.Any(name => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
                if (device != null)
                {
                    if (!output.StartPlayback(device.Id)) throw new InvalidOperationException(output.PlaybackError);
                    for (int i = 0; i < 3; i++) output.PlaySamples(new short[960]);
                    // The backend adds its initial 10 ms scheduling guard before the three buffers.
                    if (output.SubmittedPlaybackFrames != 4)
                        throw new InvalidOperationException("La salida WinMM descarta los primeros buffers de audio.");
                    using var audioTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    while (output.CompletedPlaybackFrames < 4) await Task.Delay(10, audioTimeout.Token);
                    Console.WriteLine("Verified native audio output: first buffers queued and completed (silence).");
                }
            }
            using var voice = new VoiceCallHandler(node);
            if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            {
                if (typeof(NativeAssetBundle).Assembly.GetManifestResourceNames()
                    .Any(name => name.Equals("QuicNet.Native.ffmpeg.exe", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("FFmpeg debe instalarse por separado, no incluirse en el EXE AOT.");
                var installation = await QuicNet.Video.FfmpegInstallation.GetStatusAsync(true, CancellationToken.None);
                Console.WriteLine(installation.installed ? "Verified external FFmpeg detection." : "Verified optional FFmpeg: installation guidance available.");
            }
            using var speed = new SpeedTestHandler();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var updater = new AutoUpdateService(data, "win-x64", (_, _, _) => Task.FromResult(false));
            // Obtain an ephemeral available loopback port without disturbing the running app.
            var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var server = new WebUiServer(node, new ChatHandler(), null, voice, speed,
                new AppPreferencesStore(Path.Combine(data, "settings.json")), cts, port: port) { Updates = updater };
            server.Start();
            try
            {
                using var http = new HttpClient();
                foreach (string route in new[] { "", "api/status", "api/capabilities", "api/updates", "api/profile", "api/voice/state", "api/voice/devices", "api/speedtest/status", "updates-help.html" })
                {
                    string body = await http.GetStringAsync(server.Url + route, cts.Token);
                    if (route.StartsWith("api/")) { if (body.Length == 0) throw new InvalidDataException(route + " returned no JSON: " + string.Join(" | ", WebUiServer.EventLogs)); using var document = JsonDocument.Parse(body); }
                    else if (!body.Contains("<!doctype html>", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("HTML incompleto: " + route + " => " + body[..Math.Min(body.Length, 1500)] + " LOG: " + string.Join(" | ", WebUiServer.EventLogs));
                }
                using (var media = JsonDocument.Parse(await http.GetStringAsync(server.Url + "api/voice/media-capabilities?refresh=1", cts.Token)))
                {
                    var ffmpeg = media.RootElement.GetProperty("ffmpeg");
                    if (ffmpeg.GetProperty("platform").GetString() != (OperatingSystem.IsLinux() ? "linux" : "windows") ||
                        (!ffmpeg.GetProperty("installed").GetBoolean() && ffmpeg.GetProperty("options").GetArrayLength() == 0))
                        throw new InvalidDataException("AOT media API must expose external FFmpeg detection or installation guidance.");
                }
                Console.WriteLine("Verified AOT FFmpeg installation API.");
                string html = await http.GetStringAsync(server.Url, cts.Token);
                if (!html.Contains("id=\"updateVersion\"")) throw new InvalidDataException("Missing embedded application UI.");
                foreach (System.Text.RegularExpressions.Match script in System.Text.RegularExpressions.Regex.Matches(html, "<script[^>]+src=\"(/[^\"]+)\""))
                {
                    string path = script.Groups[1].Value;
                    using var resource = await http.GetAsync(new Uri(new Uri(server.Url), path), cts.Token);
                    resource.EnsureSuccessStatusCode();
                    if (!(resource.Content.Headers.ContentType?.MediaType?.Contains("javascript") ?? false))
                        throw new InvalidDataException("Missing published UI script: " + path);
                }
                Console.WriteLine("Verified every published application script.");
                string csrf = System.Text.RegularExpressions.Regex.Match(html, "const csrf=\\\"([0-9A-F]+)\\\"").Groups[1].Value;
                if (csrf.Length != 64) throw new InvalidDataException("Missing UI CSRF bootstrap.");
                http.DefaultRequestHeaders.Add("X-QuicPunch-CSRF", csrf);
                foreach (var action in new[] { ("api/profile", "{\"displayName\":\"Release Profile\"}"), ("api/updates/automatic", "{\"enabled\":false}") })
                {
                    using var response = await http.PostAsync(server.Url + action.Item1, new StringContent(action.Item2, System.Text.Encoding.UTF8, "application/json"), cts.Token);
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                }
                byte[] avatarWasm = await http.GetByteArrayAsync(server.Url + "vendor/avif/codec/enc/avif_enc.wasm", cts.Token);
                if (avatarWasm.Length < 8 || !avatarWasm.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 97, 115, 109 }))
                    throw new InvalidDataException("The single-file app does not contain the AVIF encoder WASM.");
                string avatarWorker = await http.GetStringAsync(server.Url + "avatar-encode-worker.js", cts.Token);
                if (!avatarWorker.Contains("speed: animated ? 6 : 5", StringComparison.Ordinal)) throw new InvalidDataException("AVIF must retain the configured encoding speeds (6 for animations, 5 for stills).");
                Console.WriteLine("Verified AOT profile API and offline AVIF encoder assets.");
                using var socket = new System.Net.WebSockets.ClientWebSocket();
                await socket.ConnectAsync(new Uri(server.Url.Replace("http://", "ws://") + "ws?csrf=" + csrf), cts.Token);
                for (int message = 0; message < 2; message++)
                {
                    using var payload = new MemoryStream();
                    var buffer = new byte[65536];
                    System.Net.WebSockets.WebSocketReceiveResult frame;
                    do { frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token); payload.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
                    using var json = JsonDocument.Parse(payload.ToArray());
                }
                await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "release check", cts.Token);
            }
            finally { server.Stop(); }
            Console.WriteLine("Release smoke test passed: native assets, QUIC, SQLite, identity, preferences, embedded UI and update API.");
        }
        finally { try { Directory.Delete(data, true); } catch { } }
    }
    internal static async Task VerifyQuicConfigurationAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=release-quic-test", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var original = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            original.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null,
            System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
        byte[] pin = Sha3Crypto.Hash256(certificate.GetPublicKey());
        using var reservation = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        ushort port = (ushort)((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        await using var serverNative = QuicPunch.MsQuicBridgeTransport.Create(certificate, pin);
        serverNative.Listen(port);
        await using var clientNative = QuicPunch.MsQuicBridgeTransport.Create(certificate, pin, new IPEndPoint(IPAddress.Loopback, port));
        clientNative.Connect(new IPEndPoint(IPAddress.Loopback, port), 0);
        await Task.WhenAll(serverNative.WaitConnectedAsync(stop.Token), clientNative.WaitConnectedAsync(stop.Token));
        await using var server = QuicPunch.QuicConnection.FromTransport(serverNative);
        await using var client = QuicPunch.QuicConnection.FromTransport(clientNative);
        if (clientNative.NegotiatedTlsGroup != 4588 || serverNative.NegotiatedTlsGroup != 4588)
            throw new InvalidOperationException("The published native transport must negotiate hybrid TLS on both peers.");
        if (!client.TryGetCongestionControl(out var clientCc) || clientCc != QuicCongestionAlgorithm.Bbr ||
            !MsQuicTuner.TryGetCongestionControl(server, out var serverCc) || serverCc != QuicCongestionAlgorithm.Bbr ||
            !MsQuicTuner.TryApplyOptimalTuning(server))
            throw new InvalidOperationException("AOT must start both native controllers with BBR and apply runtime tuning successfully.");
        var receive = server.AcceptInboundStreamAsync(stop.Token);
        await using var output = await client.OpenOutboundStreamAsync(QuicPunch.QuicStreamType.Bidirectional, stop.Token);
        await output.WriteAsync(new byte[] { 42 }, stop.Token);
        await using var input = await receive;
        byte[] data = new byte[1];
        await input.ReadExactlyAsync(data, stop.Token);
        if (data[0] != 42) throw new InvalidDataException("Native AOT QUIC stream transfer failed.");
        var serverDatagrams = server.DatagramChannel ?? throw new InvalidOperationException("Missing native server datagram channel.");
        var clientDatagrams = client.DatagramChannel ?? throw new InvalidOperationException("AOT client datagram callback was not attached.");
        if (!clientDatagrams.Send(new byte[] { 43 })) throw new InvalidOperationException("AOT client cannot send QUIC datagrams.");
        var incomingDatagram = await serverDatagrams.IncomingDatagrams.Reader.ReadAsync(stop.Token);
        if (incomingDatagram.Length != 1 || incomingDatagram[0] != 43)
            throw new InvalidDataException("AOT server datagram callback did not deliver its payload.");
        if (!serverDatagrams.Send(new byte[] { 44 })) throw new InvalidOperationException("AOT server cannot send QUIC datagrams.");
        incomingDatagram = await clientDatagrams.IncomingDatagrams.Reader.ReadAsync(stop.Token);
        if (incomingDatagram.Length != 1 || incomingDatagram[0] != 44)
            throw new InvalidDataException("AOT client datagram callback did not deliver its payload.");
        Console.WriteLine("Verified native QUIC handshake: BBR on both ends, runtime tuning, stream transfer and bidirectional datagrams.");
    }

}
