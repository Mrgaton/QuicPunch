// Named JSON messages preserve the existing property names and constructor order.
#nullable enable
namespace QuicNet.Serialization;

internal sealed record PeerApiModuleMessage1(string peerId, string? name);
internal sealed record PeerApiModuleMessage2(string requestId, string peerId, string peerName, string protocolId, string protocolName, bool notify = true);
internal sealed record PeerApiModuleMessage3(bool success, string error);
internal sealed record PeerApiModuleMessage4(bool success, string profile);
internal sealed record PeerApiModuleMessage5(bool success, string permission, bool allowed);
internal sealed record PeerApiModuleMessage6(bool success, string type, bool enabled, bool running);
internal sealed record PeerApiModuleMessage7(bool success, bool trusted);
internal sealed record PeerApiModuleMessage8(bool success, bool connected);
internal sealed record PeerApiModuleMessage9(bool success, bool saved, bool autoConnect);
internal sealed record PeerApiModuleMessage10(bool success, bool saved);
internal sealed record PeerApiModuleMessage11(bool success);
internal sealed record PeerApiModuleMessage12(bool success, bool autoConnect);
internal sealed record PeerApiModuleMessage13(bool success, bool removed);
internal sealed record PeerApiModuleMessage14(bool success, int removedCount);
internal sealed record PeerApiModuleMessage15(bool success, int savedCount);
internal sealed record PeerApiModuleMessage16(bool success, bool connectionStarted);
internal sealed record PeerApiModuleMessage17(bool success, int count);
internal sealed record PeerApiModuleMessage18(bool success, bool requiresAdmin, string error);
internal sealed record PeerApiModuleMessage19(bool success, bool Reused, bool InProgress);
internal sealed record PeerApiModuleMessage20(int port);
internal sealed record PeerApiModuleMessage21(bool success, bool starting);
internal sealed record PeerApiModuleMessage22();
internal sealed record PeerApiModuleMessage23(string? onion);
internal sealed record PeerApiModuleMessage24(bool success, int port);

internal sealed record ConnectionTokenTransportError(bool success, string error, string requiredTransport);
