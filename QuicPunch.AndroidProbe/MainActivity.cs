using Android.App;
using Android.Content;
using Android.Widget;
using Android.Content.PM;
using Android.Webkit;
using System.Runtime.InteropServices;
using System.Text;

namespace QuicPunch.AndroidProbe;

[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden, Theme = "@android:style/Theme.Material.NoActionBar")]
public class MainActivity : Activity
{
    private WebView? _webView;
    private SystemInsetLayout? _content;
    private string _webUrl = AndroidConnectionService.Url;
    private MobileChromeClient? _chromeClient;
    private ActivityBackCallback? _backCallback;
    private bool _checkingWebBack;
    private bool _visible;
    private bool _recoveredRenderer;
    private static int _visibleActivityCount;
    internal static bool IsVisible => Volatile.Read(ref _visibleActivityCount) > 0;
    internal static event Action? VisibilityChanged;
    private const int NotificationPermissionRequest = 78;
    private TaskCompletionSource? _notificationPermission;

    internal bool CanRequestNotificationPermission => OperatingSystem.IsAndroidVersionAtLeast(33) &&
        CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted &&
        (ShouldShowRequestPermissionRationale(Android.Manifest.Permission.PostNotifications) ||
         GetPreferences(FileCreationMode.Private)?.GetBoolean("notificationPermissionRequested", false) != true);

    internal Task RequestNotificationPermissionAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33) || Foreground != this || !CanRequestNotificationPermission || _notificationPermission != null ||
                _cameraPermission != null || _chromeClient?.HasPendingOperation == true)
            {
                completion.TrySetException(new InvalidOperationException("Abre los ajustes de Android para revisar los permisos de avisos."));
                return;
            }
            _notificationPermission = completion;
            try { RequestPermissions([Android.Manifest.Permission.PostNotifications], NotificationPermissionRequest); }
            catch (Exception ex) { _notificationPermission = null; completion.TrySetException(ex); }
        });
        return completion.Task;
    }
    private static MainActivity? _foreground;
    internal static MainActivity? Foreground
    {
        get => Volatile.Read(ref _foreground);
        private set => Volatile.Write(ref _foreground, value);
    }
    private TaskCompletionSource<Android.Content.Intent>? _projectionPermission;
    private const int ProjectionRequest = 74;
    private const int WatchVideoRequest = 75;
    private TaskCompletionSource<Android.Net.Uri?>? _watchSelection;
    private TaskCompletionSource<Android.Content.Intent?>? _diskSelection;
    private const int DiskSelectionRequest = 76;

    internal Task<Android.Content.Intent?> PickDiskDocumentAsync(bool folder, string? name, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Android.Content.Intent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); return; }
            if (Foreground != this || _diskSelection != null) { completion.TrySetException(new IOException("Abre QuicNet y termina la selección pendiente.")); return; }
            _diskSelection = completion;
            try
            {
                var intent = new Android.Content.Intent(folder ? Android.Content.Intent.ActionOpenDocumentTree : Android.Content.Intent.ActionCreateDocument);
                intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission | Android.Content.ActivityFlags.GrantWriteUriPermission);
                if (folder) intent.AddFlags(Android.Content.ActivityFlags.GrantPersistableUriPermission | Android.Content.ActivityFlags.GrantPrefixUriPermission);
                else { intent.SetType("application/octet-stream"); intent.AddCategory(Android.Content.Intent.CategoryOpenable); intent.PutExtra(Android.Content.Intent.ExtraTitle, name); }
#pragma warning disable CA1422
                StartActivityForResult(intent, DiskSelectionRequest);
#pragma warning restore CA1422
            }
            catch (Exception ex) { _diskSelection = null; completion.TrySetException(ex); }
        });
        return completion.Task.WaitAsync(cancellationToken);
    }

    internal Task<Android.Net.Uri?> PickWatchVideoAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Android.Net.Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); return; }
            if (Foreground != this || _watchSelection != null)
            {
                completion.TrySetException(new InvalidOperationException("Abre QuicNet y termina la selección pendiente."));
                return;
            }
            _watchSelection = completion;
            try
            {
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionOpenDocument);
                intent.SetType("video/*");
                intent.AddCategory(Android.Content.Intent.CategoryOpenable);
                intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
