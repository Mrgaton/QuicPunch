using System.Text.Json.Serialization;

namespace QuicPunch.Discovery;

public sealed partial class PeerDiscoveryManager
{
    // Preserve the existing discovery JSON while avoiding reflection in trimmed Android/AOT.
    internal sealed record WanAnnouncement(string wan, string? name, string certPubKey, string sig, string presence);
    internal sealed record TorAnnouncement(string tor, string? name, string certPubKey, string sig, string presence);

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(WanAnnouncement))]
    [JsonSerializable(typeof(TorAnnouncement))]
    internal partial class AnnouncementJsonContext : JsonSerializerContext { }
}
