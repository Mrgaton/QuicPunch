#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

public sealed class PeerStore : IDisposable
{
    public sealed record SavedPeer(IPEndPoint EndPoint, byte[] CertHash)
    {
        internal string Key => $"{EndPoint.Address}|{EndPoint.Port}";

        internal SavedPeer Copy() =>
            new(new IPEndPoint(EndPoint.Address, EndPoint.Port), CertHash.ToArray());

        internal bool SameCertificate(SavedPeer other) =>
            CertHash.AsSpan().SequenceEqual(other.CertHash);
    }

    private sealed class Db
    {
        public int Version { get; set; } = 1;
        public List<Row> SavedPeers { get; set; } = [];
    }

    private sealed class Row
    {
        public string Ip { get; set; } = "";
        public int Port { get; set; }
        public string CertHash { get; set; } = "";
    }

    private readonly string _path;
    private readonly string _lockPath;
    private readonly string _dir;
    private readonly string _name;
    private readonly object _sync = new();
    private readonly Dictionary<string, SavedPeer> _peers = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private readonly TimeSpan _lockTimeout = TimeSpan.FromSeconds(10);

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private (bool Exists, long Length, long Ticks) _last;
    private bool _disposed;

    public event Action<SavedPeer, bool>? PeerAdded;
    public event Action<SavedPeer, bool>? PeerModified;
    public event Action<SavedPeer, bool>? PeerRemoved;
    public event Action<IReadOnlyList<SavedPeer>, bool>? PeersLoaded;
    public event Action<Exception>? Error;

    public PeerStore(string filePath, bool autoLoad = true, bool watch = true)
    {
        _path = Path.GetFullPath(filePath);
        _lockPath = _path + ".lock";
        _dir = Path.GetDirectoryName(_path) ?? ".";
        _name = Path.GetFileName(_path);

        Directory.CreateDirectory(_dir);

        if (autoLoad) Load();
        else _last = Fingerprint();

        if (watch) StartWatcher();
    }

    public IReadOnlyList<SavedPeer> GetAll()
    {
        lock (_sync)
            return _peers.Values.Select(p => p.Copy()).ToArray();
    }

    public bool TryGet(IPEndPoint endPoint, out SavedPeer? peer)
    {
        lock (_sync)
        {
            if (_peers.TryGetValue(Key(endPoint), out var p))
            {
                peer = p.Copy();
                return true;
            }

            peer = null;
            return false;
        }
    }

    public bool AddOrUpdate(IPEndPoint endPoint, byte[] certificate, bool save = true)
    {
        ThrowIfDisposed();

        var peer = new SavedPeer(
            new IPEndPoint(endPoint.Address, endPoint.Port),
            certificate.ToArray());

        bool added;
        bool modified;

        lock (_sync)
        {
            if (!_peers.TryGetValue(peer.Key, out var old))
            {
                _peers[peer.Key] = peer;
                added = true;
                modified = false;
            }
            else if (!old.SameCertificate(peer))
            {
                _peers[peer.Key] = peer;
                added = false;
                modified = true;
            }
            else return false;
        }

        if (added) Safe(() => PeerAdded?.Invoke(peer.Copy(), false));
        if (modified) Safe(() => PeerModified?.Invoke(peer.Copy(), false));
        if (save) Save();

        return true;
    }

    public bool Remove(IPEndPoint endPoint, bool save = true)
    {
        ThrowIfDisposed();

        SavedPeer? removed;

        lock (_sync)
            _peers.Remove(Key(endPoint), out removed);

        if (removed is null)
            return false;

        Safe(() => PeerRemoved?.Invoke(removed.Copy(), false));

        if (save)
            SaveOverwrite();

        return true;
    }

    public void Load() => Reload(false, true);

    public void Save()
    {
        ThrowIfDisposed();

        try
        {
            Dictionary<string, SavedPeer> merged;

            using (FileLock())
            {
                merged = ReadUnlocked();

                lock (_sync)
                    foreach (var p in _peers.Values)
                        merged[p.Key] = p.Copy();

                WriteUnlocked(merged);
            }

            Apply(merged, false);
            _last = Fingerprint();
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
            throw;
        }
    }

