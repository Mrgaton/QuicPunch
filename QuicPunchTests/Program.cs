using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using QuicPunch.Helpers;
using QuicPunchTests.Tests;

namespace QuicPunchTests;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Console.Error.WriteLine($"[FATAL UNHANDLED EXCEPTION] {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Console.Error.WriteLine($"[UNOBSERVED TASK EXCEPTION] {e.Exception}");
            e.SetObserved();
        };

        if (args.FirstOrDefault() == "--test-discovery-announcements")
            AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        Console.OutputEncoding = Encoding.UTF8;
        if (args.FirstOrDefault() == "--serve-geolocation-workbench") return await GeolocationWorkbenchTests.ServeAsync();
        if (args.Contains("--loopback-only"))
        {
            if (args.FirstOrDefault() is not ("--test-birthday-punch" or "--test-contact-reconnect" or "--test-contact-history" or "--test-discovery-presence" or "--test-automatic-transport" or "--test-geolocation" or "--test-discovery-announcements" or "--test-watch-reliability" or "--test-rotating-logger"))
                throw new ArgumentException("Loopback isolation has not been validated for this suite.");
            Fixtures.LoopbackOnlyNetwork.Configure();
        }

        Func<Task>? test = args.FirstOrDefault() switch
        {
            "--test-watch-reliability" => WatchReliabilityTests.RunAsync,
            "--test-output" => TestOutputTests.RunAsync,
            "--test-virtual-disk" => VirtualDiskTests.RunAsync,
            "--test-notifications" => NotificationTests.RunAsync,
              "--test-geolocation" => GeolocationTests.RunAsync,
            "--test-geolocation-workbench" => GeolocationWorkbenchTests.RunAsync,
            "--test-updates" => AutoUpdateTests.RunAsync,
            "--test-discovery-announcements" => DiscoveryAnnouncementTests.RunAsync,
            "--test-aot-json" => () => { QuicPunch.Helpers.PeerStore.VerifyAotJsonContract(); Console.WriteLine($"Verified {QuicNet.Serialization.AotContractValidation.Run()} JSON contracts."); return Task.CompletedTask; },
            "--test-critical-reliability" => CriticalReliabilityTests.RunAsync,
            "--test-resumable-files" => ResumableFileTests.RunAsync,
            "--test-session-recovery" => SessionRecoveryTests.RunAsync,
            "--test-vpn-policy" => VpnPolicyTests.RunAsync,
            "--test-vpn-orchestration" => VpnOrchestrationTests.RunAsync,
            "--test-vpn-broker" => VpnBrokerTests.RunAsync,
            "--test-connection-recovery" => ConnectionRecoveryTests.RunAsync,
            "--test-automatic-transport" => AutomaticPeerTransportTests.RunAsync,
            "--test-ffmpeg-installation" => FfmpegInstallationTests.RunAsync,
            "--test-ffmpeg-screen" => FfmpegScreenEncoderTests.RunAsync,
            "--test-platform-video" => PlatformVideoTests.RunAsync,
            "--test-screen-audio" => ScreenAudioTests.RunAsync,
            "--test-peer-state" => PeerConnectionStateTests.RunAsync,
            "--test-connectivity-security" => ConnectivitySecurityTests.RunAsync,
            "--test-punch-packet" or "--test-hole-packet" => QuicPunchHolePacketTests.RunAsync,
            "--test-multiplexer" or "--test-mux" => QuicPeerMultiplexerTests.RunAsync,
            "--test-speedtest" or "--test-speed-test" or "--test-benchmark" => SpeedTestTests.RunAsync,
            "--test-screenshare" or "--test-screen-share" or "--test-screen" => ScreenShareTests.RunAsync,
            "--test-datagram-categorization" or "--test-datagram-framing" => QuicDatagramCategorizationTests.RunAllTestsAsync,
            "--test-antireplay" => AntiReplayTests.RunAsync,
            "--test-security" => SecurityDiscoveryTests.RunAsync,
            "--test-tor-privacy" => TorIdentityPrivacyTests.RunAsync,
            "--test-nostr-trust" => SecurityDiscoveryTests.RunNostrTrustTestsAsync,
            "--test-singleflight" => SecurityDiscoveryTests.RunSingleFlightTestAsync,
            "--test-handshake-cancellation" => HandshakeCancellationLifecycleTests.RunAsync,
            "--test-webui-security" => WebUiSecurityTests.RunAsync,
            "--test-rotating-logger" => RotatingLoggerTests.RunAsync,
            "--test-peer-acl" => PeerAccessControllerTests.RunAsync,
            "--test-ip-filter" or "--test-ipsum" => IpReputationFilterTests.RunAsync,
            "--test-nostr-mailbox" or "--test-mailbox" => NostrMailboxTests.RunAsync,
            "--test-lan-discovery" => SecurityDiscoveryTests.TestLanDiscoveryWithDifferentControlPorts,
            "--test-birthday-punch" => BirthdayPunchTests.RunAsync,
            "--test-lan-ports" => LanPortAdvertisementTests.RunAsync,
            "--test-nat-pin-coordinator" => NatPinCoordinatorTests.RunAsync,
            "--test-lan-pooling" => VirtualLanPoolingTests.RunAsync,
            "--test-vpn-adapter" => VpnAdapterTests.RunAsync,
            "--benchmark-quic" => QuicPerformanceTests.RunAsync,
            "--benchmark-quic-cubic" => QuicPerformanceTests.RunCubicAsync,
            "--benchmark-quic-default" => QuicPerformanceTests.RunBaselineAsync,
            "--test-quic-bbr" or "--inspect-quic" => TestQuicCongestionControlTuning,
            "--test-quic-datagrams" => QuicDatagramTests.RunAsync,
            "--test-quic-telemetry" => QuicAdvancedNativeTests.RunAsync,
            "--test-quic-optimizations" or "--test-native-quic" => NativeQuicOptimizationsTests.RunAsync,
            "--test-pqc" or "--test-pqc-handshake" => PqcHandshakeTests.RunAsync,
            "--test-discovery-presence" => DiscoveryPresenceTests.RunAsync,
            "--test-connection-modes" => ConnectionModeTests.RunAsync,
            "--test-contact-history" => ContactHistoryTests.RunAsync,
            "--test-contact-reconnect" => AutomaticContactReconnectTests.RunAsync,
            "--test-token-readiness" => TokenReadinessTests.RunAsync,
            "--test-upnp" => UpnpTests.RunAsync,
            "--test-port-openers" => PortOpenerTests.RunAsync,
            "--test-opus" => () => { OpusCodecTests.Run(); return Task.CompletedTask; },
            "--test-native-audio" or "--test-audio" => NativeAudioServiceTests.RunAsync,
            "--test-sha3" => () => { Sha3Tests.Run(); return Task.CompletedTask; },
            "--test-tor" => () => TestTorFileShare.RunAsync(args),
            "--test-tor-cascade" or "--test-tor-fallback" => TorBypassTests.RunAsync,
            _ => null
        };

        if (test != null)
            return await TestOutput.RunAsync(args[0], test, args.Contains("--verbose"));

        if (args.Length > 0 && args[0] is not ("--help" or "-h"))
        {
            Console.Error.WriteLine($"Unknown test: {args[0]}. Use --help to list suites.");
            return 2;
        }

        Console.WriteLine("==================================================");
        Console.WriteLine("              QUICPUNCH TEST HARNESS              ");
        Console.WriteLine("==================================================");
        Console.WriteLine("Usage: dotnet run --project QuicPunchTests -- [test-flag] [--verbose]");
        Console.WriteLine("Add --verbose to show the complete test log.");
        Console.WriteLine("\nAvailable test flags:");
        Console.WriteLine("  --test-output");
        Console.WriteLine("  --test-watch-reliability");
        Console.WriteLine("  --test-geolocation / --test-geolocation-workbench");
        Console.WriteLine("  --test-virtual-disk");
        Console.WriteLine("  --test-updates");
        Console.WriteLine("  --test-critical-reliability");
        Console.WriteLine("  --test-resumable-files");
        Console.WriteLine("  --test-session-recovery");
        Console.WriteLine("  --test-vpn-policy / --test-vpn-orchestration / --test-vpn-broker");
        Console.WriteLine("  --test-connection-recovery");
        Console.WriteLine("  --test-automatic-transport");
        Console.WriteLine("  --test-contact-reconnect (persistent UDP retries; approximately one minute)");
        Console.WriteLine("  --test-peer-state");
        Console.WriteLine("  --test-connectivity-security");
        Console.WriteLine("  --test-punch-packet / --test-hole-packet");
        Console.WriteLine("  --test-multiplexer / --test-mux");
        Console.WriteLine("  --test-discovery-announcements (Android/AOT: JSON reflection disabled)");
        Console.WriteLine("  --test-speedtest / --test-speed-test / --test-benchmark");
        Console.WriteLine("  --test-screenshare / --test-screen-share");
        Console.WriteLine("  --test-screen-audio");
        Console.WriteLine("  --test-ffmpeg-screen");
        Console.WriteLine("  --test-datagram-categorization");
        Console.WriteLine("  --test-antireplay");
        Console.WriteLine("  --test-security");
        Console.WriteLine("  --test-nostr-trust / --test-tor-privacy");
        Console.WriteLine("  --test-singleflight");
        Console.WriteLine("  --test-handshake-cancellation");
        Console.WriteLine("  --test-webui-security");
        Console.WriteLine("  --test-rotating-logger");
        Console.WriteLine("  --test-peer-acl");
        Console.WriteLine("  --test-birthday-punch / --test-lan-ports / --test-lan-discovery");
        Console.WriteLine("  --test-nat-pin-coordinator");
        Console.WriteLine("  --test-lan-pooling");
        Console.WriteLine("  --test-vpn-adapter");
        Console.WriteLine("  --benchmark-quic / --benchmark-quic-default (80 ms RTT, 0.2% packet drops, 10 s duplex)");
        Console.WriteLine("  --test-quic-bbr / --inspect-quic");
        Console.WriteLine("  --test-quic-datagrams");
        Console.WriteLine("  --test-quic-telemetry");
        Console.WriteLine("  --test-quic-optimizations / --test-native-quic");
        Console.WriteLine("  --test-pqc / --test-pqc-handshake");
        Console.WriteLine("  --test-token-readiness");
        Console.WriteLine("  --test-upnp");
        Console.WriteLine("  --test-port-openers");
        Console.WriteLine("  --test-opus");
        Console.WriteLine("  --test-native-audio / --test-audio");
        Console.WriteLine("  --test-tor");
        Console.WriteLine("  --test-tor-cascade / --test-tor-fallback");
        Console.WriteLine("==================================================");
        return 0;
    }

    private static async Task TestQuicCongestionControlTuning()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("    MSQUIC DYNAMIC CONGESTION CONTROL (BBR) TEST  ");
        Console.WriteLine("==================================================");

        // Generate a quick self-signed cert for loopback QUIC
        using var rsa = RSA.Create(2048);
        var certReq = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var tempCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        using var cert = X509CertificateLoader.LoadPkcs12(tempCert.Export(X509ContentType.Pfx), null);

        var alpn = new List<System.Net.Security.SslApplicationProtocol> { new("qtest") };

        var listenerOptions = new System.Net.Quic.QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = alpn,
            ConnectionOptionsCallback = (connection, sslHello, ct) =>
            {
                return ValueTask.FromResult(new System.Net.Quic.QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    ServerAuthenticationOptions = new System.Net.Security.SslServerAuthenticationOptions
                    {
                        ServerCertificate = cert,
                        ApplicationProtocols = alpn
                    }
                });
            }
        };

        await using var listener = await System.Net.Quic.QuicListener.ListenAsync(listenerOptions);
        var listenEp = listener.LocalEndPoint;
        Console.WriteLine($"[SERVER] Listening on QUIC {listenEp}");

        var clientOptions = new System.Net.Quic.QuicClientConnectionOptions
        {
            RemoteEndPoint = listenEp,
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = alpn,
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
            }
        };

        var serverTask = listener.AcceptConnectionAsync();
        var clientConn = await System.Net.Quic.QuicConnection.ConnectAsync(clientOptions);
        var serverConn = await serverTask;
        Console.WriteLine("[SUCCESS] Local QUIC Connection established!");

        // 1. Check initial algorithm (should be CUBIC = 0)
        if (MsQuicTuner.TryGetCongestionControl(clientConn, out var initialCc))
        {
            Console.WriteLine($"[1] Initial Congestion Control: {initialCc} ({(int)initialCc})");
        }

        // A live change must not overwrite the setting while the old native controller keeps running.
        var requestedCc = initialCc == QuicCongestionAlgorithm.Bbr ? QuicCongestionAlgorithm.Cubic : QuicCongestionAlgorithm.Bbr;
        if (MsQuicTuner.TrySetCongestionControl(clientConn, requestedCc) ||
            !MsQuicTuner.TryGetCongestionControl(clientConn, out var activeCc) || activeCc != initialCc)
            throw new InvalidOperationException("Live controller switching must require reconnecting.");
        Console.WriteLine($"[2] Controller remains {activeCc}; changing it requires a prepared configuration and a new connection: PASSED");

        // 4. Test Telemetry
        bool telemOk = MsQuicTuner.TryGetTelemetry(clientConn, out var telem);
        Console.WriteLine($"[4] MsQuicTuner.TryGetTelemetry => {(telemOk ? "SUCCESS" : "FAILED")}");
        if (telemOk && telem != null)
        {
            Console.WriteLine($"    RTT: {telem.RttMs:F2}ms, MinRTT: {telem.MinRttMs:F2}ms, CWND: {telem.SendCongestionWindow}, PathMTU: {telem.PathMtu}");
        }

        await clientConn.DisposeAsync();
        await serverConn.DisposeAsync();
        Console.WriteLine("==================================================");
    }
}
