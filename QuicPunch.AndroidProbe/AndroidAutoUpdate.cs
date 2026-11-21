using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.OS;
using QuicNet.Updates;

namespace QuicPunch.AndroidProbe;

internal static class AndroidAutoUpdate
{
    internal static WeakReference<MainActivity>? ForegroundActivity;
    private static string? _pendingPermissionPackage;
    private static long _confirmationRequiredBuild;
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(PackageInstallStatus Status, string? Message)>> InstallResults = new();

    internal static bool CanInstallAutomatically(Context context, UpdateArtifact artifact) =>
        OperatingSystem.IsAndroidVersionAtLeast(31) && Interlocked.Read(ref _confirmationRequiredBuild) != artifact.Build &&
        context.PackageManager?.CanRequestPackageInstalls() == true;

    internal static Task<bool> InstallAsync(Context context, string path, UpdateArtifact artifact, bool automatic,
        Func<Action, bool> tryCommitAutomatically, CancellationToken token)
    {
        if (!QuicNet.Protocols.VoiceCallHandler.ActiveCalls.IsEmpty) return Task.FromResult(false);
        var info = context.PackageManager!.GetPackageArchiveInfo(path, PackageInfoFlags.SigningCertificates)
            ?? throw new InvalidDataException("La APK no es válida.");
        if (info.PackageName != context.PackageName || info.LongVersionCode != artifact.Build)
            throw new InvalidDataException("La APK no corresponde a esta aplicación o compilación.");

        if (automatic)
            return OperatingSystem.IsAndroidVersionAtLeast(31) && CanInstallAutomatically(context, artifact)
                ? InstallInBackgroundAsync(context, path, artifact, tryCommitAutomatically, token) : Task.FromResult(false);

        var activity = ForegroundActivity?.TryGetTarget(out var current) == true ? current : null;
        if (activity == null || activity.IsFinishing || activity.IsDestroyed) return Task.FromResult(false);
        activity.RunOnUiThread(() =>
        {
            try
            {
                if (!activity.PackageManager!.CanRequestPackageInstalls())
                {
                    _pendingPermissionPackage = path;
                    activity.StartActivity(new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,
                        Android.Net.Uri.Parse("package:" + activity.PackageName)));
                    return;
                }
                OpenInstaller(activity, path);
            }
            catch (Exception ex)
            {
                _pendingPermissionPackage = null;
                QuicNet.WebUi.WebUiServer.Instance?.Updates?.ResetInstallAttempt();
                QuicNet.WebUi.WebUiServer.AddNotification("error", "No se pudo abrir el instalador: " + ex.Message);
            }
        });
        return Task.FromResult(true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private static async Task<bool> InstallInBackgroundAsync(Context context, string path, UpdateArtifact artifact,
        Func<Action, bool> tryCommitAutomatically, CancellationToken token)
    {
        using var installer = context.PackageManager!.PackageInstaller!;
        using var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(context.PackageName);
        parameters.SetSize(artifact.Size);
        parameters.SetRequireUserAction((int)PackageInstallUserAction.NotRequired);
        int sessionId = installer.CreateSession(parameters);
        var result = new TaskCompletionSource<(PackageInstallStatus Status, string? Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        InstallResults[sessionId] = result;
        bool installed = false;
        try
        {
            using var session = installer.OpenSession(sessionId)!;
            await using (var source = File.OpenRead(path))
            using (var destination = session.OpenWrite("base.apk", 0, artifact.Size)!)
            {
                await source.CopyToAsync(destination, token);
                session.Fsync(destination);
            }

            token.ThrowIfCancellationRequested();
            using var intent = new Intent(context, typeof(AndroidUpdateResultReceiver));
            // PackageInstaller supplies the result extras through an explicit, app-private callback.
            using var callback = PendingIntent.GetBroadcast(context, sessionId, intent, PendingIntentFlags.Mutable | PendingIntentFlags.OneShot)!;
            if (!tryCommitAutomatically(() => session.Commit(callback.IntentSender!))) return false;
            var outcome = await result.Task.WaitAsync(TimeSpan.FromMinutes(2), token);
            if (outcome.Status is PackageInstallStatus.PendingUserAction or PackageInstallStatus.FailureBlocked)
            {
                Interlocked.Exchange(ref _confirmationRequiredBuild, artifact.Build);
                return false;
            }
            installed = outcome.Status == PackageInstallStatus.Success;
            if (!installed) throw new IOException("Android no pudo instalar la actualización: " + (outcome.Message ?? outcome.Status.ToString()));
            return true;
        }
        finally
        {
            InstallResults.TryRemove(sessionId, out _);
            if (!installed)
            {
                try { installer.AbandonSession(sessionId); }
                catch (Java.Lang.IllegalStateException) { /* Android already ended this session. */ }
                catch (Java.Lang.SecurityException) { /* Android already released this session. */ }
            }
        }
    }

    internal static void ReportInstallResult(Intent intent)
    {
        if (InstallResults.TryGetValue(intent.GetIntExtra(PackageInstaller.ExtraSessionId, -1), out var result))
            result.TrySetResult(((PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure),
                intent.GetStringExtra(PackageInstaller.ExtraStatusMessage)));
    }

    internal static void Resume(MainActivity activity)
    {
        ForegroundActivity = new(activity);
        string? path = _pendingPermissionPackage;
        _pendingPermissionPackage = null;
        if (path == null || !activity.PackageManager!.CanRequestPackageInstalls()) return;
        if (!QuicNet.Protocols.VoiceCallHandler.ActiveCalls.IsEmpty)
        {
            QuicNet.WebUi.WebUiServer.Instance?.Updates?.SetWaitingForCalls();
            return;
        }
        try { OpenInstaller(activity, path); }
        catch (Exception ex) { QuicNet.WebUi.WebUiServer.AddNotification("error", "No se pudo abrir el instalador: " + ex.Message); }
    }

    private static void OpenInstaller(MainActivity activity, string path)
    {
        UpdatePackageProvider.ReadyPackage = path;
        var uri = Android.Net.Uri.Parse("content://" + activity.PackageName + ".updates/package.apk")!;
        activity.StartActivity(new Intent(Intent.ActionView)
            .SetDataAndType(uri, "application/vnd.android.package-archive")
            .AddFlags(ActivityFlags.GrantReadUriPermission));
    }
}

[BroadcastReceiver(Exported = false)]
public sealed class AndroidUpdateResultReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent != null) AndroidAutoUpdate.ReportInstallResult(intent);
    }
}

