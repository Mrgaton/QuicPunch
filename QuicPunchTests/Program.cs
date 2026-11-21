using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using QuicPunch.Helpers;
using QuicPunchTests.Tests;

namespace QuicPunchTests;

internal static class Program
{
    private const string PreviewCommand = "--serve-webui-preview";

    private sealed record Suite(
        string Flag,
        Func<string[], Task> Run,
        string[]? Aliases = null,
        bool SupportsLoopback = false,
        bool DisableJsonReflection = false,
        string? Description = null);

    private static readonly Suite[] Suites =
    [
        new("--test-native-video", _ => NativeVideoTests.RunAsync()),
        new("--test-stun-validation", _ => StunValidationTests.RunAsync()),
        new("--test-wan-mobility", _ => WanMobilityTests.RunAsync(), SupportsLoopback: true),
        new("--test-ipv6", _ => Ipv6ConnectivityTests.RunAsync()),
        new("--test-voice-mobility", _ => VoiceMobilityTests.RunAsync()),
        new("--test-watch-reliability", _ => WatchReliabilityTests.RunAsync(), SupportsLoopback: true),
        new("--test-output", _ => TestOutputTests.RunAsync()),
        new("--test-virtual-disk", _ => VirtualDiskTests.RunAsync()),
        new("--test-notifications", _ => NotificationTests.RunAsync()),
        new("--test-onboarding", _ => OnboardingTests.RunAsync(), SupportsLoopback: true),
        new("--test-geolocation", _ => GeolocationTests.RunAsync(), SupportsLoopback: true),
        new("--test-updates", _ => AutoUpdateTests.RunAsync()),
        new("--test-core-logs", _ => CoreErrorReporterTests.RunAsync()),
        new("--test-discovery-announcements", _ => DiscoveryAnnouncementTests.RunAsync(), SupportsLoopback: true, DisableJsonReflection: true, Description: "Android/AOT with JSON reflection disabled"),
        new("--test-aot-json", _ => VerifyAotJsonContractAsync()),
        new("--test-critical-reliability", _ => CriticalReliabilityTests.RunAsync()),
        new("--test-resumable-files", _ => ResumableFileTests.RunAsync()),
        new("--test-session-recovery", _ => SessionRecoveryTests.RunAsync()),
        new("--test-vpn-policy", _ => VpnPolicyTests.RunAsync()),
        new("--test-vpn-orchestration", _ => VpnOrchestrationTests.RunAsync()),
        new("--test-vpn-wintun", _ => VpnOrchestrationTests.RunWintunAsync(), Description: "Isolated elevated Windows"),
        new("--test-vpn-broker", _ => VpnBrokerTests.RunAsync()),
        new("--test-connection-recovery", _ => ConnectionRecoveryTests.RunAsync()),
        new("--test-automatic-transport", _ => AutomaticPeerTransportTests.RunAsync(), SupportsLoopback: true),
        new("--test-ffmpeg-installation", _ => FfmpegInstallationTests.RunAsync()),
        new("--test-ffmpeg-screen", _ => FfmpegScreenEncoderTests.RunAsync()),
        new("--test-platform-video", _ => PlatformVideoTests.RunAsync()),
        new("--test-screen-audio", _ => ScreenAudioTests.RunAsync()),
        new("--test-peer-state", _ => PeerConnectionStateTests.RunAsync()),
        new("--test-peer-registry", _ => AvailablePeerRegistryTests.RunAsync()),
        new("--test-connectivity-security", _ => ConnectivitySecurityTests.RunAsync()),
        new("--test-punch-packet", _ => QuicPunchHolePacketTests.RunAsync(), Aliases: ["--test-hole-packet"]),
        new("--test-multiplexer", _ => QuicPeerMultiplexerTests.RunAsync(), Aliases: ["--test-mux"]),
        new("--test-speedtest", _ => SpeedTestTests.RunAsync(), Aliases: ["--test-speed-test", "--test-benchmark"]),
        new("--test-screenshare", _ => ScreenShareTests.RunAsync(), Aliases: ["--test-screen-share", "--test-screen"]),
        new("--test-datagram-categorization", _ => QuicDatagramCategorizationTests.RunAllTestsAsync(), Aliases: ["--test-datagram-framing"]),
        new("--test-antireplay", _ => AntiReplayTests.RunAsync()),
        new("--test-security", _ => SecurityDiscoveryTests.RunAsync()),
        new("--test-tor-privacy", _ => TorIdentityPrivacyTests.RunAsync()),
        new("--test-nostr-trust", _ => SecurityDiscoveryTests.RunNostrTrustTestsAsync()),
        new("--test-singleflight", _ => SecurityDiscoveryTests.RunSingleFlightTestAsync()),
        new("--test-handshake-cancellation", _ => HandshakeCancellationLifecycleTests.RunAsync()),
        new("--test-webui-security", _ => WebUiSecurityTests.RunAsync()),
        new("--test-webui-status", _ => WebUiStatusTests.RunAsync()),
        new("--benchmark-webui-status", _ => WebUiStatusTests.BenchmarkAsync(), Description: "Status projection and serialization with synthetic contacts"),
        new("--test-rotating-logger", _ => RotatingLoggerTests.RunAsync(), SupportsLoopback: true),
        new("--test-peer-acl", _ => PeerAccessControllerTests.RunAsync()),
        new("--test-ip-filter", _ => IpReputationFilterTests.RunAsync(), Aliases: ["--test-ipsum"]),
        new("--test-nostr-mailbox", _ => NostrMailboxTests.RunAsync(), Aliases: ["--test-mailbox"]),
        new("--test-lan-discovery", _ => SecurityDiscoveryTests.TestLanDiscoveryWithDifferentControlPorts()),
        new("--test-birthday-punch", _ => BirthdayPunchTests.RunAsync(), SupportsLoopback: true),
        new("--test-lan-ports", _ => LanPortAdvertisementTests.RunAsync()),
        new("--test-nat-pin-coordinator", _ => NatPinCoordinatorTests.RunAsync()),
        new("--test-lan-pooling", _ => VirtualLanPoolingTests.RunAsync()),
        new("--test-vpn-adapter", _ => VpnAdapterTests.RunAsync()),
        new("--benchmark-quic", _ => QuicPerformanceTests.RunAsync(), Description: "Prepared BBR with multiplexing, 80 ms RTT and 0.2% packet drops"),
        new("--benchmark-quic-cubic", _ => QuicPerformanceTests.RunCubicAsync(), Description: "Prepared CUBIC with multiplexing, 80 ms RTT and 0.2% packet drops"),
        new("--benchmark-quic-default", _ => QuicPerformanceTests.RunBaselineAsync(), Description: "Baseline CUBIC without multiplexing, 80 ms RTT and 0.2% packet drops"),
        new("--test-quic-bbr", _ => TestQuicCongestionControlTuning(), Aliases: ["--inspect-quic"]),
        new("--test-quic-datagrams", _ => QuicDatagramTests.RunAsync()),
        new("--test-wan-listener", _ => WanNativeProbe.RunAsync()),
        new("--test-quic-telemetry", _ => QuicAdvancedNativeTests.RunAsync()),
        new("--test-quic-optimizations", _ => NativeQuicOptimizationsTests.RunAsync(), Aliases: ["--test-native-quic"]),
        new("--test-pqc", _ => PqcHandshakeTests.RunAsync(), Aliases: ["--test-pqc-handshake"]),
        new("--test-discovery-presence", _ => DiscoveryPresenceTests.RunAsync(), SupportsLoopback: true),
        new("--test-connection-modes", _ => ConnectionModeTests.RunAsync()),
        new("--test-contact-history", _ => ContactHistoryTests.RunAsync(), SupportsLoopback: true),
        new("--test-contact-reconnect", _ => AutomaticContactReconnectTests.RunAsync(), SupportsLoopback: true, Description: "Persistent UDP retries, approximately one minute"),
        new("--test-token-readiness", _ => TokenReadinessTests.RunAsync()),
        new("--test-upnp", _ => UpnpTests.RunAsync()),
        new("--test-port-openers", _ => PortOpenerTests.RunAsync()),
        new("--test-opus", _ => { OpusCodecTests.Run(); return Task.CompletedTask; }),
        new("--test-native-audio", _ => NativeAudioServiceTests.RunAsync(), Aliases: ["--test-audio"]),
        new("--test-sha3", _ => { Sha3Tests.Run(); return Task.CompletedTask; }),
        new("--test-tor", TestTorFileShare.RunAsync),
        new("--test-tor-cascade", _ => TorBypassTests.RunAsync(), Aliases: ["--test-tor-fallback"]),
    ];

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

