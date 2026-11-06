using System.Collections.Concurrent;
using QuicPunch;
using QuicNet.Watch.Domain;
using QuicNet.Watch.Protocols;

namespace QuicNet.Watch.Services;

public sealed class RoomCoordinator
{
    private readonly WatchControlHandler _control;
    private readonly MediaLibrary _mediaLibrary;
    private readonly Guid _defaultLocalPeerId;
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<Guid, double> _clockOffsetsMs = new();
    private WatchRoom? _room;

    public event Action? RoomChanged;
    public event Action<PlaybackSnapshot>? PlaybackChanged;
    public event Action<MediaManifest>? MediaChanged;
    public event Action<string>? Error;

    public RoomCoordinator(WatchControlHandler control, MediaLibrary mediaLibrary, Guid localPeerId)
    {
        _control = control;
        _mediaLibrary = mediaLibrary;
        _defaultLocalPeerId = localPeerId;
        _mediaLibrary.RemoteReadAllowed = CanServeRemoteMedia;
        _control.MessageReceived += OnMessage;
        _control.PeerConnected += peer => _ = OnPeerConnectedAsync(peer);
    }

    public Guid LocalPeerId { get { lock (_sync) return _room?.LocalPeerId ?? _defaultLocalPeerId; } }

    public WatchRoom? Snapshot()
    {
        lock (_sync) return _room;
    }

    internal bool CanServeRemoteMedia(Guid peerId, Guid mediaId)
    {
        lock (_sync) return _room is { } room && room.OwnerPeerId == room.LocalPeerId &&
            room.Media?.MediaId == mediaId && room.Participants.ContainsKey(peerId) &&
            _control.LocalIdFor(peerId, _defaultLocalPeerId) == room.LocalPeerId;
    }

    public async Task LeaveAsync(CancellationToken ct)
    {
        WatchRoom? room;
        lock (_sync) { room = _room; _room = null; _mediaLibrary.KeepRemoteMedia(null); }
        if (room == null) return;
        RoomChanged?.Invoke();
        if (room.OwnerPeerId == room.LocalPeerId)
            await BroadcastToParticipantsAsync(room, new WatchWireMessage("room_closed", room.RoomId, room.LocalPeerId), ct).ConfigureAwait(false);
        else
        {
            if (_control.LocalIdFor(room.OwnerPeerId, _defaultLocalPeerId) != room.LocalPeerId) return;
            try { await _control.SendAsync(room.OwnerPeerId, new WatchWireMessage("room_leave", room.RoomId, room.LocalPeerId), ct).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Error?.Invoke(ex.Message); }
        }
    }

    public async Task<WatchRoom> CreateRoomAsync(string name, IEnumerable<Guid> participantIds, CancellationToken ct)
    {
        Guid[] participants = participantIds.Distinct().Where(id => id != Guid.Empty).ToArray();
        Guid[] localIds = participants.Select(id => _control.LocalIdFor(id, _defaultLocalPeerId)).Distinct().ToArray();
        if (localIds.Length > 1) throw new InvalidOperationException("Crea salas separadas para WAN y Tor.");
        Guid localId = localIds.FirstOrDefault(_defaultLocalPeerId);
        var room = new WatchRoom
        {
            RoomId = Guid.NewGuid(),
            OwnerPeerId = localId,
            LocalPeerId = localId,
            Name = string.IsNullOrWhiteSpace(name) ? "Sala" : name.Trim()
        };
        room.Participants[localId] = ParticipantRole.Owner;
        foreach (Guid peerId in participants.Where(id => id != localId))
            room.Participants[peerId] = ParticipantRole.Viewer;
        lock (_sync)
        {
            if (_room != null) throw new InvalidOperationException("Sal de la sala actual antes de crear otra.");
            _room = room;
        }
        RoomChanged?.Invoke();

        foreach (Guid peerId in room.Participants.Keys.Where(id => id != room.LocalPeerId))
            await SendInviteAsync(peerId, room, ct).ConfigureAwait(false);
        return room;
    }

