// Named JSON messages preserve the existing property names and constructor order.
#nullable enable
namespace QuicNet.Serialization;

internal sealed record WebUiServerMessage1(string entry);
internal sealed record WebUiServerMessage2(bool success, string error);
internal sealed record WebUiServerMessage3(bool success, string message);
internal sealed record WebUiServerMessage4(bool success);
internal sealed record WebUiServerMessage5(bool online, string platform, global::System.Text.Json.JsonElement capabilities, global::QuicNet.Serialization.WebUiServerMessage6 node, object[] peers, object[] petitions, string[] logs, global::QuicNet.WebUi.WebUiServer.UserNotification[] notifications, object[] chatMessages, object[] conversations, object[] savedPeers, global::System.Collections.Generic.Dictionary<string, int> disappearingMessages, global::QuicNet.Serialization.WebUiServerMessage7 apps, global::QuicNet.Serialization.WebUiServerMessage9 stun, object[] discoveredPeers, object[] activeCalls, object? incomingCall, object[] quicTelemetry, global::QuicNet.Serialization.WebUiServerMessage10 tor);
internal sealed record WebUiServerMessage6(string peerId, string name, string wanToken, string torToken, bool wanEnabled, bool torEnabled, int port, string[] addresses);
internal sealed record WebUiServerMessage7(global::QuicNet.Serialization.WebUiServerMessage8 chat, global::QuicNet.Serialization.WebUiServerMessage8 voice, global::QuicNet.Serialization.WebUiServerMessage8 lan, global::QuicNet.Serialization.WebUiServerMessage8 clipboard);
internal sealed record WebUiServerMessage8(int active, string protocolId);
internal sealed record WebUiServerMessage9(string[] publicAddresses, object[] exactMappings, ushort[] ports, string portMode, string[] localAddresses, int serverCount, string networkType);
internal sealed record WebUiServerMessage10(bool available, bool running, string onionAddress, bool bootstrapped);