    public void SaveOverwrite()
    {
        ThrowIfDisposed();

        try
        {
            Dictionary<string, SavedPeer> snapshot;

            lock (_sync)
                snapshot = _peers.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.Ordinal);

            using (FileLock())
                WriteUnlocked(snapshot);

            _last = Fingerprint();
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
            throw;
        }
    }

    private void Reload(bool external, bool rethrow)
    {
        try
        {
            Dictionary<string, SavedPeer> data;

            using (FileLock())
                data = ReadUnlocked();

            Apply(data, external);
            _last = Fingerprint();
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
            if (rethrow) throw;
        }
    }

    private void Apply(Dictionary<string, SavedPeer> next, bool external)
    {
        List<SavedPeer> added = [];
        List<SavedPeer> modified = [];
        List<SavedPeer> removed = [];

        lock (_sync)
        {
            foreach (var (k, p) in next)
            {
                if (!_peers.TryGetValue(k, out var old)) added.Add(p.Copy());
                else if (!old.SameCertificate(p)) modified.Add(p.Copy());
            }

            foreach (var (k, p) in _peers)
                if (!next.ContainsKey(k))
                    removed.Add(p.Copy());

            _peers.Clear();

            foreach (var (k, p) in next)
                _peers[k] = p.Copy();
        }

        foreach (var p in added) Safe(() => PeerAdded?.Invoke(p.Copy(), external));
        foreach (var p in modified) Safe(() => PeerModified?.Invoke(p.Copy(), external));
        foreach (var p in removed) Safe(() => PeerRemoved?.Invoke(p.Copy(), external));

        Safe(() => PeersLoaded?.Invoke(GetAll(), external));
    }

    private Dictionary<string, SavedPeer> ReadUnlocked()
    {
        if (!File.Exists(_path))
            return new(StringComparer.Ordinal);

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var db = JsonSerializer.Deserialize<Db>(fs, _json) ?? new Db();

        if (db.Version != 1)
            throw new InvalidDataException($"Versión no soportada: {db.Version}");

        var result = new Dictionary<string, SavedPeer>(StringComparer.Ordinal);

        foreach (var r in db.SavedPeers)
        {
            var ep = new IPEndPoint(IPAddress.Parse(r.Ip), r.Port);
            var p = new SavedPeer(ep, Convert.FromBase64String(r.CertHash));
            result[p.Key] = p;
        }

        return result;
    }

    private void WriteUnlocked(Dictionary<string, SavedPeer> peers)
    {
        var tmp = Path.Combine(_dir, $".{_name}.{Guid.NewGuid():N}.tmp");

        var db = new Db
        {
            SavedPeers = peers.Values
                .OrderBy(p => p.EndPoint.Address.ToString(), StringComparer.Ordinal)
                .ThenBy(p => p.EndPoint.Port)
                .Select(p => new Row
                {
                    Ip = p.EndPoint.Address.ToString(),
                    Port = p.EndPoint.Port,
                    CertHash = Convert.ToBase64String(p.CertHash)
                })
                .ToList()
        };

        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(fs, db, _json);
                fs.Flush(true);
            }

            if (File.Exists(_path))
                File.Replace(tmp, _path, null);
            else
                File.Move(tmp, _path);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    private FileStream FileLock()
    {
        var until = DateTime.UtcNow + _lockTimeout;
        Exception? last = null;

        while (DateTime.UtcNow < until)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(50);
            }
        }

        throw new TimeoutException($"No se pudo bloquear el almacén de peers: {_path}", last);
    }

    private void StartWatcher()
    {
        _debounce = new Timer(_ =>
        {
            if (!_disposed && Fingerprint() != _last)
                Reload(true, false);
        });

        _watcher = new FileSystemWatcher(_dir, _name)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        FileSystemEventHandler changed = (_, _) => _debounce.Change(300, Timeout.Infinite);
        RenamedEventHandler renamed = (_, _) => _debounce.Change(300, Timeout.Infinite);

        _watcher.Created += changed;
        _watcher.Changed += changed;
        _watcher.Deleted += changed;
        _watcher.Renamed += renamed;
        _watcher.Error += (_, e) => Error?.Invoke(e.GetException());
        _watcher.EnableRaisingEvents = true;
    }

    private (bool Exists, long Length, long Ticks) Fingerprint()
    {
        var f = new FileInfo(_path);
        return f.Exists ? (true, f.Length, f.LastWriteTimeUtc.Ticks) : default;
    }

    private static string Key(IPEndPoint ep) => $"{ep.Address}|{ep.Port}";

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { Error?.Invoke(ex); }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}