internal sealed class AndroidUpdateIdle(PowerManager power) : BroadcastReceiver
{
    private long _nonInteractiveSince = power.IsInteractive ? -1 : SystemClock.ElapsedRealtime();

    internal void Register(Context context)
    {
        using var filter = new IntentFilter(Intent.ActionScreenOff);
        filter.AddAction(Intent.ActionScreenOn);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) context.RegisterReceiver(this, filter, ReceiverFlags.NotExported);
        else context.RegisterReceiver(this, filter);
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action == Intent.ActionScreenOn) Interlocked.Exchange(ref _nonInteractiveSince, -1);
        else if (intent?.Action == Intent.ActionScreenOff) Interlocked.Exchange(ref _nonInteractiveSince, SystemClock.ElapsedRealtime());
    }

    internal TimeSpan? GetIdleTime()
    {
        if (power.IsInteractive)
        {
            Interlocked.Exchange(ref _nonInteractiveSince, -1);
            return TimeSpan.Zero;
        }
        long now = SystemClock.ElapsedRealtime();
        long since = Interlocked.CompareExchange(ref _nonInteractiveSince, now, -1);
        return TimeSpan.FromMilliseconds(since < 0 ? 0 : Math.Max(0, now - since));
    }
}

// Only the verified update APK is exposed; no arbitrary file path supplied by the caller.
[ContentProvider(new[] { "${quicNetApplicationId}.updates" }, Exported = false, GrantUriPermissions = true)]
public sealed class UpdatePackageProvider : ContentProvider
{
    internal static string? ReadyPackage { get; set; }
    public override bool OnCreate() => true;
    public override string? GetType(Android.Net.Uri uri) => "application/vnd.android.package-archive";
    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        if (uri.Path != "/package.apk" || mode != "r" || ReadyPackage == null) throw new Java.IO.FileNotFoundException();
        return ParcelFileDescriptor.Open(new Java.IO.File(ReadyPackage), ParcelFileMode.ReadOnly);
    }
    public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
    {
        if (ReadyPackage == null || uri.Path != "/package.apk") return null;
        var cursor = new MatrixCursor(new[] { "_display_name", "_size" });
        cursor.AddRow(new Java.Lang.Object[] { new Java.Lang.String("QuicNet.apk"), Java.Lang.Long.ValueOf(new FileInfo(ReadyPackage).Length)! });
        return cursor;
    }
    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => throw new Java.Lang.UnsupportedOperationException();
    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();
    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();
}
