using Android.App;
using Android.Content;
using Android.Content.PM;
using QuicNet.Protocols;
using QuicNet.Settings;
using QuicNet.WebUi;
using QuicNet.Watch;
using QuicPunch.AndroidProbe.Shared;

namespace QuicPunch.AndroidProbe;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice | ForegroundService.TypeMicrophone)]
public sealed class AndroidConnectionService : Service
{
    internal static bool Running { get; private set; }
    private static readonly bool HasProductionUpdater = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
        System.Runtime.InteropServices.Architecture.Arm64;
    private static readonly int WebPort = HasProductionUpdater ? 5071 : 5072;
    internal static readonly string Url = $"http://127.0.0.1:{WebPort}/";
    private const string ChannelId = "quicpunch-connections";
    private const string StopAction = "org.quicpunch.androidprobe.STOP";
    private bool _started, _microphoneAllowed;
    private WebUiServer? _server;
    private QuicNet.Updates.AutoUpdateService? _updater;
    private AndroidUpdateIdle? _updateIdle;
    private CancellationTokenSource? _cts;
    private global::QuicPunch.QuicPunch? _qcc;
    private ChatHandler? _chatHandler;
    private VoiceCallHandler? _voiceCallHandler;
    private SpeedTestHandler? _speedTestHandler;
    private WatchApplication? _watch;
    private AndroidWatchFilePicker? _watchPicker;
    private AppPreferencesStore? _preferences;
    private readonly object _multicastGate = new();
    private Android.Net.Wifi.WifiManager.MulticastLock? _multicastLock;
    private Android.Net.ConnectivityManager? _connectivity;
    private NetworkObserver? _networkObserver;
    private readonly object _networkObserverGate = new();

    private sealed class NetworkObserver : Android.Net.ConnectivityManager.NetworkCallback
    {
        private readonly AndroidConnectionService _owner;
        private readonly NetworkPathTracker _path;
        private readonly CancellationToken _lifetime;
        public NetworkObserver(AndroidConnectionService owner)
        {
            _owner = owner;
            _lifetime = owner._cts!.Token;
            var network = owner._connectivity?.ActiveNetwork;
            var properties = network == null ? null : owner._connectivity?.GetLinkProperties(network);
            _path = new NetworkPathTracker(network?.ToString(), Fingerprint(properties));
            owner._qcc?.SetWanNat64Prefix(Nat64Prefix(properties));
        }
        private static string? Nat64Prefix(Android.Net.LinkProperties? properties)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(30) || properties == null ||
                properties.Routes.Any(route => route.IsDefaultRoute && route.Destination?.Address is Java.Net.Inet4Address))
                return null;
            var prefix = properties.Nat64Prefix;
            return prefix?.Address?.HostAddress is { } address ? address + "/" + prefix.PrefixLength : null;
        }
        private static string Fingerprint(Android.Net.LinkProperties? properties)
        {
            if (properties == null) return "";
            // Include default IPv6 routing and NAT64 changes while ignoring
            // individual IPv6 privacy address rotations and link statistics.
            var addresses = properties.LinkAddresses.Where(a => a.Address is Java.Net.Inet4Address)
                .Select(a => a.ToString()).Order(StringComparer.Ordinal);
            var routes = properties.Routes.Where(r => r.IsDefaultRoute || r.Destination?.Address is Java.Net.Inet4Address)
                .Select(r => r.ToString()).Order(StringComparer.Ordinal);
            return properties.InterfaceName + "|" + string.Join(",", addresses) + "|" + string.Join(",", routes) + "|" + Nat64Prefix(properties);
        }
        private void Changed(Android.Net.Network network, Android.Net.LinkProperties? properties)
        {
            if (!_lifetime.IsCancellationRequested && _path.Update(network.ToString(), Fingerprint(properties)))
            {
                _owner._qcc?.SetWanNat64Prefix(Nat64Prefix(properties));
                _ = _owner.RefreshNetworkAsync(_lifetime);
            }
        }
        // API 26+ delivers OnLinkPropertiesChanged immediately after OnAvailable.
        // A synchronous GetLinkProperties in OnAvailable can return stale/null
        // data and falsely cancel handshakes before the ordered callback arrives.
        public override void OnLost(Android.Net.Network network)
        {
            if (!_lifetime.IsCancellationRequested && _path.Lost(network.ToString()))
            {
                _owner._qcc?.SetWanNat64Prefix(null);
                _ = _owner.RefreshNetworkAsync(_lifetime);
            }
        }
        public override void OnLinkPropertiesChanged(Android.Net.Network network, Android.Net.LinkProperties properties) =>
            Changed(network, properties);
    }

    private async Task RefreshNetworkAsync(CancellationToken lifetime)
    {
        try { if (!lifetime.IsCancellationRequested && _qcc != null) await _qcc.NotifyNetworkChangedAsync(lifetime).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Android.Util.Log.Warn("QuicPunch", $"[NETWORK CHANGE] {ex.Message}"); }
    }

    public override Android.OS.IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction) { StopSelf(); return StartCommandResult.NotSticky; }
        if (FilesDir?.AbsolutePath is { } dataDirectory) QuicNet.Diagnostics.CoreErrorReporter.Start(dataDirectory);
        try
        {
            _microphoneAllowed = CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted &&
                (_microphoneAllowed || intent?.GetBooleanExtra("allowMicrophone", false) == true);
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Conexiones de QuicNet", NotificationImportance.Low));
            var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(AndroidConnectionService)).SetAction(StopAction), PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var notification = new Notification.Builder(this, ChannelId)
                .SetContentTitle("QuicNet activo")
                .SetContentText("Pulsa para abrir QuicNet.")
                .SetSmallIcon(Resource.Drawable.ic_stat_quicnet)
                .SetContentIntent(open).SetOngoing(true)
                .AddAction(new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, Android.Resource.Drawable.IcMenuCloseClearCancel), "Desconectar", stop).Build())
                .Build();
            var serviceType = ForegroundService.TypeConnectedDevice;
            if (_microphoneAllowed && OperatingSystem.IsAndroidVersionAtLeast(30))
                serviceType |= ForegroundService.TypeMicrophone;
            StartForeground(5071, notification, serviceType);
            if (!_started) { InitializeNode(); _started = true; Running = true; }
        }
        catch (Exception ex)
        {
            QuicNet.Diagnostics.CoreErrorReporter.Report("Servicio Android", ex);
            Android.Util.Log.Error("QuicPunch", $"[ANDROID SERVICE] {ex}");
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    private void InitializeNode()
    {
        QuicProbe.LoadNative(ApplicationInfo?.NativeLibraryDir);

        string appDataDir = FilesDir?.AbsolutePath ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), "QuicPunchV17");
        Directory.CreateDirectory(appDataDir);
        var preferencesStore = AppPreferencesStore.OpenForApp(appDataDir);
        AppPreferences preferences = preferencesStore.Snapshot();
        preferences.LanEnabled = false;

        _cts = new CancellationTokenSource();
        _qcc = new global::QuicPunch.QuicPunch(_cts.Token, appDataPath: appDataDir, updateIpReputationOnlyWithWan: true)
        {
            SharePeers = true,
            WanEnabled = preferences.OnboardingCompleted && preferences.WanEnabled,
            LanDiscoveryInterval = () => TimeSpan.FromSeconds(MainActivity.IsVisible ? 2 : 30),
            DefaultTorRuntimeOptions = new global::QuicPunch.TorRuntimeOptions
            {
                ExecutablePath = Path.Combine(ApplicationInfo!.NativeLibraryDir!, "libtor.so"),
                InstallDirectory = ApplicationInfo.NativeLibraryDir,
                TransportMode = global::QuicPunch.TorTransportMode.Direct
            }
        };
        _preferences = preferencesStore;
        _preferences.Changed += UpdateMulticastLock;
        MainActivity.VisibilityChanged += UpdateMulticastLock;
        UpdateMulticastLock();
        LocalUserProfile.DeviceName = Android.OS.Build.Model ?? Environment.MachineName;

