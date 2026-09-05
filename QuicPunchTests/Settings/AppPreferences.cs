using System.Text.Json;

namespace QuicPunchTests.Settings;

internal sealed class AppPreferences
{
    public bool WanNostrDiscoveryEnabled { get; set; } = true;
    public bool TorNostrDiscoveryEnabled { get; set; } = true;
    public bool NostrDiscoveryEnabled
    {
        get => WanNostrDiscoveryEnabled;
        set => WanNostrDiscoveryEnabled = value;
    }
    public bool WanEnabled { get; set; } = true;
    public bool TorEnabled { get; set; }
    public int TorVirtualPort { get; set; }
    public string TorTransportMode { get; set; } = "AutoCascade";
    public bool LanEnabled { get; set; } = true;
    public bool LanAutoAssign { get; set; } = true;
    public string LanIp { get; set; } = "";
    public string LanSubnetMask { get; set; } = "255.255.255.0";
    public int LanMtu { get; set; } = 1500;
    public bool AutoAcceptTrusted { get; set; } = true;
}

internal sealed class AppPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private AppPreferences _current;

    public AppPreferencesStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

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
            update(_current);
            Normalize(_current);
            SaveLocked();
        }
    }

    private static AppPreferences Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var value = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path));
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
        WanNostrDiscoveryEnabled = value.WanNostrDiscoveryEnabled,
        TorNostrDiscoveryEnabled = value.TorNostrDiscoveryEnabled,
        WanEnabled = value.WanEnabled,
        TorEnabled = value.TorEnabled,
        TorVirtualPort = value.TorVirtualPort,
        LanEnabled = value.LanEnabled,
        LanAutoAssign = value.LanAutoAssign,
        LanIp = value.LanIp,
        LanSubnetMask = value.LanSubnetMask,
        LanMtu = value.LanMtu,
        AutoAcceptTrusted = value.AutoAcceptTrusted
    };

    private static void Normalize(AppPreferences value)
    {
        value.TorVirtualPort = Math.Clamp(value.TorVirtualPort, 0, ushort.MaxValue);
        value.LanMtu = Math.Clamp(value.LanMtu, 1200, 9000);
        if (string.IsNullOrWhiteSpace(value.LanSubnetMask))
            value.LanSubnetMask = "255.255.255.0";
        value.LanIp ??= "";
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_current, JsonOptions));
        File.Move(temp, _path, true);
    }
}
