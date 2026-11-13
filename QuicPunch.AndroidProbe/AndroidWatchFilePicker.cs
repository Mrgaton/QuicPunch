using Android.Content;
using Android.Provider;
using QuicNet.Watch.Services;

namespace QuicPunch.AndroidProbe;

// SAF providers may be remote or non-seekable. A private, disposable copy gives
// MediaLibrary reliable random access without broad storage permissions.
internal sealed class AndroidWatchFilePicker(Context context, CancellationToken shutdown) : IWatchFilePicker, IDisposable
{
    private readonly string _root = Path.Combine(context.CacheDir!.AbsolutePath, "watch-imports");
    private readonly object _gate = new();
    private string? _committedPath;
    private bool _disposed;
    private static int _importing;
    internal static bool IsImporting => Volatile.Read(ref _importing) != 0;

    public async Task<string?> PickVideoFileAsync()
    {
        if (Interlocked.CompareExchange(ref _importing, 1, 0) != 0)
            throw new InvalidOperationException("Ya se está preparando un vídeo.");
        string? directory = null;
        bool completed = false;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                CleanUnused();
            }
            var activity = MainActivity.Foreground ?? throw new InvalidOperationException("Abre QuicNet para elegir un vídeo.");
            var uri = await activity.PickWatchVideoAsync(shutdown).ConfigureAwait(false);
            if (uri == null) return null;
            shutdown.ThrowIfCancellationRequested();
            var resolver = context.ContentResolver!;
            string name = "video.mp4";
            long size = -1;
            using (var cursor = resolver.Query(uri, new[] { IOpenableColumns.DisplayName, IOpenableColumns.Size }, null, null, null))
            {
                if (cursor?.MoveToFirst() == true)
                {
                    int nameIndex = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                    int sizeIndex = cursor.GetColumnIndex(IOpenableColumns.Size);
                    if (nameIndex >= 0) name = cursor.GetString(nameIndex) ?? name;
                    if (sizeIndex >= 0 && !cursor.IsNull(sizeIndex)) size = cursor.GetLong(sizeIndex);
                }
            }
            name = Path.GetFileName(name.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..") name = "video.mp4";
            if (size == 0 || size > MediaLibrary.MaximumMediaBytes)
                throw new IOException("El vídeo está vacío o supera el límite de 64 GiB.");
            Directory.CreateDirectory(_root);
            using var space = new Android.OS.StatFs(_root);
            const long reserve = 64L * 1024 * 1024;
            long limit = Math.Min(MediaLibrary.MaximumMediaBytes, Math.Max(0, space.AvailableBytes - reserve));
            if (size > limit || limit == 0) throw new IOException("No hay espacio libre suficiente para preparar el vídeo en este dispositivo.");
            directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            using var input = resolver.OpenInputStream(uri) ?? throw new IOException("No se puede abrir el vídeo seleccionado.");
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[128 * 1024];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, shutdown).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > limit) throw new IOException("El vídeo supera el espacio disponible para prepararlo.");
                    await output.WriteAsync(buffer.AsMemory(0, count), shutdown).ConfigureAwait(false);
                }
                if (total == 0 || (size >= 0 && total != size)) throw new IOException("No se ha podido leer el vídeo completo. Vuelve a seleccionarlo.");
            }
            shutdown.ThrowIfCancellationRequested();
            completed = true;
            return path;
        }
        finally
        {
            if (!completed && directory != null) TryDelete(directory);
            Interlocked.Exchange(ref _importing, 0);
            lock (_gate) { if (_disposed) CleanUnused(); }
        }
    }

    public void MediaSelected(string path)
    {
        lock (_gate) { _committedPath = Path.GetFullPath(path); CleanUnused(); }
    }

    private void CleanUnused()
    {
        if (!Directory.Exists(_root)) return;
        foreach (string directory in Directory.EnumerateDirectories(_root))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) &&
                !string.Equals(directory, Path.GetDirectoryName(_committedPath), StringComparison.Ordinal))
                TryDelete(directory);
    }

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _committedPath = null; if (!IsImporting) CleanUnused(); }
    }
}