#pragma warning disable CA1422
                StartActivityForResult(intent, WatchVideoRequest);
#pragma warning restore CA1422
            }
            catch (Exception ex) { _watchSelection = null; completion.TrySetException(ex); }
        });
        return completion.Task.WaitAsync(cancellationToken);
    }

    private TaskCompletionSource? _cameraPermission;
    internal Task RequestNativeCameraAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() => {
            if (CheckSelfPermission(Android.Manifest.Permission.Camera) == Permission.Granted) { done.TrySetResult(); return; }
            if (Foreground != this || _cameraPermission != null) { done.TrySetException(new InvalidOperationException("Abre QuicNet para autorizar la cámara.")); return; }
            _cameraPermission = done;
            RequestPermissions(new[] { Android.Manifest.Permission.Camera }, 77);
        });
        return done.Task;
    }

    internal async Task<Android.Content.Intent> RequestProjectionAsync(bool wholeDisplay, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<Android.Content.Intent>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            if (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); return; }
            if (Foreground != this || _projectionPermission != null) { completion.TrySetException(new InvalidOperationException("Abre QuicNet para autorizar la pantalla.")); return; }
            _projectionPermission = completion;
            try
            {
                var manager = (Android.Media.Projection.MediaProjectionManager)GetSystemService(MediaProjectionService)!;
                var intent = wholeDisplay && OperatingSystem.IsAndroidVersionAtLeast(34)
                    ? manager.CreateScreenCaptureIntent(Android.Media.Projection.MediaProjectionConfig.CreateConfigForDefaultDisplay())
                    : manager.CreateScreenCaptureIntent();
                StartActivityForResult(intent, ProjectionRequest);
            }
            catch (Exception ex) { _projectionPermission = null; completion.TrySetException(ex); }
        });
        using var registration = ct.Register(() => RunOnUiThread(() =>
        {
            completion.TrySetCanceled(ct);
            if (_projectionPermission == completion) FinishActivity(ProjectionRequest);
        }));
        return await completion.Task.ConfigureAwait(false);
    }

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        if (FilesDir?.AbsolutePath is { } dataDirectory) QuicNet.Diagnostics.CoreErrorReporter.Start(dataDirectory);
        base.OnCreate(savedInstanceState);
        try
        {
            // Keep the WebView's viewport inside system bars, cutouts and the keyboard.
            // Android 15+ draws edge-to-edge even without a fullscreen window flag.
            if (OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(35))
                Window!.SetDecorFitsSystemWindows(false);
            Window!.SetSoftInputMode(Android.Views.SoftInput.AdjustResize);
#if DEBUG
            WebView.SetWebContentsDebuggingEnabled(true);
#endif
            var content = new SystemInsetLayout(this);
            _content = content;
            content.SetBackgroundColor(Android.Graphics.Color.Rgb(16, 17, 22));
            _webUrl = NotificationUrl(Intent);
            CreateWebView(content);
            SetContentView(content);
            if (OperatingSystem.IsAndroidVersionAtLeast(33) && OnBackInvokedDispatcher is { } dispatcher)
            {
                _backCallback = new ActivityBackCallback(this);
                dispatcher.RegisterOnBackInvokedCallback(Android.Window.IOnBackInvokedDispatcher.PriorityDefault, _backCallback);
            }
            QuicNet.Video.NativeVideoRuntime.Instance.Attach(new AndroidVideoHost(this, content), warmCapabilities: false);
            content.RequestApplyInsets();
        }
        catch (Exception ex)
        {
            QuicNet.Diagnostics.CoreErrorReporter.Report("Actividad Android", ex);
            SetContentView(new Android.Widget.TextView(this) { Text = $"Could not start Android WebView: {ex}" });
        }
    }

    private void CreateWebView(SystemInsetLayout content)
    {
        var webView = new WebView(this);
        _webView = webView;
        webView.SetBackgroundColor(Android.Graphics.Color.Rgb(16, 17, 22));
        webView.Settings.JavaScriptEnabled = true;
        webView.Settings.DomStorageEnabled = true;
        webView.Settings.MediaPlaybackRequiresUserGesture = false;
        webView.SetWebViewClient(new MobileWebViewClient(this));
        webView.SetRendererPriorityPolicy(RendererPriority.Important, true);
        _chromeClient = new MobileChromeClient(this, AndroidConnectionService.Url, content);
        webView.SetWebChromeClient(_chromeClient);
        content.AddView(webView, 0, new FrameLayout.LayoutParams(
            Android.Views.ViewGroup.LayoutParams.MatchParent,
            Android.Views.ViewGroup.LayoutParams.MatchParent));
        content.RequestApplyInsets();
    }

    private void DestroyWebView()
    {
        var webView = _webView;
        _webView = null;
        _checkingWebBack = false;
        _chromeClient?.CancelPendingOperations();
        _chromeClient = null;
        if (webView == null) return;
        _content?.RemoveView(webView);
        webView.Destroy();
        webView.Dispose();
    }

    private sealed class MobileWebViewClient(MainActivity activity) : WebViewClient
    {
        public override void DoUpdateVisitedHistory(WebView? view, string? url, bool isReload)
        {
            if (view == activity._webView && url?.StartsWith(AndroidConnectionService.Url, StringComparison.Ordinal) == true)
                activity._webUrl = url;
        }

        public override bool OnRenderProcessGone(WebView? view, RenderProcessGoneDetail? detail)
        {
            if (view == null || view != activity._webView) return true;
            activity.DestroyWebView();
            if (activity._visible && !activity.IsFinishing && activity._content is { } content)
            {
                content.Post(() =>
                {
                    if (!activity._visible || activity.IsFinishing || activity.IsDestroyed || activity._webView != null) return;
                    if (activity._recoveredRenderer) { activity.Finish(); return; }
                    activity._recoveredRenderer = true;
                    activity.CreateWebView(content);
                    activity._webView!.LoadUrl(activity._webUrl);
                });
            }
            return true;
        }
    }

