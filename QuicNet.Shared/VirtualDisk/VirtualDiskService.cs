using System.Security.Cryptography;
using System.Text.Json;

namespace QuicNet.VirtualDisk;

/// <summary>Host-side authority. A null actor is the local UI; remote actors are authenticated certificate hashes.</summary>
public sealed class VirtualDiskService : IDisposable
{
    public const int ChunkBytes = 128 * 1024;
    public const long MaxFileBytes = 10L * 1024 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Func<string, bool> _trusted;
    private readonly Func<string, bool> _administrator;
    private readonly string _store;
    private readonly string _journal;
    private readonly HashSet<string> _pendingCleanup = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, StoredDiskShare> _shares = new();
    private readonly Dictionary<Guid, Transfer> _transfers = new();
    private readonly Dictionary<Guid, Lease> _leases = new();
    private readonly Queue<DiskAudit> _audit = new();
    private readonly Timer _cleanup;
    private bool _disposed;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class Transfer(string? actor, Guid share, string path, FileStream stream, bool writing,
        string? version, long length, string? temporary = null, Guid leaseId = default)
    {
        public readonly string? Actor = actor;
        public readonly Guid Share = share;
        public readonly string Path = path;
        public readonly FileStream Stream = stream;
        public readonly bool Writing = writing;
        public readonly string? Version = version;
        public readonly long Length = length;
        public readonly string? Temporary = temporary;
        public readonly Guid LeaseId = leaseId;
        public DateTime Touched = DateTime.UtcNow;
        public readonly IncrementalHash ReceivedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }
    private sealed record Lease(string? Actor, Guid Share, string Path, DateTime ExpiresUtc, string? Version);

    public VirtualDiskService(string storageDirectory, Func<string, bool> trusted, Func<string, bool> administrator)
    {
        _trusted = trusted;
        _administrator = administrator;
        Directory.CreateDirectory(storageDirectory);
        _store = System.IO.Path.Combine(storageDirectory, "virtual-disks.json");
        _journal = System.IO.Path.Combine(storageDirectory, "virtual-disk-uploads.json");
        if (File.Exists(_store))
        {
            // Corrupt ACLs fail closed; never silently replace them with broader grants.
            foreach (var share in JsonSerializer.Deserialize(File.ReadAllBytes(_store), DiskJsonContext.Default.StoredDiskShareArray) ?? [])
                _shares.Add(share.Id, share);
        }
        if (File.Exists(_journal))
            foreach (string temp in JsonSerializer.Deserialize(File.ReadAllBytes(_journal), DiskJsonContext.Default.StringArray) ?? []) _pendingCleanup.Add(temp);
        CleanupAbandoned();
        _cleanup = new(_ => { lock (_gate) { if (!_disposed) { ExpireTransfers(); CleanupAbandoned(); } } }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public DiskReply Execute(string? actor, DiskRequest request)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ExpireTransfers();
            try
            {
                if (request.Path == null || request.Operation == null || request.Recipient == null) throw new ArgumentException("Solicitud inválida.");
                if (actor != null && !_trusted(actor)) throw new UnauthorizedAccessException("Contacto sin autorización.");
                var auditRequest = RequestForAudit(request);
                var reply = ExecuteCore(actor, request);
                if (request.Operation is not ("read" or "write" or "list" or "shares" or "audit")) Audit(actor, auditRequest, true);
                return reply;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or CryptographicException)
            {
                Audit(actor, request, false);
                // Do not leak absolute paths or OS diagnostics to a remote caller.
                string code = ex is UnauthorizedAccessException ? "Denied" : ex is DiskConflictException ? "Conflict" : ex is DiskLockedException ? "Locked" : "Failed";
                return new(false, actor == null || ex is DiskConflictException or DiskLockedException ? ex.Message : code == "Denied"
                    ? "Acceso no autorizado o permiso revocado." : "Operación no disponible; revisa la ruta, los permisos o si el archivo está en uso.", code);
            }
        }
    }

