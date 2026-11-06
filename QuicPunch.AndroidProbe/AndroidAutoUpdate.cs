using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.OS;
using QuicNet.Updates;

namespace QuicPunch.AndroidProbe;

internal static class AndroidAutoUpdate
{
    internal static WeakReference<MainActivity>? ForegroundActivity;
    internal static string? PendingPackage;
    internal static Task<bool> InstallAsync(string path, UpdateArtifact artifact, bool automatic)
    {
        var activity = ForegroundActivity != null && ForegroundActivity.TryGetTarget(out var current) ? current : null;
        if (activity == null || activity.IsFinishing || activity.IsDestroyed || QuicNet.Protocols.VoiceCallHandler.ActiveCalls.Count != 0)
            return Task.FromResult(false);
        var info = activity.PackageManager!.GetPackageArchiveInfo(path, PackageInfoFlags.SigningCertificates)
            ?? throw new InvalidDataException("La APK no es válida.");
        if (info.PackageName != activity.PackageName || info.LongVersionCode != artifact.Build)
            throw new InvalidDataException("La APK no corresponde a esta aplicación o compilación.");
        PendingPackage = path;
        if (automatic && !activity.PackageManager.CanRequestPackageInstalls()) return Task.FromResult(false);
        activity.RunOnUiThread(() =>
        {
            try
            {
                if (!activity.PackageManager.CanRequestPackageInstalls())
                {
                    activity.StartActivity(new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,
                        Android.Net.Uri.Parse("package:" + activity.PackageName)));
                    return;
                }
                OpenInstaller(activity);
            }
            catch (Exception ex) { QuicNet.WebUi.WebUiServer.AddNotification("error", "No se pudo abrir el instalador: " + ex.Message); }
        });
        return Task.FromResult(true);
    }

    internal static void Resume(MainActivity activity)
    {
        ForegroundActivity = new(activity);
        if (PendingPackage != null && activity.PackageManager!.CanRequestPackageInstalls())
            OpenInstaller(activity);
    }

    private static void OpenInstaller(MainActivity activity)
    {
        if (PendingPackage == null) return;
        var uri = Android.Net.Uri.Parse("content://" + activity.PackageName + ".updates/package.apk")!;
        activity.StartActivity(new Intent(Intent.ActionView)
            .SetDataAndType(uri, "application/vnd.android.package-archive")
            .AddFlags(ActivityFlags.GrantReadUriPermission));
        PendingPackage = null;
    }
}

// Only the verified update APK is exposed; no arbitrary file path supplied by the caller.
[ContentProvider(new[] { "org.quicpunch.androidprobe.updates" }, Exported = false, GrantUriPermissions = true)]
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
