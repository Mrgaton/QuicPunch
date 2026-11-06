using Android.App;
using Android.Content;
using Android.Content.PM;
using QuicNet.Protocols;
using QuicNet.Settings;
using QuicNet.WebUi;
using QuicPunch.AndroidProbe.Shared;

namespace QuicPunch.AndroidProbe;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice | ForegroundService.TypeMicrophone)]
public sealed class AndroidConnectionService : Service
{
    internal static bool Running { get; private set; }
    internal const string Url = "http://127.0.0.1:5071/";
    private const string ChannelId = "quicpunch-connections";
    private const string StopAction = "org.quicpunch.androidprobe.STOP";
    private bool _started, _microphoneAllowed;
    private WebUiServer? _server;
    private QuicNet.Updates.AutoUpdateService? _updater;
    private CancellationTokenSource? _cts;
    private global::QuicPunch.QuicPunch? _qcc;
    private ChatHandler? _chatHandler;
    private VoiceCallHandler? _voiceCallHandler;
    private SpeedTestHandler? _speedTestHandler;
    private Android.Net.Wifi.WifiManager.MulticastLock? _multicastLock;
    private Android.Net.ConnectivityManager? _connectivity;
    private NetworkObserver? _networkObserver;
    private readonly object _networkObserverGate = new();

    private sealed class NetworkObserver : Android.Net.ConnectivityManager.NetworkCallback
    {
        private readonly AndroidConnectionService _owner;
        private readonly NetworkPathTracker _path;
        public NetworkObserver(AndroidConnectionService owner)
        {
            _owner = owner;
            var network = owner._connectivity?.ActiveNetwork;
            var properties = network == null ? null : owner._connectivity?.GetLinkProperties(network);
            _path = new NetworkPathTracker(network?.ToString(), Fingerprint(properties));
        }
        private static string Fingerprint(Android.Net.LinkProperties? properties)
        {
            if (properties == null) return "";
            // The UDP transport is IPv4. IPv6 temporary-address updates, link
            // statistics and repeated callbacks must not cancel a live handshake.
            var addresses = properties.LinkAddresses.Where(a => a.Address is Java.Net.Inet4Address)
                .Select(a => a.ToString()).Order(StringComparer.Ordinal);
            var routes = properties.Routes.Where(r => r.Destination?.Address is Java.Net.Inet4Address)
                .Select(r => r.ToString()).Order(StringComparer.Ordinal);
            return properties.InterfaceName + "|" + string.Join(",", addresses) + "|" + string.Join(",", routes);
        }
        private void Changed(Android.Net.Network network, Android.Net.LinkProperties? properties)
        {
            if (_path.Update(network.ToString(), Fingerprint(properties))) _ = _owner.RefreshNetworkAsync();
        }
        public override void OnAvailable(Android.Net.Network network) =>
            Changed(network, _owner._connectivity?.GetLinkProperties(network));
        public override void OnLost(Android.Net.Network network)
        {
            if (_path.Lost(network.ToString())) _ = _owner.RefreshNetworkAsync();
        }
        public override void OnLinkPropertiesChanged(Android.Net.Network network, Android.Net.LinkProperties properties) =>
            Changed(network, properties);
    }

    private async Task RefreshNetworkAsync()
    {
        try { if (_qcc != null) await _qcc.NotifyNetworkChangedAsync(_cts?.Token ?? default).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Android.Util.Log.Warn("QuicPunch", $"[NETWORK CHANGE] {ex.Message}"); }
    }