    private DiskReply ExecuteCore(string? actor, DiskRequest r)
    {
        bool admin = actor == null || _administrator(actor);
        switch (r.Operation)
        {
            case "shares":
                return new(true, CanExplore: admin, Shares: _shares.Values.Where(s => actor == null || s.Recipient == actor)
                    .Where(Active).Select(s => View(s, actor == null)).ToArray());
            case "audit":
                if (actor != null) throw new UnauthorizedAccessException();
                return new(true, Audit: _audit.ToArray());
            case "explore":
                if (!admin) throw new UnauthorizedAccessException();
                if (string.IsNullOrEmpty(r.Path))
                    return new(true, CanExplore: true, Entries: DriveInfo.GetDrives()
                        .Select(d => new DiskEntry(d.Name, d.RootDirectory.FullName, true)).ToArray());
                return List(CheckedAbsolute(r.Path), true, r.Skip);
            case "grant":
            {
                if (!admin) throw new UnauthorizedAccessException();
                string recipient = actor ?? r.Recipient;
                if (!_trusted(recipient)) throw new UnauthorizedAccessException();
                string root = CheckedAbsolute(r.Path);
                if (!Directory.Exists(root)) throw new IOException("Selecciona una carpeta o un disco.");
                if (r.ExpiresUtc is DateTime expiry && expiry.ToUniversalTime() <= DateTime.UtcNow) throw new ArgumentException("La autorización ya ha caducado.");
                if (_shares.Count >= 256) throw new IOException("Límite de carpetas compartidas alcanzado.");
                var share = new StoredDiskShare(Guid.NewGuid(), root, recipient, r.Writable, r.Writable && r.AllowDelete,
                    r.ExpiresUtc?.ToUniversalTime(), actor != null);
                _shares.Add(share.Id, share);
                try { Save(); } catch { _shares.Remove(share.Id); throw; }
                return new(true, Shares: [View(share, actor == null)]);
            }
            case "revoke":
            {
                if (actor != null && (!admin || !_shares.TryGetValue(r.ShareId, out var owned) || owned.Recipient != actor)) throw new UnauthorizedAccessException();
                if (_shares.Remove(r.ShareId, out var old))
                {
                    try { Save(); } catch { _shares.Add(old.Id, old); throw; }
                    foreach (var id in _transfers.Where(t => t.Value.Share == r.ShareId).Select(t => t.Key).ToArray()) Close(id);
                }
                return new(true);
            }
            case "list":
                return List(Resolve(Authorize(actor, r.ShareId), r.Path), false, r.Skip, r.Path);
            case "open-read":
            {
                EnsureTransferCapacity(actor);
                var share = Authorize(actor, r.ShareId);
                string path = Resolve(share, r.Path);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                try
                {
                    string version = Convert.ToHexString(SHA256.HashData(stream)); stream.Position = 0;
                    if (r.Version != null && r.Version != version) throw new DiskConflictException();
                    var id = Guid.NewGuid();
                    _transfers.Add(id, new(actor, r.ShareId, r.Path, stream, false, version, stream.Length));
                    return new(true, TransferId: id, Version: version, Length: stream.Length);
                }
                catch { stream.Dispose(); throw; }
            }
            case "read":
            {
                var transfer = GetTransfer(actor, r.TransferId, false);
                if (r.Offset < 0 || r.Offset > transfer.Length) throw new ArgumentException("Offset inválido.");
                transfer.Stream.Position = r.Offset;
                byte[] buffer = new byte[(int)Math.Min(ChunkBytes, transfer.Length - r.Offset)];
                transfer.Stream.ReadExactly(buffer);
                return new(true, Data: buffer, Version: transfer.Version, Length: transfer.Length);
            }
            case "begin-write":
            {
                EnsureTransferCapacity(actor);
                if (r.Length < 0 || r.Length > MaxFileBytes) throw new ArgumentException("Tamaño de archivo no admitido.");
                if (_transfers.Values.Where(t => t.Writing).Sum(t => t.Length) + r.Length > 2 * MaxFileBytes)
                    throw new IOException("El límite de almacenamiento reservado para transferencias está ocupado.");
                var share = Authorize(actor, r.ShareId, write: true);
                string path = Resolve(share, r.Path, allowMissingLeaf: true);
                if (path.Equals(share.Root, PathComparison)) throw new UnauthorizedAccessException();
                var lease = CheckLease(actor, path, r.LeaseId);
                string? expected = r.Version ?? lease?.Version;
                CheckVersion(path, expected);
                // A unique sibling permits atomic publication without exposing partially uploaded files.
                string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ".quicnet-upload-" + Guid.NewGuid().ToString("N"));
                var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                var id = Guid.NewGuid();
                _transfers.Add(id, new(actor, r.ShareId, r.Path, stream, true, expected, r.Length, temp, r.LeaseId));
                try { SaveJournal(); }
                catch { Close(id); throw; }
                return new(true, TransferId: id);
            }
            case "write":
            {
                var transfer = GetTransfer(actor, r.TransferId, true);
                if (r.Data == null || r.Data.Length > ChunkBytes || r.Offset != transfer.Stream.Length || r.Data.Length > transfer.Length - r.Offset)
                    throw new ArgumentException("Bloque de escritura inválido.");
                transfer.Stream.Write(r.Data);
                transfer.ReceivedHash.AppendData(r.Data);
                return new(true, Length: transfer.Stream.Length);
            }
            case "commit":
            {
                var transfer = GetTransfer(actor, r.TransferId, true);
                try
                {
                    if (transfer.Stream.Length != transfer.Length) throw new IOException("Transferencia incompleta.");
                    transfer.Stream.Flush(true); transfer.Stream.Position = 0;
                    string hash = Convert.ToHexString(SHA256.HashData(transfer.Stream));
                    string received = Convert.ToHexString(transfer.ReceivedHash.GetHashAndReset());
                    if (hash != received || (r.Sha256 != null && !hash.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("La comprobación de integridad ha fallado.");
                    var share = Authorize(actor, transfer.Share, write: true);
                    string target = Resolve(share, transfer.Path, allowMissingLeaf: true);
                    CheckLease(actor, target, transfer.LeaseId);
                    CheckVersion(target, transfer.Version);
                    transfer.Stream.Dispose();
                    // No overwrite on a create: even a local concurrent creator wins safely.
                    if (transfer.Version == null) File.Move(transfer.Temporary!, target);
                    else File.Replace(transfer.Temporary!, target, destinationBackupFileName: null);
                    if (transfer.LeaseId != Guid.Empty && _leases.TryGetValue(transfer.LeaseId, out var held))
                        _leases[transfer.LeaseId] = held with { Version = hash };
                    return new(true, Version: hash, Length: transfer.Length);
                }
                finally { Close(r.TransferId); }
            }
            case "close":
                if (_transfers.TryGetValue(r.TransferId, out var closing) && closing.Actor != actor) throw new UnauthorizedAccessException();
                Close(r.TransferId); return new(true);
            case "mkdir":
            {
                var share = Authorize(actor, r.ShareId, write: true);
                string target = Resolve(share, r.Path, allowMissingLeaf: true);
                CheckLease(actor, target, r.LeaseId);
                if (File.Exists(target) || Directory.Exists(target)) throw new DiskConflictException();
                Directory.CreateDirectory(target); return new(true);
            }
            case "delete":
            {
                var share = Authorize(actor, r.ShareId, write: true);
                if (!share.AllowDelete) throw new UnauthorizedAccessException();
                string path = Resolve(share, r.Path);
                var lease = CheckLease(actor, path, r.LeaseId);
                if (path.Equals(share.Root, PathComparison)) throw new UnauthorizedAccessException();
                if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
                else { CheckVersion(path, r.Version ?? lease?.Version); File.Delete(path); }
                return new(true);
            }
            case "lock":
            {
                var share = Authorize(actor, r.ShareId, write: true);
                string path = Resolve(share, r.Path, allowMissingLeaf: true);
                if (Directory.Exists(path)) throw new IOException("Solo se admiten bloqueos de archivos.");
                var lease = CheckLease(actor, path, r.LeaseId);
                Guid id = r.LeaseId == Guid.Empty ? Guid.NewGuid() : r.LeaseId;
                if (r.LeaseId == Guid.Empty && _leases.Count >= 128) throw new IOException("Demasiados archivos bloqueados.");
                if (r.LeaseId == Guid.Empty && _leases.Count(l => l.Value.Actor == actor) >= 16) throw new IOException("Demasiados bloqueos para este contacto.");
                string? version = lease?.Version;
                if (r.LeaseId == Guid.Empty && File.Exists(path))
                {
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    version = Convert.ToHexString(SHA256.HashData(source));
                }
                _leases[id] = new(actor, r.ShareId, path, DateTime.UtcNow.AddSeconds(120), version);
                return new(true, LeaseId: id, Version: version);
            }
            case "unlock":
                if (!_leases.TryGetValue(r.LeaseId, out var release) || release.Actor != actor || release.Share != r.ShareId ||
                    !release.Path.Equals(Resolve(Authorize(actor, r.ShareId, write: true), r.Path, allowMissingLeaf: true), PathComparison)) throw new UnauthorizedAccessException();
                _leases.Remove(r.LeaseId); return new(true);
            default: throw new ArgumentException("Operación desconocida.");
        }
    }

    private bool Active(StoredDiskShare share) => (share.ExpiresUtc == null || share.ExpiresUtc > DateTime.UtcNow) &&
        _trusted(share.Recipient) && (!share.AdministratorGrant || _administrator(share.Recipient));

    private StoredDiskShare Authorize(string? actor, Guid id, bool write = false)
    {
        if (!_shares.TryGetValue(id, out var share) || !Active(share) || (actor != null && share.Recipient != actor) || (write && !share.Writable))
            throw new UnauthorizedAccessException();
        return share;
    }

    private Transfer GetTransfer(string? actor, Guid id, bool writing)
    {
        if (!_transfers.TryGetValue(id, out var transfer) || transfer.Actor != actor || transfer.Writing != writing) throw new UnauthorizedAccessException();
        try { Resolve(Authorize(actor, transfer.Share, write: writing), transfer.Path, allowMissingLeaf: writing); }
        catch { Close(id); throw; }
        transfer.Touched = DateTime.UtcNow;
        return transfer;
    }

    private static string Resolve(StoredDiskShare share, string relative, bool allowMissingLeaf = false)
    {
        if (System.IO.Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Length > 4096) throw new UnauthorizedAccessException();
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.StartsWith(".quicnet-", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException();
        string root = CheckedAbsolute(share.Root);
        string combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, System.IO.Path.Combine(new[] { "" }.Concat(parts).ToArray())));
        if (!combined.Equals(root, PathComparison) && !combined.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar, PathComparison))
            throw new UnauthorizedAccessException();
        return CheckedAbsolute(combined, allowMissingLeaf);
    }

    private static string CheckedAbsolute(string input, bool allowMissingLeaf = false)
    {
        if (!System.IO.Path.IsPathFullyQualified(input) || input.Length > 4096 || input.IndexOf('\0') >= 0) throw new UnauthorizedAccessException();
        string full = System.IO.Path.GetFullPath(input);
        if (OperatingSystem.IsWindows() && (full.StartsWith("\\\\") || full.AsSpan(2).Contains(':'))) throw new UnauthorizedAccessException();
        string root = System.IO.Path.GetPathRoot(full)!;
        string current = root;
        var parts = full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int i = -1; i < parts.Length; i++)
        {
            if (i >= 0)
            {
                string name = parts[i];
                if (name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(".quicnet-", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
                current = System.IO.Path.Combine(current, name);
            }
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Los enlaces y puntos de montaje indirectos no están permitidos.");
            }
            catch (FileNotFoundException) when (allowMissingLeaf && i == parts.Length - 1) { }
        }
        return System.IO.Path.TrimEndingDirectorySeparator(full);
    }

    private static DiskReply List(string absolute, bool exploration, int skip, string relative = "")
    {
        if (skip < 0 || skip > 1_000_000) throw new ArgumentException("Página inválida.");
        var entries = new List<DiskEntry>();
        int jsonBudget = 0;
        int count = 0;
        foreach (var item in new DirectoryInfo(absolute).EnumerateFileSystemInfos())
        {
            if (item.Name.StartsWith(".quicnet-", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (count++ < skip) continue;
                if (entries.Count == 200) return new(true, CanExplore: exploration, Entries: entries.ToArray(), More: true);
                bool directory = (item.Attributes & FileAttributes.Directory) != 0;
                string path = exploration ? item.FullName : string.IsNullOrEmpty(relative) ? item.Name : relative.TrimEnd('/') + "/" + item.Name;
                int cost = 200 + 6 * (item.Name.Length + path.Length);
                if (entries.Count > 0 && jsonBudget + cost > 240 * 1024) return new(true, CanExplore: exploration, Entries: entries.ToArray(), More: true);
                jsonBudget += cost;
                entries.Add(new(item.Name, path, directory, directory ? 0 : ((FileInfo)item).Length, item.LastWriteTimeUtc));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new(true, CanExplore: exploration, Entries: entries.ToArray());
    }

    private static void CheckVersion(string path, string? expected)
    {
        if (!File.Exists(path)) { if (expected != null) throw new DiskConflictException(); return; }
        if (expected == null) throw new DiskConflictException();
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!Convert.ToHexString(SHA256.HashData(source)).Equals(expected, StringComparison.Ordinal)) throw new DiskConflictException();
    }

    private void EnsureTransferCapacity(string? actor)
    {
        if (_transfers.Count >= 32 || _transfers.Count(t => t.Value.Actor == actor) >= 4) throw new IOException("Demasiadas transferencias abiertas.");
    }
    private void ExpireTransfers()
    {
        foreach (var id in _leases.Where(l => l.Value.ExpiresUtc <= DateTime.UtcNow || !_shares.TryGetValue(l.Value.Share, out var share) || !Active(share)).Select(l => l.Key).ToArray()) _leases.Remove(id);
        foreach (var id in _transfers.Where(t => t.Value.Touched < DateTime.UtcNow.AddMinutes(-2) ||
            !_shares.TryGetValue(t.Value.Share, out var s) || !Active(s)).Select(t => t.Key).ToArray()) Close(id);
    }
    private Lease? CheckLease(string? actor, string path, Guid id)
    {
        var held = _leases.FirstOrDefault(l => l.Value.Path.Equals(path, PathComparison));
        if (held.Value == null) { if (id != Guid.Empty) throw new DiskLockedException(); return null; }
        if (held.Key != id || held.Value.Actor != actor) throw new DiskLockedException();
        return held.Value;
    }
    private void Close(Guid id)
    {
        if (!_transfers.Remove(id, out var transfer)) return;
        transfer.Stream.Dispose();
        transfer.ReceivedHash.Dispose();
        if (transfer.Temporary != null)
        {
            _pendingCleanup.Add(transfer.Temporary);
            CleanupAbandoned();
        }
    }
    private DiskRequest RequestForAudit(DiskRequest r) => _transfers.TryGetValue(r.TransferId, out var t) ? r with { ShareId = t.Share, Path = t.Path } : r;
    private void CleanupAbandoned()
    {
        foreach (string path in _pendingCleanup.ToArray())
        {
            string name = System.IO.Path.GetFileName(path);
            if (!name.StartsWith(".quicnet-upload-", StringComparison.Ordinal) || name.Length != ".quicnet-upload-".Length + 32 ||
                !name[".quicnet-upload-".Length..].All(Uri.IsHexDigit)) { _pendingCleanup.Remove(path); continue; }
            try
            {
                // Journal entries only authorize deleting our exact temporary filenames in a non-link parent.
                CheckedAbsolute(System.IO.Path.GetDirectoryName(path)!);
                File.Delete(path); _pendingCleanup.Remove(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { SaveJournal(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private void SaveJournal()
    {
        string temp = _journal + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, _transfers.Values.Where(t => t.Temporary != null).Select(t => t.Temporary!).Concat(_pendingCleanup).Distinct().ToArray(), DiskJsonContext.Default.StringArray);
                file.Flush(true);
            }
            File.Move(temp, _journal, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static DiskShare View(StoredDiskShare s, bool owner) => new(s.Id, System.IO.Path.GetFileName(s.Root) is { Length: > 0 } name ? name : s.Root,
        s.Recipient, s.Writable, s.AllowDelete, s.ExpiresUtc, owner ? s.Root : null, s.AdministratorGrant);
    private void Save()
    {
        string temp = _store + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, _shares.Values.ToArray(), DiskJsonContext.Default.StoredDiskShareArray); file.Flush(true); }
            File.Move(temp, _store, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void Audit(string? actor, DiskRequest r, bool success)
    {
        _audit.Enqueue(new(DateTime.UtcNow, actor ?? "local", r.Operation, r.ShareId, r.Path, success));
        while (_audit.Count > 500) _audit.Dequeue();
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true; _cleanup.Dispose();
            foreach (var id in _transfers.Keys.ToArray()) Close(id);
        }
    }
}

internal sealed class DiskConflictException() : IOException("El archivo ha cambiado o ya existe. Actualiza antes de sobrescribirlo.");
internal sealed class DiskLockedException() : IOException("El archivo está bloqueado por otra operación o el bloqueo ha caducado.");
