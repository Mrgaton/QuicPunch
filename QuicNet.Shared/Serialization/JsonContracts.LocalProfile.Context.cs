using System.Text.Json.Serialization;
namespace QuicNet.Serialization;

[JsonSerializable(typeof(global::QuicNet.Protocols.PeerAvatarMessage))]
[JsonSerializable(typeof(global::QuicNet.WebUi.Modules.LocalProfile))]
[JsonSerializable(typeof(global::QuicNet.WebUi.Modules.ProfileError))]
internal partial class SharedJsonContext : JsonSerializerContext { }