#pragma warning disable CS0809, CA1422
    [Obsolete("Deprecated in Java")]
    public override void OnBackPressed() => HandleBack();

    private void HandleBack()
    {
        if (_chromeClient?.HideFullscreen() == true) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && Window is { InsetsController: { } controller } window &&
            window.DecorView.RootWindowInsets?.IsVisible(Android.Views.WindowInsets.Type.Ime()) == true)
        {
            controller.Hide(Android.Views.WindowInsets.Type.Ime());
            return;
        }

        if (_checkingWebBack) return;
        var webView = _webView;
        string appUrl = AndroidConnectionService.Url.TrimEnd('/') + "/";
        if (webView?.Url?.StartsWith(appUrl, StringComparison.Ordinal) != true)
        {
            NavigateBack(webView);
            return;
        }

        _checkingWebBack = true;
        webView.EvaluateJavascript("window.QP?.dismissMobileTools?.() === true", new WebBackResult(handled =>
        {
            _checkingWebBack = false;
            if (!handled && !IsFinishing && !IsDestroyed && _webView == webView) NavigateBack(webView);
        }));
    }

    private void NavigateBack(WebView? webView)
    {
        if (webView?.CanGoBack() == true) webView.GoBack();
        else base.OnBackPressed();
    }

    private sealed class WebBackResult(Action<bool> complete) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value) => complete(value?.ToString() == "true");
    }

    private sealed class ActivityBackCallback(MainActivity activity) : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => activity.HandleBack();
    }
