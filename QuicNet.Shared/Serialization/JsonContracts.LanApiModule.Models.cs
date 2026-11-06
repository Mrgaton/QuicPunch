// Named JSON messages preserve the existing property names and constructor order.
#nullable enable
namespace QuicNet.Serialization;

internal sealed record LanApiModuleMessage1(bool success, global::System.Text.Json.JsonElement configuration, string path, string? error, object orchestration, object peerDefaults);
internal sealed record LanApiModuleMessage2(bool success);
internal sealed record LanApiModuleMessage3(bool success, string error);
internal sealed record LanApiModuleMessage4(bool success, bool desiredEnabled, bool active, string ip, string? error);
internal sealed record LanApiModuleMessage5(bool success, bool requiresAdmin, string error);
internal sealed record LanApiModuleMessage6(bool success, bool active, string ip, string subnetMask, int mtu, string status);
internal sealed record LanApiModuleMessage7(bool success, bool active);
internal sealed record LanApiModuleMessage8(bool success, bool enabled, bool active, string? error);
internal sealed record LanApiModuleMessage9(bool success, string error, bool requiresAdmin, bool enabled, bool active);
internal sealed record LanApiModuleMessage10(bool success, bool enabled, bool active, bool autoAssign, string ip, string subnetMask, int mtu, string addressMode);
internal sealed record LanApiModuleMessage11(bool success, bool disconnected);
internal sealed record LanApiModuleMessage12(bool success, double rttMs, string? error);
internal sealed record LanApiModuleMessage13(bool success, string ip);
