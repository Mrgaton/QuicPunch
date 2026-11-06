namespace QuicNet.Settings;

/// <summary>A public display name, independent of certificates and the Tor pseudonym.</summary>
internal static class LocalUserProfile
{
    public const int MaxNameLength = 64;
    public static string DeviceName { get; set; } = Environment.MachineName;
    public static bool IsValid(string? name) => name != null && name.Length <= MaxNameLength && !name.Any(char.IsControl);
    public static string Normalize(string? name) => IsValid(name) ? name!.Trim() : "";
    public static string Resolve(string? name) => Normalize(name) is { Length: > 0 } custom ? custom : DeviceName;

    public static void Apply(QuicPunch.QuicPunch node, AppPreferences preferences)
    {
        node.CurrentPeer.Name = Resolve(preferences.DisplayName);
        node.InvalidateTokenCache();
    }
}