#if DEBUG
        _ = Task.Run(async () =>
        {
            try
            {
                string result = await global::QuicPunch.QuicPunchConnection.RunAndroidNativeSelfTestAsync(
                    _qcc.CertManager.PeerCertificate, _cts.Token).ConfigureAwait(false);
                Android.Util.Log.Info("QuicPunch", $"[ANDROID QUIC SELFTEST] {result}");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("QuicPunch", $"[ANDROID QUIC SELFTEST] {ex}");
            }
        });
#endif

        global::QuicPunch.QuicPunch.EnableLogging = true;
        global::QuicPunch.QuicPunch.LogHandler = msg => { Android.Util.Log.Info("QuicPunch", msg); WebUiServer.LogEvent(msg); };
        global::QuicPunch.QuicPunch.ErrorHandler = msg => { Android.Util.Log.Error("QuicPunch", msg); WebUiServer.LogEvent(msg); };

        _chatHandler = new ChatHandler();
        _voiceCallHandler = new VoiceCallHandler(_qcc, new AndroidAudioBackend());
        _speedTestHandler = new SpeedTestHandler();

        _qcc.RegisterProtocol(_chatHandler);
        _qcc.RegisterProtocol(_voiceCallHandler);
        _qcc.RegisterProtocol(_speedTestHandler);

        _watch = new WatchApplication(_qcc, _cts);
        _watchPicker = new AndroidWatchFilePicker(this, _cts.Token);
        _watch.Web.SetFilePicker(_watchPicker);

        _server = new WebUiServer(
            _qcc,
            _chatHandler,
            lanHandler: null,
            _voiceCallHandler,
            _speedTestHandler,
            preferencesStore,
            _cts,
            watchWeb: _watch.Web,
            port: WebPort,
            capabilities: NodeCapabilities.CreateAndroid(),
            startAfterOnboarding: StartNodeAsync
        )
        {
            ProbePingHandler = MainActivity.NativePing,
            AppPlatform = new AndroidAppPlatform(this),
            ChatFiles = new AndroidChatFileOpener(),
            DiskDocuments = new AndroidDiskDocuments(this),
            PlatformScreenCapture = new AndroidScreenCapture(this)
        };
        if (HasProductionUpdater)
        {
            var updateIdle = new AndroidUpdateIdle((Android.OS.PowerManager)GetSystemService(PowerService)!);
            updateIdle.Register(this);
            _updateIdle = updateIdle;
            var lifetime = _cts.Token;
            _updater = new QuicNet.Updates.AutoUpdateService(appDataDir, "android-arm64",
                (path, artifact, automatic) => AndroidAutoUpdate.InstallAsync(this, path, artifact, automatic,
                    commit => _updater!.TryCommitAutomaticInstallation(commit), lifetime), getIdleTime: updateIdle.GetIdleTime,
                canStartAutomaticInstallation: artifact => AndroidAutoUpdate.CanInstallAutomatically(this, artifact));
            _server.Updates = _updater;
        }
        _server.Start();
        _updater?.Start(_cts.Token);

        if (!preferences.OnboardingCompleted) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await StartNodeAsync(preferences).ConfigureAwait(false);
                _server.ObservePeerStore();
                WebUiServer.BroadcastStatusUpdate();
                await _server.RestoreTorAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WebUiServer.LogEvent($"[P2P STARTUP] {ex.Message}");
            }
        });
    }

    private async Task StartNodeAsync(AppPreferences selected)
    {
        var node = _qcc!;
        var token = _cts!.Token;
        LocalUserProfile.Apply(node, selected);
        node.WanEnabled = selected.WanEnabled;
        node.Discovery.WanNostrDiscoveryEnabled = selected.WanNostrDiscoveryEnabled;
        node.Discovery.TorNostrDiscoveryEnabled = selected.TorNostrDiscoveryEnabled;
        lock (_networkObserverGate)
        {
            token.ThrowIfCancellationRequested();
            _connectivity ??= (Android.Net.ConnectivityManager?)GetSystemService(ConnectivityService);
            _networkObserver ??= new NetworkObserver(this);
        }
        await node.StartAsync(token).ConfigureAwait(false);
        lock (_networkObserverGate)
        {
            token.ThrowIfCancellationRequested();
            _connectivity?.RegisterDefaultNetworkCallback(_networkObserver);
        }
    }

    private void UpdateMulticastLock()
    {
        lock (_multicastGate)
        {
            bool receiveMulticast = _cts?.IsCancellationRequested == false && MainActivity.IsVisible &&
                _preferences?.Snapshot() is { OnboardingCompleted: true, WanEnabled: true };
            try
            {
                if (!receiveMulticast)
                {
                    if (_multicastLock?.IsHeld == true) _multicastLock.Release();
                    return;
                }

                if (_multicastLock == null)
                {
                    using var wifi = (Android.Net.Wifi.WifiManager?)ApplicationContext?.GetSystemService(WifiService);
                    _multicastLock = wifi?.CreateMulticastLock("quicpunch-lan-discovery");
                    _multicastLock?.SetReferenceCounted(false);
                }
                if (_multicastLock?.IsHeld == true) return;
                _multicastLock?.Acquire();
                // Discover immediately after waking, even if the periodic beacon is still waiting.
                if (_qcc is { IsWanStarted: true } node) _ = Task.Run(node.SendLocalLanDiscoveryAsync);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("QuicPunch", $"[ANDROID SERVICE] Multicast lock unavailable: {ex.Message}");
            }
        }
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        if (!AndroidAppPlatform.Preferences(this).RunInBackground) StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        Running = false;
        StopService(new Intent(this, typeof(AndroidScreenCaptureService)));
        _cts?.Cancel();
        MainActivity.VisibilityChanged -= UpdateMulticastLock;
        if (_preferences != null) _preferences.Changed -= UpdateMulticastLock;
        UpdateMulticastLock();
        lock (_multicastGate)
        {
            _multicastLock?.Dispose();
            _multicastLock = null;
        }
        if (_updateIdle != null) UnregisterReceiver(_updateIdle);
        lock (_networkObserverGate)
        {
            if (_networkObserver != null) try { _connectivity?.UnregisterNetworkCallback(_networkObserver); } catch { }
            _networkObserver?.Dispose();
            _networkObserver = null;
        }
        _voiceCallHandler?.Dispose();
        _speedTestHandler?.Dispose();
        _server?.Stop();
        if (_watch != null) _ = DisposeWatchAsync(_watch);
        _watchPicker?.Dispose();
        _qcc?.Dispose();
        _updater?.Dispose();
        _cts?.Dispose();
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private static async Task DisposeWatchAsync(WatchApplication watch)
    {
        try { await watch.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("QuicPunch", $"[ANDROID SERVICE] QuicWatch cleanup failed: {ex.Message}");
        }
    }

}
