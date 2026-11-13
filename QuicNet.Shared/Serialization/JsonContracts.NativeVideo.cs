using System.Text.Json.Serialization;
using QuicNet.Video;

namespace QuicNet.Serialization;

internal sealed record NativeVideoPeerCapabilities(string peerId,string? name,NativeVideoCapabilities? capabilities);
internal sealed record NativeVideoSnapshot(NativeVideoCapabilities local,NativeVideoPeerCapabilities[] peers,NativeVideoState[] streams);
[JsonSerializable(typeof(NativeVideoCapabilities))]
[JsonSerializable(typeof(NativeVideoLayout))]
[JsonSerializable(typeof(NativeVideoState))]
[JsonSerializable(typeof(NativeVideoSnapshot))]
[JsonSerializable(typeof(NativeCamera[]))]
internal partial class SharedJsonContext { }
