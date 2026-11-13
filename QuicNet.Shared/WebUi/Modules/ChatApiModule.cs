using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using QuicPunch;
using QuicPunch.Helpers;
using QuicNet.Protocols;

namespace QuicNet.WebUi.Modules;

internal sealed partial class ChatApiModule : IDisposable
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly ChatHandler _chatHandler;
    private readonly WebUiWebSocketHub _hub;
    private readonly object _chatQueueLock = new();
    private long _chatHistoryBytes;
    private readonly ChatHistoryStore _history;
    private readonly ConcurrentDictionary<string, int> _disappearingSeconds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _expiryTimer;

    private readonly string _chatOutboxPath;
    private readonly string _chatDownloadsPath;
    private readonly ChatTransferStore _transferStore;

    public sealed record SharedFileInfo(Guid FileId, string Name, string FullPath, long Size, string Mime, Guid PeerId, string Sha256);
    private sealed class UploadState : IDisposable
    {
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource<bool> Receipt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task { get; set; } = Task.CompletedTask;
        public UploadState(CancellationToken lifetime) => Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public void Cancel() { try { Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose() => Cancellation.Dispose();
    }

    private readonly object _fileGate = new();
    private readonly CancellationTokenSource _fileLifetime = new();
    private readonly Task _fileMaintenanceTask;
    private int _disposed;
    private const int MaxConcurrentTransfers = 8;
    private readonly ConcurrentDictionary<Guid, SharedFileInfo> _sharedFiles = new();
    private readonly ConcurrentDictionary<(Guid PeerId, Guid FileId), ChatHandler.FileOffer> _receivedOffers = new();
    private readonly ConcurrentDictionary<(Guid PeerId, Guid FileId), ChatFileDownload> _activeDownloads = new();
    private readonly ConcurrentDictionary<(Guid PeerId, Guid FileId), CancellationTokenSource> _downloadStarts = new();
    private readonly ConcurrentDictionary<(Guid PeerId, Guid FileId), string> _completedDownloads = new();
    private readonly ConcurrentDictionary<(Guid PeerId, Guid FileId), UploadState> _uploads = new();

    public ConcurrentQueue<WebUiServer.ChatMessage> ChatMessages { get; } = new();
    internal IChatFileOpener? FileOpener { get; set; }

    private string LocalSenderName(Guid peerId)
    {
        PeerInfo? peer = ChatHandler.ActiveChats.TryGetValue(peerId, out var session) ? session.Peer :
            _qcc.AvailablePeers.TryGetValue(peerId, out var available) ? available : null;
        return (peer == null ? _qcc.CurrentPeer : _qcc.GetLocalIdentityForPeer(peer)).Name ?? "Me";
    }

    public ChatApiModule(QuicPunch.QuicPunch qcc, ChatHandler chatHandler, WebUiWebSocketHub hub, string? downloadsPath = null, string? outboxPath = null)
    {
        _qcc = qcc;
        _chatHandler = chatHandler;
        _hub = hub;

        _history = new ChatHistoryStore(Path.Combine(_qcc.NodeAppDataPath, "chat-history.db"));
        foreach (var setting in _history.GetDurations()) _disappearingSeconds[setting.Key] = setting.Value;
        foreach (var message in _history.LoadRecent(1000))
        {
            ChatMessages.Enqueue(message);
            _chatHistoryBytes += Encoding.UTF8.GetByteCount(message.Message);
        }
        _history.DeleteExpired(DateTime.UtcNow);
        _expiryTimer = new Timer(_ => PruneExpired(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        _chatOutboxPath = outboxPath ?? Path.Combine(_qcc.NodeAppDataPath, "ChatOutbox");
        _chatDownloadsPath = downloadsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "QuicPunch");

        try { Directory.CreateDirectory(_chatOutboxPath); } catch { }
        try { Directory.CreateDirectory(_chatDownloadsPath); } catch { }
        _transferStore = new ChatTransferStore(Path.Combine(_qcc.NodeAppDataPath, "chat-transfers.db"));
        foreach (var saved in _transferStore.Load())
        {
            if (saved.IsUpload && File.Exists(saved.FinalPath))
                _sharedFiles[saved.FileId] = new SharedFileInfo(saved.FileId, saved.Name, saved.FinalPath, saved.Size, saved.Mime, saved.PeerId, saved.Sha256);
            else if (!saved.IsUpload && saved.Completed && File.Exists(saved.FinalPath))
                _completedDownloads[(saved.PeerId, saved.FileId)] = saved.FinalPath;
        }
        _fileMaintenanceTask = MaintainTransfersAsync();

        if (_chatHandler.NostrMessagingEnabled && _qcc.Mailbox != null)
        {
            _qcc.Mailbox.OnGlobalMessageReceived += (senderPubKey, senderName, protocolId, payload, message, timestamp, eventId) =>
            {
                var msg = EnqueueChat(new WebUiServer.ChatMessage("global", eventId, senderName, message, timestamp.LocalDateTime, false, true));
                _hub.Broadcast("chat_msg", new global::QuicNet.Serialization.ChatApiModuleMessage1("global", msg.MsgId, msg.Sender, senderPubKey, msg.Message, msg.Timestamp.ToString("HH:mm:ss"), msg.IsMe, msg.IsConfirmed, true, ToUnixMs(msg.ExpiresAtUtc)) { timestampMs = new DateTimeOffset(msg.Timestamp).ToUnixTimeMilliseconds() });
            };
        }

        _chatHandler.OnMessageReceived += (peer, message, msgId, remoteDisappearSeconds) =>
        {
            if (_history.IsHidden(peer.Id.ToString(), msgId) || _history.GetMessage(peer.Id.ToString(), msgId) != null) return;
            WebUiServer.Instance?.Notifications?.Publish("messages", peer.Name ?? "Mensaje", MessagePreview(message), "/chat.html?peer=" + peer.Id,
                Convert.ToHexString(peer.CertHash), "message:" + peer.Id + ":" + msgId);
            bool isOffline = _chatHandler.NostrMessagingEnabled && !ChatHandler.ActiveChats.ContainsKey(peer.Id);
            var msg = EnqueueChat(new WebUiServer.ChatMessage(peer.Id.ToString(), msgId, peer.Name ?? "Unknown", message, DateTime.Now, false, true), remoteDisappearSeconds);
            WebUiServer.LogEvent($"[CHAT{(isOffline ? " NOSTR" : "")}] Message received from {peer.Name ?? "Unknown"}.");
            _hub.Broadcast("chat_msg", new global::QuicNet.Serialization.ChatApiModuleMessage2(msg.PeerId, msg.MsgId, msg.Sender, msg.Message, msg.Timestamp.ToString("HH:mm:ss"), msg.IsMe, msg.IsConfirmed, isOffline, ToUnixMs(msg.ExpiresAtUtc)) { timestampMs = new DateTimeOffset(msg.Timestamp).ToUnixTimeMilliseconds() });
        };

        _chatHandler.OnMessageAckReceived += (_, msgId) =>
        {
            MarkChatConfirmed(msgId);
            _hub.Broadcast("chat_ack", new global::QuicNet.Serialization.ChatApiModuleMessage3(msgId));
        };

        _chatHandler.OnTypingReceived += (peer, isTyping) =>
        {
            _hub.Broadcast("chat_typing", new global::QuicNet.Serialization.ChatApiModuleMessage4(peer.Id.ToString(), peer.Name ?? "Peer", isTyping));
        };
        _chatHandler.OnActionsReceived += ReceiveActions;
        _chatHandler.OnGetActionsForPeer += GetActionsForPeer;

        _chatHandler.OnVerifiedFileOfferReceived += ReceiveFileOffer;

        _chatHandler.OnFileRequestReceived += StartUpload;
        _chatHandler.OnFileChunkReceivedAsync += ReceiveFileChunkAsync;
        _chatHandler.OnFileCancelReceivedAsync += CancelPeerTransferAsync;
        _chatHandler.OnFileCompletedReceived += ConfirmUpload;
        _chatHandler.OnPeerDisconnectedAsync += CancelPeerTransfersAsync;

        _chatHandler.OnGetHistoryForPeer += peerId => ChatMessages
            .Where(m => m.PeerId == peerId.ToString() && m.DeliveryState == "sent" && (m.ExpiresAtUtc == null || m.ExpiresAtUtc > DateTime.UtcNow))
            .Select(m => (m.MsgId, m.Sender, m.Message, m.Timestamp, m.ExpiresAtUtc))
            .ToList();

        _chatHandler.OnHistorySyncReceived += (peer, items) =>
        {
            bool anyNew = false;
            foreach (var item in items)
            {
                if (item.ExpiresAtUtc is { } expiry && expiry <= DateTime.UtcNow) continue;
                if (!_history.IsHidden(peer.Id.ToString(), item.MsgId) && !ChatMessages.Any(m => m.MsgId == item.MsgId))
                {
                    EnqueueChat(new WebUiServer.ChatMessage(peer.Id.ToString(), item.MsgId, item.Sender, item.Content, item.Timestamp, false, true, item.ExpiresAtUtc));
                    anyNew = true;
                }
            }
            if (anyNew)
            {
                _hub.Broadcast("chat_sync", new global::QuicNet.Serialization.ChatApiModuleMessage5(peer.Id.ToString()));
            }
        };

        _chatHandler.OnPeerConnected += peer =>
        {
            WebUiServer.ClearPeerPetitions(peer.Id);
            _hub.Broadcast("peer_connected", new global::QuicNet.Serialization.ChatApiModuleMessage6(peer.Id.ToString(), "chat"));
            _ = FlushOutboxAsync();
        };

        _chatHandler.OnPeerDisconnected += peer =>
        {
            _hub.Broadcast("peer_disconnected", new global::QuicNet.Serialization.ChatApiModuleMessage6(peer.Id.ToString(), "chat"));
        };
    }

    private static long? ToUnixMs(DateTime? value) => value.HasValue
        ? new DateTimeOffset(value.Value.ToUniversalTime()).ToUnixTimeMilliseconds() : null;

    private static string MessagePreview(string message)
    {
        if (message.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                if (doc.RootElement.TryGetProperty("type", out var textType) && textType.GetString() == "text" && doc.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    string content = text.GetString() ?? "";
                    return content[..Math.Min(content.Length, 180)].Replace('\r', ' ').Replace('\n', ' ');
                }
                if (doc.RootElement.TryGetProperty("type", out var type)) return type.GetString() switch
                { "image" => "Imagen recibida", "video" => "Vídeo recibido", "audio" => "Nota de voz recibida", "file" or "file_offer" => "Archivo recibido", _ => "Mensaje recibido" };
            }
            catch (JsonException) { }
        }
        return message[..Math.Min(message.Length, 180)].Replace('\r', ' ').Replace('\n', ' ');
    }

    public IReadOnlyDictionary<string, int> DisappearingSeconds => _disappearingSeconds;
    public IReadOnlyDictionary<string, long> ConversationActivity => _history.GetConversationActivity();

    private int GetDisappearingSeconds(string peerId) =>
        _disappearingSeconds.TryGetValue(peerId, out int seconds) ? seconds : 0;

    public WebUiServer.ChatMessage EnqueueChat(WebUiServer.ChatMessage message, int? remoteSeconds = null)
    {
        lock (_chatQueueLock)
        {
            int localSeconds = GetDisappearingSeconds(message.PeerId);
            int seconds = remoteSeconds is > 0 && localSeconds > 0
                ? Math.Min(remoteSeconds.Value, localSeconds)
                : remoteSeconds is > 0 ? remoteSeconds.Value : localSeconds;
            if (message.DeliveryState == "sent" && message.ExpiresAtUtc == null && seconds > 0)
                message = message with { ExpiresAtUtc = DateTime.UtcNow.AddSeconds(seconds) };
            if (ChatMessages.Any(existing => existing.PeerId == message.PeerId && existing.MsgId == message.MsgId))
                return message;
            _history.Save(message);
            ChatMessages.Enqueue(message);
            _chatHistoryBytes += Encoding.UTF8.GetByteCount(message.Message);
            while ((ChatMessages.Count > 1000 || _chatHistoryBytes > WebUiContext.MaxChatHistoryBytes) && ChatMessages.TryDequeue(out var removed))
                _chatHistoryBytes = Math.Max(0, _chatHistoryBytes - Encoding.UTF8.GetByteCount(removed.Message));
            return message;
        }
    }

    private void PruneExpired()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            string[] removed;
            WebUiServer.ChatMessage[] expired;
            lock (_chatQueueLock)
            {
                expired = ChatMessages.Where(m => m.ExpiresAtUtc is { } expiry && expiry <= now).ToArray();
                removed = expired.Select(m => m.MsgId).ToArray();
                if (removed.Length > 0)
                {
                    var active = ChatMessages.Where(m => m.ExpiresAtUtc is not { } expiry || expiry > now).ToArray();
                    while (ChatMessages.TryDequeue(out _)) { }
                    _chatHistoryBytes = 0;
                    foreach (var item in active)
                    {
                        ChatMessages.Enqueue(item);
                        _chatHistoryBytes += Encoding.UTF8.GetByteCount(item.Message);
                    }
                }
                _history.DeleteExpired(now);
            }
            foreach (var message in expired) ForgetAttachment(message);
            if (removed.Length > 0) _hub.Broadcast("chat_expired", new global::QuicNet.Serialization.ChatApiModuleMessage7(removed));
        }
        catch (Exception ex) { WebUiServer.LogEvent($"[CHAT] Expiry cleanup failed: {ex.Message}"); }
        _ = FlushOutboxAsync();
    }

    private void ReceiveFileOffer(PeerInfo peer, string msgId, ChatHandler.FileOffer offer, string sender)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Guid fileId = offer.FileId;
        string fileName = offer.Name;
        long fileSize = offer.Size;
        string mime = offer.Mime;
        lock (_fileGate)
        {
            if (_receivedOffers.Count >= 1024 && !_receivedOffers.ContainsKey((peer.Id, fileId))) return;
            _receivedOffers[(peer.Id, fileId)] = offer;
        }
        string offerPayload = JsonSerializer.Serialize(new global::QuicNet.Serialization.ChatApiModuleMessage8("file_offer", fileId.ToString(), fileName, fileSize, offer.Sha256, mime),global::QuicNet.WebUi.WebUiContext.JsonOptions);
        var msg = EnqueueChat(new WebUiServer.ChatMessage(peer.Id.ToString(), msgId, sender, offerPayload, DateTime.Now, false, true));
        WebUiServer.LogEvent($"[CHAT] File offered by {sender}: {fileName} ({fileSize / 1024.0 / 1024.0:F1} MB)");
        _hub.Broadcast("chat_msg", new global::QuicNet.Serialization.ChatApiModuleMessage9(msg.PeerId, msg.MsgId, msg.Sender, msg.Message, msg.Timestamp.ToString("HH:mm:ss"), msg.IsMe, msg.IsConfirmed, ToUnixMs(msg.ExpiresAtUtc)) { timestampMs = new DateTimeOffset(msg.Timestamp).ToUnixTimeMilliseconds() });
        WebUiServer.Instance?.Notifications?.Publish("files", peer.Name ?? "Archivo recibido", fileName, "/chat.html?peer=" + peer.Id,
            Convert.ToHexString(peer.CertHash), "file-offer:" + peer.Id + ":" + msgId);
    }

    private void StartUpload(PeerInfo peer, Guid fileId, long offset)
    {
        if (!_qcc.IsTrustedPeer(peer) || !_sharedFiles.TryGetValue(fileId, out var item) || item.PeerId != peer.Id ||
            offset < 0 || offset > item.Size || !File.Exists(item.FullPath)) return;
        var key = (peer.Id, fileId);
        lock (_fileGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _uploads.Count >= MaxConcurrentTransfers || _uploads.ContainsKey(key)) return;
            var state = new UploadState(_fileLifetime.Token);
            _uploads[key] = state;
            // One owned asynchronous task per transfer; blocks never create workers.
            state.Task = SendFileAsync(peer, item, offset, state);
        }
    }

    private async Task SendFileAsync(PeerInfo peer, SharedFileInfo item, long offset, UploadState state)
    {
        CancellationToken ct = state.Cancellation.Token;
        state.Cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            const int bufferSize = ChatHandler.FileChunkBytes;
            byte[] buffer = new byte[bufferSize];
            await using var source = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != item.Size) throw new InvalidDataException("The offered file size changed.");
            source.Seek(offset, SeekOrigin.Begin);
            long currentOffset = offset;
            DateTime lastProgress = DateTime.MinValue;
            while (currentOffset < item.Size)
            {
                ct.ThrowIfCancellationRequested();
                if (!_qcc.IsTrustedPeer(peer)) throw new UnauthorizedAccessException("Peer trust was revoked.");
                int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The offered file ended early.");
                byte[] frame = ChatHandler.BuildFileChunkFrame(item.FileId, currentOffset, buffer, read, false);
                if (!await ChatHandler.SendBinaryChunkAsync(peer.Id, frame, ct).ConfigureAwait(false))
                    throw new IOException("The file connection closed before the upload finished.");
                currentOffset += read;
                state.Cancellation.CancelAfter(TimeSpan.FromMinutes(2));
                if (DateTime.UtcNow - lastProgress > TimeSpan.FromMilliseconds(150))
                {
                    lastProgress = DateTime.UtcNow;
                    _hub.Broadcast("chat_file_progress", new global::QuicNet.Serialization.ChatApiModuleMessage10(peer.Id.ToString(), item.FileId.ToString(), true, currentOffset, item.Size, item.Size > 0 ? (int)(100.0 * currentOffset / item.Size) : 100));
                }
            }
            // An explicit empty terminal frame also completes zero-byte files and fully resumed ranges.
            if (!await ChatHandler.SendBinaryChunkAsync(peer.Id,
                ChatHandler.BuildFileChunkFrame(item.FileId, currentOffset, [], 0, true), ct).ConfigureAwait(false))
                throw new IOException("Could not send the file end marker.");
            await state.Receipt.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            try { _transferStore.UpdateUpload(peer.Id, item.FileId, record => record with { Completed = true, Received = record.Size }); }
            catch { WebUiServer.LogEvent("[CHAT] El contacto verificó el archivo, pero no se pudo guardar el recibo local."); }
            WebUiServer.LogEvent($"[CHAT] File received and verified by {peer.Name}: {item.Name}");
            _hub.Broadcast("chat_file_sent", new global::QuicNet.Serialization.ChatApiModuleMessage11(peer.Id.ToString(), item.FileId.ToString(), item.Size));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!_fileLifetime.IsCancellationRequested)
                _hub.Broadcast("chat_file_cancelled", new global::QuicNet.Serialization.ChatApiModuleMessage12(peer.Id.ToString(), item.FileId.ToString()));
        }
        catch (Exception ex)
        {
            BroadcastFileError(peer.Id, item.FileId, ex.Message);
            await ChatHandler.CancelFileAsync(peer.Id, item.FileId, ct).ConfigureAwait(false);
        }
        finally
        {
            _uploads.TryRemove(new KeyValuePair<(Guid, Guid), UploadState>((peer.Id, item.FileId), state));
            state.Dispose();
        }
    }

    private void ConfirmUpload(PeerInfo peer, Guid fileId, string sha256)
    {
        if (_qcc.IsTrustedPeer(peer) && _sharedFiles.TryGetValue(fileId, out var item) && item.PeerId == peer.Id &&
            string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase) && _uploads.TryGetValue((peer.Id, fileId), out var upload))
            upload.Receipt.TrySetResult(true);
    }

    private async Task ReceiveFileChunkAsync(PeerInfo peer, Guid fileId, long offset, ReadOnlyMemory<byte> data, bool isEnd, CancellationToken ct)
    {
        var key = (peer.Id, fileId);
        if (!_activeDownloads.TryGetValue(key, out var download)) return;
        if (download.IsRestoring) return;
        try
        {
            if (!_qcc.IsTrustedPeer(peer)) throw new UnauthorizedAccessException("Peer trust was revoked.");
            bool completed = await download.WriteChunkAsync(peer.Id, offset, data, isEnd, ct).ConfigureAwait(false);
            if (DateTime.UtcNow - download.LastProgressBroadcastUtc > TimeSpan.FromMilliseconds(120) || completed)
            {
                download.LastProgressBroadcastUtc = DateTime.UtcNow;
                _hub.Broadcast("chat_file_progress", new global::QuicNet.Serialization.ChatApiModuleMessage10(peer.Id.ToString(), fileId.ToString(), false, download.ReceivedBytes, download.TotalSize, download.TotalSize > 0 ? (int)(100.0 * download.ReceivedBytes / download.TotalSize) : 100));
            }
            if (!completed) return;
            _completedDownloads[key] = download.FinalPath;
            _activeDownloads.TryRemove(new KeyValuePair<(Guid, Guid), ChatFileDownload>(key, download));
            await download.DisposeAsync().ConfigureAwait(false);
            _hub.Broadcast("chat_file_complete", new global::QuicNet.Serialization.ChatApiModuleMessage13(peer.Id.ToString(), fileId.ToString(), download.FileName, download.FinalPath));
            WebUiServer.Instance?.Notifications?.Publish("files", peer.Name ?? "Archivo recibido", download.FileName, "/chat.html?peer=" + peer.Id, Convert.ToHexString(peer.CertHash));
            await ChatHandler.SendFileCompletionAsync(peer.Id, fileId, download.Sha256, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_activeDownloads.TryRemove(new KeyValuePair<(Guid, Guid), ChatFileDownload>(key, download))) return;
            if (ex is OperationCanceledException or IOException && ex is not InvalidDataException)
            {
                await download.PauseAsync().ConfigureAwait(false);
                BroadcastDownloadPaused(peer.Id, fileId);
            }
            else
                await download.DisposeAsync().ConfigureAwait(false);
            BroadcastFileError(peer.Id, fileId, ex.Message);
            await ChatHandler.CancelFileAsync(peer.Id, fileId, ct).ConfigureAwait(false);
        }
    }

    private void BroadcastFileError(Guid peerId, Guid fileId, string error)
    {
        WebUiServer.LogEvent($"[CHAT] File transfer failed: {error}");
        _hub.Broadcast("chat_file_error", new global::QuicNet.Serialization.ChatApiModuleMessage14(peerId.ToString(), fileId.ToString(), error));
    }

    private async Task MaintainTransfersAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(_fileLifetime.Token).ConfigureAwait(false))
                foreach (var entry in _activeDownloads.Where(e => e.Value.Inactivity >= TimeSpan.FromMinutes(2)))
                {
                    if (!_activeDownloads.TryRemove(new KeyValuePair<(Guid, Guid), ChatFileDownload>(entry.Key, entry.Value))) continue;
                    await entry.Value.PauseAsync().ConfigureAwait(false);
                    BroadcastDownloadPaused(entry.Key.PeerId, entry.Key.FileId);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_fileLifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await ChatHandler.CancelFileAsync(entry.Key.PeerId, entry.Key.FileId, timeout.Token).ConfigureAwait(false);
                }
        }
        catch (OperationCanceledException) when (_fileLifetime.IsCancellationRequested) { }
        catch (Exception ex) { WebUiServer.LogEvent($"[CHAT] File maintenance stopped: {ex.Message}"); }
    }

    private async Task CancelPeerTransferAsync(PeerInfo peer, Guid fileId, CancellationToken ct)
    {
        var key = (peer.Id, fileId);
        bool found = _uploads.TryGetValue(key, out var upload);
        if (found)
        {
            upload!.Cancel();
            await upload.Task.ConfigureAwait(false);
        }
        if (_activeDownloads.TryRemove(key, out var download))
        {
            found = true;
            await download.PauseAsync().ConfigureAwait(false);
        }
        if (!found) return;
        BroadcastDownloadPaused(peer.Id, fileId);
    }

    private async Task CancelPeerTransfersAsync(PeerInfo peer)
    {
        foreach (var key in _downloadStarts.Keys.Where(key => key.PeerId == peer.Id)) CancelDownloadStart(key.PeerId, key.FileId);
        foreach (var entry in _uploads.Where(e => e.Key.PeerId == peer.Id)) entry.Value.Cancel();
        foreach (var entry in _activeDownloads.Where(e => e.Key.PeerId == peer.Id))
        {
            if (!_activeDownloads.TryRemove(new KeyValuePair<(Guid, Guid), ChatFileDownload>(entry.Key, entry.Value))) continue;
            await entry.Value.PauseAsync().ConfigureAwait(false);
            BroadcastDownloadPaused(peer.Id, entry.Key.FileId);
        }
    }

    private ChatHandler.FileOffer? FindOffer(Guid peerId, Guid fileId)
    {
        if (_receivedOffers.TryGetValue((peerId, fileId), out var offer)) return offer;
        if (_transferStore.Find(peerId, fileId) is { } saved)
            return new ChatHandler.FileOffer(fileId, saved.Name, saved.Size, saved.Mime, saved.Sha256);
        // Existing history remains the authoritative manifest after restarting the application.
        foreach (var message in ChatMessages.Where(m => !m.IsMe && m.PeerId == peerId.ToString() &&
            (m.ExpiresAtUtc == null || m.ExpiresAtUtc > DateTime.UtcNow)))
        {
            try
            {
                using var doc = JsonDocument.Parse(message.Message);
                var root = doc.RootElement;
                if (root.GetProperty("type").GetString() != "file_offer" || root.GetProperty("fileId").GetGuid() != fileId) continue;
                string hash = root.GetProperty("sha256").GetString() ?? "";
                long size = root.GetProperty("size").GetInt64();
                if (!ChatHandler.IsValidFileHash(hash) || size < 0) continue;
                return new ChatHandler.FileOffer(fileId, root.GetProperty("name").GetString() ?? "file", size,
                    root.GetProperty("mime").GetString() ?? "application/octet-stream", hash);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return null;
    }

    internal async Task<(bool Cached, bool InProgress, string? Path)> BeginDownloadAsync(Guid peerId, Guid fileId, CancellationToken ct = default)
    {
        var key = (peerId, fileId);
        using var start = CancellationTokenSource.CreateLinkedTokenSource(ct, _fileLifetime.Token);
        if (!_downloadStarts.TryAdd(key, start)) return (false, true, null);
        try { return await BeginDownloadCoreAsync(peerId, fileId, start.Token).ConfigureAwait(false); }
        finally { _downloadStarts.TryRemove(new KeyValuePair<(Guid, Guid), CancellationTokenSource>(key, start)); }
    }

    private void CancelDownloadStart(Guid peerId, Guid fileId)
    {
        // Pair cancellation with the reservation lock: once this returns, a
        // cancelled start cannot insert a new writer after the caller removes it.
        lock (_fileGate)
            if (_downloadStarts.TryGetValue((peerId, fileId), out var start))
                try { start.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task<(bool Cached, bool InProgress, string? Path)> BeginDownloadCoreAsync(Guid peerId, Guid fileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = (peerId, fileId);
        var offer = FindOffer(peerId, fileId);
        if (offer == null || !_qcc.IsTrustedPeer(peerId))
            throw new InvalidOperationException("No verified file offer from this trusted peer. Update both peers to the current protocol.");
        if (_completedDownloads.TryGetValue(key, out var existingPath) && File.Exists(existingPath)) return (true, false, existingPath);
        // A crash can occur between the verified rename and the SQLite completion commit.
        if (await RecoverCompletedDownloadAsync(peerId, fileId, ct).ConfigureAwait(false) is { } recoveredPath)
            return (true, false, recoveredPath);
        if (!ChatHandler.ActiveChats.ContainsKey(peerId)) throw new IOException("The sender is disconnected. Saved progress is available when it reconnects.");
        ChatFileDownload? state = null;
        try
        {
            lock (_fileGate)
            {
                ct.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (_activeDownloads.ContainsKey(key)) return (false, true, null);
                if (_activeDownloads.Count >= MaxConcurrentTransfers) throw new InvalidOperationException("Too many active downloads.");
                string name = SanitizeFileName(offer.Name);
                var saved = _transferStore.Find(peerId, fileId);
                if (saved != null && (saved.Size != offer.Size || !string.Equals(saved.Sha256, offer.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The offer changed. Discard the saved download before retrying.");
                if (saved != null && !File.Exists(saved.TempPath))
                {
                    _transferStore.Delete(peerId, fileId);
                    saved = null;
                }
                string finalPath = saved != null && !File.Exists(saved.FinalPath) ? saved.FinalPath : SafeUniquePath(_chatDownloadsPath, name);
                string tempPath = saved?.TempPath ?? Path.Combine(_chatDownloadsPath, $".{peerId:N}.{fileId:N}.{Guid.NewGuid():N}.qp-part");
                var record = saved ?? new ChatTransferRecord(peerId, fileId, name, offer.Size, offer.Mime, offer.Sha256,
                    tempPath, finalPath, 0, Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
                record = record with { FinalPath = finalPath, ExpiresAtUtc = saved?.ExpiresAtUtc ?? FindAttachmentExpiry(peerId, fileId) };
                _transferStore.Save(record);
                var stream = new FileStream(tempPath, saved == null ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                    ChatHandler.FileChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                state = new ChatFileDownload(peerId, fileId, name, offer.Size, offer.Sha256, tempPath, finalPath, stream, _transferStore, record);
                _activeDownloads[key] = state;
            }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _fileLifetime.Token);
            if (state.IsRestoring)
                _hub.Broadcast("chat_file_verifying", GetDownloadStatuses(peerId).First(r => r.fileId == fileId.ToString()));
            await state.RestoreAsync(lifetime.Token).ConfigureAwait(false);
            _hub.Broadcast("chat_file_progress", new global::QuicNet.Serialization.ChatApiModuleMessage10(peerId.ToString(), fileId.ToString(), false,
                state.ReceivedBytes, state.TotalSize, state.TotalSize > 0 ? (int)(100.0 * state.ReceivedBytes / state.TotalSize) : 0));
            if (!await ChatHandler.RequestFileAsync(peerId, fileId, state.ReceivedBytes, lifetime.Token).ConfigureAwait(false))
                throw new IOException("Could not request the file from its sender.");
            return (false, false, null);
        }
        catch (Exception ex)
        {
            if (state != null)
            {
                if (_activeDownloads.TryRemove(new KeyValuePair<(Guid, Guid), ChatFileDownload>(key, state)))
                {
                    if (ex is InvalidDataException) await state.DisposeAsync().ConfigureAwait(false);
                    else await state.PauseAsync().ConfigureAwait(false);
                }
            }
            throw;
        }
    }

    private void ForgetAttachment(WebUiServer.ChatMessage message)
    {
        if (!message.Message.StartsWith('{')) return;
        try
        {
            using var doc = JsonDocument.Parse(message.Message);
            if (!doc.RootElement.TryGetProperty("fileId", out var fileIdElement) ||
                fileIdElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(fileIdElement.GetString(), out Guid fileId)) return;
            if (_sharedFiles.TryRemove(fileId, out var shared))
            {
                _transferStore.Delete(shared.PeerId, fileId, upload: true);
                try { File.Delete(shared.FullPath); } catch { }
            }
            if (Guid.TryParse(message.PeerId, out var peerId))
            {
                CancelDownloadStart(peerId, fileId);
                _receivedOffers.TryRemove((peerId, fileId), out _);
                _completedDownloads.TryRemove((peerId, fileId), out _);
                if (_activeDownloads.TryRemove((peerId, fileId), out var download)) download.DisposeAsync().AsTask().GetAwaiter().GetResult();
                DiscardSavedDownload(peerId, fileId);
                if (_uploads.TryGetValue((peerId, fileId), out var upload)) upload.Cancel();
            }
        }
        catch (JsonException) { }
    }

    public void MarkChatConfirmed(string msgId)
    {
        lock (_chatQueueLock)
        {
            var items = ChatMessages.ToArray();
            while (ChatMessages.TryDequeue(out _)) { }
            foreach (var item in items)
                ChatMessages.Enqueue(item.MsgId == msgId ? item with { IsConfirmed = true, DeliveryState = "sent" } : item);
            _history.Confirm(msgId);
        }
    }

    private static string SanitizeFileName(string name)
    {
        string safe = Path.GetFileName(name);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in invalid)
            safe = safe.Replace(c, '_');
        return string.IsNullOrWhiteSpace(safe) ? "file" : safe;
    }

    private static string SafeUniquePath(string folder, string filename)
    {
        string destination = Path.Combine(folder, filename);
        if (!File.Exists(destination)) return destination;

        string nameOnly = Path.GetFileNameWithoutExtension(filename);
        string ext = Path.GetExtension(filename);
        for (int i = 1; i < 10000; i++)
        {
            string candidate = Path.Combine(folder, $"{nameOnly} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{nameOnly}_{Guid.NewGuid():N}{ext}");
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (await HandleDownloadRequestAsync(path, req, resp).ConfigureAwait(false)) return true;
        if (await HandleSocialRequestAsync(path, req, resp).ConfigureAwait(false)) return true;
        if (await HandleOutboxRequestAsync(path, req, resp).ConfigureAwait(false)) return true;
        if (path == "/api/chat/messages" && req.HttpMethod == "GET")
        {
            string peerId = req.QueryString["peerId"] ?? "";
            if (peerId != "global" && !Guid.TryParse(peerId, out _))
                throw new InvalidDataException("Invalid conversation.");
            if (peerId != "global") peerId = Guid.Parse(peerId).ToString();
            string? limitRaw = req.QueryString["limit"];
            if (limitRaw != null && (!int.TryParse(limitRaw, out int parsed) || parsed is < 1 or > 100))
                throw new InvalidDataException("Message limit must be between 1 and 100.");
            int limit = limitRaw == null ? 50 : int.Parse(limitRaw);
            var page = _history.LoadPage(peerId, limit, req.QueryString["before"]);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage15(page.Messages.Select(ToWebMessage), page.NextBefore)
            {
                downloads = Guid.TryParse(peerId, out var downloadPeer) ? GetDownloadStatuses(downloadPeer) : [],
                uploads = Guid.TryParse(peerId, out var uploadPeer) ? _transferStore.Load(uploadPeer)
                    .Where(record => record.IsUpload && record.Completed)
                    .Select(record => new global::QuicNet.Serialization.ChatApiModuleMessage11(record.PeerId.ToString(), record.FileId.ToString(), record.Size)).ToArray() : []
            }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/disappearing" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string peerId = doc.RootElement.GetProperty("peerId").GetString() ?? "";
            if (!Guid.TryParse(peerId, out _) && peerId != "global")
                throw new InvalidDataException("Invalid conversation.");
            int seconds = doc.RootElement.GetProperty("seconds").GetInt32();
            if (seconds is not (0 or 60 or 3600 or 86400 or 604800 or 2592000))
                throw new InvalidDataException("Choose a supported disappearing message duration.");
            _history.SetDuration(peerId, seconds);
            _disappearingSeconds[peerId] = seconds;
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage17(true, peerId, seconds)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat-send" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string peerIdRaw = doc.RootElement.TryGetProperty("peerId", out var pEl) ? pEl.GetString() ?? "" : "";
            string message = doc.RootElement.GetProperty("message").GetString() ?? "";
            string myName = _qcc.CurrentPeer?.Name ?? "Me";

            // 1. GLOBAL CHAT
            if (string.Equals(peerIdRaw, "global", StringComparison.OrdinalIgnoreCase))
            {
                if (!_chatHandler.NostrMessagingEnabled)
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, "El canal público está temporalmente desactivado. Abre una conversación con un contacto."), 409).ConfigureAwait(false);
                    return true;
                }
                if (_qcc.Mailbox == null)
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, "Nostr mailbox is not initialized."), 503).ConfigureAwait(false);
                    return true;
                }

                string eventId = await _qcc.Mailbox.SendGlobalMessageAsync(myName, message).ConfigureAwait(false);

                var myMsg = EnqueueChat(new WebUiServer.ChatMessage("global", eventId, myName, message, DateTime.Now, true, true));
                _hub.Broadcast("chat_msg", new global::QuicNet.Serialization.ChatApiModuleMessage19("global", myMsg.MsgId, myMsg.Sender, myMsg.Message, myMsg.Timestamp.ToString("HH:mm:ss"), true, true, true, ToUnixMs(myMsg.ExpiresAtUtc)) { timestampMs = new DateTimeOffset(myMsg.Timestamp).ToUnixTimeMilliseconds() });
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage20(true, eventId, true)).ConfigureAwait(false);
                return true;
            }

            // 2. DIRECT PEER MESSAGE
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            myName = LocalSenderName(peerId);
            int disappearAfterSeconds = GetDisappearingSeconds(peerId.ToString());
            var (sent, msgId, isOffline) = await _chatHandler.SendMessageAsync(peerId, myName, message,
                disappearAfterSeconds: disappearAfterSeconds).ConfigureAwait(false);

            if (sent)
            {
                _qcc.RecordPeerUsage(peerId);
                var myMsg = EnqueueChat(new WebUiServer.ChatMessage(peerId.ToString(), msgId, "Me", message, DateTime.Now, true, isOffline));
                _hub.Broadcast("chat_msg", new global::QuicNet.Serialization.ChatApiModuleMessage2(myMsg.PeerId, myMsg.MsgId, myMsg.Sender, myMsg.Message, myMsg.Timestamp.ToString("HH:mm:ss"), true, myMsg.IsConfirmed, isOffline, ToUnixMs(myMsg.ExpiresAtUtc)) { timestampMs = new DateTimeOffset(myMsg.Timestamp).ToUnixTimeMilliseconds() });
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage21(true, msgId, isOffline)).ConfigureAwait(false);
                return true;
            }

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, "No se pudo enviar el mensaje porque el contacto está desconectado. El borrador se conserva; vuelve a intentarlo cuando se conecte."), 409).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/share-file" && req.HttpMethod == "POST")
        {
            string? peerIdStr = req.Headers["X-Peer-Id"] ?? req.QueryString["peerId"];
            if (string.IsNullOrWhiteSpace(peerIdStr) || !Guid.TryParse(peerIdStr, out var peerId))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, "Missing or invalid X-Peer-Id."), 400).ConfigureAwait(false);
                return true;
            }

            string fileName;
            string? b64Name = req.Headers["X-File-Name-B64"];
            if (!string.IsNullOrWhiteSpace(b64Name))
            {
                try { fileName = Encoding.UTF8.GetString(Convert.FromBase64String(b64Name)); }
                catch { fileName = "file"; }
            }
            else
            {
                fileName = req.Headers["X-File-Name"] ?? "file";
            }
            fileName = SanitizeFileName(fileName);

            string mime = req.Headers["Content-Type"] ?? "application/octet-stream";
            Guid fileId = Guid.NewGuid();
            string outboxFilePath = Path.Combine(_chatOutboxPath, $"{fileId:N}_{fileName}");

            if (!_qcc.IsTrustedPeer(peerId) || !ChatHandler.ActiveChats.ContainsKey(peerId))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, "The recipient must be trusted and connected."), 409).ConfigureAwait(false);
                return true;
            }
            long totalBytes = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                const int bufferSize = 64 * 1024;
                byte[] buffer = new byte[bufferSize];
                await using (var output = new FileStream(outboxFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (true)
                    {
                        int read = await req.InputStream.ReadAsync(buffer.AsMemory(0, bufferSize), _fileLifetime.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        totalBytes = checked(totalBytes + read);
                        if (totalBytes > WebUiContext.GetRequestBodyLimit(path)) throw new InvalidDataException("El archivo supera el tamaño máximo permitido.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), _fileLifetime.Token).ConfigureAwait(false);
                    }
                    if (req.ContentLength64 >= 0 && totalBytes != req.ContentLength64)
                        throw new EndOfStreamException("La carga se interrumpió antes de recibir el archivo completo.");
                    await output.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                try { File.Delete(outboxFilePath); } catch { }
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, $"Failed to save file: {ex.Message}"), 500).ConfigureAwait(false);
                return true;
            }

            if (!_qcc.IsTrustedPeer(peerId) || !ChatHandler.ActiveChats.ContainsKey(peerId))
            {
                try { File.Delete(outboxFilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, "El contacto se desconectó o perdió sus permisos durante la carga. Vuelve a intentarlo cuando esté conectado."), 409).ConfigureAwait(false);
                return true;
            }

            string sha256 = Convert.ToHexString(hash.GetHashAndReset());
            var sharedInfo = new SharedFileInfo(fileId, fileName, outboxFilePath, totalBytes, mime, peerId, sha256);
            _sharedFiles[fileId] = sharedInfo;
            int disappearSeconds = GetDisappearingSeconds(peerId.ToString());
            var uploadRecord = new ChatTransferRecord(peerId, fileId, fileName, totalBytes, mime, sha256,
                "", outboxFilePath, 0, "", IsUpload: true,
                ExpiresAtUtc: disappearSeconds > 0 ? DateTime.UtcNow.AddSeconds(disappearSeconds) : null);
            _transferStore.Save(uploadRecord);

            var (sent, msgId) = await ChatHandler.SendFileOfferAsync(peerId, LocalSenderName(peerId), fileId, fileName, totalBytes, mime, sha256, _fileLifetime.Token).ConfigureAwait(false);

            string offerPayload = JsonSerializer.Serialize(new global::QuicNet.Serialization.ChatApiModuleMessage22("file_offer", fileId.ToString(), fileName, totalBytes, sha256, mime, true, outboxFilePath),global::QuicNet.WebUi.WebUiContext.JsonOptions);

            var myMsg = EnqueueChat(new WebUiServer.ChatMessage(peerId.ToString(), msgId, "Me", offerPayload, DateTime.Now, true, sent));
            _transferStore.UpdateUpload(peerId, fileId, record => record with { ExpiresAtUtc = myMsg.ExpiresAtUtc });

            _hub.Broadcast("chat_msg", new global::QuicNet.Serialization.ChatApiModuleMessage9(myMsg.PeerId, myMsg.MsgId, myMsg.Sender, myMsg.Message, myMsg.Timestamp.ToString("HH:mm:ss"), myMsg.IsMe, myMsg.IsConfirmed, ToUnixMs(myMsg.ExpiresAtUtc)) { timestampMs = new DateTimeOffset(myMsg.Timestamp).ToUnixTimeMilliseconds() });

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage23(sent, sent ? null : "The recipient disconnected before the offer was delivered.", msgId, fileId.ToString(), fileName, totalBytes)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/retry-file-offer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            Guid fileId = WebUiContext.ParseGuid(doc.RootElement, "fileId");
            string messageId = WebUiContext.ParseGuid(doc.RootElement, "msgId").ToString();
            var message = _history.GetMessage(peerId.ToString(), messageId);
            if (!_qcc.IsTrustedPeer(peerId) || !_sharedFiles.TryGetValue(fileId, out var shared) || shared.PeerId != peerId ||
                !File.Exists(shared.FullPath) || message == null || !message.IsMe ||
                (message.ExpiresAtUtc is { } expiry && expiry <= DateTime.UtcNow))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, "El adjunto ya no está disponible o el contacto no tiene permiso. Vuelve a elegir el archivo si quieres compartirlo."), 409).ConfigureAwait(false);
                return true;
            }
            using var original = JsonDocument.Parse(message.Message);
            if (!original.RootElement.TryGetProperty("fileId", out var originalId) || !originalId.TryGetGuid(out var originalFileId) || originalFileId != fileId)
                throw new InvalidDataException("La oferta no corresponde al archivo solicitado.");
            bool sent = message.IsConfirmed;
            if (!sent)
            {
                (sent, _) = await ChatHandler.SendFileOfferAsync(peerId, LocalSenderName(peerId), fileId, shared.Name,
                    shared.Size, shared.Mime, shared.Sha256, _fileLifetime.Token, messageId).ConfigureAwait(false);
                if (sent)
                {
                    MarkChatConfirmed(messageId);
                    _hub.Broadcast("chat_ack", new global::QuicNet.Serialization.ChatApiModuleMessage3(messageId));
                }
            }
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage23(sent,
                sent ? null : "El contacto sigue desconectado. El archivo preparado se conserva para reintentarlo.",
                messageId, fileId.ToString(), shared.Name, shared.Size), sent ? 200 : 409).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/download-file" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            Guid fileId = WebUiContext.ParseGuid(doc.RootElement, "fileId");
            try
            {
                var result = await BeginDownloadAsync(peerId, fileId, _fileLifetime.Token).ConfigureAwait(false);
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage24(true, result.Cached, result.InProgress, !result.Cached && !result.InProgress, result.Path)
                { download = GetDownloadStatuses(peerId).FirstOrDefault(r => r.fileId == fileId.ToString()) }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, ex.Message), 409).ConfigureAwait(false);
            }
            return true;
        }

        if (path == "/api/chat/cancel-file" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            Guid fileId = WebUiContext.ParseGuid(doc.RootElement, "fileId");
            var key = (peerId, fileId);
            CancelDownloadStart(peerId, fileId);
            if (_uploads.TryGetValue(key, out var upload)) upload.Cancel();
            if (_activeDownloads.TryRemove(key, out var download)) await download.DisposeAsync().ConfigureAwait(false);
            DiscardSavedDownload(peerId, fileId, preserveCompleted: true);
            await ChatHandler.CancelFileAsync(peerId, fileId, _fileLifetime.Token).ConfigureAwait(false);
            _hub.Broadcast("chat_file_cancelled", new global::QuicNet.Serialization.ChatApiModuleMessage12(peerId.ToString(), fileId.ToString()));
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage25(true)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/open-file" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid fileId = WebUiContext.ParseGuid(doc.RootElement, "fileId");
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            string? filePath = null;
            string? fileName = null;
            string? mime = null;
            // The client supplies an attachment ID, never authority to open an arbitrary local path.
            if (_completedDownloads.TryGetValue((peerId, fileId), out var cd))
                filePath = cd;
            else if (_sharedFiles.TryGetValue(fileId, out var sf) && sf.PeerId == peerId)
            {
                filePath = sf.FullPath;
                fileName = sf.Name;
                mime = sf.Mime;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, "File not found on disk."), 404).ConfigureAwait(false);
                return true;
            }

            bool openFolder = doc.RootElement.TryGetProperty("openFolder", out var ofEl) && ofEl.GetBoolean();
            try
            {
                filePath = Path.GetFullPath(filePath);
                fileName ??= Path.GetFileName(filePath);
                mime ??= GetMimeFromExtension(Path.GetExtension(fileName));
                if (FileOpener != null)
                {
                    if (openFolder) throw new NotSupportedException("En este dispositivo, abre el archivo con una aplicación compatible.");
                    await FileOpener.OpenAsync(filePath, fileName, mime, _fileLifetime.Token).ConfigureAwait(false);
                }
                else if (OperatingSystem.IsAndroid())
                    throw new NotSupportedException("La apertura de archivos no está disponible en esta versión de Android.");
                else if (openFolder)
                {
                    if (OperatingSystem.IsWindows())
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
                    else
                        Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(filePath) ?? filePath, UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true });
                }
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage25(true)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage18(false, ex.Message), 500).ConfigureAwait(false);
            }
            return true;
        }

        if (path == "/api/chat/typing" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            bool isTyping = doc.RootElement.TryGetProperty("isTyping", out var itEl) && itEl.GetBoolean();
            bool sent = _qcc.IsTrustedPeer(peerId) && await ChatHandler.SendTypingAsync(peerId, isTyping).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage25(sent), sent ? 200 : 409).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/clear" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string peerIdStr = doc.RootElement.TryGetProperty("peerId", out var pEl) ? pEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(peerIdStr))
            {
                Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
                peerIdStr = peerId.ToString();
            }

            WebUiServer.ChatMessage[] cleared;
            await _outboxGate.WaitAsync(_fileLifetime.Token).ConfigureAwait(false);
            try
            {
              lock (_chatQueueLock)
              {
                cleared = ChatMessages.Where(m => m.PeerId == peerIdStr).ToArray();
                var remaining = ChatMessages.Where(m => m.PeerId != peerIdStr).ToList();
                while (ChatMessages.TryDequeue(out _)) { }
                _chatHistoryBytes = 0;
                foreach (var item in remaining)
                {
                    ChatMessages.Enqueue(item);
                    _chatHistoryBytes += Encoding.UTF8.GetByteCount(item.Message);
                }
                _history.DeletePeer(peerIdStr);
              }
            }
            finally { _outboxGate.Release(); }
            foreach (var message in cleared) ForgetAttachment(message);

            _hub.Broadcast("chat_cleared", new global::QuicNet.Serialization.ChatApiModuleMessage5(peerIdStr));
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ChatApiModuleMessage25(true)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/view-file" && req.HttpMethod == "GET")
        {
            string? fileIdStr = req.QueryString["fileId"];
            if (string.IsNullOrWhiteSpace(fileIdStr) || !Guid.TryParse(fileIdStr, out var fileId))
            {
                resp.StatusCode = 400;
                return true;
            }

            string? filePath = null;
            string? mime = null;

            if (_completedDownloads.FirstOrDefault(e => e.Key.FileId == fileId).Value is { } cdPath && File.Exists(cdPath))
            {
                filePath = cdPath;
            }
            else if (_sharedFiles.TryGetValue(fileId, out var sf) && File.Exists(sf.FullPath))
            {
                filePath = sf.FullPath;
                mime = sf.Mime;
            }

            if (filePath == null || !File.Exists(filePath))
            {
                resp.StatusCode = 404;
                return true;
            }

            mime ??= GetMimeFromExtension(Path.GetExtension(filePath));

            try
            {
                var fileInfo = new FileInfo(filePath);
                long totalLength = fileInfo.Length;

                resp.ContentType = mime;
                resp.AddHeader("Accept-Ranges", "bytes");

                string? rangeHeader = req.Headers["Range"];
                if (!string.IsNullOrWhiteSpace(rangeHeader) && rangeHeader.StartsWith("bytes="))
                {
                    string rangeSpec = rangeHeader["bytes=".Length..].Trim();
                    string[] parts = rangeSpec.Split('-');
                    long start = 0;
                    long end = totalLength - 1;

                    if (!string.IsNullOrEmpty(parts[0]))
                        long.TryParse(parts[0], out start);
                    if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
                        long.TryParse(parts[1], out end);

                    start = Math.Max(0, Math.Min(start, totalLength - 1));
                    end = Math.Max(start, Math.Min(end, totalLength - 1));
                    long rangeLength = end - start + 1;

                    resp.StatusCode = 206;
                    resp.AddHeader("Content-Range", $"bytes {start}-{end}/{totalLength}");
                    resp.ContentLength64 = rangeLength;

                    await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    fs.Seek(start, SeekOrigin.Begin);

                    byte[] buf = new byte[64 * 1024];
                    long remaining = rangeLength;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buf.Length, remaining);
                        int read = await fs.ReadAsync(buf.AsMemory(0, toRead)).ConfigureAwait(false);
                        if (read == 0) break;
                        await resp.OutputStream.WriteAsync(buf.AsMemory(0, read)).ConfigureAwait(false);
                        remaining -= read;
                    }
                }
                else
                {
                    resp.StatusCode = 200;
                    resp.ContentLength64 = totalLength;
                    await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await fs.CopyToAsync(resp.OutputStream).ConfigureAwait(false);
                }
            }
            catch
            {
                // Client aborted or socket closed
            }
            return true;
        }

        return false;
    }

    private static string GetMimeFromExtension(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".m4a" or ".aac" => "audio/mp4",
            ".webm" => "video/webm",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _expiryTimer.Dispose();
        _chatHandler.OnActionsReceived -= ReceiveActions;
        _chatHandler.OnGetActionsForPeer -= GetActionsForPeer;
        _fileLifetime.Cancel();
        _chatHandler.OnVerifiedFileOfferReceived -= ReceiveFileOffer;
        _chatHandler.OnFileRequestReceived -= StartUpload;
        _chatHandler.OnFileChunkReceivedAsync -= ReceiveFileChunkAsync;
        _chatHandler.OnFileCancelReceivedAsync -= CancelPeerTransferAsync;
        _chatHandler.OnFileCompletedReceived -= ConfirmUpload;
        _chatHandler.OnPeerDisconnectedAsync -= CancelPeerTransfersAsync;
        foreach (var upload in _uploads.Values) upload.Cancel();
        foreach (var download in _activeDownloads.Values) download.Cancel();
        foreach (var entry in _activeDownloads.ToArray())
            if (_activeDownloads.TryRemove(entry)) entry.Value.PauseAsync().GetAwaiter().GetResult();
        _activeDownloads.Clear();
        Task.WhenAll(_uploads.Values.Select(upload => upload.Task)).GetAwaiter().GetResult();
        _fileMaintenanceTask.GetAwaiter().GetResult();
    }
}
