// Named JSON messages preserve the existing property names and constructor order.
#nullable enable
namespace QuicNet.Serialization;

internal sealed record WatchWebServerMessage1(string message);
internal sealed record WatchWebServerMessage2(global::System.Guid mediaId, long cached, long total);
internal sealed record WatchWebServerMessage3(string error);
internal sealed record WatchWebServerMessage4(bool success, global::System.Guid peerId);
internal sealed record WatchWebServerMessage5(bool success);
internal sealed record WatchWebServerMessage6(bool success, bool cancelled);
internal sealed record WatchWebServerMessage7(bool success, global::QuicNet.Watch.Domain.MediaManifest media);
internal sealed record WatchWebServerMessage8(string csrf, global::QuicNet.Serialization.WatchWebServerMessage9 local, global::QuicNet.Serialization.WatchWebServerMessage10[] peers, global::QuicNet.Serialization.WatchWebServerMessage11? room, bool canPickMedia = true, bool canSelectPath = true);
internal sealed record WatchWebServerMessage9(global::System.Guid peerId, string name, string token);
internal sealed record WatchWebServerMessage10(global::System.Guid peerId, string? name, bool connected, bool inRoom, string? role);
internal sealed record WatchWebServerMessage11(global::System.Guid roomId, string Name, global::System.Guid OwnerPeerId, bool isOwner, string myRole, global::QuicNet.Serialization.WatchWebServerMessage12[] participants, global::QuicNet.Watch.Domain.MediaManifest? Media, global::QuicNet.Watch.Domain.PlaybackSnapshot? Playback);
internal sealed record WatchWebServerMessage12(global::System.Guid peerId, string role);
