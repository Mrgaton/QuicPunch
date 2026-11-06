// Named JSON messages preserve the existing property names and constructor order.
#nullable enable
namespace QuicNet.Serialization;

internal sealed record ChatHandlerMessage1(string type);
internal sealed record ChatHandlerMessage2(string type, string msgId, string status);
internal sealed record ChatHandlerMessage3(string type, string msgId, string sender, string content, string timestamp, global::System.Nullable<long> expiresAtUnixMs);
internal sealed record ChatHandlerMessage4(string type, bool isTyping);
internal sealed record ChatHandlerMessage5(string type, string msgId, string sender, string fileId, string name, long size, string mime, string sha256, string timestamp);
internal sealed record ChatHandlerMessage6(string type, string fileId, long offset);
internal sealed record ChatHandlerMessage7(string type, string fileId);
internal sealed record ChatHandlerMessage8(string type, global::System.Guid fileId, string sha256);
internal sealed record ChatHandlerMessage9(string type, string msgId, string sender, string content, int disappearAfterSeconds, string timestamp);
internal sealed record ChatHandlerMessage10(string content, int disappearAfterSeconds);
