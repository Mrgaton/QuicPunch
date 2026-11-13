using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using QuicPunch;
using QuicNet.Watch.Domain;
using QuicNet.Watch.Protocols;
using QuicNet.Watch.Services;

namespace QuicNet.Watch.Web;

public sealed class WatchWebServer : IAsyncDisposable
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly WatchControlHandler _control;
    private readonly MediaTransferHandler _media;
    private readonly MediaLibrary _library;
    private readonly RoomCoordinator _rooms;
    private readonly PeerConnector _connector;
    private readonly CancellationTokenSource _cts;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly WebSocketHub _hub;
    private readonly string _csrf = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    private IWatchFilePicker? _filePicker;
    private readonly SemaphoreSlim _mediaSelection = new(1, 1);

    public WatchWebServer(
        QuicPunch.QuicPunch qcc,
        WatchControlHandler control,
        MediaTransferHandler media,
        MediaLibrary library,
        RoomCoordinator rooms,
        PeerConnector connector,
        CancellationTokenSource cts)
    {
        _qcc = qcc;
        _control = control;
        _media = media;
        _library = library;
        _rooms = rooms;
        _connector = connector;
        _cts = cts;
        _hub = new WebSocketHub(_json);

        _rooms.RoomChanged += () => _ = PublishStatusAsync();
        _rooms.PlaybackChanged += state => _ = _hub.BroadcastAsync("playback", state);
        _rooms.MediaChanged += manifest => _ = _hub.BroadcastAsync("media", manifest);
        _rooms.Error += message => _ = _hub.BroadcastAsync("error", new global::QuicNet.Serialization.WatchWebServerMessage1(message));
        _connector.Error += message => _ = _hub.BroadcastAsync("error", new global::QuicNet.Serialization.WatchWebServerMessage1(message));
        _library.CacheProgress += (id, cached, total) => _ = _hub.BroadcastAsync("cache", new global::QuicNet.Serialization.WatchWebServerMessage2(id, cached, total));
        _control.PeerConnected += peer => _ = PublishStatusAsync();
        _control.PeerDisconnected += peer => _ = PublishStatusAsync();
    }

    public void SetFilePicker(IWatchFilePicker? filePicker) => _filePicker = filePicker;

    public async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerRequest req = context.Request;
        HttpListenerResponse resp = context.Response;
        bool websocket = false;
        try
        {
            AddHeaders(resp);
            string path = req.Url?.AbsolutePath ?? "/watch/";
            path = path["/watch".Length..];
            if (path.Length == 0) path = "/";
            if (path == "/ws" && req.IsWebSocketRequest)
            {
                if (!FixedEquals(req.QueryString["csrf"], _csrf))
                {
                    await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage3("WebSocket no autorizado."), 403).ConfigureAwait(false);
                    return;
                }
                websocket = true;
                HttpListenerWebSocketContext upgraded = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                await _hub.AddAsync(upgraded.WebSocket, _cts.Token).ConfigureAwait(false);
                return;
            }

            if (req.HttpMethod == "POST" && !FixedEquals(req.Headers["X-QuicWatch-Key"], _csrf))
            {
                await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage3("Solicitud local no válida."), 403).ConfigureAwait(false);
                return;
            }

            if (req.HttpMethod == "GET" && path == "/api/status")
            {
                await JsonAsync(resp, BuildStatus()).ConfigureAwait(false);
                return;
            }
            if ((req.HttpMethod == "GET" || req.HttpMethod == "HEAD") && path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
            {
                await ServeMediaAsync(req, resp, path, _cts.Token).ConfigureAwait(false);
                return;
            }

            if (req.HttpMethod == "POST")
            {
                switch (path)
                {
                    case "/api/token/connect":
                    {
                        TokenRequest body = await ReadAsync<TokenRequest>(req).ConfigureAwait(false);
                        PeerInfo peer = await _connector.AddTokenAsync(body.Token ?? "", _cts.Token).ConfigureAwait(false);
                        await _connector.ConnectAsync(peer.Id, _cts.Token).ConfigureAwait(false);
                        await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage4(true, peer.Id)).ConfigureAwait(false);
                        return;
                    }
                    case "/api/peer/connect":
                    {
                        PeerRequest body = await ReadAsync<PeerRequest>(req).ConfigureAwait(false);
                        await _connector.ConnectAsync(body.PeerId, _cts.Token).ConfigureAwait(false);
                        await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage5(true)).ConfigureAwait(false);
                        return;
                    }
                    case "/api/room/leave":
                    {
                        await _rooms.LeaveAsync(_cts.Token).ConfigureAwait(false);
                        await JsonAsync(resp, BuildStatus()).ConfigureAwait(false);
                        return;
                    }
                    case "/api/room/create":
                    {
                        RoomRequest body = await ReadAsync<RoomRequest>(req).ConfigureAwait(false);
                        foreach (Guid peerId in body.PeerIds ?? []) await _connector.ConnectAsync(peerId, _cts.Token).ConfigureAwait(false);
                        await _rooms.CreateRoomAsync(body.Name ?? "Sala", body.PeerIds ?? [], _cts.Token).ConfigureAwait(false);
                        await JsonAsync(resp, BuildStatus()).ConfigureAwait(false);
                        return;
                    }
                    case "/api/room/invite":
                    {
                        PeerRequest body = await ReadAsync<PeerRequest>(req).ConfigureAwait(false);
                        await _connector.ConnectAsync(body.PeerId, _cts.Token).ConfigureAwait(false);
                        await _rooms.InviteAsync(body.PeerId, _cts.Token).ConfigureAwait(false);
                        await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage5(true)).ConfigureAwait(false);
                        return;
                    }
                    case "/api/media/pick":
                    {
                        await _mediaSelection.WaitAsync(_cts.Token).ConfigureAwait(false);
                        try
                        {
                            WatchRoom selectedRoom = _rooms.Snapshot() ?? throw new InvalidOperationException("Crea una sala antes de elegir el vídeo.");
                            if (selectedRoom.OwnerPeerId != _rooms.LocalPeerId) throw new UnauthorizedAccessException("Solo el anfitrión puede elegir el vídeo.");
                            string? pathSelected = await PickVideoAsync().ConfigureAwait(false);
                            if (string.IsNullOrWhiteSpace(pathSelected))
                            {
                                await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage6(false, true)).ConfigureAwait(false);
                                return;
                            }
                            if (_rooms.Snapshot()?.RoomId != selectedRoom.RoomId)
                                throw new InvalidOperationException("La sala ha cambiado. Vuelve a elegir el vídeo.");
                            Guid localId = _rooms.LocalPeerId;
                            MediaManifest manifest = _library.SelectLocalMedia(pathSelected, localId);
                            await _rooms.SetMediaAsync(manifest, _cts.Token).ConfigureAwait(false);
                            _filePicker?.MediaSelected(pathSelected);
                            await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage7(true, manifest)).ConfigureAwait(false);
                            return;
                        }
                        finally { _mediaSelection.Release(); }
                    }
                    case "/api/media/select-path":
                    {
                        if (OperatingSystem.IsAndroid()) throw new UnauthorizedAccessException("Utiliza el selector de vídeos de Android.");
                        var room = _rooms.Snapshot();
                        if (room == null || room.OwnerPeerId != _rooms.LocalPeerId) throw new UnauthorizedAccessException("Solo el anfitrión puede elegir el vídeo.");
                        PathRequest body = await ReadAsync<PathRequest>(req).ConfigureAwait(false);
                        Guid localId = _rooms.LocalPeerId;
                        MediaManifest manifest = _library.SelectLocalMedia(body.Path ?? "", localId);
                        await _rooms.SetMediaAsync(manifest, _cts.Token).ConfigureAwait(false);
                        await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage7(true, manifest)).ConfigureAwait(false);
                        return;
                    }
                    case "/api/playback":
                    {
                        PlaybackRequest body = await ReadAsync<PlaybackRequest>(req).ConfigureAwait(false);
                        await _rooms.RequestPlaybackAsync(body.Paused, body.PositionSeconds, body.PlaybackRate, _cts.Token).ConfigureAwait(false);
                        await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage5(true)).ConfigureAwait(false);
                        return;
                    }
                    case "/api/permission":
                    {
                        PermissionRequest body = await ReadAsync<PermissionRequest>(req).ConfigureAwait(false);
                        if (!Enum.TryParse<ParticipantRole>(body.Role, true, out var role) || role == ParticipantRole.Owner)
                            throw new ArgumentException("Permiso no válido.");
                        await _rooms.SetPermissionAsync(body.PeerId, role, _cts.Token).ConfigureAwait(false);
                        await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage5(true)).ConfigureAwait(false);
                        return;
                    }
                }
            }

            if (req.HttpMethod == "GET")
            {
                string resource = path switch
                {
                    "/" or "/index.html" => "index.html",
                    "/app.js" => "app.js",
                    "/diagnostics.js" => "diagnostics.js",
                    "/app.css" => "app.css",
                    "/watch-design.css" => "watch-design.css",
                    _ => ""
                };
                if (resource.Length > 0)
                {
                    await ServeResourceAsync(resp, resource).ConfigureAwait(false);
                    return;
                }
            }
            await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage3("No encontrado."), 404).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!websocket && resp.OutputStream.CanWrite)
                await JsonAsync(resp, new global::QuicNet.Serialization.WatchWebServerMessage3(ex.Message), ex switch
                {
                    ArgumentException or InvalidDataException or JsonException => 400,
                    UnauthorizedAccessException => 403,
                    FileNotFoundException or KeyNotFoundException => 404,
                    TimeoutException => 504,
                    _ => 500
                }).ConfigureAwait(false);
        }
        finally
        {
            if (!websocket) try { resp.Close(); } catch { }
        }
    }

    private object BuildStatus()
    {
        WatchRoom? room = _rooms.Snapshot();
        Guid localId = _rooms.LocalPeerId;
        return new global::QuicNet.Serialization.WatchWebServerMessage8(_csrf, new global::QuicNet.Serialization.WatchWebServerMessage9(localId, _qcc.CurrentPeer?.Name ?? Environment.MachineName, _qcc.GetToken() ?? ""), _qcc.AvailablePeers.Values.Where(p => p.Id != localId).Select(p => new global::QuicNet.Serialization.WatchWebServerMessage10(p.Id, p.Name, _control.ConnectedPeerIds.Contains(p.Id) && _media.ConnectedPeerIds.Contains(p.Id), room?.Participants.ContainsKey(p.Id) == true, room?.Participants.TryGetValue(p.Id, out ParticipantRole role) == true ? role.ToString() : null)).OrderBy(p => p.name).ToArray(), room == null ? null : new global::QuicNet.Serialization.WatchWebServerMessage11(room.RoomId, room.Name, room.OwnerPeerId, room.OwnerPeerId == localId, room.Participants.GetValueOrDefault(localId, ParticipantRole.Viewer).ToString(), room.Participants.Select(p => new global::QuicNet.Serialization.WatchWebServerMessage12(p.Key, p.Value.ToString())).ToArray(), room.Media, room.Playback), canPickMedia: _filePicker != null, canSelectPath: !OperatingSystem.IsAndroid());
    }

    private Task PublishStatusAsync() => _hub.BroadcastAsync("status", BuildStatus());

    private async Task<string?> PickVideoAsync()
    {
        IWatchFilePicker? picker = _filePicker;
        if (picker == null) throw new InvalidOperationException("Usa la ruta manual si ejecutas QuicWatch sin selector de archivos.");
        return await picker.PickVideoFileAsync().ConfigureAwait(false);
    }

    private async Task ServeMediaAsync(HttpListenerRequest req, HttpListenerResponse resp, string path, CancellationToken ct)
    {
        if (!Guid.TryParse(path["/media/".Length..], out Guid mediaId)) { resp.StatusCode = 404; return; }
        MediaManifest? manifest = _library.GetManifest(mediaId);
        if (manifest == null) { resp.StatusCode = 404; return; }

        long start = 0;
        long requestedEnd = manifest.Size - 1;
        string? range = req.Headers["Range"];
        if (!string.IsNullOrEmpty(range))
        {
            if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || range.Contains(','))
            {
                resp.StatusCode = 416;
                resp.AddHeader("Content-Range", $"bytes */{manifest.Size}");
                return;
            }
            string[] parts = range[6..].Split('-', 2);
            if (parts.Length != 2 ||
                (parts[0].Length == 0 && (!long.TryParse(parts[1], out long suffix) || suffix <= 0)) ||
                (parts[0].Length > 0 && !long.TryParse(parts[0], out start)) ||
                (parts[1].Length > 0 && parts[0].Length > 0 && !long.TryParse(parts[1], out requestedEnd)))
            {
                resp.StatusCode = 416;
                resp.AddHeader("Content-Range", $"bytes */{manifest.Size}");
                return;
            }
            if (parts[0].Length == 0 && long.TryParse(parts[1], out long suffixLength))
                start = Math.Max(0, manifest.Size - suffixLength);
        }
        if (start < 0 || start >= manifest.Size || requestedEnd < start) { resp.StatusCode = 416; resp.AddHeader("Content-Range", $"bytes */{manifest.Size}"); return; }
        long end = Math.Min(Math.Min(requestedEnd, manifest.Size - 1), start + MediaLibrary.LogicalChunkBytes - 1);
        int length = checked((int)(end - start + 1));

        byte[]? bytes = req.HttpMethod == "HEAD" ? null : await _library.ReadRangeAsync(mediaId, start, length, ct).ConfigureAwait(false);

        resp.StatusCode = 206;
        resp.ContentType = manifest.MimeType;
        resp.ContentLength64 = length;
        resp.AddHeader("Accept-Ranges", "bytes");
        resp.AddHeader("Content-Range", $"bytes {start}-{end}/{manifest.Size}");
        resp.AddHeader("Cache-Control", "no-store");
        if (bytes != null) await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task ServeResourceAsync(HttpListenerResponse resp, string file)
    {
        string resourceName = file == "diagnostics.js" ? "QuicNet.wwwroot.diagnostics.js" : $"QuicNet.Watch.wwwroot.{file}";
        await using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(resourceName);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string content = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (file == "index.html") content = content.Replace("__QUICWATCH_CSRF__", _csrf, StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        resp.ContentType = file.EndsWith(".css") ? "text/css; charset=utf-8" : file.EndsWith(".js") ? "text/javascript; charset=utf-8" : "text/html; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private async Task<T> ReadAsync<T>(HttpListenerRequest req)
    {
        const int maxBytes = 128 * 1024;
        if (req.ContentLength64 > maxBytes) throw new InvalidDataException("Solicitud demasiado grande.");
        byte[] buffer = new byte[maxBytes + 1];
        int used = 0;
        while (true)
        {
            int read = await req.InputStream.ReadAsync(buffer.AsMemory(used), _cts.Token).ConfigureAwait(false);
            used += read;
            if (used > maxBytes) throw new InvalidDataException("Solicitud demasiado grande.");
            if (read == 0) break;
        }
        T? value = JsonSerializer.Deserialize<T>(buffer.AsSpan(0, used), QuicNet.Serialization.SharedJson.WithOptions(_json));
        return value ?? throw new InvalidDataException("Solicitud vacía.");
    }

    private async Task JsonAsync(HttpListenerResponse resp, object value, int status = 200)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, global::QuicNet.WebUi.WebUiContext.JsonOptionsFor(_json));
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static bool FixedEquals(string? left, string right)
    {
        if (left == null) return false;
        byte[] a = Encoding.UTF8.GetBytes(left);
        byte[] b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static void AddHeaders(HttpListenerResponse response)
    {
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self' ws://127.0.0.1:*; media-src 'self' blob:; style-src 'self'; script-src 'self'; frame-ancestors 'self'";
    }

    public async ValueTask DisposeAsync()
    {
        await _hub.DisposeAsync().ConfigureAwait(false);
    }

    internal sealed record TokenRequest(string? Token);
    internal sealed record PeerRequest(Guid PeerId);
    internal sealed record RoomRequest(string? Name, Guid[]? PeerIds);
    internal sealed record PathRequest(string? Path);
    internal sealed record PlaybackRequest(bool Paused, double PositionSeconds, double PlaybackRate);
    internal sealed record PermissionRequest(Guid PeerId, string? Role);
}
