using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using QuicPunch;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicNet.Protocols
{
    public sealed partial class ChatHandler : QuicPunch.QuicPunch.IProtocolHandler
    {
        private const int MaxFrameBytes = 32 * 1024 * 1024;
        internal const int FileChunkBytes = 64 * 1024;
        public sealed record FileOffer(Guid FileId, string Name, long Size, string Mime, string Sha256);
        internal static bool IsValidFileHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public string ProtocolName => "Chat";
        public bool SupportsSessionRecovery => true;
        public ushort StreamPriority => (ushort)QuicPunch.Helpers.QuicStreamPriority.High;

        // Temporarily paused for chat only; retain the implementation for a future opt-in.
        public bool NostrMessagingEnabled { get; init; } = false;
        public bool SupportsMailbox => NostrMessagingEnabled;

        private Func<PeerInfo, PeerInfo>? _localIdentityForPeer;
        public void OnLocalIdentityProviderAttached(Func<PeerInfo, PeerInfo> provider) => _localIdentityForPeer = provider;
        private IMailboxChannel? _mailboxChannel;
        public IMailboxChannel? MailboxChannel => _mailboxChannel;

        public void OnMailboxAttached(IMailboxChannel channel)
        {
            if (NostrMessagingEnabled) _mailboxChannel = channel;
        }

        public Task OnOfflineMessageReceivedAsync(PeerInfo peer, ReadOnlyMemory<byte> payload, DateTimeOffset timestamp, string messageId)
        {
            if (!NostrMessagingEnabled) return Task.CompletedTask;
            string text = "";
            try { text = Encoding.UTF8.GetString(payload.Span); } catch { }
            if (!string.IsNullOrEmpty(text))
            {
                int? disappearAfterSeconds = null;
                const string envelopePrefix = "\u001eQPCHAT/1\n";
                if (text.StartsWith(envelopePrefix, StringComparison.Ordinal))
                {
                    try
                    {
                        using var envelope = JsonDocument.Parse(text[envelopePrefix.Length..]);
                        string? content = envelope.RootElement.GetProperty("content").GetString();
                        if (content != null)
                        {
                            text = content;
                            if (envelope.RootElement.TryGetProperty("disappearAfterSeconds", out var ttlEl) &&
                                ttlEl.ValueKind == JsonValueKind.Number && ttlEl.TryGetInt32(out int ttl) && ttl is > 0 and <= 2592000)
                                disappearAfterSeconds = ttl;
                        }
                    }
                    catch (JsonException) { }
                }
                OnMessageReceived?.Invoke(peer, text, messageId, disappearAfterSeconds);
            }
            return Task.CompletedTask;
        }

        public event Action<PeerInfo, string, string, int?>? OnMessageReceived;
        public event Action<PeerInfo, string>? OnMessageAckReceived;
        public event Action<PeerInfo, List<(string MsgId, string Sender, string Content, DateTime Timestamp, DateTime? ExpiresAtUtc)>>? OnHistorySyncReceived;
        public event Func<Guid, List<(string MsgId, string Sender, string Content, DateTime Timestamp, DateTime? ExpiresAtUtc)>>? OnGetHistoryForPeer;
        public event Action<PeerInfo>? OnPeerConnected;
        public event Action<PeerInfo>? OnPeerDisconnected;
        public event Action<PeerInfo, bool>? OnTypingReceived;
        internal event Action<PeerInfo, global::QuicNet.Serialization.ChatActionUpdate[]>? OnActionsReceived;
        internal event Func<Guid, global::QuicNet.Serialization.ChatActionUpdate[]>? OnGetActionsForPeer;
        public event Action<PeerInfo, Guid, long>? OnFileRequestReceived;
        public event Func<PeerInfo, Guid, long, ReadOnlyMemory<byte>, bool, CancellationToken, Task>? OnFileChunkReceivedAsync;
        public event Action<PeerInfo, string, FileOffer, string>? OnVerifiedFileOfferReceived;
        public event Func<PeerInfo, Guid, CancellationToken, Task>? OnFileCancelReceivedAsync;
        public event Action<PeerInfo, Guid, string>? OnFileCompletedReceived;
        public event Func<PeerInfo, Task>? OnPeerDisconnectedAsync;

        public sealed class ChatSession : IDisposable
        {
            public PeerInfo Peer { get; }
            public Stream Stream { get; }
            public SemaphoreSlim WriteLock { get; } = new(1, 1);
            private int _disposed;
            private readonly Func<PeerInfo, string>? _localSenderName;
            internal bool IsTor => Peer.ActiveTransport == QuicPunch.QuicPunch.TransportType.Tor ||
                Peer.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrWhiteSpace(Peer.OnionAddress);
            internal string SenderNameForTransport(string requested) => IsTor ? _localSenderName?.Invoke(Peer) ?? "Tor" : requested;

            public ChatSession(PeerInfo peer, Stream stream, Func<PeerInfo, string>? localSenderName = null)
            {
                Peer = peer;
                Stream = stream;
                _localSenderName = localSenderName;
            }

            public async Task SendJsonAsync(object value, CancellationToken ct = default)
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value,global::QuicNet.Serialization.SharedJson.Options);
                if (payload.Length <= 0 || payload.Length > MaxFrameBytes)
                    throw new InvalidDataException("Chat frame is too large.");

                byte[] header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await WriteLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    await Stream.WriteAsync(header, ct).ConfigureAwait(false);
                    await Stream.WriteAsync(payload, ct).ConfigureAwait(false);
                    await Stream.FlushAsync(ct).ConfigureAwait(false);
                }
                catch { try { Stream.Close(); } catch { } throw; }
                finally { WriteLock.Release(); }
            }

            public async Task SendBinaryChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
            {
                if (chunk.Length <= 0 || chunk.Length > MaxFrameBytes)
                    throw new InvalidDataException("Binary frame size is invalid.");

                byte[] header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, chunk.Length);
                await WriteLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    await Stream.WriteAsync(header, ct).ConfigureAwait(false);
                    await Stream.WriteAsync(chunk, ct).ConfigureAwait(false);
                    await Stream.FlushAsync(ct).ConfigureAwait(false);
                }
                catch { try { Stream.Close(); } catch { } throw; }
                finally { WriteLock.Release(); }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                // A sender can still be unwinding its finally block after session closure.
                // This managed gate must stay alive until those references are released.
            }
        }

        public static ConcurrentDictionary<Guid, ChatSession> ActiveChats { get; } = new();

        public Task DeniedAsync(PeerInfo peer, CancellationToken ct)
        {
            Console.WriteLine($"\n[CHAT] Connection with {peer.Name} ({peer.ActiveEndPoint}) failed or denied.");
            return Task.CompletedTask;
        }

        public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
        {
            QuicPunch.Helpers.MsQuicTuner.TrySetStreamPriority(stream, QuicPunch.Helpers.QuicStreamPriority.High);
            Console.WriteLine($"\n--- DIRECT CHAT SESSION STARTED with {peer.Name} ({peer.ActiveEndPoint}) ---");
            var session = new ChatSession(peer, stream, p => _localIdentityForPeer?.Invoke(p).Name ?? "Tor");
            if (ActiveChats.TryRemove(peer.Id, out var previousSession))
            {
                try { previousSession.Stream.Close(); } catch { }
                previousSession.Dispose();
            }
            ActiveChats[peer.Id] = session;
            OnPeerConnected?.Invoke(peer);

            try { await session.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage1("chat_sync_req"), ct).ConfigureAwait(false); }
            catch { }

            _ = SynchronizeAvatarAsync(session, ct);
            byte[] header = new byte[4];
            byte[] firstByte = new byte[1];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Idle sessions may wait indefinitely, but a partial frame must make progress.
                    if (await stream.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false) == 0) break;
                    using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    frameTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await stream.ReadExactlyAsync(header.AsMemory(1, 3), frameTimeout.Token).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length <= 0 || length > MaxFrameBytes)
                        throw new InvalidDataException("Invalid chat frame length.");
                    await stream.ReadExactlyAsync(firstByte, frameTimeout.Token).ConfigureAwait(false);
                    if (firstByte[0] == 0x01 && length > FileChunkBytes + 26)
                        throw new InvalidDataException("File frame exceeds the chunk budget.");
                    byte[] payload = new byte[length];
                    payload[0] = firstByte[0];
                    int offset = 1;
                    while (offset < length)
                    {
                        frameTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                        int read = await stream.ReadAsync(payload.AsMemory(offset, Math.Min(FileChunkBytes, length - offset)), frameTimeout.Token).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException("Truncated chat frame.");
                        offset += read;
                    }
                    await ProcessFrameAsync(session, peer, payload, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (EndOfStreamException) { }
            catch (Exception ex) { Console.WriteLine($"[CHAT] Session error: {ex.Message}"); }
            finally
            {
                bool wasCurrent = ActiveChats.TryRemove(new KeyValuePair<Guid, ChatSession>(peer.Id, session));
                session.Dispose();
                if (wasCurrent)
                {
                    OnPeerDisconnected?.Invoke(peer);
                    if (OnPeerDisconnectedAsync != null)
                        foreach (Func<PeerInfo, Task> callback in OnPeerDisconnectedAsync.GetInvocationList())
                            await callback(peer).ConfigureAwait(false);
                    Console.WriteLine($"\n[CHAT] Chat session with {peer.Name} ended.");
                }
            }
        }

        private async Task ProcessFrameAsync(ChatSession session, PeerInfo peer, byte[] payload, CancellationToken ct)
        {
            if (payload.Length >= 26 && payload[0] == 0x01)
            {
                Guid fileId = new(payload.AsSpan(1, 16));
                long offset = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(17, 8));
                bool isEnd = payload[25] == 1;
                ReadOnlyMemory<byte> data = payload.AsMemory(26);
                if (fileId == Guid.Empty || offset < 0 || payload[25] > 1 || data.Length > FileChunkBytes)
                    throw new InvalidDataException("Invalid file chunk.");
                if (OnFileChunkReceivedAsync != null)
                    foreach (Func<PeerInfo, Guid, long, ReadOnlyMemory<byte>, bool, CancellationToken, Task> callback in OnFileChunkReceivedAsync.GetInvocationList())
                        await callback(peer, fileId, offset, data, isEnd, ct).ConfigureAwait(false);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "" : "";
                string msgId = root.TryGetProperty("msgId", out var idEl) ? idEl.GetString() ?? "" : "";

                switch (type)
                {
                    case "peer_avatar_req":
                        _ = SendAvatarAsync(session, ct);
                        break;
                    case "peer_avatar":
                        ReceiveAvatar(session, root);
                        break;
                    case "chat_msg":
                    {
                        string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
                        string sender = (root.TryGetProperty("sender", out var sEl) ? sEl.GetString() : null) ?? peer.Name ?? "Unknown";
                        int? disappearAfterSeconds = root.TryGetProperty("disappearAfterSeconds", out var ttlEl) &&
                            ttlEl.ValueKind == JsonValueKind.Number && ttlEl.TryGetInt32(out int ttl) && ttl is > 0 and <= 2592000 ? ttl : null;
                        OnMessageReceived?.Invoke(peer, content, msgId, disappearAfterSeconds);
                        await session.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage2("chat_ack", msgId, "delivered"), ct).ConfigureAwait(false);
                        break;
                    }
                    case "chat_ack":
                        OnMessageAckReceived?.Invoke(peer, msgId);
                        break;
                    case "chat_typing":
                    {
                        bool isTyping = root.TryGetProperty("isTyping", out var itEl) && itEl.GetBoolean();
                        OnTypingReceived?.Invoke(peer, isTyping);
                        break;
                    }
                    case "chat_actions":
                    {
                        var frame = JsonSerializer.Deserialize<global::QuicNet.Serialization.ChatActionFrame>(payload, global::QuicNet.Serialization.SharedJson.Options);
                        if (frame?.updates is { Length: <= 8192 } updates) OnActionsReceived?.Invoke(peer, updates);
                        break;
                    }
                    case "chat_file_offer":
                    {
                        Guid fileId = root.TryGetProperty("fileId", out var fEl) && Guid.TryParse(fEl.GetString(), out var fid) ? fid : Guid.Empty;
                        string fileName = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                        long fileSize = root.TryGetProperty("size", out var sEl) ? sEl.GetInt64() : 0;
                        string mime = root.TryGetProperty("mime", out var mEl) ? mEl.GetString() ?? "" : "";
                        string sender = (root.TryGetProperty("sender", out var sndEl) ? sndEl.GetString() : null) ?? peer.Name ?? "Unknown";
                        if (fileId == Guid.Empty || fileSize < 0 || fileName.Length is 0 or > 255)
                            throw new InvalidDataException("Invalid file offer.");
                        string? sha256 = root.TryGetProperty("sha256", out var hashEl) ? hashEl.GetString() : null;
                        if (!IsValidFileHash(sha256))
                            throw new InvalidDataException("File offers require a valid SHA-256 digest. Update both peers to the current protocol.");
                        OnVerifiedFileOfferReceived?.Invoke(peer, msgId, new FileOffer(fileId, fileName, fileSize, mime, sha256!), sender);
                        await session.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage2("chat_ack", msgId, "delivered"), ct).ConfigureAwait(false);
                        break;
                    }
                    case "chat_file_req":
                    {
                        Guid reqFileId = root.TryGetProperty("fileId", out var rfEl) && Guid.TryParse(rfEl.GetString(), out var rfid) ? rfid : Guid.Empty;
                        long reqOffset = root.TryGetProperty("offset", out var roEl) ? roEl.GetInt64() : 0;
                        OnFileRequestReceived?.Invoke(peer, reqFileId, reqOffset);
                        break;
                    }
                    case "chat_file_cancel":
                    {
                        Guid cancelFileId = root.TryGetProperty("fileId", out var cfEl) && Guid.TryParse(cfEl.GetString(), out var cfid) ? cfid : Guid.Empty;
                        if (OnFileCancelReceivedAsync != null)
                            foreach (Func<PeerInfo, Guid, CancellationToken, Task> callback in OnFileCancelReceivedAsync.GetInvocationList())
                                await callback(peer, cancelFileId, ct).ConfigureAwait(false);
                        break;
                    }
                    case "chat_file_complete":
                    {
                        Guid completedId = root.TryGetProperty("fileId", out var id) && Guid.TryParse(id.GetString(), out var parsed) ? parsed : Guid.Empty;
                        string? sha256 = root.TryGetProperty("sha256", out var hash) ? hash.GetString() : null;
                        if (completedId == Guid.Empty || !IsValidFileHash(sha256))
                            throw new InvalidDataException("File completion requires a file ID and valid SHA-256 digest.");
                        OnFileCompletedReceived?.Invoke(peer, completedId, sha256!);
                        break;
                    }
                    case "chat_sync_req":
                    {
                        var history = OnGetHistoryForPeer?.Invoke(peer.Id) ?? new();
                        foreach (var item in history)
                        {
                            await session.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage3("chat_sync_item", item.MsgId, session.IsTor ? (item.Sender == "Me" ? session.SenderNameForTransport("Tor") : "Tor") : item.Sender, item.Content, item.Timestamp.ToString("o"), item.ExpiresAtUtc.HasValue
                                    ? new DateTimeOffset(item.ExpiresAtUtc.Value.ToUniversalTime()).ToUnixTimeMilliseconds() : (long?)null), ct).ConfigureAwait(false);
                        }
                        var actions = OnGetActionsForPeer?.Invoke(peer.Id) ?? [];
                        if (actions.Length > 0) await session.SendJsonAsync(new global::QuicNet.Serialization.ChatActionFrame("chat_actions", actions), ct).ConfigureAwait(false);
                        await session.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage1("chat_sync_done"), ct).ConfigureAwait(false);
                        break;
                    }
                    case "chat_sync_item":
                    {
                        string sender = root.TryGetProperty("sender", out var sEl) ? sEl.GetString() ?? "" : "";
                        string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
                        DateTime timestamp = root.TryGetProperty("timestamp", out var tsEl) && DateTime.TryParse(tsEl.GetString(), out var parsed) ? parsed : DateTime.Now;
                        DateTime? expiresAtUtc = root.TryGetProperty("expiresAtUnixMs", out var expiryEl) && expiryEl.ValueKind == JsonValueKind.Number && expiryEl.TryGetInt64(out long expiryMs) &&
                            expiryMs >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() && expiryMs <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                            ? DateTimeOffset.FromUnixTimeMilliseconds(expiryMs).UtcDateTime : null;
                        if (!string.IsNullOrWhiteSpace(msgId))
                            OnHistorySyncReceived?.Invoke(peer, new() { (msgId, sender, content, timestamp, expiresAtUtc) });
                        break;
                    }
                }
            }
            catch (JsonException) { }
        }

        public static byte[] BuildFileChunkFrame(Guid fileId, long offset, byte[] data, int count, bool isEnd)
        {
            if (fileId == Guid.Empty || offset < 0 || count < 0 || count > Math.Min(data.Length, FileChunkBytes))
                throw new ArgumentOutOfRangeException(nameof(count));
            byte[] frame = new byte[1 + 16 + 8 + 1 + count];
            frame[0] = 0x01;
            fileId.TryWriteBytes(frame.AsSpan(1, 16));
            BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(17, 8), offset);
            frame[25] = (byte)(isEnd ? 1 : 0);
            Buffer.BlockCopy(data, 0, frame, 26, count);
            return frame;
        }

        public static async Task<bool> SendTypingAsync(Guid peerId, bool isTyping)
        {
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return false;
            try
            {
                await chat.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage4("chat_typing", isTyping)).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        public static async Task<(bool Success, string MsgId)> SendFileOfferAsync(Guid peerId, string senderName, Guid fileId, string fileName, long fileSize, string mime, string sha256, CancellationToken ct = default, string? existingMessageId = null)
        {
            if (fileId == Guid.Empty || fileSize < 0 || fileName.Length is 0 or > 255 || !IsValidFileHash(sha256))
                throw new InvalidDataException("File offers require a valid ID, name, size and SHA-256 digest.");
            string msgId = existingMessageId ?? Guid.NewGuid().ToString();
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return (false, msgId);
            try
            {
                await chat.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage5("chat_file_offer", msgId, chat.SenderNameForTransport(senderName), fileId.ToString(), fileName, fileSize, mime, sha256, DateTime.UtcNow.ToString("o")), ct).ConfigureAwait(false);
                return (true, msgId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHAT] Error sending file offer: {ex.Message}");
                return (false, msgId);
            }
        }

        public static async Task<bool> RequestFileAsync(Guid peerId, Guid fileId, long offset = 0, CancellationToken ct = default)
        {
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return false;
            try
            {
                await chat.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage6("chat_file_req", fileId.ToString(), offset), ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHAT] Error requesting file: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> CancelFileAsync(Guid peerId, Guid fileId, CancellationToken ct = default)
        {
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return false;
            try
            {
                await chat.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage7("chat_file_cancel", fileId.ToString()), ct).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        public static async Task<bool> SendBinaryChunkAsync(Guid peerId, byte[] frame, CancellationToken ct = default)
        {
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return false;
            try
            {
                await chat.SendBinaryChunkAsync(frame, ct).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        public static async Task<bool> SendFileCompletionAsync(Guid peerId, Guid fileId, string sha256, CancellationToken ct = default)
        {
            if (fileId == Guid.Empty || !IsValidFileHash(sha256))
                throw new InvalidDataException("File completion requires a file ID and valid SHA-256 digest.");
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return false;
            try
            {
                await chat.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage8("chat_file_complete", fileId, sha256), ct).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Sends a direct chat message to a peer. Attempts direct QUIC transport first if an active session exists;
        /// Mailbox fallback is available only when explicitly enabled for this handler.
        /// </summary>
        public async Task<(bool Success, string MsgId, bool IsOffline)> SendMessageAsync(Guid peerId, string senderName, string message, CancellationToken ct = default, int disappearAfterSeconds = 0, string? messageId = null, bool directOnly = false)
        {
            // 1. Direct QUIC session if connected
            if (ActiveChats.TryGetValue(peerId, out var chat))
            {
                try
                {
                    string msgId = messageId ?? Guid.NewGuid().ToString();
                    await chat.SendJsonAsync(new global::QuicNet.Serialization.ChatHandlerMessage9("chat_msg", msgId, chat.SenderNameForTransport(senderName), message, disappearAfterSeconds, DateTime.UtcNow.ToString("o")), ct).ConfigureAwait(false);
                    return (true, msgId, false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CHAT] Direct send failed: {ex.Message}");
                }
            }

            // 2. Offline fallback via Nostr Mailbox Channel
            if (!directOnly && NostrMessagingEnabled && _mailboxChannel != null)
            {
                try
                {
                    string payload = disappearAfterSeconds > 0
                        ? "\u001eQPCHAT/1\n" + JsonSerializer.Serialize(new global::QuicNet.Serialization.ChatHandlerMessage10(message, disappearAfterSeconds),global::QuicNet.Serialization.SharedJson.Options)
                        : message;
                    string msgId = await _mailboxChannel.SendOfflineAsync(peerId, payload, ct).ConfigureAwait(false);
                    return (true, msgId, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CHAT] Nostr Mailbox send failed: {ex.Message}");
                    return (false, "", true);
                }
            }

            return (false, "", false);
        }
    }
}
