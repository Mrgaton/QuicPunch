using System.Text.Json.Serialization;

namespace QuicNet.Serialization;

internal sealed record ChatActionUpdate(string msgId, string kind, string key, bool active, long revision);
internal sealed record ChatActionFrame(string type, ChatActionUpdate[] updates);
internal sealed record ChatActionState(string msgId, string kind, string key, bool active, long revision, bool mine);
internal sealed record ChatSocialSnapshot(bool success, string peerId, ChatActionState[] states, ChatApiModuleMessage16[] pins);

[JsonSerializable(typeof(ChatActionFrame))]
[JsonSerializable(typeof(ChatActionUpdate[]))]
[JsonSerializable(typeof(ChatSocialSnapshot))]
internal partial class SharedJsonContext : JsonSerializerContext { }
