using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using QuicNet.Protocols;
using QuicNet.Serialization;

namespace QuicNet.WebUi.Modules;

internal sealed partial class ChatApiModule
{
    private DateTime? FindAttachmentExpiry(Guid peerId, Guid fileId)
    {
        foreach (var message in ChatMessages.Where(m => !m.IsMe && m.PeerId == peerId.ToString()))
        {
            try
            {
                using var doc = JsonDocument.Parse(message.Message);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("fileId", out var id) &&
                    id.ValueKind == JsonValueKind.String && id.GetString() == fileId.ToString())
                    return message.ExpiresAtUtc;
            }
            catch (JsonException) { }
        }
        return null;
    }

    internal async Task<string?> RecoverCompletedDownloadAsync(Guid peerId, Guid fileId, CancellationToken ct)
    {
        var saved = _transferStore.Find(peerId, fileId);
        if (saved == null || File.Exists(saved.TempPath) || !File.Exists(saved.FinalPath)) return null;
        await using var stream = new FileStream(saved.FinalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != saved.Size || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)), saved.Sha256, StringComparison.OrdinalIgnoreCase))
            return null;
        _transferStore.Save(saved with { Completed = true, Received = saved.Size });
        _completedDownloads[(peerId, fileId)] = saved.FinalPath;
        return saved.FinalPath;
    }

    internal ChatDownloadStatus[] GetDownloadStatuses(Guid peerId) => _transferStore.Load(peerId)
        .Where(r => !r.IsUpload)
        .Select(r =>
        {
            bool active = _activeDownloads.TryGetValue((peerId, r.FileId), out var download);
            long received = active ? download!.ReceivedBytes : r.Received;
            string status = active ? download!.IsRestoring ? "verifying" : "transferring" : r.Completed && File.Exists(r.FinalPath) ? "completed" : "paused";
            return new ChatDownloadStatus(peerId.ToString(), r.FileId.ToString(), r.Name, received, r.Size,
                r.Size > 0 ? (int)(100.0 * received / r.Size) : status == "completed" ? 100 : 0,
                status, status == "completed" ? r.FinalPath : null);
        }).ToArray();

    private void BroadcastDownloadPaused(Guid peerId, Guid fileId)
    {
        var status = GetDownloadStatuses(peerId).FirstOrDefault(r => r.fileId == fileId.ToString());
        if (status != null) _hub.Broadcast("chat_file_paused", status);
    }

    private void DiscardSavedDownload(Guid peerId, Guid fileId, bool preserveCompleted = false)
    {
        var saved = _transferStore.Find(peerId, fileId, includeExpired: true);
        if (preserveCompleted && saved?.Completed == true) return;
        if (saved != null && !saved.Completed)
            try { File.Delete(saved.TempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _transferStore.Delete(peerId, fileId);
    }

    private async Task<bool> HandleDownloadRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/chat/downloads" && req.HttpMethod == "GET")
        {
            if (!Guid.TryParse(req.QueryString["peerId"], out var peerId)) throw new InvalidDataException("Invalid peer.");
            await WebUiContext.WriteJsonAsync(resp, GetDownloadStatuses(peerId)).ConfigureAwait(false);
            return true;
        }
        if (path != "/api/chat/pause-file" || req.HttpMethod != "POST") return false;
        using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
        Guid peer = WebUiContext.ParseGuid(doc.RootElement, "peerId");
        Guid file = WebUiContext.ParseGuid(doc.RootElement, "fileId");
        CancelDownloadStart(peer, file);
        if (_activeDownloads.TryRemove((peer, file), out var download)) await download.PauseAsync().ConfigureAwait(false);
        await ChatHandler.CancelFileAsync(peer, file, _fileLifetime.Token).ConfigureAwait(false);
        BroadcastDownloadPaused(peer, file);
        await WebUiContext.WriteJsonAsync(resp, new ChatApiModuleMessage25(true)).ConfigureAwait(false);
        return true;
    }
}
