using Android.App;
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
    private MobileChromeClient? _chromeClient;
    private bool _startupPermissionsPending;
    internal static MainActivity? Foreground { get; private set; }
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

    internal Task<Android.Content.Intent> RequestProjectionAsync()
    {
        var completion = new TaskCompletionSource<Android.Content.Intent>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            if (Foreground != this || _projectionPermission != null) { completion.TrySetException(new InvalidOperationException("Abre QuicNet para autorizar la pantalla.")); return; }
            _projectionPermission = completion;
            try
            {
                var manager = (Android.Media.Projection.MediaProjectionManager)GetSystemService(MediaProjectionService)!;
                StartActivityForResult(manager.CreateScreenCaptureIntent(), ProjectionRequest);
            }
            catch (Exception ex) { _projectionPermission = null; completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            // Keep the WebView's viewport inside system bars, cutouts and the keyboard.
            // Android 15+ draws edge-to-edge even without a fullscreen window flag.
            if (OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(35))
                Window!.SetDecorFitsSystemWindows(false);
            Window!.SetSoftInputMode(Android.Views.SoftInput.AdjustResize);
            StartNodeService();

#if DEBUG
            WebView.SetWebContentsDebuggingEnabled(true);
#endif
            _webView = new WebView(this);
            _webView.SetBackgroundColor(Android.Graphics.Color.Rgb(16, 17, 22));
            _webView.Settings.JavaScriptEnabled = true;
            _webView.Settings.DomStorageEnabled = true;
            _webView.Settings.MediaPlaybackRequiresUserGesture = false;
            _webView.SetWebViewClient(new WebViewClient());
            _chromeClient = new MobileChromeClient(this, AndroidConnectionService.Url);
            _webView.SetWebChromeClient(_chromeClient);
            var content = new SystemInsetLayout(this);
            content.SetBackgroundColor(Android.Graphics.Color.Rgb(16, 17, 22));
            content.AddView(_webView, new FrameLayout.LayoutParams(
                Android.Views.ViewGroup.LayoutParams.MatchParent,
                Android.Views.ViewGroup.LayoutParams.MatchParent));
            SetContentView(content);
            QuicNet.Video.NativeMedia.qpv_android_init(Android.Runtime.JNIEnv.Handle);
            QuicNet.Video.NativeVideoRuntime.Instance.Attach(new AndroidVideoHost(this, content));
            content.RequestApplyInsets();
            _webView.PostDelayed(() => OpenNotification(Intent), 300);

            var missingPermissions = new List<string>();
            foreach (string permission in new[] { Android.Manifest.Permission.RecordAudio })
                if (CheckSelfPermission(permission) != Permission.Granted) missingPermissions.Add(permission);
            if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
                CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
                missingPermissions.Add(Android.Manifest.Permission.PostNotifications);
            if (missingPermissions.Count > 0)
            {
                _startupPermissionsPending = true;
                try { RequestPermissions(missingPermissions.ToArray(), 1); }
                catch { _startupPermissionsPending = false; throw; }
            }
        }
        catch (Exception ex)
        {
            SetContentView(new Android.Widget.TextView(this) { Text = $"Could not start Android WebView: {ex}" });
        }
    }