#pragma warning restore CS0809, CA1422

    protected override void OnPause()
    {
        Foreground = null;
        AndroidAutoUpdate.ForegroundActivity = null;
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && _backCallback is { } callback)
        {
            OnBackInvokedDispatcher?.UnregisterOnBackInvokedCallback(callback);
            _backCallback = null;
        }
        _cameraPermission?.TrySetCanceled(); _cameraPermission = null;
        _notificationPermission?.TrySetCanceled(); _notificationPermission = null;
        _projectionPermission?.TrySetCanceled();
        _projectionPermission = null;
        _watchSelection?.TrySetResult(null);
        _watchSelection = null;
        _diskSelection?.TrySetResult(null);
        _diskSelection = null;
        QuicNet.Video.NativeVideoRuntime.Instance.Detach();
        DestroyWebView();
        base.OnDestroy();
    }

    protected override void OnStart()
    {
        base.OnStart();
        _visible = true;
        _recoveredRenderer = false;
        Interlocked.Increment(ref _visibleActivityCount);
        VisibilityChanged?.Invoke();
    }

    protected override void OnStop()
    {
        base.OnStop();
        if (_webView?.Url is { } url && url.StartsWith(AndroidConnectionService.Url, StringComparison.Ordinal))
            _webUrl = url;
        _visible = false;
        Interlocked.Decrement(ref _visibleActivityCount);
        VisibilityChanged?.Invoke();
        if (!IsChangingConfigurations && _notificationPermission == null && _projectionPermission == null && _cameraPermission == null && _watchSelection == null && _diskSelection == null && !AndroidWatchFilePicker.IsImporting && !AndroidScreenCaptureService.IsCapturing && _chromeClient?.HasPendingOperation != true && !AndroidAppPlatform.Preferences(this).RunInBackground)
            StopService(new Android.Content.Intent(this, typeof(AndroidConnectionService)));
    }

    protected override void OnNewIntent(Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        _webUrl = NotificationUrl(intent);
        _webView?.LoadUrl(_webUrl);
    }
    private static string NotificationUrl(Android.Content.Intent? intent)
    {
        string route = intent?.GetStringExtra("quicnetRoute") ?? "/";
        if (!route.StartsWith('/') || route.StartsWith("//") || route.Contains('\\')) route = "/";
        return AndroidConnectionService.Url.TrimEnd('/') + route;
    }

    protected override void OnResume()
    {
        base.OnResume();
        Foreground = this;
        QuicNet.WebUi.WebUiServer.Instance?.Updates?.ResetInstallAttempt();
        AndroidAutoUpdate.Resume(this);
        bool reconnect = !AndroidConnectionService.Running;
        StartNodeService();
        if (_webView == null && _content is { } content) CreateWebView(content);
        if (_webView is { } webView && (reconnect || webView.Url == null))
            webView.PostDelayed(() => { if (webView == _webView) webView.LoadUrl(_webUrl); }, 400);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == NotificationPermissionRequest)
        {
            if (grantResults.Length > 0)
                GetPreferences(FileCreationMode.Private)?.Edit()?.PutBoolean("notificationPermissionRequested", true)?.Apply();
            var notification = _notificationPermission;
            _notificationPermission = null;
            notification?.TrySetResult();
            _chromeClient?.ProcessPermissionQueue();
            return;
        }
        if (requestCode == 77) {
            var camera = _cameraPermission; _cameraPermission = null;
            if (CheckSelfPermission(Android.Manifest.Permission.Camera) == Permission.Granted) camera?.TrySetResult();
            else camera?.TrySetException(new UnauthorizedAccessException("Permiso de cámara denegado."));
        }
        StartNodeService();
        _chromeClient?.CompletePermissionRequest(requestCode);
    }

#pragma warning disable CS0672, CA1422
    protected override void OnActivityResult(int requestCode, Result resultCode, Android.Content.Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == DiskSelectionRequest)
        {
            var completion = _diskSelection; _diskSelection = null;
            completion?.TrySetResult(resultCode == Result.Ok ? data : null);
        }
        if (requestCode == WatchVideoRequest)
        {
            var completion = _watchSelection;
            _watchSelection = null;
            completion?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
        }
        if (requestCode == ProjectionRequest)
        {
            var completion = _projectionPermission; _projectionPermission = null;
            if (resultCode == Result.Ok && data != null) completion?.TrySetResult(data);
            else completion?.TrySetException(new UnauthorizedAccessException("Se ha rechazado la captura de pantalla en Android."));
        }
        _chromeClient?.CompleteFileSelection(requestCode, resultCode, data);
    }