        Console.OutputEncoding = Encoding.UTF8;
        string? command = args.FirstOrDefault();
        if (command == PreviewCommand)
            return await Fixtures.WebUiPreview.ServeAsync();

        Suite? suite = Suites.FirstOrDefault(entry =>
            entry.Flag == command || entry.Aliases?.Contains(command) == true);
        if (suite?.DisableJsonReflection == true)
            AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        if (args.Contains("--loopback-only"))
        {
            if (suite?.SupportsLoopback != true)
                throw new ArgumentException("Loopback isolation has not been validated for this suite.");
            Fixtures.LoopbackOnlyNetwork.Configure();
        }

        if (suite != null)
            return await TestOutput.RunAsync(command!, () => suite.Run(args), args.Contains("--verbose"));

        if (command == "--list")
        {
            foreach (Suite entry in Suites)
                Console.WriteLine(entry.Flag);
            return 0;
        }

        if (command is not (null or "--help" or "-h"))
        {
            Console.Error.WriteLine($"Unknown test: {command}. Use --help to list suites.");
            return 2;
        }

        Console.WriteLine("Usage: dotnet run --project QuicPunchTests -- [test-flag] [--verbose]");
        Console.WriteLine("Use --list for canonical suite flags, one per line.");
        Console.WriteLine("Add --loopback-only to a suite that supports it.");
        Console.WriteLine("Available test flags:");
        foreach (Suite entry in Suites)
        {
            string aliases = entry.Aliases is { Length: > 0 } ? " / " + string.Join(" / ", entry.Aliases) : "";
            string loopback = entry.SupportsLoopback ? " [--loopback-only]" : "";
            string description = entry.Description is not null ? " (" + entry.Description + ")" : "";
            Console.WriteLine($"  {entry.Flag}{aliases}{loopback}{description}");
        }
        Console.WriteLine($"  {PreviewCommand} (start the WebUI preview server)");
        return 0;
    }

    private static Task VerifyAotJsonContractAsync()
    {
        PeerStore.VerifyAotJsonContract();
        Console.WriteLine($"Verified {QuicNet.Serialization.AotContractValidation.Run()} JSON contracts.");
        return Task.CompletedTask;
    }

    private static Task TestQuicCongestionControlTuning() => NativeBridgeTests.RunAsync();
}
