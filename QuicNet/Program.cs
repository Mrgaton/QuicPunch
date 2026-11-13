using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Web;
using Microsoft.Win32;
using QuicPunch.Helpers;
using QuicNet.Protocols;
using QuicNet.Settings;
using QuicNet.WebUi;
using QuicNet.Watch.Protocols;
using QuicNet.Watch.Services;
using QuicNet.Watch.Web;
using QuicNet.Vpn;
using QuicPunch.Vpn.Broker;

namespace QuicNet;

internal static class Program
{
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

        if (args.FirstOrDefault() == "--vpn-broker-service")
        {
            WindowsBrokerService.Run(args.Length > 1 ? args[1] : VpnBrokerConfiguration.DefaultPath);
            return;
        }
        if (args.FirstOrDefault() == "--vpn-broker")
        {
            using var brokerStop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; brokerStop.Cancel(); };
            using var termination = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; brokerStop.Cancel(); }) : null;
            try { await VpnBrokerHost.RunAsync(args.Length > 1 ? args[1] : VpnBrokerConfiguration.DefaultPath, brokerStop.Token); }
            catch (OperationCanceledException) when (brokerStop.IsCancellationRequested) { }
            return;
        }

        if (args.FirstOrDefault() == "--apply-update") { Environment.ExitCode = await WindowsAutoUpdate.ApplyAsync(args); return; }
        if (args.FirstOrDefault() == "--release-smoke-test") { await ReleaseSmokeTest.RunAsync(); return; }
        if (args.FirstOrDefault() == "--version" && args.Contains("--channel")) { Console.WriteLine(QuicNet.Updates.ReleaseIdentity.Channel); return; }
        if (args.FirstOrDefault() == "--version") { Console.WriteLine($"{QuicNet.Updates.ReleaseIdentity.Version} ({QuicNet.Updates.ReleaseIdentity.Build}) AOT={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}"); return; }
        string? updateHealth = args.Length == 2 && args[0] == "--update-health" ? args[1] : null;
        if (updateHealth != null) args = [];
        Console.OutputEncoding = Encoding.UTF8;
        bool startup = args.FirstOrDefault() == "--startup";
        if (startup) args = [];

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
        if (startup && !preferences.StartWithSystem) return;
        Directory.CreateDirectory(QuicPunch.QuicPunch.AppDataPath);
        FileStream instance;
        string instanceUrl = Path.Combine(QuicPunch.QuicPunch.AppDataPath, "desktop-url.txt");
        try { instance = new FileStream(Path.Combine(QuicPunch.QuicPunch.AppDataPath, "desktop.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            if (!startup && File.Exists(instanceUrl))
            {
                string target = File.ReadAllText(instanceUrl).Trim();
                if (Uri.TryCreate(target, UriKind.Absolute, out var local) && local.Scheme == "http" && local.Host == "127.0.0.1")
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            return;
        }
        using var instanceLock = instance;

        using var cts = new CancellationTokenSource();
        var qcc = new QuicPunch.QuicPunch(cts, null, null)
        {
            SharePeers = true,
            WanEnabled = preferences.WanEnabled
        };
        LocalUserProfile.Apply(qcc, preferences);
        qcc.Discovery.WanNostrDiscoveryEnabled = preferences.WanNostrDiscoveryEnabled;
        qcc.Discovery.TorNostrDiscoveryEnabled = preferences.TorNostrDiscoveryEnabled;

        QuicPunch.QuicPunch.EnableLogging = true;
        QuicPunch.QuicPunch.LogHandler = msg => { Console.WriteLine(msg); WebUiServer.LogEvent(msg); };
        QuicPunch.QuicPunch.ErrorHandler = msg => { Console.Error.WriteLine(msg); WebUiServer.LogEvent(msg); };

        await qcc.Discovery.SetWanPeerDiscoveryEnabledAsync(preferences.WanNostrDiscoveryEnabled, cts.Token);
        await qcc.Discovery.SetTorPeerDiscoveryEnabledAsync(preferences.TorNostrDiscoveryEnabled, cts.Token);
        await qcc.StartAsync(cts.Token);

        var lanHandler = new VirtualLanHandler();
        if (preferences.LanAutoAssign)
            lanHandler.ConfigureAutoAssignment(qcc.CertManager.CertPublicHash, qcc.PoolId);
        else if (!lanHandler.ConfigureManualAddress(preferences.LanIp, preferences.LanSubnetMask))
            lanHandler.ConfigureAutoAssignment(qcc.CertManager.CertPublicHash, qcc.PoolId);
        lanHandler.SetMtu(preferences.LanMtu);
        using var vpnStore = new VpnConfigurationStore(Path.Combine(qcc.NodeAppDataPath, "vpn.json"), new VpnConfiguration {
            Enabled = preferences.LanEnabled, AutoAssign = preferences.LanAutoAssign,
            LocalAddress = lanHandler.LocalIp.ToString(), SubnetMask = lanHandler.SubnetMask, Mtu = lanHandler.Mtu });
        await using var vpnController = new VpnController(qcc, lanHandler, vpnStore, cts.Token);

        var chatHandler = new ChatHandler();
        using var voiceCallHandler = new VoiceCallHandler(qcc);
        using var speedTestHandler = new SpeedTestHandler();
        using var watchControlHandler = new WatchControlHandler();
        using var watchMediaHandler = new MediaTransferHandler();

        qcc.RegisterProtocol(chatHandler);
        qcc.RegisterProtocol(voiceCallHandler);
        qcc.RegisterProtocol(speedTestHandler);
        qcc.RegisterProtocol(lanHandler);
        qcc.RegisterProtocol(watchControlHandler);
        qcc.RegisterProtocol(watchMediaHandler);

        using var watchLibrary = new MediaLibrary(watchMediaHandler, Path.Combine(qcc.NodeAppDataPath, "watch-media-cache"));
        var watchRooms = new RoomCoordinator(watchControlHandler, watchLibrary, qcc.CurrentPeer?.Id
            ?? throw new InvalidOperationException("The local peer identity is unavailable."));
        var watchConnector = new PeerConnector(qcc, watchControlHandler, watchMediaHandler, cts.Token);
        await using var watchWeb = new WatchWebServer(qcc, watchControlHandler, watchMediaHandler, watchLibrary, watchRooms, watchConnector, cts);

        var webUi = new WebUiServer(qcc, chatHandler, lanHandler, voiceCallHandler, speedTestHandler, preferencesStore, cts, watchWeb);
        using var updater = new QuicNet.Updates.AutoUpdateService(qcc.NodeAppDataPath, OperatingSystem.IsLinux() ? "linux-x64" : "win-x64", WindowsAutoUpdate.InstallAsync);
        webUi.Updates = updater;
        webUi.Start();
        File.WriteAllText(instanceUrl, webUi.Url);
        using var appPlatform = new DesktopAppPlatform(webUi.Url, qcc.NodeAppDataPath, cts);
        webUi.AppPlatform = appPlatform;
        try { if (appPlatform.StartupSupported) appPlatform.SetStartWithSystem(preferencesStore.Snapshot().StartWithSystem); }
        catch (Exception ex) { WebUiServer.LogEvent("No se pudo aplicar el inicio automático: " + ex.Message); }
        if (updateHealth != null) File.WriteAllText(updateHealth, "ready");
        updater.Start(cts.Token);
        DesktopWindowManager.ShowWindow(webUi.Url, qcc.NodeAppDataPath, cts, watchWeb,
            () => preferencesStore.Snapshot().RunInBackground, startup && preferences.RunInBackground,
            open => appPlatform.OpenWindow = open);

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
        if (string.IsNullOrEmpty(myToken))
        {
            Console.WriteLine("Your WAN token is not available yet. Start WAN or wait for endpoint discovery.\n");
        }
        else
        {
            Console.WriteLine($"Your token: {myToken}\n");
            Console.WriteLine($"Share this URI for quick connection: qp://{myToken}\n");
        }

        qcc.OnPeerAvailable += peer => Console.WriteLine($"Peer available: {peer.Name}");

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                Console.WriteLine("\nSelect a trusted peer to open an application, or use the web console.");
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

        try { webUi.Stop(); } catch { }
        try { await vpnController.DisposeAsync(); } catch { }
        try { await qcc.DisposeAsync(); } catch { }
        try { await watchWeb.DisposeAsync(); } catch { }
        try { watchLibrary.Dispose(); } catch { }
        try { watchControlHandler.Dispose(); } catch { }
        try { watchMediaHandler.Dispose(); } catch { }
        try { lanHandler.Dispose(); } catch { }
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
}