#pragma warning restore CS0672, CA1422

    private void StartNodeService()
    {
        var keyguard = (KeyguardManager)GetSystemService(KeyguardService)!;
        bool allowMicrophone = !keyguard.IsKeyguardLocked &&
            CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted;
        StartForegroundService(new Android.Content.Intent(this, typeof(AndroidConnectionService))
            .PutExtra("allowMicrophone", allowMicrophone));
    }

    internal static string NativePing(string host, ushort port, string fingerprint)
    {
        byte[] output = new byte[200];
        int success = QpProbePing(host, port, fingerprint, output, (uint)output.Length);
        int length = Array.IndexOf(output, (byte)0);
        string message = Encoding.UTF8.GetString(output, 0, length < 0 ? output.Length : length);
        return success == 1 ? $"Android ↔ desktop: {message}" : $"Connection failed: {message}";
    }

    [DllImport("qp_probe", EntryPoint = "qp_probe_ping", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QpProbePing([MarshalAs(UnmanagedType.LPUTF8Str)] string host,
        ushort port, [MarshalAs(UnmanagedType.LPUTF8Str)] string fingerprint,
        [Out] byte[] output, uint outputCapacity);

    private sealed class MobileChromeClient(MainActivity activity, string localUrl, SystemInsetLayout content) : WebChromeClient
    {
        private Android.Views.View? _fullscreenView;
        private ICustomViewCallback? _fullscreenCallback;
        private FullscreenBackCallback? _fullscreenBack;
        private int _barsToRestore;
        private int _previousBarsBehavior;
        private Android.Views.SystemUiFlags _previousUiFlags;

        public override void OnShowCustomView(Android.Views.View? view, ICustomViewCallback? callback)
        {
            var window = activity.Window;
            if (view == null || callback == null || _fullscreenView != null || window == null || activity.IsFinishing || activity.IsDestroyed)
            {
                callback?.OnCustomViewHidden();
                return;
            }
            // Keep the fullscreen WebView content in the activity, between the
            // original WebView and the native video textures that share its coordinates.
            _fullscreenView = view;
            _fullscreenCallback = callback;
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    if (window.InsetsController is { } controller)
                    {
                        var insets = content.RootWindowInsets;
                        int status = Android.Views.WindowInsets.Type.StatusBars();
                        int navigation = Android.Views.WindowInsets.Type.NavigationBars();
                        _barsToRestore = (insets?.IsVisible(status) != false ? status : 0) |
                            (insets?.IsVisible(navigation) != false ? navigation : 0);
                        _previousBarsBehavior = controller.SystemBarsBehavior;
                        controller.SystemBarsBehavior = 2; // BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                        controller.Hide(status | navigation);
                    }
                }
                else
                {
#pragma warning disable CA1422
                    _previousUiFlags = window.DecorView.SystemUiFlags;
                    window.DecorView.SystemUiFlags =
                        _previousUiFlags | Android.Views.SystemUiFlags.Fullscreen | Android.Views.SystemUiFlags.HideNavigation |
                        Android.Views.SystemUiFlags.ImmersiveSticky | Android.Views.SystemUiFlags.LayoutFullscreen |
                        Android.Views.SystemUiFlags.LayoutHideNavigation | Android.Views.SystemUiFlags.LayoutStable;
#pragma warning restore CA1422
                }
                content.AddView(view, 1, new FrameLayout.LayoutParams(
                    Android.Views.ViewGroup.LayoutParams.MatchParent, Android.Views.ViewGroup.LayoutParams.MatchParent));
                if (OperatingSystem.IsAndroidVersionAtLeast(33) && activity.OnBackInvokedDispatcher is { } dispatcher)
                {
                    _fullscreenBack = new FullscreenBackCallback(this);
                    dispatcher.RegisterOnBackInvokedCallback(Android.Window.IOnBackInvokedDispatcher.PriorityDefault, _fullscreenBack);
                }
                content.RequestApplyInsets();
            }
            catch { HideFullscreen(); }
        }

        public override void OnHideCustomView() => HideFullscreen();

        public bool HideFullscreen()
        {
            if (_fullscreenView is not { } view) return false;
            var callback = _fullscreenCallback;
            _fullscreenView = null; _fullscreenCallback = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(33) && _fullscreenBack is { } back)
            {
                activity.OnBackInvokedDispatcher?.UnregisterOnBackInvokedCallback(back);
                _fullscreenBack = null;
                back.Dispose();
            }
            content.RemoveView(view);
            if (activity.Window is { } window)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    if (window.InsetsController is { } controller)
                    {
                        controller.SystemBarsBehavior = _previousBarsBehavior;
                        if (_barsToRestore != 0) controller.Show(_barsToRestore);
                    }
                }
                else
                {
#pragma warning disable CA1422
                    window.DecorView.SystemUiFlags = _previousUiFlags;
#pragma warning restore CA1422
                }
            }
            content.RequestApplyInsets();
            callback?.OnCustomViewHidden();
            return true;
        }

        private sealed class FullscreenBackCallback(MobileChromeClient client) : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
        {
            public void OnBackInvoked() => client.HideFullscreen();
        }

        private IValueCallback? _fileCallback;
        private const int PhotoSelectionRequest = 73;
        public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams)
        {
            CancelFileSelection();
            if (filePathCallback == null) return false;
            if (!Uri.TryCreate(webView?.Url, UriKind.Absolute, out var current) || current.GetLeftPart(UriPartial.Authority) != new Uri(localUrl).GetLeftPart(UriPartial.Authority)) return false;
            _fileCallback = filePathCallback;
            try
            {
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionOpenDocument);
                intent.SetType("*/*");
                var types = fileChooserParams?.GetAcceptTypes()?.Where(t => t.Contains('/') && !t.StartsWith('.')).ToArray() ?? [];
                if (types.Length > 0) intent.PutExtra(Android.Content.Intent.ExtraMimeTypes, types);
                intent.PutExtra(Android.Content.Intent.ExtraAllowMultiple, fileChooserParams?.Mode == ChromeFileChooserMode.OpenMultiple);
                intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
                intent.AddCategory(Android.Content.Intent.CategoryOpenable);
                activity.StartActivityForResult(intent, PhotoSelectionRequest);
            }
            catch { CancelFileSelection(); }
            return true;
        }
        public void CompleteFileSelection(int code, Result result, Android.Content.Intent? data)
        {
            if (code != PhotoSelectionRequest || _fileCallback == null) return;
            var callback = _fileCallback; _fileCallback = null;
            var uris = new List<Android.Net.Uri>();
            if (result == Result.Ok)
            {
                if (data?.ClipData is { } clips)
                    for (int i = 0; i < clips.ItemCount; i++) { if (clips.GetItemAt(i)?.Uri is { } uri) uris.Add(uri); }
                else if (data?.Data is { } uri) uris.Add(uri);
            }
            callback.OnReceiveValue(uris.Count > 0 ? Java.Lang.Object.FromArray(uris.ToArray()) : null);
        }
        public void CancelFileSelection() { _fileCallback?.OnReceiveValue(null); _fileCallback = null; }
        public void CancelPendingOperations()
        {
            _closing = true;
            HideFullscreen(); CancelFileSelection();
            var pending = _pending; _pending = null; _resources = [];
            pending?.Deny();
            foreach (var item in _permissionQueue.ToArray()) item.Request.Deny();
            _permissionQueue.Clear();
        }
        private PermissionRequest? _pending;
        private string[] _resources = [];
        private readonly Queue<(PermissionRequest Request, string[] Resources)> _permissionQueue = new();
        private readonly HashSet<string> _deniedPermissions = new(StringComparer.Ordinal);
        private string[] _requestedPermissions = [];
        private bool _runtimePermissionPending;
        private bool _closing;
        private const int MediaPermissionRequest = 42;
        public bool HasPendingOperation => _fileCallback != null || _pending != null || _runtimePermissionPending ||
            _permissionQueue.Count > 0;
        public override void OnPermissionRequest(PermissionRequest? request)
        {
            if (request is null) return;
            activity.RunOnUiThread(() =>
            {
                string origin = request.Origin?.ToString() ?? "";
                string expected = new Uri(localUrl).GetLeftPart(UriPartial.Authority);
                if (_closing || activity.IsFinishing || activity.IsDestroyed || origin.TrimEnd('/') != expected ||
                    request.GetResources() is not { Length: > 0 } resources ||
                    resources.Any(resource => resource != PermissionRequest.ResourceAudioCapture && resource != PermissionRequest.ResourceVideoCapture))
                {
                    request.Deny();
                    return;
                }
                if (_permissionQueue.Count >= 4) { request.Deny(); return; }
                _permissionQueue.Enqueue((request, resources));
                ProcessPermissionQueue();
            });
        }

        internal void ProcessPermissionQueue()
        {
            // Android's result code belongs to the one system dialog in flight.
            // Never replace its WebView request with a later camera/audio request.
            if (_closing) return;
            while (!_closing && _pending == null && !_runtimePermissionPending && activity._notificationPermission == null &&
                _permissionQueue.TryDequeue(out var next))
            {
                if (activity.IsFinishing || activity.IsDestroyed) { next.Request.Deny(); continue; }
                _pending = next.Request; _resources = next.Resources;
                var missing = new List<string>();
                if (_resources.Contains(PermissionRequest.ResourceAudioCapture) &&
                    activity.CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted)
                    missing.Add(Android.Manifest.Permission.RecordAudio);
                if (_resources.Contains(PermissionRequest.ResourceVideoCapture) &&
                    activity.CheckSelfPermission(Android.Manifest.Permission.Camera) != Permission.Granted)
                    missing.Add(Android.Manifest.Permission.Camera);
                if (missing.Count == 0) { ResolvePermissionRequest(); continue; }
                if (missing.Any(_deniedPermissions.Contains))
                {
                    var denied = _pending; _pending = null; _resources = [];
                    denied?.Deny(); continue;
                }
                _runtimePermissionPending = true;
                _requestedPermissions = missing.ToArray();
                try { activity.RequestPermissions(_requestedPermissions, MediaPermissionRequest); }
                catch
                {
                    _runtimePermissionPending = false; _requestedPermissions = [];
                    var request = _pending; _pending = null; _resources = [];
                    request?.Deny();
                    continue;
                }
                return;
            }
            if (_pending == null && !_runtimePermissionPending && _permissionQueue.Count == 0) _deniedPermissions.Clear();
        }

        public void CompletePermissionRequest(int code)
        {
            if (code == MediaPermissionRequest)
            {
                foreach (string permission in _requestedPermissions)
                    if (activity.CheckSelfPermission(permission) != Permission.Granted) _deniedPermissions.Add(permission);
                _requestedPermissions = [];
                _runtimePermissionPending = false;
                ResolvePermissionRequest();
            }
            if (code == MediaPermissionRequest) ProcessPermissionQueue();
        }

        private void ResolvePermissionRequest()
        {
            if (_pending == null) return;
            var request = _pending;
            _pending = null;
            var resources = _resources; _resources = [];
            bool audio = !resources.Contains(PermissionRequest.ResourceAudioCapture) ||
                activity.CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted;
            bool video = !resources.Contains(PermissionRequest.ResourceVideoCapture) ||
                activity.CheckSelfPermission(Android.Manifest.Permission.Camera) == Permission.Granted;
            if (!activity.IsFinishing && !activity.IsDestroyed && audio && video) request.Grant(resources);
            else request.Deny();
        }

        public override void OnPermissionRequestCanceled(PermissionRequest? request)
        {
            if (_pending == request) { _pending = null; _resources = []; }
            var remaining = _permissionQueue.Where(item => item.Request != request).ToArray();
            _permissionQueue.Clear();
            foreach (var item in remaining) _permissionQueue.Enqueue(item);
            // Cancelling the document request does not dismiss Android's dialog.
            // Its result must arrive before another dialog uses the same code.
            ProcessPermissionQueue();
        }
    }
}