#pragma warning disable CS0809, CA1422
    [Obsolete("Deprecated in Java")]
    public override void OnBackPressed()
    {
        if (_chromeClient?.HideFullscreen() == true) return;
        if (_webView != null && _webView.CanGoBack())
        {
            _webView.GoBack();
        }
        else
        {
            base.OnBackPressed();
        }
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
        _cameraPermission?.TrySetCanceled(); _cameraPermission = null;
        _projectionPermission?.TrySetCanceled();
        _projectionPermission = null;
        _watchSelection?.TrySetResult(null);
        _watchSelection = null;
        _diskSelection?.TrySetResult(null);
        _diskSelection = null;
        QuicNet.Video.NativeVideoRuntime.Instance.Detach();
        _chromeClient?.CancelPendingOperations();
        _webView?.Destroy();
        base.OnDestroy();
    }

    protected override void OnStop()
    {
        base.OnStop();
        if (!IsChangingConfigurations && !_startupPermissionsPending && _projectionPermission == null && _cameraPermission == null && _watchSelection == null && _diskSelection == null && !AndroidWatchFilePicker.IsImporting && !AndroidScreenCaptureService.IsCapturing && _chromeClient?.HasPendingOperation != true && !AndroidAppPlatform.Preferences(this).RunInBackground)
            StopService(new Android.Content.Intent(this, typeof(AndroidConnectionService)));
    }

    protected override void OnNewIntent(Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        OpenNotification(intent);
    }
    private void OpenNotification(Android.Content.Intent? intent)
    {
        string route = intent?.GetStringExtra("quicnetRoute") ?? "/";
        if (!route.StartsWith('/') || route.StartsWith("//") || route.Contains('\\')) route = "/";
        _webView?.LoadUrl(AndroidConnectionService.Url.TrimEnd('/') + route);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Foreground = this;
        QuicNet.WebUi.WebUiServer.Instance?.Updates?.ResetInstallAttempt();
        AndroidAutoUpdate.Resume(this);
        bool reconnect = !AndroidConnectionService.Running;
        StartNodeService();
        if (reconnect && _webView != null) _webView.PostDelayed(() => _webView.Reload(), 400);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 1) _startupPermissionsPending = false;
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
            else completion?.TrySetCanceled();
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

    private sealed class MobileChromeClient(MainActivity activity, string localUrl) : WebChromeClient
    {
        private Dialog? _fullscreenDialog;
        private SystemInsetLayout? _fullscreenContent;
        private ICustomViewCallback? _fullscreenCallback;

        public override void OnShowCustomView(Android.Views.View? view, ICustomViewCallback? callback)
        {
            if (view == null || callback == null || _fullscreenDialog != null || activity.IsFinishing || activity.IsDestroyed)
            {
                callback?.OnCustomViewHidden();
                return;
            }
            // A separate window leaves the activity's normal system-bar and inset
            // configuration intact when fullscreen closes or the device rotates.
            var dialog = new Dialog(activity, Android.Resource.Style.ThemeBlackNoTitleBarFullScreen);
            var content = new SystemInsetLayout(activity);
            content.SetBackgroundColor(Android.Graphics.Color.Black);
            content.AddView(view, new FrameLayout.LayoutParams(
                Android.Views.ViewGroup.LayoutParams.MatchParent, Android.Views.ViewGroup.LayoutParams.MatchParent));
            _fullscreenDialog = dialog;
            _fullscreenContent = content;
            _fullscreenCallback = callback;
            dialog.SetContentView(content);
            dialog.CancelEvent += (_, _) => HideFullscreen(); // Includes Android's Back gesture.
            try
            {
                dialog.Show();
                var window = dialog.Window!;
                window.SetLayout(Android.Views.ViewGroup.LayoutParams.MatchParent, Android.Views.ViewGroup.LayoutParams.MatchParent);
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    if (!OperatingSystem.IsAndroidVersionAtLeast(35)) window.SetDecorFitsSystemWindows(false);
                    if (window.InsetsController is { } controller)
                    {
                        controller.SystemBarsBehavior = 2; // BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                        controller.Hide(Android.Views.WindowInsets.Type.SystemBars());
                    }
                }
                else
                {
#pragma warning disable CA1422
                    window.DecorView.SystemUiFlags =
                        Android.Views.SystemUiFlags.Fullscreen | Android.Views.SystemUiFlags.HideNavigation |
                        Android.Views.SystemUiFlags.ImmersiveSticky | Android.Views.SystemUiFlags.LayoutFullscreen |
                        Android.Views.SystemUiFlags.LayoutHideNavigation | Android.Views.SystemUiFlags.LayoutStable;
#pragma warning restore CA1422
                }
                content.RequestApplyInsets();
            }
            catch { HideFullscreen(); }
        }

        public override void OnHideCustomView() => HideFullscreen();

        public bool HideFullscreen()
        {
            if (_fullscreenDialog == null) return false;
            var dialog = _fullscreenDialog;
            var callback = _fullscreenCallback;
            _fullscreenDialog = null; _fullscreenCallback = null;
            _fullscreenContent?.RemoveAllViews(); _fullscreenContent = null;
            dialog.Dismiss();
            dialog.Dispose();
            callback?.OnCustomViewHidden();
            return true;
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
            _permissionQueue.Count > 0 || activity._startupPermissionsPending;
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

        private void ProcessPermissionQueue()
        {
            // Android's result code belongs to the one system dialog in flight.
            // Never replace its WebView request with a later camera/audio request.
            if (_closing) return;
            while (!_closing && _pending == null && !_runtimePermissionPending && !activity._startupPermissionsPending &&
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
            else if (code == 1 && activity.CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted)
                _deniedPermissions.Add(Android.Manifest.Permission.RecordAudio);
            if (code == MediaPermissionRequest || code == 1) ProcessPermissionQueue();
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