    public async Task SetMediaAsync(MediaManifest manifest, CancellationToken ct)
    {
        WatchRoom room = RequireOwnedRoom();
        lock (_sync)
        {
            room.Media = manifest;
            room.Playback = BuildSnapshot(room, manifest.MediaId, paused: true, position: 0, rate: 1);
        }
        await BroadcastStateAsync(room, "media", ct).ConfigureAwait(false);
        MediaChanged?.Invoke(manifest);
        PlaybackChanged?.Invoke(room.Playback!);
        RoomChanged?.Invoke();
    }

    public async Task RequestPlaybackAsync(bool paused, double positionSeconds, double rate, CancellationToken ct)
    {
        if (!double.IsFinite(positionSeconds) || !double.IsFinite(rate))
            throw new ArgumentException("La posición y la velocidad deben ser números válidos.");
        WatchRoom room = RequireRoom();
        ParticipantRole role = room.Participants.GetValueOrDefault(room.LocalPeerId, ParticipantRole.Viewer);
        if (role == ParticipantRole.Viewer) throw new UnauthorizedAccessException("No tienes permiso para controlar la reproducción.");

        if (room.OwnerPeerId == room.LocalPeerId)
        {
            await CommitPlaybackAsync(room, paused, positionSeconds, rate, ct).ConfigureAwait(false);
            return;
        }

        var request = new WatchWireMessage(
            "control_request",
            room.RoomId,
            room.LocalPeerId,
            room.Media?.MediaId ?? Guid.Empty,
            Paused: paused,
            PositionSeconds: Math.Max(0, positionSeconds),
            PlaybackRate: Math.Clamp(rate, 0.25, 4));
        if (_control.LocalIdFor(room.OwnerPeerId, _defaultLocalPeerId) != room.LocalPeerId)
            throw new InvalidOperationException("El transporte de la sala ha cambiado; vuelve a entrar.");
        if (!await _control.SendAsync(room.OwnerPeerId, request, ct).ConfigureAwait(false))
            throw new InvalidOperationException("El coordinador de la sala no está conectado.");
    }

    public async Task SetPermissionAsync(Guid peerId, ParticipantRole role, CancellationToken ct)
    {
        WatchRoom room = RequireOwnedRoom();
        if (peerId == room.LocalPeerId || role == ParticipantRole.Owner || !room.Participants.ContainsKey(peerId))
            throw new ArgumentException("Participante o permiso no válido.");
        room.Participants[peerId] = role;
        var message = new WatchWireMessage(
            "permission",
            room.RoomId,
            room.LocalPeerId,
            Role: role.ToString(),
            TargetPeerId: peerId);
        await BroadcastToParticipantsAsync(room, message, ct).ConfigureAwait(false);
        RoomChanged?.Invoke();
    }

    public async Task InviteAsync(Guid peerId, CancellationToken ct)
    {
        WatchRoom room = RequireOwnedRoom();
        if (_control.LocalIdFor(peerId, _defaultLocalPeerId) != room.LocalPeerId)
            throw new InvalidOperationException("Crea salas separadas para WAN y Tor.");
        if (peerId == Guid.Empty || peerId == room.LocalPeerId) throw new ArgumentException("Participante no válido.");
        room.Participants.TryAdd(peerId, ParticipantRole.Viewer);
        await SendInviteAsync(peerId, room, ct).ConfigureAwait(false);
        RoomChanged?.Invoke();
    }

    private async Task CommitPlaybackAsync(WatchRoom room, bool paused, double positionSeconds, double rate, CancellationToken ct)
    {
        if (!double.IsFinite(positionSeconds) || !double.IsFinite(rate)) return;
        PlaybackSnapshot snapshot;
        lock (_sync)
        {
            snapshot = BuildSnapshot(room, room.Media?.MediaId ?? Guid.Empty, paused, positionSeconds, rate);
            room.Playback = snapshot;
        }
        await BroadcastToParticipantsAsync(room, ToWire("state_commit", room, snapshot), ct).ConfigureAwait(false);
        PlaybackChanged?.Invoke(snapshot);
        RoomChanged?.Invoke();
    }

