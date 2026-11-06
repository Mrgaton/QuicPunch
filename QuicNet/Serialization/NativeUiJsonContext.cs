using System.Text.Json.Serialization;
namespace QuicNet.Serialization;

internal sealed record VpnPeerDefaultResponse(string certHash, string virtualAddress, bool autoAccept, bool autoConnect, string[] accessibleDestinations);
internal sealed record VpnConnectionAttemptResponse(Guid peerId, string state, int failures, string? error);
internal sealed record VpnOrchestrationResponse(long appliedRevision, string state, VpnConnectionAttemptResponse[] peers);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LanPeerSummaryResponse))]
[JsonSerializable(typeof(VpnPeerDefaultResponse[]))]
[JsonSerializable(typeof(VpnOrchestrationResponse))]
[JsonSerializable(typeof(QuicNet.WindowStateConfig))]
internal partial class NativeUiJsonContext : JsonSerializerContext { }

internal sealed record LanPeerSummaryResponse(string peerId, string? peerName, string? name, string virtualIp, string assignedIp,
    string mode, string status, TimeSpan? rttMs, long txPackets, long rxPackets, long txBytes, long rxBytes,
    long droppedPackets, string connectedAt, string lastActivity);
