#nullable enable
using System.Text.Json.Serialization;
namespace QuicNet.Serialization;

[JsonSerializable(typeof(global::QuicNet.Serialization.WebUiWebSocketHubMessage1))]
[JsonSerializable(typeof(global::QuicNet.Serialization.WebUiWebSocketHubMessage2))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(global::QuicNet.Serialization.WebSocketReply))]
[JsonSerializable(typeof(global::QuicNet.Updates.UpdateStatus))]
internal partial class SharedJsonContext : JsonSerializerContext { }
