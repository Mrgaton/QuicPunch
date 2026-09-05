using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Web;
using Microsoft.Win32;
using QuicPunch.Helpers;
using QuicPunchTests.Helpers;
using QuicPunchTests.Protocols;
using QuicPunchTests.Settings;
using QuicPunchTests.Tests;
using QuicPunchTests.WebUi;

namespace QuicPunchTests;

internal static class Program
{
    public static Process CurrentProcess = Process.GetCurrentProcess();
    public static string FileName = CurrentProcess.MainModule?.FileName ?? "";

    [STAThread]
    private static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Console.WriteLine($"[FATAL UNHANDLED EXCEPTION] {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Console.WriteLine($"[UNOBSERVED TASK EXCEPTION] {e.Exception}");
            e.SetObserved();
        };

        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0 && args[0] == "--test-antireplay") { await AntiReplayTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-security") { await SecurityDiscoveryTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-singleflight") { await SecurityDiscoveryTests.RunSingleFlightTestAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-handshake-cancellation") { await HandshakeCancellationLifecycleTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-webui-security") { await WebUiSecurityTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-rotating-logger") { await RotatingLoggerTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-peer-acl") { await PeerAccessControllerTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-nat-pin-coordinator") { await NatPinCoordinatorTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-lan-pooling") { await VirtualLanPoolingTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-vpn-adapter") { await VpnAdapterTests.RunAsync(); return; }
        if (args.Length > 0 && (args[0] == "--test-quic-bbr" || args[0] == "--inspect-quic")) { await TestQuicCongestionControlTuning(); return; }
        if (args.Length > 0 && args[0] == "--test-quic-datagrams") { await QuicDatagramTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-quic-telemetry") { await QuicAdvancedNativeTests.RunAsync(); return; }
        if (args.Length > 0 && (args[0] == "--test-quic-optimizations" || args[0] == "--test-native-quic")) { await NativeQuicOptimizationsTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-upnp") { await UpnpTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-port-openers") { await PortOpenerTests.RunAsync(); return; }
        if (args.Length > 0 && args[0] == "--test-opus") { OpusCodecTests.Run(); return; }
        if (args.Length > 0 && args[0] == "--test-tor") { await TestTorFileShare.RunAsync(args); return; }
        if (args.Length > 0 && (args[0] == "--test-tor-cascade" || args[0] == "--test-tor-fallback")) { await TorBypassTests.RunAsync(); return; }

        if (args.Length > 0 && args[0].Contains("://"))
            args = args[0].Split("/").Skip(2).Select(e => HttpUtility.UrlDecode(e)).ToArray();

        if (args.Length > 0)
        {
            using var ps = new PeerStore(Path.Combine(QuicPunch.QuicPunch.AppDataPath, "peers.db"));
            ps.AddOrUpdate(args[0]);
            return;
        }

        TryRegisterProtocolHandler();

        string preferencesPath = Path.Combine(QuicPunch.QuicPunch.AppDataPath, "ui-settings.json");
        var preferencesStore = new AppPreferencesStore(preferencesPath);
        AppPreferences preferences = preferencesStore.Snapshot();

        string? inputPwd = null;
        if (!Console.IsInputRedirected)
        {
            Console.Write("Enter the password for auto connections (leave empty for none): ");
            inputPwd = Console.ReadLine();
        }
        byte[]? pwdBytes = string.IsNullOrEmpty(inputPwd) ? null : Encoding.UTF8.GetBytes(inputPwd);

        using var cts = new CancellationTokenSource();
        var qcc = new QuicPunch.QuicPunch(cts, null, pwdBytes, preferences.AutoAcceptTrusted)
        {
            SharePeers = true,
            WanEnabled = preferences.WanEnabled,
            WanNostrDiscoveryEnabled = preferences.WanNostrDiscoveryEnabled,
            TorNostrDiscoveryEnabled = preferences.TorNostrDiscoveryEnabled
        };

        QuicPunch.QuicPunch.LogHandler = msg => { Console.WriteLine(msg); WebUiServer.LogEvent(msg); };
        QuicPunch.QuicPunch.ErrorHandler = msg => { Console.Error.WriteLine(msg); WebUiServer.LogEvent(msg); };

        await qcc.SetWanPeerDiscoveryEnabledAsync(preferences.WanNostrDiscoveryEnabled, cts.Token);
        await qcc.SetTorPeerDiscoveryEnabledAsync(preferences.TorNostrDiscoveryEnabled, cts.Token);
        await qcc.StartAsync(cts.Token);

        var lanHandler = new VirtualLanHandler();
        if (preferences.LanAutoAssign)
            lanHandler.ConfigureAutoAssignment(qcc.CertManager.CertPublicHash, qcc.PoolId);
        else if (!lanHandler.ConfigureManualAddress(preferences.LanIp, preferences.LanSubnetMask))
            lanHandler.ConfigureAutoAssignment(qcc.CertManager.CertPublicHash, qcc.PoolId);
        lanHandler.SetMtu(preferences.LanMtu);

        var chatHandler = new ChatHandler();
        using var voiceCallHandler = new VoiceCallHandler(qcc);
        using var relayDriveHandler = new RelayDriveHandler(qcc.CurrentPeer?.Id ?? Guid.Empty);

        qcc.RegisterProtocol(chatHandler);
        qcc.RegisterProtocol(voiceCallHandler);
        qcc.RegisterProtocol(relayDriveHandler);

        if (preferences.LanEnabled)
        {
            lanHandler.SetupTun();
            if (lanHandler.IsActive)
                qcc.RegisterProtocol(lanHandler);
        }

        var webUi = new WebUiServer(qcc, chatHandler, lanHandler, voiceCallHandler, relayDriveHandler, preferencesStore, cts);
        webUi.Start();

        if (preferences.TorEnabled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    WebUiServer.LogEvent("[TOR] Restoring Tor from the previous session...");
                    await qcc.StartTorAsync(preferences.TorVirtualPort, cancellationToken: cts.Token);
                    WebUiServer.LogEvent($"[TOR] Hidden service ready at {qcc.TorOnionAddress}");
                }
                catch (Exception ex)
                {
                    WebUiServer.LogEvent($"[TOR] Automatic startup failed: {ex.Message}");
                }
            });
        }

        string myToken = qcc.GetToken();
        Console.WriteLine($"Your public endpoints: {string.Join(", ", qcc.CurrentPeer?.Addresses ?? [])}\n");
        Console.WriteLine($"Your token: {myToken}\n");
        string quickUri = $"qp://{myToken}";
        Console.WriteLine($"Share this URI for quick connection: {quickUri}\n");
        DiyClipper.SetText(quickUri);

        qcc.OnPeerAvailable += peer => Console.WriteLine($"Peer available: {peer.Name}");

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                Console.WriteLine("\nSelect a trusted peer to connect, or use the web console.");
                Console.WriteLine("0: Enter token manually");
                var trustedPeers = qcc.AvailablePeers.Values.Where(qcc.IsTrustedPeer).ToArray();
                for (int i = 0; i < trustedPeers.Length; i++)
                    Console.WriteLine($"{i + 1}: {trustedPeers[i].Name} ({trustedPeers[i].Ping}) - {trustedPeers[i].Id}");
                if (qcc.AvailablePeers.Count > trustedPeers.Length)
                    Console.WriteLine($"{qcc.AvailablePeers.Count - trustedPeers.Length} untrusted peer(s) await review in the dashboard.");
                Console.WriteLine("R: Refresh");

                if (Console.IsInputRedirected)
                {
                    await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                    continue;
                }

                while (!cts.Token.IsCancellationRequested && !Console.KeyAvailable)
                {
                    await Task.Delay(150, cts.Token).ConfigureAwait(false);
                }
                if (cts.Token.IsCancellationRequested) break;

                var input = Console.ReadKey(true);
                if (char.ToLowerInvariant(input.KeyChar) == 'r') continue;
                if (input.KeyChar == '0')
                {
                    Console.Write("\nEnter token: ");
                    string? token = Console.ReadLine();
                    if (!string.IsNullOrWhiteSpace(token)) _ = qcc.PeerInterrogation(token, cts.Token);
                    continue;
                }

                int index = input.KeyChar - '1';
                if (index < 0 || index >= trustedPeers.Length) continue;
                var peer = trustedPeers[index];

                Console.WriteLine("\nSelect protocol:");
                for (int i = 0; i < qcc.ProtocolHandlers.Count; i++)
                    Console.WriteLine($"{i}: {qcc.ProtocolHandlers.ElementAt(i).Value.ProtocolName}");

                while (!cts.Token.IsCancellationRequested && !Console.KeyAvailable)
                {
                    await Task.Delay(150, cts.Token).ConfigureAwait(false);
                }
                if (cts.Token.IsCancellationRequested) break;

                var protocolInput = Console.ReadKey(true);
                int protocolIndex = protocolInput.KeyChar - '0';
                if (protocolIndex >= 0 && protocolIndex < qcc.ProtocolHandlers.Count)
                {
                    Guid protocolId = qcc.ProtocolHandlers.ElementAt(protocolIndex).Key;
                    _ = Task.Run(() => qcc.InitQuicConnection(protocolId, peer, 0, cts.Token));
                }
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"Console loop error: {ex.Message}");
                try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
            }
        }

        try { lanHandler.Dispose(); } catch { }
        try { await qcc.DisposeAsync(); } catch { }
        Environment.Exit(0);
    }

    private static void TryRegisterProtocolHandler()
    {
        const string scheme = "qp";
        string exe = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var existing = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scheme}\shell\open\command");
                if (existing?.GetValue("") is string command && command.Contains(exe, StringComparison.OrdinalIgnoreCase))
                    return;

                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
                key.SetValue("", $"URL:{scheme} Protocol");
                key.SetValue("URL Protocol", "");
                using var cmd = key.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", $"\"{exe}\" \"%1\"");
            }
            catch (UnauthorizedAccessException)
            {
                try
                {
                    using var id = WindowsIdentity.GetCurrent();
                    bool admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                    if (!admin)
                    {
                        Process.Start(new ProcessStartInfo(exe, "--elevated")
                        {
                            UseShellExecute = true,
                            Verb = "runas"
                        });
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Protocol setup failed: {ex.Message}");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                string appsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications");
                Directory.CreateDirectory(appsDir);
                string desktopFile = Path.Combine(appsDir, "quicpunch.desktop");
                string content = $"[Desktop Entry]\nType=Application\nName=QuicPunch\nExec=\"{exe}\" %u\nMimeType=x-scheme-handler/qp;x-scheme-handler/qphp;\nNoDisplay=true\nTerminal=false\n";
                if (!File.Exists(desktopFile) || File.ReadAllText(desktopFile) != content)
                {
                    File.WriteAllText(desktopFile, content);
                    Process.Start(new ProcessStartInfo("xdg-mime", "default quicpunch.desktop x-scheme-handler/qp") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(1000);
                }
            }
            catch { }
        }
    }

    private static void InspectMsQuicParams()
    {
        TestQuicCongestionControlTuning().GetAwaiter().GetResult();
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
        using var cert = new X509Certificate2(tempCert.Export(X509ContentType.Pfx));

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

        // 2. Dynamically switch to BBR via native SetParam
        bool switched = MsQuicTuner.TrySetCongestionControl(clientConn, QuicCongestionAlgorithm.Bbr);
        Console.WriteLine($"[2] MsQuicTuner.TrySetCongestionControl(Bbr) => {(switched ? "SUCCESS" : "FAILED")}");

        // 3. Confirm with GetParam
        if (MsQuicTuner.TryGetCongestionControl(clientConn, out var activeCc))
        {
            Console.WriteLine($"[3] Active Congestion Control: {activeCc} ({(int)activeCc}) [{(activeCc == QuicCongestionAlgorithm.Bbr ? "CONFIRMED BBR ACTIVATED!" : "FAILED")}]");
        }

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
