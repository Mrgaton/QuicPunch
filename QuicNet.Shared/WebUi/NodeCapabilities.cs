namespace QuicNet.WebUi;

public sealed record CapabilityStatus(bool Available, string? Reason = null);

public sealed class NodeCapabilities
{
    public string Platform { get; set; } = "desktop";
    public CapabilityStatus Peers { get; set; } = new(true);
    public CapabilityStatus Chat { get; set; } = new(true);
    public CapabilityStatus Voice { get; set; } = new(true);
    public CapabilityStatus Video { get; set; } = new(true);
    public CapabilityStatus SpeedTest { get; set; } = new(true);
    public CapabilityStatus Watch { get; set; } = new(true);
    public CapabilityStatus Vpn { get; set; } = new(true);

    public static NodeCapabilities CreateDesktop(bool vpnAvailable = true) => new()
    {
        Platform = "desktop",
        Peers = new(true),
        Chat = new(true),
        Voice = new(true),
        Video = new(true),
        SpeedTest = new(true),
        Watch = new(true),
        Vpn = vpnAvailable ? new(true) : new(false, "Adaptador TUN no disponible en esta máquina")
    };

    public static NodeCapabilities CreateAndroid() => new()
    {
        Platform = "android",
        Peers = new(true),
        Chat = new(true),
        Voice = new(true),
        Video = new(false, "La videollamada integrada espera señalización y transporte entre peers"),
        SpeedTest = new(true),
        Watch = new(false, "Requiere sincronización de medios en Android (Hito 5)"),
        Vpn = new(false, "Fuera de alcance en Android")
    };

    public System.Text.Json.JsonElement ToResponseObject() => System.Text.Json.JsonSerializer.SerializeToElement(
        new NodeCapabilitiesResponse(true, Platform,
            new NodeCapabilityFeatures(Peers, Chat, Voice, Video, SpeedTest, Watch, Vpn)),
        NodeCapabilitiesJsonContext.Default.NodeCapabilitiesResponse);
}

internal sealed record NodeCapabilitiesResponse(bool Success, string Platform, NodeCapabilityFeatures Capabilities);
internal sealed record NodeCapabilityFeatures(CapabilityStatus Peers, CapabilityStatus Chat, CapabilityStatus Voice,
    CapabilityStatus Video, CapabilityStatus Speedtest, CapabilityStatus Watch, CapabilityStatus Vpn);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(NodeCapabilitiesResponse))]
internal partial class NodeCapabilitiesJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }