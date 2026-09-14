using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using QuicPunch;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicPunchTests.Protocols
{
    public sealed class ChatHandler : QuicPunch.QuicPunch.IProtocolHandler
    {
        private const int MaxFrameBytes = 32 * 1024 * 1024;
        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public ushort PreferredPort => 0;
        public string ProtocolName => "Chat";
        public ushort StreamPriority => (ushort)QuicPunch.Helpers.QuicStreamPriority.High;
        public ZstandardCompressionOptions? CompressionOptions => null;

        public event Action<PeerInfo, string, string>? OnMessageReceived;
        public event Action<PeerInfo, string>? OnMessageAckReceived;
        public event Action<PeerInfo, List<(string MsgId, string Sender, string Content, DateTime Timestamp)>>? OnHistorySyncReceived;
        public event Func<Guid, List<(string MsgId, string Sender, string Content, DateTime Timestamp)>>? OnGetHistoryForPeer;
        public event Action<PeerInfo>? OnPeerConnected;
        public event Action<PeerInfo>? OnPeerDisconnected;
        public event Action<PeerInfo, bool>? OnTypingReceived;
        public event Action<PeerInfo, string, Guid, string, long, string, string>? OnFileOfferReceived;
        public event Action<PeerInfo, Guid, long>? OnFileRequestReceived;
        public event Action<PeerInfo, Guid>? OnFileCancelReceived;
        public event Action<PeerInfo, Guid, long, ReadOnlyMemory<byte>, bool>? OnFileChunkReceived;

        public sealed class ChatSession : IDisposable
        {
            public PeerInfo Peer { get; }
            public Stream Stream { get; }
            public SemaphoreSlim WriteLock { get; } = new(1, 1);
            private int _disposed;

            public ChatSession(PeerInfo peer, Stream stream)
            {
                Peer = peer;
                Stream = stream;
            }

            public async Task SendJsonAsync(object value, CancellationToken ct = default)
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
                if (payload.Length <= 0 || payload.Length > MaxFrameBytes)
                    throw new InvalidDataException("Chat frame is too large.");

                byte[] header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await WriteLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Stream.WriteAsync(header, ct).ConfigureAwait(false);
                    await Stream.WriteAsync(payload, ct).ConfigureAwait(false);
                    await Stream.FlushAsync(ct).ConfigureAwait(false);
                }
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
                    await Stream.WriteAsync(header, ct).ConfigureAwait(false);
                    await Stream.WriteAsync(chunk, ct).ConfigureAwait(false);
                    await Stream.FlushAsync(ct).ConfigureAwait(false);
                }
                finally { WriteLock.Release(); }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                WriteLock.Dispose();
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
            var session = new ChatSession(peer, stream);
            if (ActiveChats.TryRemove(peer.Id, out var previousSession))
            {
                try { previousSession.Stream.Close(); } catch { }
                previousSession.Dispose();
            }
            ActiveChats[peer.Id] = session;
            OnPeerConnected?.Invoke(peer);

            try { await session.SendJsonAsync(new { type = "chat_sync_req" }, ct).ConfigureAwait(false); }
            catch { }

            byte[] header = new byte[4];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length <= 0 || length > MaxFrameBytes)
                        throw new InvalidDataException("Invalid chat frame length.");
                    byte[] payload = new byte[length];
                    await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
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
                OnFileChunkReceived?.Invoke(peer, fileId, offset, data, isEnd);
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
                    case "chat_msg":
                    {
                        string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
                        string sender = (root.TryGetProperty("sender", out var sEl) ? sEl.GetString() : null) ?? peer.Name ?? "Unknown";
                        OnMessageReceived?.Invoke(peer, content, msgId);
                        await session.SendJsonAsync(new { type = "chat_ack", msgId, status = "delivered" }, ct).ConfigureAwait(false);
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
                    case "chat_file_offer":
                    {
                        Guid fileId = root.TryGetProperty("fileId", out var fEl) && Guid.TryParse(fEl.GetString(), out var fid) ? fid : Guid.Empty;
                        string fileName = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                        long fileSize = root.TryGetProperty("size", out var sEl) ? sEl.GetInt64() : 0;
                        string mime = root.TryGetProperty("mime", out var mEl) ? mEl.GetString() ?? "" : "";
                        string sender = (root.TryGetProperty("sender", out var sndEl) ? sndEl.GetString() : null) ?? peer.Name ?? "Unknown";
                        OnFileOfferReceived?.Invoke(peer, msgId, fileId, fileName, fileSize, mime, sender);
                        await session.SendJsonAsync(new { type = "chat_ack", msgId, status = "delivered" }, ct).ConfigureAwait(false);
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
                        OnFileCancelReceived?.Invoke(peer, cancelFileId);
                        break;
                    }
                    case "chat_sync_req":
                    {
                        var history = OnGetHistoryForPeer?.Invoke(peer.Id) ?? new();
                        foreach (var item in history)
                        {
                            await session.SendJsonAsync(new
                            {
                                type = "chat_sync_item",
                                msgId = item.MsgId,
                                sender = item.Sender,
                                content = item.Content,
                                timestamp = item.Timestamp.ToString("o")
                            }, ct).ConfigureAwait(false);
                        }
                        await session.SendJsonAsync(new { type = "chat_sync_done" }, ct).ConfigureAwait(false);
                        break;
                    }
                    case "chat_sync_item":
                    {
                        string sender = root.TryGetProperty("sender", out var sEl) ? sEl.GetString() ?? "" : "";
                        string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
                        DateTime timestamp = root.TryGetProperty("timestamp", out var tsEl) && DateTime.TryParse(tsEl.GetString(), out var parsed) ? parsed : DateTime.Now;
                        if (!string.IsNullOrWhiteSpace(msgId))
                            OnHistorySyncReceived?.Invoke(peer, new() { (msgId, sender, content, timestamp) });
                        break;
                    }
                }
            }
            catch (JsonException) { }
        }

        public static byte[] BuildFileChunkFrame(Guid fileId, long offset, byte[] data, int count, bool isEnd)
        {
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
                await chat.SendJsonAsync(new { type = "chat_typing", isTyping }).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        public static async Task<(bool Success, string MsgId)> SendFileOfferAsync(Guid peerId, string senderName, Guid fileId, string fileName, long fileSize, string mime)
        {
            string msgId = Guid.NewGuid().ToString();
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return (false, msgId);
            try
            {
                await chat.SendJsonAsync(new
                {
                    type = "chat_file_offer",
                    msgId,
                    sender = senderName,
                    fileId = fileId.ToString(),
                    name = fileName,
                    size = fileSize,
                    mime,
                    timestamp = DateTime.UtcNow.ToString("o")
                }).ConfigureAwait(false);
                return (true, msgId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHAT] Error sending file offer: {ex.Message}");
                return (false, msgId);
            }
        }

        public static async Task<bool> RequestFileAsync(Guid peerId, Guid fileId, long offset = 0)
        {
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return false;
            try
            {
                await chat.SendJsonAsync(new
                {
                    type = "chat_file_req",
                    fileId = fileId.ToString(),
                    offset
                }).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHAT] Error requesting file: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> CancelFileAsync(Guid peerId, Guid fileId)
        {
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return false;
            try
            {
                await chat.SendJsonAsync(new
                {
                    type = "chat_file_cancel",
                    fileId = fileId.ToString()
                }).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        public static async Task<bool> SendBinaryChunkAsync(Guid peerId, byte[] frame)
        {
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return false;
            try
            {
                await chat.SendBinaryChunkAsync(frame).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        public static async Task<(bool Success, string MsgId)> SendMessageAsync(Guid peerId, string senderName, string message)
        {
            string msgId = Guid.NewGuid().ToString();
            if (!ActiveChats.TryGetValue(peerId, out var chat)) return (false, msgId);
            try
            {
                await chat.SendJsonAsync(new
                {
                    type = "chat_msg",
                    msgId,
                    sender = senderName,
                    content = message,
                    timestamp = DateTime.UtcNow.ToString("o")
                }).ConfigureAwait(false);
                return (true, msgId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CHAT] Error sending message: {ex.Message}");
                return (false, msgId);
            }
        }
    }
}