    private PlaybackSnapshot BuildSnapshot(WatchRoom room, Guid mediaId, bool paused, double position, double rate)
    {
        long revision = (room.Playback?.Revision ?? 0) + 1;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new PlaybackSnapshot(
            room.RoomId,
            mediaId,
            revision,
            paused,
            Math.Max(0, position),
            Math.Clamp(rate, 0.25, 4),
            now,
            now + 400);
    }

    private async Task BroadcastStateAsync(WatchRoom room, string type, CancellationToken ct)
    {
        PlaybackSnapshot snapshot = room.Playback ?? BuildSnapshot(room, room.Media?.MediaId ?? Guid.Empty, true, 0, 1);
        await BroadcastToParticipantsAsync(room, ToWire(type, room, snapshot), ct).ConfigureAwait(false);
    }

    private Task BroadcastToParticipantsAsync(WatchRoom room, WatchWireMessage message, CancellationToken ct) =>
        Task.WhenAll(room.Participants.Keys
            .Where(id => id != room.LocalPeerId)
            .Select(async id =>
            {
                try
                {
                    if (_control.LocalIdFor(id, _defaultLocalPeerId) != room.LocalPeerId)
                        throw new InvalidOperationException("El transporte de la sala ha cambiado; vuelve a entrar.");
                    await _control.SendAsync(id, message, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { Error?.Invoke(ex.Message); }
            }));

    private WatchWireMessage ToWire(string type, WatchRoom room, PlaybackSnapshot snapshot, Guid target = default)
    {
        MediaManifest? media = room.Media;
        return new WatchWireMessage(
            type,
            room.RoomId,
            room.LocalPeerId,
            snapshot.MediaId,
            snapshot.Revision,
            snapshot.Paused,
            snapshot.PositionSeconds,
            snapshot.PlaybackRate,
            snapshot.IssuedAtUnixMs,
            snapshot.ApplyAtUnixMs,
            room.Name,
            media?.Name,
            media?.Size ?? 0,
            media?.MimeType,
            media?.ChunkSize ?? 0,
            target != Guid.Empty ? room.Participants.GetValueOrDefault(target).ToString() : null,
            target);
    }

    private async Task SendInviteAsync(Guid peerId, WatchRoom room, CancellationToken ct)
    {
        if (_control.LocalIdFor(peerId, _defaultLocalPeerId) != room.LocalPeerId)
            throw new InvalidOperationException("Crea salas separadas para WAN y Tor.");
        PlaybackSnapshot snapshot = room.Playback ?? BuildSnapshot(room, room.Media?.MediaId ?? Guid.Empty, true, 0, 1);
        await _control.SendAsync(peerId, ToWire("room_invite", room, snapshot, peerId), ct).ConfigureAwait(false);
    }

    private async Task OnPeerConnectedAsync(PeerInfo peer)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _control.SendAsync(peer.Id, new WatchWireMessage("clock_ping", Guid.Empty, _control.LocalIdFor(peer, _defaultLocalPeerId), IssuedAtUnixMs: now)).ConfigureAwait(false);
        WatchRoom? room = Snapshot();
        if (room != null && room.OwnerPeerId == room.LocalPeerId && room.Participants.ContainsKey(peer.Id))
            await SendInviteAsync(peer.Id, room, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnMessage(PeerInfo peer, WatchWireMessage message) => _ = HandleMessageAsync(peer, message);

    internal async Task HandleMessageAsync(PeerInfo peer, WatchWireMessage message)
    {
        try
        {
            if (message.SenderPeerId != peer.Id) return;
            if (message.Type is not ("clock_ping" or "clock_pong"))
                lock (_sync)
                    if (_room != null && _room.LocalPeerId != _control.LocalIdFor(peer, _defaultLocalPeerId)) return;
            switch (message.Type)
            {
                case "room_closed":
                {
                    lock (_sync)
                    {
                        if (_room == null || _room.RoomId != message.RoomId || _room.OwnerPeerId != peer.Id) return;
                        _room = null;
                        _mediaLibrary.KeepRemoteMedia(null);
                    }
                    RoomChanged?.Invoke();
                    return;
                }
                case "room_leave":
                {
                    lock (_sync)
                    {
                        if (_room == null || _room.RoomId != message.RoomId || _room.OwnerPeerId != _room.LocalPeerId || peer.Id == _room.LocalPeerId) return;
                        if (!_room.Participants.TryRemove(peer.Id, out _)) return;
                    }
                    RoomChanged?.Invoke();
                    return;
                }
                case "clock_ping":
                    await _control.SendAsync(peer.Id, message with
                    {
                        Type = "clock_pong",
                        SenderPeerId = _control.LocalIdFor(peer, _defaultLocalPeerId),
                        ApplyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }).ConfigureAwait(false);
                    return;
                case "clock_pong":
                {
                    long received = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    double offset = message.ApplyAtUnixMs - ((message.IssuedAtUnixMs + received) / 2.0);
                    _clockOffsetsMs.AddOrUpdate(peer.Id, offset, (_, old) => old * 0.7 + offset * 0.3);
                    WatchRoom? active = Snapshot();
                    if (active?.OwnerPeerId == peer.Id)
                        await _control.SendAsync(peer.Id, new WatchWireMessage("state_request", active.RoomId, active.LocalPeerId)).ConfigureAwait(false);
                    return;
                }
                case "state_request":
                {
                    WatchRoom room = RequireOwnedRoom();
                    if (message.RoomId != room.RoomId || !room.Participants.ContainsKey(peer.Id)) return;
                    PlaybackSnapshot snapshot = room.Playback ?? BuildSnapshot(room, room.Media?.MediaId ?? Guid.Empty, true, 0, 1);
                    await _control.SendAsync(peer.Id, ToWire("state_sync", room, snapshot, peer.Id)).ConfigureAwait(false);
                    return;
                }
                case "room_invite":
                case "media":
                    AcceptRoomState(peer, message);
                    return;
                case "permission":
                {
                    WatchRoom room = RequireRoom();
                    if (peer.Id != room.OwnerPeerId || message.RoomId != room.RoomId) return;
                    if (Enum.TryParse<ParticipantRole>(message.Role, true, out var role))
                        room.Participants[message.TargetPeerId] = role;
                    RoomChanged?.Invoke();
                    return;
                }
                case "control_request":
                {
                    WatchRoom room = RequireOwnedRoom();
                    if (message.RoomId != room.RoomId) return;
                    ParticipantRole role = room.Participants.GetValueOrDefault(peer.Id, ParticipantRole.Viewer);
                    if (role is ParticipantRole.Controller or ParticipantRole.Owner)
                        await CommitPlaybackAsync(room, message.Paused, message.PositionSeconds, message.PlaybackRate, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                case "state_commit":
                case "state_sync":
                {
                    WatchRoom room = RequireRoom();
                    if (peer.Id != room.OwnerPeerId || message.RoomId != room.RoomId) return;
                    if (message.Type == "state_commit" && message.Revision <= (room.Playback?.Revision ?? 0)) return;
                    if (message.Type == "state_sync" && message.Revision < (room.Playback?.Revision ?? 0)) return;
                    if (!double.IsFinite(message.PositionSeconds) || message.PositionSeconds < 0 ||
                        !double.IsFinite(message.PlaybackRate) || message.PlaybackRate <= 0) return;
                    MediaManifest? newMedia = UpdateRemoteMedia(room, peer, message);
                    double offset = _clockOffsetsMs.GetValueOrDefault(peer.Id);
                    var snapshot = new PlaybackSnapshot(
                        room.RoomId,
                        message.MediaId,
                        message.Revision,
                        message.Paused,
                        message.PositionSeconds,
                        message.PlaybackRate,
                        message.IssuedAtUnixMs - (long)offset,
                        message.ApplyAtUnixMs - (long)offset);
                    room.Playback = snapshot;
                    if (newMedia != null) MediaChanged?.Invoke(newMedia);
                    PlaybackChanged?.Invoke(snapshot);
                    RoomChanged?.Invoke();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    internal void AcceptRoomState(PeerInfo peer, WatchWireMessage message)
    {
        if (message.RoomId == Guid.Empty || message.SenderPeerId != peer.Id) return;
        if (message.Revision < 0 || !double.IsFinite(message.PositionSeconds) || message.PositionSeconds < 0 ||
            !double.IsFinite(message.PlaybackRate) || message.PlaybackRate <= 0) return;
        if (message.MediaId != Guid.Empty &&
            (message.MediaSize <= 0 || message.ChunkSize is <= 0 or > MediaTransferHandler.MaxRangeBytes)) return;
        Guid localId = _control.LocalIdFor(peer, _defaultLocalPeerId);
        WatchRoom room;
        MediaManifest? newMedia;
        lock (_sync)
        {
            if (message.Type == "room_invite")
            {
                if (message.TargetPeerId != localId) return;
                if (_room != null && (_room.OwnerPeerId != peer.Id || _room.LocalPeerId != localId)) return;
            }
            else if (_room == null || _room.RoomId != message.RoomId || _room.OwnerPeerId != peer.Id)
            {
                return;
            }
            if (_room?.RoomId == message.RoomId && message.Revision < (_room.Playback?.Revision ?? 0)) return;

            room = _room?.RoomId == message.RoomId ? _room : new WatchRoom
            {
                RoomId = message.RoomId,
                OwnerPeerId = peer.Id,
                LocalPeerId = localId,
                Name = message.RoomName ?? "Sala"
            };
            room.Participants[peer.Id] = ParticipantRole.Owner;
            if (message.Type == "room_invite")
                room.Participants[room.LocalPeerId] = Enum.TryParse<ParticipantRole>(message.Role, true, out var role) && role != ParticipantRole.Owner
                    ? role : ParticipantRole.Viewer;

            newMedia = UpdateRemoteMedia(room, peer, message);
            double offset = _clockOffsetsMs.GetValueOrDefault(peer.Id);
            room.Playback = new PlaybackSnapshot(
                room.RoomId,
                message.MediaId,
                message.Revision,
                message.Paused,
                message.PositionSeconds,
                message.PlaybackRate,
                message.IssuedAtUnixMs - (long)offset,
                message.ApplyAtUnixMs - (long)offset);
            _room = room;
        }
        if (newMedia != null) MediaChanged?.Invoke(newMedia);
        if (room.Playback != null) PlaybackChanged?.Invoke(room.Playback);
        RoomChanged?.Invoke();
        _ = _control.SendAsync(peer.Id, new WatchWireMessage("state_request", room.RoomId, room.LocalPeerId));
    }

    private MediaManifest? UpdateRemoteMedia(WatchRoom room, PeerInfo peer, WatchWireMessage message)
    {
        if (message.MediaId == Guid.Empty || room.Media?.MediaId == message.MediaId) return null;
        if (message.MediaSize <= 0 || message.ChunkSize is <= 0 or > MediaTransferHandler.MaxRangeBytes)
            throw new InvalidDataException("Invalid remote media manifest.");
        var manifest = new MediaManifest(
            message.MediaId,
            message.MediaName ?? "Vídeo",
            message.MediaSize,
            message.MimeType ?? "application/octet-stream",
            message.ChunkSize,
            peer.Id);
        _mediaLibrary.RegisterRemoteMedia(manifest);
        room.Media = manifest;
        _mediaLibrary.KeepRemoteMedia(manifest.MediaId);
        return manifest;
    }

    private WatchRoom RequireRoom() => Snapshot() ?? throw new InvalidOperationException("No hay una sala activa.");

    private WatchRoom RequireOwnedRoom()
    {
        WatchRoom room = RequireRoom();
        if (room.OwnerPeerId != room.LocalPeerId) throw new UnauthorizedAccessException("Solo el propietario puede realizar esta acción.");
        return room;
    }
}
