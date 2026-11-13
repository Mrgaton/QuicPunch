using Android.Content;
using Android.Provider;
using QuicNet.VirtualDisk;
using AUri = Android.Net.Uri;

namespace QuicPunch.AndroidProbe;

/// <summary>SAF trees, never translated into guessed /storage filesystem paths.</summary>
internal sealed class AndroidDiskDocuments(Context context) : IVirtualDiskDocuments
{
    private ContentResolver Resolver => context.ContentResolver!;
    private static MainActivity Activity => MainActivity.Foreground ?? throw new IOException("Abre QuicNet para elegir una carpeta o guardar un archivo.");
    private static readonly string[] Columns = ["document_id", "_display_name", "mime_type", "_size", "last_modified"];
    private const string DirectoryMime = "vnd.android.document/directory";

    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (Java.Lang.SecurityException ex) { throw new UnauthorizedAccessException("Android ha retirado el acceso. Vuelve a seleccionar la carpeta.", ex); }
        catch (Java.Lang.Exception ex) { throw new IOException("El proveedor de Android no pudo completar la operación.", ex); }
    }

    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken)
    {
        var result = await Activity.PickDiskDocumentAsync(true, null, cancellationToken);
        if (result?.Data is not { } uri) return null;
        return Guard(() =>
        {
            var flags = result.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            Resolver.TakePersistableUriPermission(uri, flags);
            return ValidateRoot(uri.ToString()!, false);
        });
    }

    public async Task<IVirtualDiskDownload?> CreateDownloadAsync(string name, CancellationToken cancellationToken)
    {
        var result = await Activity.PickDiskDocumentAsync(false, name, cancellationToken);
        if (result?.Data is not { } uri) return null;
        return Guard<IVirtualDiskDownload>(() =>
        {
            try { return new Download(Resolver, uri, Resolver.OpenOutputStream(uri, "wt") ?? throw new IOException("No se puede escribir en el destino.")); }
            catch { try { DocumentsContract.DeleteDocument(Resolver, uri); } catch { } throw; }
        });
    }

    private sealed class Download(ContentResolver resolver, AUri uri, Stream stream) : IVirtualDiskDownload
    {
        public Stream Stream { get; } = stream;
        private bool _complete;
        public void Complete() { Stream.Flush(); Stream.Dispose(); _complete = true; }
        public void Dispose()
        {
            try { Stream.Dispose(); }
            finally { if (!_complete) try { DocumentsContract.DeleteDocument(resolver, uri); } catch { } }
        }
    }

    public string ValidateRoot(string root, bool writable) => Guard(() =>
    {
        if (!root.StartsWith("content://", StringComparison.Ordinal) || root.Contains('#')) throw new UnauthorizedAccessException();
        var permission = Resolver.PersistedUriPermissions?.FirstOrDefault(p => p.Uri?.ToString() == root);
        if (permission == null || !permission.IsReadPermission || (writable && !permission.IsWritePermission)) throw new UnauthorizedAccessException("Selecciona de nuevo la carpeta y permite el acceso solicitado.");
        var uri = AUri.Parse(root)!;
        if (!DocumentsContract.IsTreeUri(uri)) throw new UnauthorizedAccessException();
        return root;
    });

    public string GetName(string root)
    {
        try { return Stat(root, "")?.Name ?? "Carpeta Android"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Carpeta Android (sin acceso)"; }
    }
    public DiskEntry[] GetRoots() => Guard(() => (Resolver.PersistedUriPermissions ?? [])
        .Where(p => p.IsReadPermission && p.Uri != null && DocumentsContract.IsTreeUri(p.Uri))
        .Select(p => new DiskEntry(GetName(p.Uri!.ToString()!), p.Uri!.ToString()!, true)).ToArray());

    private sealed record Document(AUri Uri, string Name, bool Directory, long Length, DateTime? Modified);
    private Document? Read(AUri uri) => Guard(() =>
    {
        using var cursor = Resolver.Query(uri, Columns, null, null, null);
        return cursor?.MoveToFirst() == true ? FromCursor(cursor, uri) : null;
    });
    private static Document FromCursor(Android.Database.ICursor c, AUri uri) => new(uri,
        c.GetString(1) ?? "archivo", c.GetString(2) == DirectoryMime, c.IsNull(3) ? 0 : c.GetLong(3),
        c.IsNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(c.GetLong(4)).UtcDateTime);
    private IEnumerable<Document> Children(string root, Document parent)
    {
        if (!parent.Directory) throw new IOException("La ubicación no es una carpeta.");
        var tree = AUri.Parse(root)!;
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, DocumentsContract.GetDocumentId(parent.Uri))!;
        using var cursor = Guard(() => Resolver.Query(children, Columns, null, null, null)) ?? throw new IOException("No se puede listar esta carpeta.");
        while (Guard(() => cursor.MoveToNext()))
        {
            var uri = DocumentsContract.BuildDocumentUriUsingTree(tree, cursor.GetString(0))!;
            yield return FromCursor(cursor, uri);
        }
    }
    private static bool SafeName(string name) => name.Length > 0 && name is not ("." or "..") &&
        name.IndexOfAny(['/', '\\', ':', '\0']) < 0 && !name.EndsWith('.') && !name.EndsWith(' ') && !name.StartsWith(".quicnet-", StringComparison.OrdinalIgnoreCase);
    private Document? Resolve(string root, string path)
    {
        ValidateRoot(root, false);
        var tree = AUri.Parse(root)!;
        var current = Read(DocumentsContract.BuildDocumentUriUsingTree(tree, DocumentsContract.GetTreeDocumentId(tree))!) ?? throw new IOException("La carpeta ya no existe.");
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SafeName(segment)) throw new UnauthorizedAccessException();
            // Duplicate display names are ambiguous; never choose an arbitrary document.
            var matches = Children(root, current).Where(c => c.Name == segment).Take(2).ToArray();
            if (matches.Length > 1) throw new IOException("Hay nombres duplicados en el proveedor de documentos.");
            if (matches.Length == 0) return null;
            current = matches[0];
        }
        return current;
    }
    public DiskEntry? Stat(string root, string path)
    {
        var item = Resolve(root, path);
        return item == null ? null : new(item.Name, path, item.Directory, item.Length, item.Modified);
    }
    public DiskReply List(string root, string path, int skip)
    {
        if (skip < 0 || skip > 1_000_000) throw new ArgumentException("Página inválida.");
        var folder = Resolve(root, path) ?? throw new DirectoryNotFoundException();
        var entries = new List<DiskEntry>(); int budget = 0, count = 0;
        foreach (var item in Children(root, folder).Where(c => SafeName(c.Name)))
        {
            if (count++ < skip) continue;
            string relative = string.IsNullOrEmpty(path) ? item.Name : path.TrimEnd('/') + "/" + item.Name;
            int cost = 200 + 6 * (item.Name.Length + relative.Length);
            if (entries.Count > 0 && (entries.Count == 200 || budget + cost > 240 * 1024)) return new(true, Entries: entries.ToArray(), More: true);
            budget += cost;
            entries.Add(new(item.Name, relative, item.Directory, item.Length, item.Modified));
        }
        return new(true, Entries: entries.ToArray());
    }
    public Stream OpenRead(string root, string path) => Guard<Stream>(() =>
    {
        var item = Resolve(root, path) ?? throw new FileNotFoundException();
        if (item.Directory) throw new IOException("Selecciona un archivo.");
        if (item.Length > VirtualDiskService.MaxFileBytes) throw new IOException("Archivo demasiado grande.");
        using var input = Resolver.OpenInputStream(item.Uri) ?? throw new IOException("No se puede leer el archivo.");
        string temp = Path.Combine(context.CacheDir!.AbsolutePath, "disk-read-" + Guid.NewGuid().ToString("N"));
        var snapshot = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.DeleteOnClose);
        try
        {
            byte[] buffer = new byte[131072]; int read;
            while ((read = input.Read(buffer)) != 0)
            {
                if (snapshot.Length + read > VirtualDiskService.MaxFileBytes) throw new IOException("Archivo demasiado grande.");
                snapshot.Write(buffer, 0, read);
            }
            snapshot.Position = 0; return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    });
    private (Document Parent, string Name) Destination(string root, string path)
    {
        ValidateRoot(root, true);
        int slash = path.LastIndexOf('/');
        string name = path[(slash + 1)..];
        if (!SafeName(name)) throw new UnauthorizedAccessException();
        var parent = Resolve(root, slash < 0 ? "" : path[..slash]) ?? throw new DirectoryNotFoundException();
        if (!parent.Directory) throw new DirectoryNotFoundException();
        if (Resolve(root, path) != null) throw new IOException("El archivo ya existe. Usa otro nombre en Android.");
        return (parent, name);
    }
    public void CreateDirectory(string root, string path) => Guard(() =>
    {
        var (parent, name) = Destination(root, path);
        return DocumentsContract.CreateDocument(Resolver, parent.Uri, DirectoryMime, name) ?? throw new IOException("No se pudo crear la carpeta.");
    });
    public void Delete(string root, string path) => Guard(() =>
    {
        ValidateRoot(root, true);
        if (string.IsNullOrEmpty(path)) throw new UnauthorizedAccessException();
        var item = Resolve(root, path) ?? throw new FileNotFoundException();
        if (item.Directory && Children(root, item).Any()) throw new IOException("Solo se pueden borrar carpetas vacías.");
        if (!DocumentsContract.DeleteDocument(Resolver, item.Uri)) throw new IOException("No se pudo borrar el documento.");
        return true;
    });
    public void Publish(string root, string path, string stagingFile, string? expectedVersion) => Guard(() =>
    {
        // SAF has no compare-and-swap replacement. Keep the existing file intact.
        if (expectedVersion != null) throw new IOException("Android no permite reemplazos seguros aquí. Envía el archivo con otro nombre.");
        var (parent, name) = Destination(root, path);
        AUri? temporary = DocumentsContract.CreateDocument(Resolver, parent.Uri, "application/octet-stream", ".quicnet-upload-" + Guid.NewGuid().ToString("N"));
        if (temporary == null) throw new IOException("No se pudo crear el archivo.");
        try
        {
            using (var output = Resolver.OpenOutputStream(temporary, "wt") ?? throw new IOException("No se puede escribir en esta carpeta."))
            using (var input = File.OpenRead(stagingFile)) { input.CopyTo(output); output.Flush(); }
            using (var written = Resolver.OpenInputStream(temporary) ?? throw new IOException("No se puede verificar el archivo."))
            using (var original = File.OpenRead(stagingFile))
                if (!System.Security.Cryptography.SHA256.HashData(written).SequenceEqual(System.Security.Cryptography.SHA256.HashData(original)))
                    throw new IOException("La comprobación de integridad del proveedor ha fallado.");
            if (Resolve(root, path) != null) throw new IOException("El destino ha aparecido durante el envío. Usa otro nombre.");
            var published = DocumentsContract.RenameDocument(Resolver, temporary, name) ?? throw new IOException("El proveedor no admite publicar el archivo. Usa la bandeja de recepción.");
            temporary = published;
            if (Read(published)?.Name != name) throw new IOException("El proveedor cambió el nombre del archivo.");
            temporary = null;
            return true;
        }
        finally { if (temporary != null) try { DocumentsContract.DeleteDocument(Resolver, temporary); } catch { } }
    });
}
