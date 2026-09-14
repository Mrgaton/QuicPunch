using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using QuicPunch;
using QuicPunchTests.Protocols;

namespace QuicPunchTests.WebUi.Modules;

internal sealed class ChatApiModule
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly ChatHandler _chatHandler;
    private readonly WebUiWebSocketHub _hub;
    private readonly object _chatQueueLock = new();
    private long _chatHistoryBytes;

    private readonly string _chatOutboxPath;
    private readonly string _chatDownloadsPath;

    public sealed record SharedFileInfo(Guid FileId, string Name, string FullPath, long Size, string Mime);

    private sealed class ActiveDownloadState : IDisposable
    {
        public Guid PeerId { get; }
        public Guid FileId { get; }
        public string FileName { get; }
        public long TotalSize { get; }
        public string TempPath { get; }
        public string FinalPath { get; }
        public FileStream Stream { get; set; }
        public long ReceivedBytes;
        public DateTime LastProgressBroadcastUtc = DateTime.MinValue;

        public ActiveDownloadState(Guid peerId, Guid fileId, string fileName, long totalSize, string tempPath, string finalPath, FileStream stream)
        {
            PeerId = peerId;
            FileId = fileId;
            FileName = fileName;
            TotalSize = totalSize;
            TempPath = tempPath;
            FinalPath = finalPath;
            Stream = stream;
        }

        public void Dispose()
        {
            try { Stream?.Dispose(); } catch { }
            Stream = null!;
        }
    }

    private readonly ConcurrentDictionary<Guid, SharedFileInfo> _sharedFiles = new();
    private readonly ConcurrentDictionary<Guid, ActiveDownloadState> _activeDownloads = new();
    private readonly ConcurrentDictionary<Guid, string> _completedDownloads = new();

    public ConcurrentQueue<WebUiServer.ChatMessage> ChatMessages { get; } = new();

    public ChatApiModule(QuicPunch.QuicPunch qcc, ChatHandler chatHandler, WebUiWebSocketHub hub)
    {
        _qcc = qcc;
        _chatHandler = chatHandler;
        _hub = hub;

        _chatOutboxPath = Path.Combine(Path.GetTempPath(), "QuicPunch", "ChatOutbox");
        _chatDownloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "QuicPunch");

        try { Directory.CreateDirectory(_chatOutboxPath); } catch { }
        try { Directory.CreateDirectory(_chatDownloadsPath); } catch { }

        _chatHandler.OnMessageReceived += (peer, message, msgId) =>
        {
            var msg = new WebUiServer.ChatMessage(peer.Id.ToString(), msgId, peer.Name ?? "Unknown", message, DateTime.Now, false, true);
            EnqueueChat(msg);
            WebUiServer.LogEvent($"[CHAT] Message from {peer.Name ?? "Unknown"}");
            _hub.Broadcast("chat_msg", new
            {
                peerId = msg.PeerId,
                msgId = msg.MsgId,
                sender = msg.Sender,
                message = msg.Message,
                time = msg.Timestamp.ToString("HH:mm:ss"),
                isMe = msg.IsMe,
                isConfirmed = msg.IsConfirmed
            });
        };

        _chatHandler.OnMessageAckReceived += (_, msgId) =>
        {
            MarkChatConfirmed(msgId);
            _hub.Broadcast("chat_ack", new { msgId });
        };

        _chatHandler.OnTypingReceived += (peer, isTyping) =>
        {
            _hub.Broadcast("chat_typing", new
            {
                peerId = peer.Id.ToString(),
                peerName = peer.Name ?? "Peer",
                isTyping
            });
        };

        _chatHandler.OnFileOfferReceived += (peer, msgId, fileId, fileName, fileSize, mime, sender) =>
        {
            string offerPayload = JsonSerializer.Serialize(new
            {
                type = "file_offer",
                fileId = fileId.ToString(),
                name = fileName,
                size = fileSize,
                mime
            });
            var msg = new WebUiServer.ChatMessage(peer.Id.ToString(), msgId, sender, offerPayload, DateTime.Now, false, true);
            EnqueueChat(msg);
            WebUiServer.LogEvent($"[CHAT] File offered by {sender}: {fileName} ({fileSize / 1024.0 / 1024.0:F1} MB)");
            _hub.Broadcast("chat_msg", new
            {
                peerId = msg.PeerId,
                msgId = msg.MsgId,
                sender = msg.Sender,
                message = msg.Message,
                time = msg.Timestamp.ToString("HH:mm:ss"),
                isMe = msg.IsMe,
                isConfirmed = msg.IsConfirmed
            });
        };

        _chatHandler.OnFileRequestReceived += (peer, fileId, offset) =>
        {
            if (!_sharedFiles.TryGetValue(fileId, out var item) || !File.Exists(item.FullPath))
            {
                Console.WriteLine($"[CHAT] File {fileId} requested by {peer.Name} not found.");
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    WebUiServer.LogEvent($"[CHAT] Streaming {item.Name} to {peer.Name}...");
                    const int bufferSize = 64 * 1024;
                    byte[] buffer = new byte[bufferSize];
                    await using var fs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (offset > 0 && offset < fs.Length)
                        fs.Seek(offset, SeekOrigin.Begin);

                    long currentOffset = fs.Position;
                    long total = fs.Length;
                    DateTime lastProgress = DateTime.MinValue;

                    while (currentOffset < total)
                    {
                        int read = await fs.ReadAsync(buffer.AsMemory(0, bufferSize)).ConfigureAwait(false);
                        if (read == 0) break;
                        bool isEnd = (currentOffset + read) >= total;
                        byte[] frame = ChatHandler.BuildFileChunkFrame(fileId, currentOffset, buffer, read, isEnd);
                        bool ok = await ChatHandler.SendBinaryChunkAsync(peer.Id, frame).ConfigureAwait(false);
                        if (!ok) break;

                        currentOffset += read;
                        if (DateTime.UtcNow - lastProgress > TimeSpan.FromMilliseconds(150) || isEnd)
                        {
                            lastProgress = DateTime.UtcNow;
                            _hub.Broadcast("chat_file_progress", new
                              {
                                peerId = peer.Id.ToString(),
                                fileId = fileId.ToString(),
                                isUpload = true,
                                received = currentOffset,
                                total,
                                percent = total > 0 ? (int)(currentOffset * 100 / total) : 100
                            });
                        }
                    }
                    WebUiServer.LogEvent($"[CHAT] Completed sending {item.Name} to {peer.Name}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CHAT] Error streaming file: {ex.Message}");
                }
            });
        };

        _chatHandler.OnFileChunkReceived += (peer, fileId, offset, data, isEnd) =>
        {
            if (!_activeDownloads.TryGetValue(fileId, out var download)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await download.Stream.WriteAsync(data).ConfigureAwait(false);
                    download.ReceivedBytes += data.Length;

                    if (DateTime.UtcNow - download.LastProgressBroadcastUtc > TimeSpan.FromMilliseconds(120) || isEnd)
                    {
                        download.LastProgressBroadcastUtc = DateTime.UtcNow;
                        _hub.Broadcast("chat_file_progress", new
                        {
                            peerId = peer.Id.ToString(),
                            fileId = fileId.ToString(),
                            isUpload = false,
                            received = download.ReceivedBytes,
                            total = download.TotalSize,
                            percent = download.TotalSize > 0 ? (int)(download.ReceivedBytes * 100 / download.TotalSize) : 100
                        });
                    }

                    if (isEnd)
                    {
                        await download.Stream.FlushAsync().ConfigureAwait(false);
                        download.Dispose();

                        if (File.Exists(download.FinalPath))
                            try { File.Delete(download.FinalPath); } catch { }
                        File.Move(download.TempPath, download.FinalPath, true);

                        _completedDownloads[fileId] = download.FinalPath;
                        _activeDownloads.TryRemove(fileId, out _);

                        WebUiServer.LogEvent($"[CHAT] File downloaded: {download.FileName} -> {download.FinalPath}");
                        _hub.Broadcast("chat_file_complete", new
                        {
                            peerId = peer.Id.ToString(),
                            fileId = fileId.ToString(),
                            name = download.FileName,
                            path = download.FinalPath
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CHAT] Error writing chunk: {ex.Message}");
                }
            });
        };

        _chatHandler.OnFileCancelReceived += (peer, fileId) =>
        {
            if (_activeDownloads.TryRemove(fileId, out var download))
            {
                download.Dispose();
                try { File.Delete(download.TempPath); } catch { }
            }
            _hub.Broadcast("chat_file_cancelled", new { peerId = peer.Id.ToString(), fileId = fileId.ToString() });
        };

        _chatHandler.OnGetHistoryForPeer += peerId => ChatMessages
            .Where(m => m.PeerId == peerId.ToString())
            .Select(m => (m.MsgId, m.Sender, m.Message, m.Timestamp))
            .ToList();

        _chatHandler.OnHistorySyncReceived += (peer, items) =>
        {
            bool anyNew = false;
            foreach (var item in items)
            {
                if (!ChatMessages.Any(m => m.MsgId == item.MsgId))
                {
                    var msg = new WebUiServer.ChatMessage(peer.Id.ToString(), item.MsgId, item.Sender, item.Content, item.Timestamp, false, true);
                    EnqueueChat(msg);
                    anyNew = true;
                }
            }
            if (anyNew)
            {
                _hub.Broadcast("chat_sync", new { peerId = peer.Id.ToString() });
            }
        };

        _chatHandler.OnPeerConnected += peer =>
        {
            WebUiServer.ClearPeerPetitions(peer.Id);
            _hub.Broadcast("peer_connected", new { peerId = peer.Id.ToString(), protocol = "chat" });
        };

        _chatHandler.OnPeerDisconnected += peer =>
        {
            _hub.Broadcast("peer_disconnected", new { peerId = peer.Id.ToString(), protocol = "chat" });
        };
    }

    public void EnqueueChat(WebUiServer.ChatMessage message)
    {
        lock (_chatQueueLock)
        {
            ChatMessages.Enqueue(message);
            _chatHistoryBytes += Encoding.UTF8.GetByteCount(message.Message);
            while ((ChatMessages.Count > 1000 || _chatHistoryBytes > WebUiContext.MaxChatHistoryBytes) && ChatMessages.TryDequeue(out var removed))
                _chatHistoryBytes = Math.Max(0, _chatHistoryBytes - Encoding.UTF8.GetByteCount(removed.Message));
        }
    }

    public void MarkChatConfirmed(string msgId)
    {
        lock (_chatQueueLock)
        {
            var items = ChatMessages.ToArray();
            while (ChatMessages.TryDequeue(out _)) { }
            foreach (var item in items)
                ChatMessages.Enqueue(item.MsgId == msgId ? item with { IsConfirmed = true } : item);
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
        if (path == "/api/chat-send" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            string message = doc.RootElement.GetProperty("message").GetString() ?? "";
            var (sent, msgId) = await ChatHandler.SendMessageAsync(peerId, _qcc.CurrentPeer?.Name ?? "Me", message).ConfigureAwait(false);
            if (!sent)
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Chat is not connected." }, 409).ConfigureAwait(false);
                return true;
            }
            var myMsg = new WebUiServer.ChatMessage(peerId.ToString(), msgId, "Me", message, DateTime.Now, true, false);
            EnqueueChat(myMsg);
            _hub.Broadcast("chat_msg", new
            {
                peerId = myMsg.PeerId,
                msgId = myMsg.MsgId,
                sender = myMsg.Sender,
                message = myMsg.Message,
                time = myMsg.Timestamp.ToString("HH:mm:ss"),
                isMe = myMsg.IsMe,
                isConfirmed = myMsg.IsConfirmed
            });
            await WebUiContext.WriteJsonAsync(resp, new { success = true, msgId }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/share-file" && req.HttpMethod == "POST")
        {
            string? peerIdStr = req.Headers["X-Peer-Id"] ?? req.QueryString["peerId"];
            if (string.IsNullOrWhiteSpace(peerIdStr) || !Guid.TryParse(peerIdStr, out var peerId))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Missing or invalid X-Peer-Id." }, 400).ConfigureAwait(false);
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

            long totalBytes = 0;
            try
            {
                const int bufferSize = 64 * 1024;
                byte[] buffer = new byte[bufferSize];
                await using (var output = new FileStream(outboxFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (true)
                    {
                        int read = await req.InputStream.ReadAsync(buffer.AsMemory(0, bufferSize)).ConfigureAwait(false);
                        if (read == 0) break;
                        totalBytes += read;
                        await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    }
                    await output.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                try { File.Delete(outboxFilePath); } catch { }
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = $"Failed to save file: {ex.Message}" }, 500).ConfigureAwait(false);
                return true;
            }

            var sharedInfo = new SharedFileInfo(fileId, fileName, outboxFilePath, totalBytes, mime);
            _sharedFiles[fileId] = sharedInfo;

            var (sent, msgId) = await ChatHandler.SendFileOfferAsync(peerId, _qcc.CurrentPeer?.Name ?? "Me", fileId, fileName, totalBytes, mime).ConfigureAwait(false);

            string offerPayload = JsonSerializer.Serialize(new
            {
                type = "file_offer",
                fileId = fileId.ToString(),
                name = fileName,
                size = totalBytes,
                mime,
                isMe = true,
                path = outboxFilePath
            });

            var myMsg = new WebUiServer.ChatMessage(peerId.ToString(), msgId, "Me", offerPayload, DateTime.Now, true, sent);
            EnqueueChat(myMsg);

            _hub.Broadcast("chat_msg", new
            {
                peerId = myMsg.PeerId,
                msgId = myMsg.MsgId,
                sender = myMsg.Sender,
                message = myMsg.Message,
                time = myMsg.Timestamp.ToString("HH:mm:ss"),
                isMe = myMsg.IsMe,
                isConfirmed = myMsg.IsConfirmed
            });

            await WebUiContext.WriteJsonAsync(resp, new
            {
                success = true,
                msgId,
                fileId = fileId.ToString(),
                name = fileName,
                size = totalBytes
            }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/download-file" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            Guid fileId = WebUiContext.ParseGuid(doc.RootElement, "fileId");
            string fileName = doc.RootElement.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "file" : "file";
            long size = doc.RootElement.TryGetProperty("size", out var sEl) ? sEl.GetInt64() : 0;
            fileName = SanitizeFileName(fileName);

            if (_completedDownloads.TryGetValue(fileId, out var existingPath) && File.Exists(existingPath))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = true, cached = true, path = existingPath }).ConfigureAwait(false);
                return true;
            }

            if (_activeDownloads.ContainsKey(fileId))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = true, inProgress = true }).ConfigureAwait(false);
                return true;
            }

            string finalPath = SafeUniquePath(_chatDownloadsPath, fileName);
            string tempPath = finalPath + ".qp-part";

            try
            {
                var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var state = new ActiveDownloadState(peerId, fileId, fileName, size, tempPath, finalPath, fs);
                _activeDownloads[fileId] = state;

                bool requested = await ChatHandler.RequestFileAsync(peerId, fileId, 0).ConfigureAwait(false);
                if (!requested)
                {
                    _activeDownloads.TryRemove(fileId, out _);
                    state.Dispose();
                    try { File.Delete(tempPath); } catch { }
                    await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Failed to request file from peer." }, 502).ConfigureAwait(false);
                    return true;
                }

                await WebUiContext.WriteJsonAsync(resp, new { success = true, started = true }).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = ex.Message }, 500).ConfigureAwait(false);
                return true;
            }
        }

        if (path == "/api/chat/cancel-file" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            Guid fileId = WebUiContext.ParseGuid(doc.RootElement, "fileId");

            if (_activeDownloads.TryRemove(fileId, out var download))
            {
                download.Dispose();
                try { File.Delete(download.TempPath); } catch { }
            }
            await ChatHandler.CancelFileAsync(peerId, fileId).ConfigureAwait(false);
            _hub.Broadcast("chat_file_cancelled", new { peerId = peerId.ToString(), fileId = fileId.ToString() });

            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/open-file" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string? filePath = doc.RootElement.TryGetProperty("path", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(filePath) && doc.RootElement.TryGetProperty("fileId", out var fidEl) && Guid.TryParse(fidEl.GetString(), out var fileId))
            {
                if (_completedDownloads.TryGetValue(fileId, out var cd)) filePath = cd;
                else if (_sharedFiles.TryGetValue(fileId, out var sf)) filePath = sf.FullPath;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "File not found on disk." }, 404).ConfigureAwait(false);
                return true;
            }

            bool openFolder = doc.RootElement.TryGetProperty("openFolder", out var ofEl) && ofEl.GetBoolean();
            try
            {
                if (openFolder)
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
                await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = ex.Message }, 500).ConfigureAwait(false);
            }
            return true;
        }

        if (path == "/api/chat/typing" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            bool isTyping = doc.RootElement.TryGetProperty("isTyping", out var itEl) && itEl.GetBoolean();
            await ChatHandler.SendTypingAsync(peerId, isTyping).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/chat/clear" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            string peerIdStr = peerId.ToString();

            lock (_chatQueueLock)
            {
                var remaining = ChatMessages.Where(m => m.PeerId != peerIdStr).ToList();
                while (ChatMessages.TryDequeue(out _)) { }
                _chatHistoryBytes = 0;
                foreach (var item in remaining)
                {
                    ChatMessages.Enqueue(item);
                    _chatHistoryBytes += Encoding.UTF8.GetByteCount(item.Message);
                }
            }

            _hub.Broadcast("chat_cleared", new { peerId = peerIdStr });
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
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

            if (_completedDownloads.TryGetValue(fileId, out var cdPath) && File.Exists(cdPath))
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
}
