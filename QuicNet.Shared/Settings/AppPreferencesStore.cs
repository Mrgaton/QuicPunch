using System.Text.Json;
using QuicNet.Protocols;

namespace QuicNet.Settings;

internal sealed class AppPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private AppPreferences _current;
    internal event Action? Changed;

    public AppPreferencesStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    internal string StorageDirectory => Path.GetDirectoryName(Path.GetFullPath(_path))!;

    public AppPreferences Snapshot()
    {
        lock (_sync)
        {
            return Clone(_current);
        }
    }

    public void Update(Action<AppPreferences> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync)
        {
            var previous = _current;
            var next = Clone(previous);
            update(next);
            Normalize(next);
            _current = next;
            try { SaveLocked(); }
            catch { _current = previous; throw; }
        }
        Changed?.Invoke();
    }

    private static AppPreferences Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var value = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path), QuicNet.Serialization.SharedJson.Options);
                if (value != null)
                {
                    Normalize(value);
                    return value;
                }
            }
        }
        catch { }

        return new AppPreferences();
    }

    private static AppPreferences Clone(AppPreferences value) => new()
    {
        StartWithSystem = value.StartWithSystem,
        RunInBackground = value.RunInBackground,
        Notifications = value.Notifications.Clone(),
        ReviewedUntrustedPeers = [.. value.ReviewedUntrustedPeers],
        DisplayName = value.DisplayName,
        PresenceIdleMinutes = value.PresenceIdleMinutes,
        WanNostrDiscoveryEnabled = value.WanNostrDiscoveryEnabled,
        TorNostrDiscoveryEnabled = value.TorNostrDiscoveryEnabled,
        TorDiscoveryRiskAcknowledged = value.TorDiscoveryRiskAcknowledged,
        WanEnabled = value.WanEnabled,
        TorEnabled = value.TorEnabled,
        TorVirtualPort = value.TorVirtualPort,
        TorTransportMode = value.TorTransportMode,
        LanEnabled = value.LanEnabled,
        LanAutoAssign = value.LanAutoAssign,
        LanIp = value.LanIp,
        LanSubnetMask = value.LanSubnetMask,
        LanMtu = value.LanMtu
    };

    private static void Normalize(AppPreferences value)
    {
        // Older opt-ins predate the privacy warning and must not publish again silently.
        if (!value.TorDiscoveryRiskAcknowledged) value.TorNostrDiscoveryEnabled = false;
        if (!value.TorNostrDiscoveryEnabled) value.TorDiscoveryRiskAcknowledged = false;
        value.Notifications ??= new();
        value.Notifications.Validate();
        value.ReviewedUntrustedPeers = (value.ReviewedUntrustedPeers ?? []).Where(hash => !string.IsNullOrWhiteSpace(hash)).Distinct(StringComparer.Ordinal).TakeLast(4096).ToArray();
        value.TorVirtualPort = Math.Clamp(value.TorVirtualPort, 0, ushort.MaxValue);
        if (value.LanMtu <= 0 || value.LanMtu == 1500)
            value.LanMtu = IVirtualLanHandler.DefaultDatagramMtu;
        else
            value.LanMtu = Math.Clamp(value.LanMtu, 1200, 9000);
        if (string.IsNullOrWhiteSpace(value.LanSubnetMask))
            value.LanSubnetMask = "255.255.255.0";
        value.LanIp ??= "";
        value.DisplayName = LocalUserProfile.Normalize(value.DisplayName);
        value.PresenceIdleMinutes = Math.Clamp(value.PresenceIdleMinutes, 1, 60);
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_current, QuicNet.Serialization.SharedJson.WithOptions(JsonOptions)));
        File.Move(temp, _path, true);
    }
}

internal sealed class AppPreferences
{
    public bool StartWithSystem { get; set; } = true;
    public bool RunInBackground { get; set; } = true;
    public NotificationPreferences Notifications { get; set; } = new();
    public string[] ReviewedUntrustedPeers { get; set; } = [];
    public string DisplayName { get; set; } = "";
    public int PresenceIdleMinutes { get; set; } = 5;
    public bool WanNostrDiscoveryEnabled { get; set; } = true;
    public bool TorNostrDiscoveryEnabled { get; set; } = false;
    public bool TorDiscoveryRiskAcknowledged { get; set; } = false;
    public bool NostrDiscoveryEnabled
    {
        get => WanNostrDiscoveryEnabled;
        set => WanNostrDiscoveryEnabled = value;
    }
    public bool WanEnabled { get; set; } = true;
    public bool TorEnabled { get; set; } = false;
    public int TorVirtualPort { get; set; }
    public string TorTransportMode { get; set; } = "AutoCascade";
    public bool LanEnabled { get; set; } = true;
    public bool LanAutoAssign { get; set; } = true;
    public string LanIp { get; set; } = "";
    public string LanSubnetMask { get; set; } = "255.255.255.0";
    public int LanMtu { get; set; } = IVirtualLanHandler.DefaultDatagramMtu;
}
