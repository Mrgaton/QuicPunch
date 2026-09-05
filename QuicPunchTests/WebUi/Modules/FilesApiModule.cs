using System.Net;
using System.Text;
using System.Text.Json;
using QuicPunchTests.Protocols;

namespace QuicPunchTests.WebUi.Modules;

internal sealed class FilesApiModule
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly RelayDriveHandler _relayDriveHandler;
    private readonly CancellationTokenSource _cts;

    public FilesApiModule(QuicPunch.QuicPunch qcc, RelayDriveHandler relayDriveHandler, CancellationTokenSource cts)
    {
        _qcc = qcc;
        _relayDriveHandler = relayDriveHandler;
        _cts = cts;
    }

    public object GetRelayDriveStatus()
    {
        return new
        {
            outboxPath = _relayDriveHandler.OutboxPath,
            cachePath = _relayDriveHandler.CachePath,
            sessions = RelayDriveHandler.ActiveSessions.Values.Select(s => new { peerId = s.Peer.Id.ToString(), peerName = s.Peer.Name ?? "Peer" }).ToList(),
            published = _relayDriveHandler.PublishedFiles.Select(f => new { id = f.Id.ToString(), name = f.Name, size = f.Size }).ToList(),
            remote = _relayDriveHandler.RemoteFiles.Select(f => new
            {
                peerId = f.PeerId.ToString(),
                peerName = f.PeerName,
                id = f.Id.ToString(),
                name = f.Name,
                size = f.Size,
                cached = _relayDriveHandler.TryGetCached(f.PeerId, f.Id, out _)
            }).ToList(),
            transfers = _relayDriveHandler.ActiveTransfers.Select(t => new
            {
                peerId = t.PeerId.ToString(),
                fileId = t.FileId.ToString(),
                name = t.Name,
                received = t.Received,
                expected = t.ExpectedSize
            }).ToList(),
            cached = _relayDriveHandler.CachedFiles.Select(f => new
            {
                peerId = f.PeerId.ToString(),
                peerName = f.PeerName,
                id = f.Id.ToString(),
                name = f.Name,
                size = f.Size,
                materializedUtc = f.MaterializedUtc.ToString("O")
            }).ToList()
        };
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/files/status" && req.HttpMethod == "GET")
        {
            bool changed = _relayDriveHandler.RefreshPublishedFiles();
            if (changed) WebUiServer.LogEvent("[RELAYDRIVE] Outbox manifest refreshed.");
            await WebUiContext.WriteJsonAsync(resp, GetRelayDriveStatus()).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/files/publish" && req.HttpMethod == "POST")
        {
            string encodedName = req.Headers["X-File-Name-B64"] ?? "";
            string fileName;
            try { fileName = Encoding.UTF8.GetString(Convert.FromBase64String(encodedName)); }
            catch { fileName = "file"; }
            var published = await _relayDriveHandler.PublishAsync(fileName, req.InputStream, req.ContentLength64 >= 0 ? req.ContentLength64 : null, _cts.Token).ConfigureAwait(false);
            WebUiServer.LogEvent($"[RELAYDRIVE] Published {published.Name} ({published.Size} bytes)");
            await WebUiContext.WriteJsonAsync(resp, new { success = true, fileId = published.Id, name = published.Name, size = published.Size }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/files/remove" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid fileId = WebUiContext.ParseGuid(doc.RootElement, "fileId");
            bool removed = await _relayDriveHandler.RemovePublishedAsync(fileId, _cts.Token).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new { success = removed }, removed ? 200 : 404).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/files/materialize" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            Guid fileId = WebUiContext.ParseGuid(doc.RootElement, "fileId");
            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer) || !_qcc.IsTrustedPeer(peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Trusted peer not found." }, 404).ConfigureAwait(false);
                return true;
            }
            if (!RelayDriveHandler.ActiveSessions.ContainsKey(peerId))
            {
                WebUiServer.StartProtocolConnection(peer, _relayDriveHandler.ProtocolId);
                for (int i = 0; i < 40 && !RelayDriveHandler.ActiveSessions.ContainsKey(peerId); i++)
                    await Task.Delay(100, _cts.Token).ConfigureAwait(false);
            }
            if (!RelayDriveHandler.ActiveSessions.ContainsKey(peerId))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "RelayDrive session could not be established." }, 409).ConfigureAwait(false);
                return true;
            }
            string pathOnDisk = await _relayDriveHandler.MaterializeAsync(peerId, fileId, _cts.Token).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new { success = true, materialized = true, fileName = Path.GetFileName(pathOnDisk) }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/files/download" && req.HttpMethod == "GET")
        {
            if (!Guid.TryParse(req.QueryString["peerId"], out Guid peerId) || !Guid.TryParse(req.QueryString["fileId"], out Guid fileId)
                || !_relayDriveHandler.TryGetCached(peerId, fileId, out var cached))
            {
                resp.StatusCode = 404;
                return true;
            }
            await WriteDownloadAsync(resp, cached.Path, cached.Name).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/files/open-outbox" && req.HttpMethod == "POST")
        {
            _relayDriveHandler.OpenOutboxFolder();
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/files/open-cache" && req.HttpMethod == "POST")
        {
            _relayDriveHandler.OpenCacheFolder();
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static async Task WriteDownloadAsync(HttpListenerResponse resp, string path, string displayName)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { resp.StatusCode = 404; return; }
        resp.ContentType = "application/octet-stream";
        resp.ContentLength64 = info.Length;
        resp.Headers["Content-Disposition"] = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(displayName)}";
        await using var file = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await file.CopyToAsync(resp.OutputStream).ConfigureAwait(false);
    }
}
