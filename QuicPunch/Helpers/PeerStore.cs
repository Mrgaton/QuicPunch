#nullable enable

using QuicPunch;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace QuicPunch.Helpers;

public sealed class PeerStore : IDisposable
{
    public sealed record SavedPeer
    {
        public IPAddress[] Addresses { get; init; }
        public ushort[] PortArray { get; init; } = [];
        public byte[] CertHash { get; init; }
        public byte[]? EcdhPublicKey { get; init; }
        public string? Name { get; init; }
        public string? OnionAddress { get; init; }
        public bool AutoConnect { get; init; } = true;
        public QuicPunch.NetworkType NetworkType { get; init; } = QuicPunch.NetworkType.Static;
        public ConnectionFlags ConnectionFlags { get; init; } = new();
        public string? NostrPubKey { get; init; }
        public byte[]? ResumptionTicket { get; init; }

        // Certificate hash is the stable peer identity. Addresses and ports can change with NAT.
        internal string Key => PeerStore.Key(CertHash);

        public SavedPeer(IPAddress[] addresses, ushort[] portArray, byte[] certHash, byte[]? ecdhPublicKey = null, string? name = null, string? onionAddress = null, bool autoConnect = true, QuicPunch.NetworkType networkType = QuicPunch.NetworkType.Static, string? nostrPubKey = null, byte[]? resumptionTicket = null, ConnectionFlags? connectionFlags = null)
        {
            Addresses = NormalizeAddresses(addresses);
            PortArray = portArray ?? [];

            if (certHash is null || certHash.Length == 0)
                throw new ArgumentException("Certificate hash cannot be empty.", nameof(certHash));

            CertHash = certHash.ToArray();
            EcdhPublicKey = ecdhPublicKey?.ToArray();
            Name = name;
            OnionAddress = onionAddress;
            AutoConnect = autoConnect;
            NetworkType = networkType;
            ConnectionFlags = connectionFlags ?? new ConnectionFlags
            {
                IsTor = networkType == QuicPunch.NetworkType.Tor,
                MultipleIps = Addresses.Length > 1,
                PortMode = PortArray.Length <= 1 ? PortMode.Single : PortMode.Multiple
            };
            NostrPubKey = nostrPubKey;
            ResumptionTicket = resumptionTicket?.ToArray();
        }

        public SavedPeer(IEnumerable<IPAddress> addresses, ushort[] portArray, byte[] certHash, byte[]? ecdhPublicKey = null, string? name = null, string? onionAddress = null, bool autoConnect = true, QuicPunch.NetworkType networkType = QuicPunch.NetworkType.Static, string? nostrPubKey = null, byte[]? resumptionTicket = null, ConnectionFlags? connectionFlags = null)
            : this(NormalizeAddresses(addresses), portArray, certHash, ecdhPublicKey, name, onionAddress, autoConnect, networkType, nostrPubKey, resumptionTicket, connectionFlags)
        {
        }

        internal SavedPeer Copy() => new(Addresses.Select(CloneAddress).ToArray(), (ushort[])PortArray.Clone(), CertHash.ToArray(), EcdhPublicKey?.ToArray(), Name, OnionAddress, AutoConnect, NetworkType, NostrPubKey, ResumptionTicket?.ToArray(), new ConnectionFlags(ConnectionFlags.RawValue));

        internal bool SameCertificate(SavedPeer other) =>
            CertHash.AsSpan().SequenceEqual(other.CertHash);

        internal bool SameValue(SavedPeer other) =>
            SameCertificate(other) &&
            PortArray.SequenceEqual(other.PortArray) &&
            Name == other.Name &&
            OnionAddress == other.OnionAddress &&
            AutoConnect == other.AutoConnect &&
            NetworkType == other.NetworkType &&
            ConnectionFlags.RawValue == other.ConnectionFlags.RawValue &&
            string.Equals(NostrPubKey, other.NostrPubKey, StringComparison.OrdinalIgnoreCase) &&
            ((ResumptionTicket == null && other.ResumptionTicket == null) || (ResumptionTicket != null && other.ResumptionTicket != null && ResumptionTicket.AsSpan().SequenceEqual(other.ResumptionTicket))) &&
            Addresses.Select(a => a.ToString()).SequenceEqual(other.Addresses.Select(a => a.ToString()), StringComparer.Ordinal);
    }

    private sealed class Db
    {
        public int Version { get; set; } = 2;
        public List<Row> SavedPeers { get; set; } = [];
    }

