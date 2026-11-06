using System.Collections.Concurrent;

namespace QuicNet.Watch.Domain;

public enum ParticipantRole
{
    Viewer = 0,
    Controller = 1,
    Owner = 2
}

public sealed record MediaManifest(
    Guid MediaId,
    string Name,
    long Size,
    string MimeType,
    int ChunkSize,
    Guid SourcePeerId);

public sealed record PlaybackSnapshot(
    Guid RoomId,
    Guid MediaId,
    long Revision,
    bool Paused,
    double PositionSeconds,
    double PlaybackRate,
    long IssuedAtUnixMs,
    long ApplyAtUnixMs);

public sealed class WatchRoom
{
    public Guid LocalPeerId { get; init; }
    public Guid RoomId { get; init; } = Guid.NewGuid();
    public Guid OwnerPeerId { get; init; }
    public string Name { get; set; } = "Sala";
    public MediaManifest? Media { get; set; }
    public PlaybackSnapshot? Playback { get; set; }
    public ConcurrentDictionary<Guid, ParticipantRole> Participants { get; } = new();
}

public sealed record WatchWireMessage(
    string Type,
    Guid RoomId,
    Guid SenderPeerId,
    Guid MediaId = default,
    long Revision = 0,
    bool Paused = true,
    double PositionSeconds = 0,
    double PlaybackRate = 1,
    long IssuedAtUnixMs = 0,
    long ApplyAtUnixMs = 0,
    string? RoomName = null,
    string? MediaName = null,
    long MediaSize = 0,
    string? MimeType = null,
    int ChunkSize = 0,
    string? Role = null,
    Guid TargetPeerId = default,
    string? Error = null);
