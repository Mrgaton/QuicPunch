// Named JSON messages preserve the existing property names and constructor order.
#nullable enable
namespace QuicNet.Serialization;

internal sealed record VoiceApiModuleMessage1(global::System.Guid peerId, global::System.Guid requestId, global::QuicNet.Protocols.ScreenTransmissionSettings settings);
internal sealed record VoiceApiModuleMessage2(string peerId, string peerName, int width, int height, byte codecId, int fps);
internal sealed record VoiceApiModuleMessage3(string peerId, string? peerName) { public string? requestId { get; init; } }
internal sealed record VoiceApiModuleMessage4(global::System.Guid peerId, global::System.Text.Json.JsonElement config);
internal sealed record VoiceApiModuleMessage5(string peerId, string signal);
internal sealed record VoiceApiModuleMessage6(bool isLocal, float level, bool speaking);
internal sealed record VoiceApiModuleMessage7(bool isLocal, string peerId, float level, bool speaking);
internal sealed record VoiceApiModuleMessage8(string peerId, string? peerName, int width, int height, byte codecId, byte fps, string kind = "screen");
internal sealed record VoiceApiModuleMessage9(string peerId, string? peerName, uint encoderCodecMask, uint decoderCodecMask) { public uint? decoderHardwarePreferenceMask { get; init; } public bool capabilitiesReceived { get; init; } }
internal sealed record VoiceApiModuleMessage10(string peerId, string peerName, float volume, bool muted, bool outboundMuted, bool screenShareAllowed, uint screenEncoderCodecMask, uint screenDecoderCodecMask) { public long startedAtUnixMs { get; init; } public uint? screenDecoderHardwarePreferenceMask { get; init; } public bool screenCapabilitiesReceived { get; init; } }
internal sealed record VoiceApiModuleMessage11(bool isMuted, bool isDeafened, float masterVolume, bool isTestLoopback, string currentInputDevice, string currentOutputDevice, string backendName, bool captureRunning, bool playbackRunning, string? captureError, string? playbackError, int opusBitrate, int requestedOpusBitrate, bool isScreenSharing, bool isNativeScreenSharing, global::System.Nullable<global::System.Guid> screenShareTargetPeerId, global::QuicNet.Video.NativeScreenProfile? nativeEncoder, global::QuicNet.Video.ScreenEncoderOptions? nativeSettings, global::System.Collections.Generic.List<global::QuicNet.Serialization.VoiceApiModuleMessage10> activeCalls);
internal sealed record VoiceApiModuleMessage12(string reason);
internal sealed record VoiceApiModuleMessage13(string peerId, string peerName, double bufferedAudioMs, float volume, bool muted, bool outboundMuted, bool isRemoteScreenSharing, bool screenShareAllowed, uint screenEncoderCodecMask, uint screenDecoderCodecMask) { public long startedAtUnixMs { get; init; } public uint? screenDecoderHardwarePreferenceMask { get; init; } public bool screenCapabilitiesReceived { get; init; } }
internal sealed record VoiceApiModuleMessage14(bool isMuted, bool isDeafened, float masterVolume, bool isTestLoopback, string currentInputDevice, string currentOutputDevice, string backendName, bool captureRunning, bool playbackRunning, string? captureError, string? playbackError, int opusBitrate, int requestedOpusBitrate, bool isScreenSharing, bool isNativeScreenSharing, global::System.Nullable<global::System.Guid> screenShareTargetPeerId, global::QuicNet.Video.NativeScreenProfile? nativeEncoder, global::QuicNet.Video.ScreenEncoderOptions? nativeSettings, global::System.Collections.Generic.List<global::QuicNet.Serialization.VoiceApiModuleMessage13> activeCalls);
internal sealed record VoiceApiModuleMessage15(string id, string name, bool isDefault);
internal sealed record VoiceApiModuleMessage16(global::System.Collections.Generic.List<global::QuicNet.Serialization.VoiceApiModuleMessage15> inputs, global::System.Collections.Generic.List<global::QuicNet.Serialization.VoiceApiModuleMessage15> outputs, string currentInput, string currentOutput);
internal sealed record VoiceApiModuleMessage17(string id, string name, global::System.Nullable<bool> gpuAccelerated, global::System.Nullable<int> maxFps, string recommendedBitrate, string description);
internal sealed record VoiceApiModuleMessage18(string gpuName, bool nativeCaptureAvailable, global::QuicNet.Video.ScreenEncoderCapability[] nativeEncoders, global::System.Collections.Generic.IReadOnlyList<global::QuicNet.Video.ScreenCaptureSource> sources, global::System.Collections.Generic.IReadOnlyList<global::QuicNet.Video.CaptureDisplay> displays, int[] frameRates, int[] resolutionHeights, int maxWidth, int maxHeight, string audioBackend, global::System.Nullable<bool> hardwareSupported, global::System.Nullable<int> maxSupportedFps, int maxSupportedBitrate, global::System.Collections.Generic.List<object> encoders, global::QuicNet.Video.FfmpegInstallationStatus ffmpeg) { public int graphicsAdapterCount { get; init; } = global::QuicNet.Video.ScreenCaptureInventory.GraphicsAdapterCount(); public bool systemAudioExcludesCall { get; init; } = global::QuicNet.Video.WindowsSystemAudioCapture.IsSupported; }
internal sealed record VoiceApiModuleMessage19(bool success, uint encoderCodecMask, uint decoderCodecMask) { public uint? decoderHardwarePreferenceMask { get; init; } }
internal sealed record VoiceApiModuleMessage20(bool success);
internal sealed record VoiceApiModuleMessage21(string error) { public string? audioSessionId { get; init; } }
internal sealed record VoiceApiModuleMessage22(global::System.Collections.Generic.IReadOnlyList<global::QuicNet.Video.ScreenCaptureSource> sources);
internal sealed record VoiceApiModuleMessage23(bool success, bool supported);
internal sealed record VoiceApiModuleMessage24(bool success, global::QuicNet.Video.NativeScreenProfile profile);
internal sealed record VoiceApiModuleMessage25(bool success, string type, string deviceId, string? error);
internal sealed record VoiceApiModuleMessage26(bool success, bool isMuted);
internal sealed record VoiceApiModuleMessage27(bool success, bool isDeafened);
internal sealed record VoiceApiModuleMessage28(bool success, float masterVolume);
internal sealed record VoiceApiModuleMessage29(bool success, bool isTestLoopback, string? error);
internal sealed record VoiceApiModuleMessage30(bool success, bool hungUp);
internal sealed record VoiceApiModuleMessage31(bool success, string error);
internal sealed record VoiceApiModuleMessage32(bool success, int bitrate, int requestedBitrate);
internal sealed record VoiceApiModuleMessage33(bool success, bool isSharing);
internal sealed record VoiceApiModuleMessage34(bool success, string peerId, bool allowed);
