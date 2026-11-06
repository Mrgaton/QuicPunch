namespace QuicNet.Settings;

public sealed class NotificationRule
{
    public bool Enabled { get; set; } = true;
    public bool Popup { get; set; } = true;
    public bool Sound { get; set; } = true;
    public NotificationRule Clone() => new() { Enabled = Enabled, Popup = Popup, Sound = Sound };
}

public sealed class NotificationPreferences
{
    public static readonly string[] Categories = ["messages", "calls", "files", "screen", "system"];
    public bool Enabled { get; set; } = true;
    public bool Desktop { get; set; } = true;
    public bool InApp { get; set; } = true;
    public bool Sound { get; set; } = true;
    public bool Preview { get; set; } = true;
    public bool WhileFocused { get; set; } = true;
    public bool DoNotDisturb { get; set; }
    public DateTimeOffset? DoNotDisturbUntil { get; set; }
    public bool QuietHours { get; set; }
    public int QuietStart { get; set; } = 22 * 60;
    public int QuietEnd { get; set; } = 8 * 60;
    public int Volume { get; set; } = 60;
    public string Tone { get; set; } = "soft";
    public Dictionary<string, NotificationRule> Features { get; set; } = new();
    // Certificate fingerprints survive reconnects and display-name changes.
    public Dictionary<string, NotificationRule> Contacts { get; set; } = new();
    public NotificationPreferences Clone() => new()
    {
        Enabled = Enabled, Desktop = Desktop, InApp = InApp, Sound = Sound, Preview = Preview,
        WhileFocused = WhileFocused, DoNotDisturb = DoNotDisturb, DoNotDisturbUntil = DoNotDisturbUntil,
        QuietHours = QuietHours, QuietStart = QuietStart, QuietEnd = QuietEnd, Volume = Volume, Tone = Tone,
        Features = Features.ToDictionary(p => p.Key, p => p.Value.Clone()),
        Contacts = Contacts.ToDictionary(p => p.Key, p => p.Value.Clone())
    };
    public void Validate()
    {
        if (Volume is < 0 or > 100 || QuietStart is < 0 or > 1439 || QuietEnd is < 0 or > 1439 ||
            Tone is not ("soft" or "bell" or "digital")) throw new InvalidDataException("Preferencias de notificación no válidas.");
        Features ??= new(); Contacts ??= new();
        if (Features.Count > Categories.Length || Features.Any(p => !Categories.Contains(p.Key) || p.Value == null) ||
            Contacts.Count > 1024 || Contacts.Any(p => p.Key.Length != 64 || !p.Key.All(Uri.IsHexDigit) || p.Value == null))
            throw new InvalidDataException("Reglas de notificación no válidas.");
        Contacts = Contacts.ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Value);
    }
    public bool IsQuiet(DateTimeOffset now, bool focused = false)
    {
        if (!Enabled || (!WhileFocused && focused) || (DoNotDisturb && (DoNotDisturbUntil == null || now < DoNotDisturbUntil))) return true;
        int minute = now.Hour * 60 + now.Minute;
        return QuietHours && (QuietStart == QuietEnd || (QuietStart < QuietEnd
            ? minute >= QuietStart && minute < QuietEnd : minute >= QuietStart || minute < QuietEnd));
    }
}