    public override Android.OS.IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction) { StopSelf(); return StartCommandResult.NotSticky; }
        try
        {
            _microphoneAllowed |= intent?.GetBooleanExtra("allowMicrophone", false) == true &&
                CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted;
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Conexiones de QuicPunch", NotificationImportance.Low));
            var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(AndroidConnectionService)).SetAction(StopAction), PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var notification = new Notification.Builder(this, ChannelId)
                .SetContentTitle("QuicPunch conectado")
                .SetContentText("Conexiones y llamadas disponibles. Pulsa para abrir.")
                .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)
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
            Android.Util.Log.Error("QuicPunch", $"[ANDROID SERVICE] {ex}");
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    private void InitializeNode()
    {
        QuicProbe.LoadNative(ApplicationInfo?.NativeLibraryDir);
        AcquireMulticastLock();

        string appDataDir = FilesDir?.AbsolutePath ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), "QuicPunchV17");
        Directory.CreateDirectory(appDataDir);
        string preferencesPath = Path.Combine(appDataDir, "ui-settings.json");
        var preferencesStore = new AppPreferencesStore(preferencesPath);
        AppPreferences preferences = preferencesStore.Snapshot();
        preferences.LanEnabled = false;

        _cts = new CancellationTokenSource();
        _qcc = new global::QuicPunch.QuicPunch(_cts, null, null, appDataPath: appDataDir)
        {
            SharePeers = true,
            WanEnabled = preferences.WanEnabled,
            DefaultTorRuntimeOptions = new global::QuicPunch.TorRuntimeOptions
            {
                ExecutablePath = Path.Combine(ApplicationInfo!.NativeLibraryDir!, "libtor.so"),
                InstallDirectory = ApplicationInfo.NativeLibraryDir,
                TransportMode = global::QuicPunch.TorTransportMode.Direct
            }
        };
        LocalUserProfile.DeviceName = Android.OS.Build.Model ?? Environment.MachineName;
        LocalUserProfile.Apply(_qcc, preferences);
        _qcc.Discovery.WanNostrDiscoveryEnabled = preferences.WanNostrDiscoveryEnabled;
        _qcc.Discovery.TorNostrDiscoveryEnabled = preferences.TorNostrDiscoveryEnabled;

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

        _server = new WebUiServer(
            _qcc,
            _chatHandler,
            lanHandler: null,
            _voiceCallHandler,
            _speedTestHandler,
            preferencesStore,
            _cts,
            watchWeb: null,
            port: 5071,
            capabilities: NodeCapabilities.CreateAndroid()
        )
        {
            ProbePingHandler = MainActivity.NativePing,
            AppPlatform = new AndroidAppPlatform(this),
            PlatformScreenCapture = new AndroidScreenCapture(this)
        };
        _updater = new QuicNet.Updates.AutoUpdateService(appDataDir, "android-arm64", (path, artifact, automatic) => {
            UpdatePackageProvider.ReadyPackage = path;
            return AndroidAutoUpdate.InstallAsync(path, artifact, automatic);
        });
        _server.Updates = _updater;
        _server.Start();
        _server.StartGeolocation();
        _updater.Start(_cts.Token);

        _ = Task.Run(async () =>
        {
            try
            {
                await _qcc.Discovery.SetWanPeerDiscoveryEnabledAsync(preferences.WanNostrDiscoveryEnabled, _cts.Token).ConfigureAwait(false);
                await _qcc.StartAsync(_cts.Token).ConfigureAwait(false);
                lock (_networkObserverGate)
                {
                    if (_cts.IsCancellationRequested) return;
                    _connectivity = (Android.Net.ConnectivityManager?)GetSystemService(ConnectivityService);
                    _networkObserver = new NetworkObserver(this);
                    _connectivity?.RegisterDefaultNetworkCallback(_networkObserver);
                }
                WebUiServer.BroadcastStatusUpdate();
                if (preferences.TorEnabled)
                {
                    WebUiServer.LogEvent("[TOR] Restoring Tor from the previous session...");
                    try
                    {
                        await _qcc.StartTorAsync(preferences.TorVirtualPort, cancellationToken: _cts.Token).ConfigureAwait(false);
                        WebUiServer.LogEvent($"[TOR] Hidden service ready at {_qcc.TorOnionAddress}");
                    }
                    catch (Exception ex) when (!_cts.IsCancellationRequested)
                    {
                        WebUiServer.LogEvent($"[TOR] Start failed: {ex.Message}");
                    }
                    WebUiServer.BroadcastStatusUpdate();
                }
            }
            catch (Exception ex)
            {
                WebUiServer.LogEvent($"[P2P STARTUP] {ex.Message}");
            }
        });

    }

    // Without this lock Android drops inbound multicast, so desktop LAN discovery
    // announcements never reach the phone.
    private void AcquireMulticastLock()
    {
        try
        {
            var wifi = (Android.Net.Wifi.WifiManager?)ApplicationContext?.GetSystemService(WifiService);
            _multicastLock = wifi?.CreateMulticastLock("quicpunch-lan-discovery");
            _multicastLock?.SetReferenceCounted(false);
            _multicastLock?.Acquire();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("QuicPunch", $"[ANDROID SERVICE] Multicast lock unavailable: {ex.Message}");
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
        lock (_networkObserverGate)
        {
            if (_networkObserver != null) try { _connectivity?.UnregisterNetworkCallback(_networkObserver); } catch { }
            _networkObserver?.Dispose();
            _networkObserver = null;
        }
        _voiceCallHandler?.Dispose();
        _speedTestHandler?.Dispose();
        _qcc?.Dispose();
        _updater?.Dispose();
        _server?.Stop();
        _cts?.Dispose();
        try { if (_multicastLock?.IsHeld == true) _multicastLock.Release(); } catch { }
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }
}
