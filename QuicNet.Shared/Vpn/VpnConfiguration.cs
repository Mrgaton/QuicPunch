using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuicNet.Vpn;

public sealed class VpnConfiguration
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public string Mode { get; set; } = "simple";
    public bool Enabled { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool AutoAssign { get; set; } = true;
    public string LocalAddress { get; set; } = "";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public int Mtu { get; set; } = 1420;
    public List<VpnPeerConfiguration> Peers { get; set; } = [];
}

public sealed class VpnPeerConfiguration
{
    public string CertHash { get; set; } = "";
    // Missing values preserve the mode used by configurations written before per-peer modes.
    public string? Mode { get; set; }
    public bool UsesAutomaticLan(string fallbackMode) => (Mode ?? fallbackMode) == "simple";
    public bool Enabled { get; set; } = true;
    public bool Paused { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool AutoAccept { get; set; }
    public string VirtualAddress { get; set; } = "";
    public string[] AllowedSources { get; set; } = [];
    public string[] AccessibleDestinations { get; set; } = [];
    public string[] Routes { get; set; } = [];
    public bool AllowBroadcast { get; set; }
    public bool AllowForwarding { get; set; }
}

/// <summary>Validated snapshots and atomic saves. Invalid external edits never replace a working policy.</summary>
public sealed class VpnConfigurationStore : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly object _sync = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _reload;
    private VpnConfiguration _current;
    private string _content;
    public string Path { get; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    public VpnConfigurationStore(string path, VpnConfiguration initial)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        Validate(initial);
        _current = Clone(initial);
        _content = "";
        if (File.Exists(Path)) { if (!Reload()) _current.Enabled = false; }
        else Save(initial, expectedRevision: initial.Revision);
        _reload = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(Path)!, System.IO.Path.GetFileName(Path)) {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.EnableRaisingEvents = true;
    }
    private void OnFileChanged(object sender, FileSystemEventArgs e) { try { _reload.Change(250, Timeout.Infinite); } catch (ObjectDisposedException) { } }
    public VpnConfiguration Snapshot() { lock (_sync) return Clone(_current); }
    private static VpnConfiguration Clone(VpnConfiguration value) => JsonSerializer.Deserialize<VpnConfiguration>(JsonSerializer.Serialize(value, QuicNet.Serialization.SharedJson.WithOptions(JsonOptions)), QuicNet.Serialization.SharedJson.WithOptions(JsonOptions))!;

    public void Save(VpnConfiguration value, long expectedRevision)
    {
        Validate(value);
        lock (_sync)
        {
            if (File.Exists(Path) && File.ReadAllText(Path) != _content)
                throw new InvalidDataException("The configuration file changed on disk. Wait for reload and refresh before saving.");
            if (_current.Revision != expectedRevision) throw new InvalidDataException("Configuration changed. Refresh before saving.");
            var next = Clone(value);
            next.Revision = checked(_current.Revision + 1);
            string content = JsonSerializer.Serialize(next, QuicNet.Serialization.SharedJson.WithOptions(JsonOptions));
            if (System.Text.Encoding.UTF8.GetByteCount(content) > 128 * 1024)
                throw new InvalidDataException("VPN configuration exceeds 128 KiB.");
            string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, content);
                File.Move(temporary, Path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _current = next;
            _content = content;
            LastError = null;
        }
        Changed?.Invoke();
    }

    public bool Reload()
    {
        try
        {
            lock (_sync)
            {
                if (new FileInfo(Path).Length > 128 * 1024) throw new InvalidDataException("VPN configuration exceeds 128 KiB.");
                string content = File.ReadAllText(Path);
                if (content == _content) return false;
                var next = JsonSerializer.Deserialize<VpnConfiguration>(content, QuicNet.Serialization.SharedJson.WithOptions(JsonOptions)) ?? throw new InvalidDataException("Empty configuration.");
                Validate(next);
                next.Revision = Math.Max(next.Revision, checked(_current.Revision + 1));
                _current = Clone(next);
                _content = content;
                LastError = null;
            }
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OverflowException)
        {
            LastError = ex.Message;
            Changed?.Invoke();
            return false;
        }
    }

    public static void Validate(VpnConfiguration value)
    {
        if (value.Version != 1 || value.Mode is not ("simple" or "advanced") || value.Revision < 0)
            throw new InvalidDataException("Unsupported VPN configuration version or mode.");
        if (value.Mtu is < 1200 or > 9000) throw new InvalidDataException("MTU must be between 1200 and 9000.");
        Ipv4Prefix.MaskLength(value.SubnetMask);
        if (value.AutoAssign) value.SubnetMask = "255.255.255.0";
        if (!value.AutoAssign || !string.IsNullOrEmpty(value.LocalAddress)) ValidateHost(value.LocalAddress);
        if (value.Peers == null || value.Peers.Count > 1024) throw new InvalidDataException("Too many peers.");
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<Ipv4Prefix, string>();
        var addresses = new HashSet<uint>();
        foreach (var peer in value.Peers)
        {
            if (peer == null || peer.CertHash == null || peer.CertHash.Length != 64 || !peer.CertHash.All(Uri.IsHexDigit) || !hashes.Add(peer.CertHash))
                throw new InvalidDataException("Each VPN peer needs a unique 32-byte certificate identity.");
            if (peer.Mode is not (null or "simple" or "advanced"))
                throw new InvalidDataException("Unsupported VPN peer mode.");
            if (!string.IsNullOrEmpty(peer.VirtualAddress))
            {
                ValidateHost(peer.VirtualAddress);
                if (peer.Enabled && peer.Mode != "simple" && (!addresses.Add(Ipv4Prefix.Address(peer.VirtualAddress)) || peer.VirtualAddress == value.LocalAddress))
                    throw new InvalidDataException("Duplicate virtual address.");
            }
            foreach (var list in new[] { peer.AllowedSources, peer.AccessibleDestinations, peer.Routes })
            {
                if (list == null || list.Length > 128) throw new InvalidDataException("Too many prefixes.");
                foreach (string prefix in list) Ipv4Prefix.Parse(prefix);
            }
            if (!peer.Enabled) continue;
            var routes = peer.Mode == "simple" ? Array.Empty<string>() : peer.Routes;
            string address = peer.Mode == "simple" ? "" : peer.VirtualAddress;
            foreach (string route in routes.Append(string.IsNullOrEmpty(address) ? "" : address + "/32").Where(p => p.Length != 0))
            {
                var prefix = Ipv4Prefix.Parse(route);
                if (owners.TryGetValue(prefix, out var owner) && !owner.Equals(peer.CertHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Ambiguous route {prefix} belongs to multiple peers.");
                owners[prefix] = peer.CertHash;
            }
        }
    }
    private static void ValidateHost(string address)
    {
        uint ip = Ipv4Prefix.Address(address);
        if (ip >> 24 is 0 or 127 || ip >= 0xE0000000) throw new InvalidDataException("Expected a unicast VPN address.");
    }
    public void Dispose() { _watcher.Dispose(); _reload.Dispose(); }
}
