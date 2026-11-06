// Named JSON messages preserve the existing property names and constructor order.
#nullable enable
namespace QuicNet.Serialization;

internal sealed record SpeedTestApiModuleMessage1(string peerId, string peerName, string mode, double uploadMbps, double downloadMbps, long totalBytes, long bytesUploaded, long bytesDownloaded, double durationSeconds, double rttMs, global::System.Text.Json.JsonElement quality, global::System.Text.Json.JsonElement baseline, global::System.Text.Json.JsonElement stages, string testId = "", string state = "completed", string? error = null, global::System.DateTime? startedAt = null);
internal sealed record SpeedTestApiModuleMessage2(string peerId, string phase, global::System.Text.Json.JsonElement baseline);
internal sealed record SpeedTestApiModuleMessage3(string peerId, string peerName, string direction, long bytesTransferred, double currentMbps, double averageMbps, double elapsedSeconds, double totalSeconds, double rttMs, int sampleWindowMs);
internal sealed record SpeedTestApiModuleMessage4(string peerId, string error, SpeedTestApiModuleMessage1? result);
internal sealed record SpeedTestApiModuleMessage5(bool success, string error);
internal sealed record SpeedTestApiModuleMessage6(string peerId, string peerName, string mode, int duration, bool isTor, int sampleWindowMs);
internal sealed record SpeedTestApiModuleMessage7(bool success, global::System.Guid peerId, string mode, int duration);
internal sealed record SpeedTestApiModuleMessage8(bool success);
internal sealed record SpeedTestApiModuleMessage9(bool isRunning, global::QuicNet.Serialization.SpeedTestApiModuleMessage10? active, object? lastResult, string? error, SpeedTestApiModuleMessage1[] history);
internal sealed record SpeedTestApiModuleMessage10(string peerId, string peerName, string mode, string phase, int durationSeconds, double elapsedSeconds, double lastUploadMbps, double lastDownloadMbps, long bytesUploaded, long bytesDownloaded, double rttMs, int sampleWindowMs, global::System.Text.Json.JsonElement baseline, global::System.Text.Json.JsonElement stages);
