// Named JSON messages preserve the existing property names and constructor order.
#nullable enable
namespace QuicNet.Serialization;

internal sealed record ChatApiModuleMessage1(string peerId, string msgId, string sender, string senderPubKey, string message, string time, bool isMe, bool isConfirmed, bool isGlobal, global::System.Nullable<long> expiresAtUnixMs) { public long timestampMs { get; init; } }
internal sealed record ChatApiModuleMessage2(string peerId, string msgId, string sender, string message, string time, bool isMe, bool isConfirmed, bool viaNostr, global::System.Nullable<long> expiresAtUnixMs) { public long timestampMs { get; init; } }
internal sealed record ChatApiModuleMessage3(string msgId);
internal sealed record ChatApiModuleMessage4(string peerId, string peerName, bool isTyping);
internal sealed record ChatApiModuleMessage5(string peerId);
internal sealed record ChatApiModuleMessage6(string peerId, string protocol);
internal sealed record ChatApiModuleMessage7(string[] msgIds);
internal sealed record ChatApiModuleMessage8(string type, string fileId, string name, long size, string sha256, string mime);
internal sealed record ChatApiModuleMessage9(string peerId, string msgId, string sender, string message, string time, bool isMe, bool isConfirmed, global::System.Nullable<long> expiresAtUnixMs) { public long timestampMs { get; init; } }
internal sealed record ChatApiModuleMessage10(string peerId, string fileId, bool isUpload, long received, long total, int percent);
internal sealed record ChatApiModuleMessage11(string peerId, string fileId, long total);
internal sealed record ChatApiModuleMessage12(string peerId, string fileId);
internal sealed record ChatApiModuleMessage13(string peerId, string fileId, string name, string path);
internal sealed record ChatApiModuleMessage14(string peerId, string fileId, string error);
internal sealed record ChatApiModuleMessage15(global::System.Collections.Generic.IEnumerable<global::QuicNet.Serialization.ChatApiModuleMessage16> messages, string? nextBefore) { public ChatDownloadStatus[] downloads { get; init; } = []; public ChatApiModuleMessage11[] uploads { get; init; } = []; }
internal sealed record ChatApiModuleMessage16(string peerId, string msgId, string sender, string message, long timestampMs, string time, bool isMe, bool isConfirmed, bool isGlobal, global::System.Nullable<long> expiresAtUnixMs) { public string deliveryState { get; init; } = "sent"; }
internal sealed record ChatApiModuleMessage17(bool success, string peerId, int seconds);
internal sealed record ChatApiModuleMessage18(bool success, string error);
internal sealed record ChatApiModuleMessage19(string peerId, string msgId, string sender, string message, string time, bool isMe, bool isConfirmed, bool isGlobal, global::System.Nullable<long> expiresAtUnixMs) { public long timestampMs { get; init; } }
internal sealed record ChatApiModuleMessage20(bool success, string msgId, bool isGlobal);
internal sealed record ChatApiModuleMessage21(bool success, string msgId, bool viaNostr);
internal sealed record ChatApiModuleMessage22(string type, string fileId, string name, long size, string sha256, string mime, bool isMe, string path);
internal sealed record ChatApiModuleMessage23(bool success, string? error, string msgId, string fileId, string name, long size);
internal sealed record ChatApiModuleMessage24(bool success, bool cached, bool inProgress, bool started, string? path) { public ChatDownloadStatus? download { get; init; } }
internal sealed record ChatApiModuleMessage25(bool success);
internal sealed record ChatOutboxResponse(bool success, ChatApiModuleMessage16 message);
internal sealed record ChatMessageDeleted(string peerId, string msgId);
internal sealed record ChatDownloadStatus(string peerId, string fileId, string name, long received, long total, int percent, string status, string? path);