    private sealed class Row
    {
        public string[] Ips { get; set; } = [];
        public string CertHash { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ushort[]? Ports { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MinPort { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxPort { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OnionAddress { get; set; }

        public bool AutoConnect { get; set; } = true;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EcdhPublicKey { get; set; }

        public int NetworkType { get; set; } = 0;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public byte? ConnectionFlags { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NostrPubKey { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResumptionTicket { get; set; }

        // Legacy-read support for older files that used { Ip, Port, CertHash }.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Ip { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Port { get; set; }
    }

    private readonly string _path;
    private readonly string _lockPath;
    private readonly string _dir;
    private readonly string _name;
    private readonly object _sync = new();
    private readonly Dictionary<string, SavedPeer> _peers = new(StringComparer.Ordinal);
    private IReadOnlyList<SavedPeer> _cachedPeers = [];
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
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

    public IReadOnlyList<SavedPeer> SavedPeers
    {
        get
        {
            lock (_sync)
                return _cachedPeers;
        }
    }

    public IReadOnlyList<SavedPeer> GetAll() => SavedPeers;

    public bool TryGet(byte[] certificate, out SavedPeer? peer)
    {
        if (certificate is null || certificate.Length == 0)
        {
            peer = null;
            return false;
        }

        lock (_sync)
        {
            if (_peers.TryGetValue(Key(certificate), out var p))
            {
                peer = p.Copy();
                return true;
            }

            peer = null;
            return false;
        }
    }

    public bool TryGetResumptionTicket(byte[] certificate, out byte[]? ticket)
    {
        if (TryGet(certificate, out var peer) && peer?.ResumptionTicket != null)
        {
            ticket = peer.ResumptionTicket;
            return true;
        }
        ticket = null;
        return false;
    }

    public bool SetResumptionTicket(byte[] certificate, byte[] ticket, bool save = false)
    {
        ThrowIfDisposed();
        if (certificate == null || certificate.Length == 0 || ticket == null) return false;
        lock (_sync)
        {
            if (!_peers.TryGetValue(Key(certificate), out var old)) return false;
            var updated = new SavedPeer(old.Addresses, old.PortArray, old.CertHash, old.EcdhPublicKey, old.Name, old.OnionAddress, old.AutoConnect, old.NetworkType, old.NostrPubKey, ticket, old.ConnectionFlags);
            _peers[old.Key] = updated;
            UpdateCacheLocked();
        }
        if (save) Save();
        return true;
    }



    public bool TryGetByNostrPubKey(string? nostrPubKey, out SavedPeer? peer)
    {
        peer = null;
        if (string.IsNullOrWhiteSpace(nostrPubKey)) return false;

        lock (_sync)
        {
            var p = _peers.Values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.NostrPubKey) && string.Equals(x.NostrPubKey, nostrPubKey, StringComparison.OrdinalIgnoreCase));
            peer = p?.Copy();
            return peer is not null;
        }
    }
    public bool AddOrUpdate(string token, bool autoConnect = true, bool save = true)
    {
        var decodedPeer = Utilities.DecodeEndpointToken(token);
        return AddOrUpdate(decodedPeer, autoConnect, save);
    }

    public bool AddOrUpdate(PeerInfo peer, bool autoConnect = true, bool save = true)
    {
        if (peer.CertHash == null || peer.CertHash.Length == 0)
            return false;

        var addrsList = new List<IPAddress>();
        if (peer.Addresses != null && peer.Addresses.Length > 0)
        {
            foreach (var a in peer.Addresses)
            {
                if (Utilities.IsValidPeerAddress(a) && !addrsList.Contains(a))
                    addrsList.Add(a);
            }
        }

        // CRITICAL: Ensure ActiveEndPoint is merged so public IP is NEVER lost when peer.Addresses only had local IP!
        if (peer.ActiveEndPoint != null && Utilities.IsValidPeerAddress(peer.ActiveEndPoint.Address) && !addrsList.Contains(peer.ActiveEndPoint.Address))
        {
            addrsList.Insert(0, peer.ActiveEndPoint.Address);
        }

        if (addrsList.Count == 0 && !string.IsNullOrEmpty(peer.OnionAddress))
        {
            addrsList.Add(IPAddress.Loopback);
        }

        if (addrsList.Count == 0)
            return false;

        var ports = new List<ushort>(peer.PortArray ?? []);
        if (peer.ActiveEndPoint != null && peer.ActiveEndPoint.Port > 0)
        {
            ushort activePort = (ushort)peer.ActiveEndPoint.Port;
            if (!ports.Contains(activePort))
            {
                ports.Add(activePort);
            }
        }
        var portArray = ports.ToArray();

        peer.PortArray = portArray;
        peer.ConnectionFlags.MultipleIps = addrsList.Count > 1;
        peer.ConnectionFlags.PortMode = portArray.Length <= 1 ? PortMode.Single : (peer.ConnectionFlags.PortMode == PortMode.Single ? PortMode.Multiple : peer.ConnectionFlags.PortMode);
        
        return AddOrUpdate(addrsList, portArray, peer.CertHash, peer.EcdhPublicKey, peer.Name, peer.OnionAddress, autoConnect, save, peer.NetworkType, peer.NostrPubKey, peer.ResumptionTicket, peer.ConnectionFlags);
    }

    public bool AddOrUpdate(PeerInfo peer, bool autoConnect, bool save, string? nostrPubKey)
    {
        if (peer.CertHash == null || peer.CertHash.Length == 0)
            return false;

        var addrsList = new List<IPAddress>();
        if (peer.Addresses != null && peer.Addresses.Length > 0)
        {
            foreach (var a in peer.Addresses)
            {
                if (Utilities.IsValidPeerAddress(a) && !addrsList.Contains(a))
                    addrsList.Add(a);
            }
        }

        if (peer.ActiveEndPoint != null && Utilities.IsValidPeerAddress(peer.ActiveEndPoint.Address) && !addrsList.Contains(peer.ActiveEndPoint.Address))
        {
            addrsList.Insert(0, peer.ActiveEndPoint.Address);
        }

        if (addrsList.Count == 0 && !string.IsNullOrEmpty(peer.OnionAddress))
        {
            addrsList.Add(IPAddress.Loopback);
        }

        if (addrsList.Count == 0)
            return false;

        var ports = new List<ushort>(peer.PortArray ?? []);
        if (peer.ActiveEndPoint != null && peer.ActiveEndPoint.Port > 0)
        {
            ushort activePort = (ushort)peer.ActiveEndPoint.Port;
            if (!ports.Contains(activePort))
            {
                ports.Add(activePort);
            }
        }
        var portArray = ports.ToArray();

        peer.PortArray = portArray;
        peer.ConnectionFlags.MultipleIps = addrsList.Count > 1;
        peer.ConnectionFlags.PortMode = portArray.Length <= 1 ? PortMode.Single : (peer.ConnectionFlags.PortMode == PortMode.Single ? PortMode.Multiple : peer.ConnectionFlags.PortMode);

        return AddOrUpdate(addrsList, portArray, peer.CertHash, peer.EcdhPublicKey, peer.Name, peer.OnionAddress, autoConnect, save, peer.NetworkType, peer.NostrPubKey ?? nostrPubKey, peer.ResumptionTicket, peer.ConnectionFlags);
    }

    public bool AddOrUpdate(IPAddress[] addresses, ushort[] portArray, byte[] certificate, bool save) =>
        AddOrUpdate((IEnumerable<IPAddress>)addresses, portArray, certificate, null, null, null, true, save);

    public bool AddOrUpdate(IPAddress[] addresses, ushort[] portArray, byte[] certificate, byte[]? ecdhPublicKey = null, bool save = true) =>
        AddOrUpdate((IEnumerable<IPAddress>)addresses, portArray, certificate, ecdhPublicKey, null, null, true, save);

    public bool AddOrUpdate(IEnumerable<IPAddress> addresses, ushort[] portArray, byte[] certificate, bool save) =>
        AddOrUpdate(addresses, portArray, certificate, null, null, null, true, save);

    public bool AddOrUpdate(IEnumerable<IPAddress> addresses, ushort[] portArray, byte[] certificate, byte[]? ecdhPublicKey = null, bool save = true) =>
        AddOrUpdate(addresses, portArray, certificate, ecdhPublicKey, null, null, true, save);

    public bool AddOrUpdate(IEnumerable<IPAddress> addresses, ushort[] portArray, byte[] certificate, byte[]? ecdhPublicKey = null, string? name = null, string? onionAddress = null, bool autoConnect = true, bool save = true, QuicPunch.NetworkType networkType = QuicPunch.NetworkType.Static, string? nostrPubKey = null, byte[]? resumptionTicket = null, ConnectionFlags? connectionFlags = null)
    {
        ThrowIfDisposed();

        bool added;
        bool modified;
        SavedPeer savedPeerToNotify;

        lock (_sync)
        {
            if (!_peers.TryGetValue(Key(certificate), out var old))
            {
                var peer = new SavedPeer(addresses, portArray, certificate, ecdhPublicKey, name, onionAddress, autoConnect, networkType, nostrPubKey, resumptionTicket, connectionFlags);
                _peers[peer.Key] = peer;
                savedPeerToNotify = peer;
                added = true;
                modified = false;
                UpdateCacheLocked();
            }
            else
            {
                string? mergedNostrPubKey = !string.IsNullOrWhiteSpace(nostrPubKey) ? nostrPubKey : old.NostrPubKey;
                byte[]? mergedEcdh = (ecdhPublicKey != null && ecdhPublicKey.Length > 0) ? ecdhPublicKey : old.EcdhPublicKey;
                string? mergedName = !string.IsNullOrWhiteSpace(name) ? name : old.Name;
                string? mergedOnion = !string.IsNullOrWhiteSpace(onionAddress) ? onionAddress : old.OnionAddress;
                byte[]? mergedTicket = (resumptionTicket != null && resumptionTicket.Length > 0) ? resumptionTicket : old.ResumptionTicket;
                var mergedFlags = connectionFlags ?? old.ConnectionFlags;

                var mergedPeer = new SavedPeer(addresses, portArray, certificate, mergedEcdh, mergedName, mergedOnion, autoConnect, networkType, mergedNostrPubKey, mergedTicket, mergedFlags);
                if (!old.SameValue(mergedPeer))
                {
                    _peers[mergedPeer.Key] = mergedPeer;
                    savedPeerToNotify = mergedPeer;
                    added = false;
                    modified = true;
                    UpdateCacheLocked();
                }
                else return false;
            }
        }

        if (added) Safe(() => PeerAdded?.Invoke(savedPeerToNotify.Copy(), false));
        if (modified) Safe(() => PeerModified?.Invoke(savedPeerToNotify.Copy(), false));
        if (save) Save();

        return true;
    }

    public bool ToggleAutoConnect(byte[] certificate, bool autoConnect, bool save = true)
    {
        ThrowIfDisposed();
        if (certificate is null || certificate.Length == 0)
            return false;

        SavedPeer? updated = null;
        lock (_sync)
        {
            if (_peers.TryGetValue(Key(certificate), out var old))
            {
                if (old.AutoConnect == autoConnect)
                    return true;

                updated = old with { AutoConnect = autoConnect };
                _peers[updated.Key] = updated;
                UpdateCacheLocked();
            }
        }

        if (updated is not null)
        {
            Safe(() => PeerModified?.Invoke(updated.Copy(), false));
            if (save) Save();
            return true;
        }

        return false;
    }

    public bool Remove(byte[] certificate, bool save = true)
    {
        ThrowIfDisposed();

        if (certificate is null || certificate.Length == 0)
            return false;

        SavedPeer? removed;

        lock (_sync)
        {
            if (_peers.Remove(Key(certificate), out removed))
            {
                UpdateCacheLocked();
            }
        }

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
                else if (!old.SameValue(p)) modified.Add(p.Copy());
            }

            foreach (var (k, p) in _peers)
                if (!next.ContainsKey(k))
                    removed.Add(p.Copy());

            _peers.Clear();

            foreach (var (k, p) in next)
                _peers[k] = p.Copy();

            UpdateCacheLocked();
        }

        foreach (var p in added) Safe(() => PeerAdded?.Invoke(p.Copy(), external));
        foreach (var p in modified) Safe(() => PeerModified?.Invoke(p.Copy(), external));
        foreach (var p in removed) Safe(() => PeerRemoved?.Invoke(p.Copy(), external));

        Safe(() => PeersLoaded?.Invoke(SavedPeers, external));
    }

    private void UpdateCacheLocked()
    {
        _cachedPeers = _peers.Values.ToArray();
    }

    private Dictionary<string, SavedPeer> ReadUnlocked()
    {
        if (!File.Exists(_path))
            return new(StringComparer.Ordinal);

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var db = JsonSerializer.Deserialize<Db>(fs, _json) ?? new Db();

        if (db.Version is < 1 or > 2)
            throw new InvalidDataException($"VersiÃ³n no soportada: {db.Version}");

        var result = new Dictionary<string, SavedPeer>(StringComparer.Ordinal);

        foreach (var r in db.SavedPeers)
        {
            var ips = r.Ips is { Length: > 0 }
                ? r.Ips
                : !string.IsNullOrWhiteSpace(r.Ip)
                    ? new[] { r.Ip }
                    : throw new InvalidDataException("Peer row does not contain any IP address.");

            int minP = (r.Port is not null && r.Ips is not { Length: > 0 } ? r.Port.Value : r.MinPort).GetValueOrDefault();
            int maxP = (r.Port is not null && r.Ips is not { Length: > 0 } ? r.Port.Value : r.MaxPort).GetValueOrDefault(minP);
            var certHash = Convert.FromBase64String(r.CertHash);
            var ecdhPublicKey = !string.IsNullOrWhiteSpace(r.EcdhPublicKey) ? Convert.FromBase64String(r.EcdhPublicKey) : null;
            var resumptionTicket = !string.IsNullOrWhiteSpace(r.ResumptionTicket) ? Convert.FromBase64String(r.ResumptionTicket) : null;
            var addresses = ParseAddresses(ips);

            ushort[] portArray = r.Ports is { Length: > 0 }
                ? r.Ports
                : (minP == maxP
                    ? (minP > 0 ? [(ushort)minP] : [])
                    : Enumerable.Range(minP, Math.Max(0, maxP - minP + 1)).Select(p => (ushort)p).ToArray());

            var legacyNetworkType = r.NetworkType;
            var flags = r.ConnectionFlags is byte rawFlags
                ? new ConnectionFlags(rawFlags)
                : new ConnectionFlags
                {
                    IsTor = !string.IsNullOrWhiteSpace(r.OnionAddress) || (db.Version == 1 && legacyNetworkType == 4),
                    MultipleIps = addresses.Length > 1 || (db.Version == 1 && legacyNetworkType is 2 or 3),
                    PortMode = db.Version == 1 && legacyNetworkType == 1
                        ? PortMode.Multiple
                        : minP == maxP ? PortMode.Single : PortMode.Range,
                    IsNat = db.Version == 1 && legacyNetworkType is 1 or 2 or 3
                };
            var netType = flags.IsTor ? QuicPunch.NetworkType.Tor : QuicPunch.NetworkType.Static;

            var p = new SavedPeer(addresses, portArray, certHash, ecdhPublicKey, r.Name, r.OnionAddress, r.AutoConnect, netType, r.NostrPubKey, resumptionTicket, flags);

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
                .OrderBy(p => p.Addresses[0].ToString(), StringComparer.Ordinal)
                .ThenBy(p => p.PortArray.Length > 0 ? p.PortArray[0] : 0)
                .ThenBy(p => Convert.ToBase64String(p.CertHash), StringComparer.Ordinal)
                .Select(p => new Row
                {
                    Ips = p.Addresses.Select(a => a.ToString()).ToArray(),
                    Ports = p.PortArray,
                    CertHash = Convert.ToBase64String(p.CertHash),
                    Name = p.Name,
                    OnionAddress = p.OnionAddress,
                    AutoConnect = p.AutoConnect,
                    EcdhPublicKey = p.EcdhPublicKey != null ? Convert.ToBase64String(p.EcdhPublicKey) : null,
                    NetworkType = (int)p.NetworkType,
                    ConnectionFlags = p.ConnectionFlags.RawValue,
                    NostrPubKey = p.NostrPubKey,
                    ResumptionTicket = p.ResumptionTicket != null ? Convert.ToBase64String(p.ResumptionTicket) : null
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

        throw new TimeoutException($"No se pudo bloquear el almacÃ©n de peers: {_path}", last);
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

    private static string Key(byte[] certificate) => Convert.ToBase64String(certificate);

    private static IPAddress[] ParseAddresses(IEnumerable<string> ips)
    {
        var addresses = new List<IPAddress>();

        foreach (var ip in ips)
        {
            if (!IPAddress.TryParse(ip, out var address))
                throw new InvalidDataException($"Invalid peer IP address: {ip}");

            addresses.Add(address);
        }

        return NormalizeAddresses(addresses);
    }

    private static IPAddress[] NormalizeAddresses(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var result = addresses
            .Select(a => a ?? throw new ArgumentException("IP address entries cannot be null.", nameof(addresses)))
            .GroupBy(a => a.ToString(), StringComparer.Ordinal)
            .Select(g => CloneAddress(g.First()))
            .OrderBy(a => a.ToString(), StringComparer.Ordinal)
            .ToArray();

        if (result.Length == 0)
            throw new ArgumentException("At least one IP address is required.", nameof(addresses));

        return result;
    }

    private static IPAddress CloneAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var clone = new IPAddress(address.GetAddressBytes());

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            clone.ScopeId = address.ScopeId;

        return clone;
    }


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
