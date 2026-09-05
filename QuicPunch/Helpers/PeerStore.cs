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
        public int MinPort { get; init; }
        public int MaxPort { get; init; }
        public byte[] CertHash { get; init; }
        public byte[]? EcdhPublicKey { get; init; }
        public string? Name { get; init; }
        public string? OnionAddress { get; init; }
        public bool AutoConnect { get; init; } = true;
        public QuicPunch.NetworkType NetworkType { get; init; } = QuicPunch.NetworkType.Static;
        public string? NostrPubKey { get; init; }
        public byte[]? ResumptionTicket { get; init; }

        // Certificate hash is the stable peer identity. Addresses and ports can change with NAT.
        internal string Key => PeerStore.Key(CertHash);

        public SavedPeer(IPAddress[] addresses, int minPort, int maxPort, byte[] certHash, byte[]? ecdhPublicKey = null, string? name = null, string? onionAddress = null, bool autoConnect = true, QuicPunch.NetworkType networkType = QuicPunch.NetworkType.Static, string? nostrPubKey = null, byte[]? resumptionTicket = null)
        {
            Addresses = NormalizeAddresses(addresses);
            ValidatePorts(minPort, maxPort);

            if (certHash is null || certHash.Length == 0)
                throw new ArgumentException("Certificate hash cannot be empty.", nameof(certHash));

            MinPort = minPort;
            MaxPort = maxPort;
            CertHash = certHash.ToArray();
            EcdhPublicKey = ecdhPublicKey?.ToArray();
            Name = name;
            OnionAddress = onionAddress;
            AutoConnect = autoConnect;
            NetworkType = networkType;
            NostrPubKey = nostrPubKey;
            ResumptionTicket = resumptionTicket?.ToArray();
        }

        public SavedPeer(IEnumerable<IPAddress> addresses, int minPort, int maxPort, byte[] certHash, byte[]? ecdhPublicKey = null, string? name = null, string? onionAddress = null, bool autoConnect = true, QuicPunch.NetworkType networkType = QuicPunch.NetworkType.Static, string? nostrPubKey = null, byte[]? resumptionTicket = null)
            : this(NormalizeAddresses(addresses), minPort, maxPort, certHash, ecdhPublicKey, name, onionAddress, autoConnect, networkType, nostrPubKey, resumptionTicket)
        {
        }

        internal SavedPeer Copy() => new(Addresses.Select(CloneAddress).ToArray(), MinPort, MaxPort, CertHash.ToArray(), EcdhPublicKey?.ToArray(), Name, OnionAddress, AutoConnect, NetworkType, NostrPubKey, ResumptionTicket?.ToArray());

        internal bool SameCertificate(SavedPeer other) =>
            CertHash.AsSpan().SequenceEqual(other.CertHash);

        internal bool SameValue(SavedPeer other) =>
            SameCertificate(other) &&
            MinPort == other.MinPort &&
            MaxPort == other.MaxPort &&
            Name == other.Name &&
            OnionAddress == other.OnionAddress &&
            AutoConnect == other.AutoConnect &&
            NetworkType == other.NetworkType &&
            string.Equals(NostrPubKey, other.NostrPubKey, StringComparison.OrdinalIgnoreCase) &&
            ((ResumptionTicket == null && other.ResumptionTicket == null) || (ResumptionTicket != null && other.ResumptionTicket != null && ResumptionTicket.AsSpan().SequenceEqual(other.ResumptionTicket))) &&
            Addresses.Select(a => a.ToString()).SequenceEqual(other.Addresses.Select(a => a.ToString()), StringComparer.Ordinal);

        internal bool Contains(IPEndPoint endPoint) =>
            endPoint.Port >= MinPort &&
            endPoint.Port <= MaxPort &&
            Addresses.Any(a => SameAddress(a, endPoint.Address));
    }

    private sealed class Db
    {
        public int Version { get; set; } = 1;
        public List<Row> SavedPeers { get; set; } = [];
    }

    private sealed class Row
    {
        public string[] Ips { get; set; } = [];
        public int MinPort { get; set; }
        public int MaxPort { get; set; }
        public string CertHash { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OnionAddress { get; set; }

        public bool AutoConnect { get; set; } = true;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EcdhPublicKey { get; set; }

        public int NetworkType { get; set; } = 0;

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
            var updated = new SavedPeer(old.Addresses, old.MinPort, old.MaxPort, old.CertHash, old.EcdhPublicKey, old.Name, old.OnionAddress, old.AutoConnect, old.NetworkType, old.NostrPubKey, ticket);
            _peers[old.Key] = updated;
            UpdateCacheLocked();
        }
        if (save) Save();
        return true;
    }

    // Range lookup: returns the peer whose address array contains endPoint.Address
    // and whose port range contains endPoint.Port.
    public bool TryGet(IPEndPoint endPoint, out SavedPeer? peer)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return TryGet(endPoint.Address, endPoint.Port, out peer);
    }

    public bool TryGet(IPAddress address, int port, out SavedPeer? peer)
    {
        ArgumentNullException.ThrowIfNull(address);

        lock (_sync)
        {
            var p = _peers.Values.FirstOrDefault(x => x.Contains(new IPEndPoint(address, port)));
            peer = p?.Copy();
            return peer is not null;
        }
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

    public bool TryGetByAddress(IEnumerable<IPAddress>? addresses, out SavedPeer? peer)
    {
        peer = null;
        if (addresses is null) return false;
        var nonLoopback = addresses.Where(a => !IPAddress.IsLoopback(a)).ToList();
        if (nonLoopback.Count == 0) return false;

        lock (_sync)
        {
            var p = _peers.Values.FirstOrDefault(x => x.Addresses != null && x.Addresses.Any(a => !IPAddress.IsLoopback(a) && nonLoopback.Contains(a)));
            peer = p?.Copy();
            return peer is not null;
        }
    }

    public bool TryGetByOnion(string? onionAddress, out SavedPeer? peer)
    {
        peer = null;
        if (string.IsNullOrWhiteSpace(onionAddress)) return false;

        lock (_sync)
        {
            var p = _peers.Values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.OnionAddress) && string.Equals(x.OnionAddress, onionAddress, StringComparison.OrdinalIgnoreCase));
            peer = p?.Copy();
            return peer is not null;
        }
    }

    public bool UpdatePeerCertificate(byte[] oldCert, byte[] newCert, bool save = true)
    {
        ThrowIfDisposed();
        if (oldCert is null || oldCert.Length == 0 || newCert is null || newCert.Length == 0)
            return false;

        SavedPeer? updated = null;
        lock (_sync)
        {
            if (_peers.Remove(Key(oldCert), out var old))
            {
                updated = old with { CertHash = newCert.ToArray() };
                _peers[updated.Key] = updated;
                UpdateCacheLocked();
            }
            else return false;
        }

        Safe(() => PeerModified?.Invoke(updated.Copy(), false));
        if (save) Save();
        return true;
    }

    // Exact lookup by stored group, useful for tools/tests that do not know the certificate yet.
    public bool TryGet(IEnumerable<IPAddress> addresses, int minPort, int maxPort, out SavedPeer? peer)
    {
        var normalized = NormalizeAddresses(addresses);
        ValidatePorts(minPort, maxPort);

        lock (_sync)
        {
            var p = _peers.Values.FirstOrDefault(x =>
                x.MinPort == minPort &&
                x.MaxPort == maxPort &&
                x.Addresses.Select(a => a.ToString()).SequenceEqual(normalized.Select(a => a.ToString()), StringComparer.Ordinal));

            peer = p?.Copy();
            return peer is not null;
        }
    }

    // Backward-compatible helper: stores one IP with min/max equal to the endpoint port.
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

        int minPort = peer.MinPort;
        int maxPort = peer.MaxPort;
        if (peer.ActiveEndPoint != null && peer.ActiveEndPoint.Port > 0)
        {
            if (minPort <= 0) minPort = peer.ActiveEndPoint.Port;
            if (maxPort <= 0) maxPort = peer.ActiveEndPoint.Port;
            minPort = Math.Min(minPort, peer.ActiveEndPoint.Port);
            maxPort = Math.Max(maxPort, peer.ActiveEndPoint.Port);
        }

        var netType = peer.NetworkType;
        if (netType == QuicPunch.NetworkType.Unknown || netType == QuicPunch.NetworkType.Static)
        {
            if (addrsList.Count > 1 && minPort != maxPort)
                netType = QuicPunch.NetworkType.DynamicPortAndAddress;
            else if (addrsList.Count > 1)
                netType = QuicPunch.NetworkType.DynamicAddress;
            else if (minPort != maxPort)
                netType = QuicPunch.NetworkType.DynamicPort;
        }

        return AddOrUpdate(addrsList, minPort, maxPort, peer.CertHash, peer.EcdhPublicKey, peer.Name, peer.OnionAddress, autoConnect, save, netType, peer.NostrPubKey, peer.ResumptionTicket);
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

        int minPort = peer.MinPort;
        int maxPort = peer.MaxPort;
        if (peer.ActiveEndPoint != null && peer.ActiveEndPoint.Port > 0)
        {
            if (minPort <= 0) minPort = peer.ActiveEndPoint.Port;
            if (maxPort <= 0) maxPort = peer.ActiveEndPoint.Port;
            minPort = Math.Min(minPort, peer.ActiveEndPoint.Port);
            maxPort = Math.Max(maxPort, peer.ActiveEndPoint.Port);
        }

        var netType = peer.NetworkType;
        if (netType == QuicPunch.NetworkType.Unknown || netType == QuicPunch.NetworkType.Static)
        {
            if (addrsList.Count > 1 && minPort != maxPort)
                netType = QuicPunch.NetworkType.DynamicPortAndAddress;
            else if (addrsList.Count > 1)
                netType = QuicPunch.NetworkType.DynamicAddress;
            else if (minPort != maxPort)
                netType = QuicPunch.NetworkType.DynamicPort;
        }

        return AddOrUpdate(addrsList, minPort, maxPort, peer.CertHash, peer.EcdhPublicKey, peer.Name, peer.OnionAddress, autoConnect, save, netType, peer.NostrPubKey ?? nostrPubKey, peer.ResumptionTicket);
    }

    public bool AddOrUpdate(IPAddress[] addresses, int minPort, int maxPort, byte[] certificate, bool save) =>
        AddOrUpdate((IEnumerable<IPAddress>)addresses, minPort, maxPort, certificate, null, null, null, true, save);

    public bool AddOrUpdate(IPAddress[] addresses, int minPort, int maxPort, byte[] certificate, byte[]? ecdhPublicKey = null, bool save = true) =>
        AddOrUpdate((IEnumerable<IPAddress>)addresses, minPort, maxPort, certificate, ecdhPublicKey, null, null, true, save);

    public bool AddOrUpdate(IEnumerable<IPAddress> addresses, int minPort, int maxPort, byte[] certificate, bool save) =>
        AddOrUpdate(addresses, minPort, maxPort, certificate, null, null, null, true, save);

    public bool AddOrUpdate(IEnumerable<IPAddress> addresses, int minPort, int maxPort, byte[] certificate, byte[]? ecdhPublicKey = null, bool save = true) =>
        AddOrUpdate(addresses, minPort, maxPort, certificate, ecdhPublicKey, null, null, true, save);

    public bool AddOrUpdate(IEnumerable<IPAddress> addresses, int minPort, int maxPort, byte[] certificate, byte[]? ecdhPublicKey = null, string? name = null, string? onionAddress = null, bool autoConnect = true, bool save = true, QuicPunch.NetworkType networkType = QuicPunch.NetworkType.Static, string? nostrPubKey = null, byte[]? resumptionTicket = null)
    {
        ThrowIfDisposed();

        bool added;
        bool modified;
        SavedPeer savedPeerToNotify;

        lock (_sync)
        {
            if (!_peers.TryGetValue(Key(certificate), out var old))
            {
                var peer = new SavedPeer(addresses, minPort, maxPort, certificate, ecdhPublicKey, name, onionAddress, autoConnect, networkType, nostrPubKey, resumptionTicket);
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

                var mergedPeer = new SavedPeer(addresses, minPort, maxPort, certificate, mergedEcdh, mergedName, mergedOnion, autoConnect, networkType, mergedNostrPubKey, mergedTicket);
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

    // Compatibility removal for older code that only has one endpoint.
    public bool Remove(IPEndPoint endPoint, bool save = true)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(endPoint);

        SavedPeer? removed = null;
        string? removeKey = null;

        lock (_sync)
        {
            foreach (var (key, peer) in _peers)
            {
                if (peer.Contains(endPoint))
                {
                    removeKey = key;
                    removed = peer;
                    break;
                }
            }

            if (removeKey is not null)
            {
                _peers.Remove(removeKey);
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

    public bool Remove(IEnumerable<IPAddress> addresses, int minPort, int maxPort, bool save = true)
    {
        ThrowIfDisposed();

        var normalized = NormalizeAddresses(addresses);
        ValidatePorts(minPort, maxPort);

        SavedPeer? removed = null;
        string? removeKey = null;

        lock (_sync)
        {
            foreach (var (key, peer) in _peers)
            {
                if (peer.MinPort == minPort &&
                    peer.MaxPort == maxPort &&
                    peer.Addresses.Select(a => a.ToString()).SequenceEqual(normalized.Select(a => a.ToString()), StringComparer.Ordinal))
                {
                    removeKey = key;
                    removed = peer;
                    break;
                }
            }

            if (removeKey is not null)
            {
                _peers.Remove(removeKey);
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

        if (db.Version != 1)
            throw new InvalidDataException($"VersiÃ³n no soportada: {db.Version}");

        var result = new Dictionary<string, SavedPeer>(StringComparer.Ordinal);

        foreach (var r in db.SavedPeers)
        {
            var ips = r.Ips is { Length: > 0 }
                ? r.Ips
                : !string.IsNullOrWhiteSpace(r.Ip)
                    ? new[] { r.Ip }
                    : throw new InvalidDataException("Peer row does not contain any IP address.");

            var minPort = r.Port is not null && r.Ips is not { Length: > 0 } ? r.Port.Value : r.MinPort;
            var maxPort = r.Port is not null && r.Ips is not { Length: > 0 } ? r.Port.Value : r.MaxPort;
            var certHash = Convert.FromBase64String(r.CertHash);
            var ecdhPublicKey = !string.IsNullOrWhiteSpace(r.EcdhPublicKey) ? Convert.FromBase64String(r.EcdhPublicKey) : null;
            var resumptionTicket = !string.IsNullOrWhiteSpace(r.ResumptionTicket) ? Convert.FromBase64String(r.ResumptionTicket) : null;
            var addresses = ParseAddresses(ips);

            var netType = (QuicPunch.NetworkType)r.NetworkType;
            if (netType == QuicPunch.NetworkType.Unknown || netType == QuicPunch.NetworkType.Static)
            {
                if (addresses.Length > 1 && minPort != maxPort)
                    netType = QuicPunch.NetworkType.DynamicPortAndAddress;
                else if (addresses.Length > 1)
                    netType = QuicPunch.NetworkType.DynamicAddress;
                else if (minPort != maxPort)
                    netType = QuicPunch.NetworkType.DynamicPort;
            }

            var p = new SavedPeer(addresses, minPort, maxPort, certHash, ecdhPublicKey, r.Name, r.OnionAddress, r.AutoConnect, netType, r.NostrPubKey, resumptionTicket);

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
                .ThenBy(p => p.MinPort)
                .ThenBy(p => p.MaxPort)
                .ThenBy(p => Convert.ToBase64String(p.CertHash), StringComparer.Ordinal)
                .Select(p => new Row
                {
                    Ips = p.Addresses.Select(a => a.ToString()).ToArray(),
                    MinPort = p.MinPort,
                    MaxPort = p.MaxPort,
                    CertHash = Convert.ToBase64String(p.CertHash),
                    Name = p.Name,
                    OnionAddress = p.OnionAddress,
                    AutoConnect = p.AutoConnect,
                    EcdhPublicKey = p.EcdhPublicKey != null ? Convert.ToBase64String(p.EcdhPublicKey) : null,
                    NetworkType = (int)p.NetworkType,
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

    private static void ValidatePorts(int minPort, int maxPort)
    {
        if (minPort < 1 || minPort > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(minPort), "MinPort must be between 1 and 65535.");

        if (maxPort < 1 || maxPort > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(maxPort), "MaxPort must be between 1 and 65535.");

        if (minPort > maxPort)
            throw new ArgumentException("MinPort cannot be greater than MaxPort.", nameof(minPort));
    }

    private static bool SameAddress(IPAddress left, IPAddress right)
    {
        if (left.AddressFamily != right.AddressFamily)
            return false;

        if (!left.GetAddressBytes().AsSpan().SequenceEqual(right.GetAddressBytes()))
            return false;

        return left.AddressFamily != AddressFamily.InterNetworkV6 || left.ScopeId == right.ScopeId;
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
