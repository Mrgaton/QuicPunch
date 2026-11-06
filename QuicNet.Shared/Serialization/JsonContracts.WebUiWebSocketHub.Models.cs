// Named JSON messages preserve the existing property names and constructor order.
#nullable enable
namespace QuicNet.Serialization;

internal sealed record WebUiWebSocketHubMessage1(string type, string clientId);
internal sealed record WebUiWebSocketHubMessage2(string type, object data);
internal sealed record WebSocketReply(string type, string id, bool success, object? data = null, string? error = null);
