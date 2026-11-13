using System.Collections.Concurrent;
using System.Security.Cryptography;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Webkit;
using QuicNet.WebUi;

namespace QuicPunch.AndroidProbe;

internal sealed class AndroidChatFileOpener : IChatFileOpener
{
    public Task OpenAsync(string path, string name, string mime, CancellationToken cancellationToken)
    {
        var activity = MainActivity.Foreground;
        if (activity == null || activity.IsFinishing || activity.IsDestroyed)
            throw new InvalidOperationException("Abre QuicNet para ver el archivo.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(() =>
        {
            Android.Net.Uri? uri = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MainActivity.Foreground != activity || activity.IsFinishing || activity.IsDestroyed)
                    throw new InvalidOperationException("Abre QuicNet para ver el archivo.");
                var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
                var resolvedMime = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension) ?? mime;
                if (string.IsNullOrWhiteSpace(resolvedMime) || !resolvedMime.Contains('/') || resolvedMime.Any(char.IsWhiteSpace))
                    resolvedMime = "application/octet-stream";
                uri = ChatAttachmentProvider.Grant(path, name, resolvedMime);
                var intent = new Intent(Intent.ActionView).SetDataAndType(uri, resolvedMime)
                    .AddFlags(ActivityFlags.GrantReadUriPermission);
                intent.ClipData = ClipData.NewRawUri(name, uri);
                // Do not query package visibility: Android may hide a compatible viewer from queries.
                activity.StartActivity(intent);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                if (uri != null) ChatAttachmentProvider.Revoke(uri);
                if (ex is System.OperationCanceledException) completion.TrySetCanceled(cancellationToken);
                else completion.TrySetException(new IOException(ex is ActivityNotFoundException
                    ? "No hay una aplicación compatible para abrir este archivo. Instala un visor e inténtalo de nuevo."
                    : "No se pudo abrir el archivo. Vuelve a QuicNet e inténtalo de nuevo."));
            }
        });
        return completion.Task;
    }
}

// Each URI authorizes one already-resolved attachment; it never contains or accepts a filesystem path.
[ContentProvider(new[] { Authority }, Exported = false, GrantUriPermissions = true)]
public sealed class ChatAttachmentProvider : ContentProvider
{
    private const string Authority = "org.quicpunch.androidprobe.chatfiles";
    private sealed record GrantEntry(string Path, string Name, string Mime, DateTime Expires);
    private static readonly ConcurrentDictionary<string, GrantEntry> Grants = new(StringComparer.Ordinal);
    private static readonly object GrantGate = new();
    private static readonly Timer Cleanup = new(_ => RemoveExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    internal static Android.Net.Uri Grant(string path, string name, string mime)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || new FileInfo(path).LinkTarget != null)
            throw new IOException("El archivo ya no está disponible.");
        path = new Java.IO.File(path).CanonicalPath;
        lock (GrantGate)
        {
            RemoveExpired();
            while (Grants.Count >= 64)
            {
                var oldest = Grants.MinBy(pair => pair.Value.Expires);
                Revoke(ToUri(oldest.Key));
            }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            Grants[token] = new(path, Path.GetFileName(name), mime, DateTime.UtcNow.AddMinutes(15));
            return ToUri(token);
        }
    }

    private static Android.Net.Uri ToUri(string token) => Android.Net.Uri.Parse($"content://{Authority}/{token}")!;

    internal static void Revoke(Android.Net.Uri uri)
    {
        if (uri.Authority != Authority) return;
        lock (GrantGate)
        {
            Grants.TryRemove(uri.Path?.TrimStart('/') ?? "", out _);
            try { Android.App.Application.Context.RevokeUriPermission(uri, ActivityFlags.GrantReadUriPermission); }
            catch (Java.Lang.SecurityException) { }
        }
    }

    private static void RemoveExpired()
    {
        lock (GrantGate)
            foreach (var pair in Grants)
                if (pair.Value.Expires <= DateTime.UtcNow) Revoke(ToUri(pair.Key));
    }

    private static GrantEntry Resolve(Android.Net.Uri uri)
    {
        var token = uri.Path?.TrimStart('/') ?? "";
        if (uri.Scheme != "content" || uri.Authority != Authority || uri.Query != null || uri.Fragment != null ||
            token.Length != 64 || !token.All(Uri.IsHexDigit) || !Grants.TryGetValue(token, out var entry))
            throw new Java.IO.FileNotFoundException("Adjunto no disponible.");
        if (entry.Expires <= DateTime.UtcNow)
        {
            Revoke(uri);
            throw new Java.IO.FileNotFoundException("Abre de nuevo el archivo desde QuicNet.");
        }
        if (!File.Exists(entry.Path) || new FileInfo(entry.Path).LinkTarget != null ||
            !string.Equals(entry.Path, new Java.IO.File(entry.Path).CanonicalPath, StringComparison.Ordinal))
            throw new Java.IO.FileNotFoundException("Adjunto no disponible.");
        return entry;
    }

    public override bool OnCreate() => true;
    public override string? GetType(Android.Net.Uri uri) => Resolve(uri).Mime;
    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        if (mode != "r") throw new Java.IO.FileNotFoundException("Solo lectura.");
        return ParcelFileDescriptor.Open(new Java.IO.File(Resolve(uri).Path), ParcelFileMode.ReadOnly);
    }

    public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
    {
        var entry = Resolve(uri);
        var columns = projection ?? ["_display_name", "_size"];
        var cursor = new MatrixCursor(columns);
        var values = columns.Select(column => column switch
        {
            "_display_name" => (Java.Lang.Object)new Java.Lang.String(entry.Name),
            "_size" => Java.Lang.Long.ValueOf(new FileInfo(entry.Path).Length)!,
            _ => null!
        }).ToArray();
        cursor.AddRow(values);
        return cursor;
    }

    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => throw new Java.Lang.UnsupportedOperationException();
    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();
    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();
}
