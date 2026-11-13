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
        if (args.FirstOrDefault() == "--serve-webui-preview") return await Fixtures.WebUiPreview.ServeAsync();
        if (args.Contains("--loopback-only"))
        {
            if (args.FirstOrDefault() is not ("--test-birthday-punch" or "--test-contact-reconnect" or "--test-contact-history" or "--test-discovery-presence" or "--test-automatic-transport" or "--test-geolocation" or "--test-discovery-announcements" or "--test-watch-reliability" or "--test-rotating-logger"))
                throw new ArgumentException("Loopback isolation has not been validated for this suite.");
            Fixtures.LoopbackOnlyNetwork.Configure();
        }

        Func<Task>? test = args.FirstOrDefault() switch
        {
            "--test-native-video" => NativeVideoTests.RunAsync,
            "--test-stun-validation" => StunValidationTests.RunAsync,
            "--test-ipv6" => Ipv6ConnectivityTests.RunAsync,
            "--test-voice-mobility" => VoiceMobilityTests.RunAsync,
            "--test-watch-reliability" => WatchReliabilityTests.RunAsync,
            "--test-output" => TestOutputTests.RunAsync,
            "--test-virtual-disk" => VirtualDiskTests.RunAsync,
            "--test-notifications" => NotificationTests.RunAsync,
              "--test-geolocation" => GeolocationTests.RunAsync,
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
            "--test-wan-listener" => WanNativeProbe.RunAsync,
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
        Console.WriteLine("  --test-geolocation");
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
        Console.WriteLine("  --test-stun-validation");
        Console.WriteLine("  --test-voice-mobility");
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

    private static Task TestQuicCongestionControlTuning() => NativeBridgeTests.RunAsync();
}
