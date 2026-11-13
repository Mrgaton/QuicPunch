using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuicNet.Serialization;

// Protocol and settings contracts are generated at build time. Unknown types fail explicitly.
internal static class SharedJson
{
    internal static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = SharedJsonContext.Default };
    internal static JsonSerializerOptions WithOptions(JsonSerializerOptions? options) =>
        options == null ? Options : new(options) { TypeInfoResolver = SharedJsonContext.Default };
}

[JsonSerializable(typeof(QuicNet.Protocols.SpeedTestHandler.ConfigPayload))]
[JsonSerializable(typeof(QuicNet.Protocols.SpeedTestHandler.ProgressPayload))]
[JsonSerializable(typeof(QuicNet.Protocols.SpeedTestHandler.CompletePayload))]
[JsonSerializable(typeof(QuicNet.Protocols.SpeedTestHandler.SummaryPayload))]
[JsonSerializable(typeof(QuicNet.Protocols.ScreenControlRequest))]
[JsonSerializable(typeof(QuicNet.Protocols.ScreenControlReply))]
[JsonSerializable(typeof(QuicNet.Protocols.ScreenControlCapabilities))]
[JsonSerializable(typeof(QuicNet.Settings.AppPreferences))]
[JsonSerializable(typeof(QuicNet.Vpn.VpnConfiguration))]
[JsonSerializable(typeof(QuicNet.Watch.Domain.WatchWireMessage))]
internal partial class SharedJsonContext : JsonSerializerContext { }

internal sealed record ScreenDecoderProbeMessage(Guid requestId, string codec, string? description, int width, int height);
internal sealed record ScreenDecoderDiagnostic(string codec, int width, int height, string? mode, string reason);
internal sealed record ScreenDecoderProbeReplyMessage(Guid requestId, bool supported, ScreenDecoderDiagnostic? diagnostic = null);
[JsonSerializable(typeof(ScreenDecoderDiagnostic))]
[JsonSerializable(typeof(ScreenDecoderProbeMessage))]
[JsonSerializable(typeof(ScreenDecoderProbeReplyMessage))]
internal partial class SharedJsonContext { }
internal sealed record ScreenFrameMetadata(string c, string? d, long t, long g, int w, int h);
internal sealed record Av1FrameMetadata(string c, long t, long g, int w, int h);
[JsonSerializable(typeof(ScreenFrameMetadata))]
[JsonSerializable(typeof(Av1FrameMetadata))]
internal partial class SharedJsonContext { }
internal sealed record WatchBroadcastMessage(string type, object? data);
[JsonSerializable(typeof(WatchBroadcastMessage))]
internal partial class SharedJsonContext { }
