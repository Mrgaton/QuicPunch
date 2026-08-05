using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Quic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch;

namespace QuicPunchTests
{
    public class ChatHandler : QuicPunch.QuicPunch.IProtocolHandler
    {
        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public ushort PreferredPort => 0; 
        public string ProtocolName => "Chat";

        public ZstandardCompressionOptions? CompressionOptions => null;

        public event Action<PeerInfo, string, string>? OnMessageReceived;
        public event Action<PeerInfo, string>? OnMessageAckReceived;
        public event Action<PeerInfo, List<(string MsgId, string Sender, string Content, DateTime Timestamp)>>? OnHistorySyncReceived;
        public event Func<Guid, List<(string MsgId, string Sender, string Content, DateTime Timestamp)>>? OnGetHistoryForPeer;
        public event Action<PeerInfo>? OnPeerConnected;
        public event Action<PeerInfo>? OnPeerDisconnected;

        public static ConcurrentDictionary<Guid, (PeerInfo Peer, StreamWriter Writer)> ActiveChats { get; } = new();

        public async Task DeniedAsync(PeerInfo peer, CancellationToken ct)
        {
            Console.WriteLine($"\n[CHAT] Connection with {peer.Name} ({peer.ActiveEndPoint}) failed or denied.");
        }

        public async Task HandleAsync(
            QuicConnection connection,
            Stream stream,
            PeerInfo peer,
            CancellationToken ct)
        {
            Console.WriteLine($"\n--- DIRECT CHAT SESSION STARTED with {peer.Name} ({peer.ActiveEndPoint}) ---");
            var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            ActiveChats[peer.Id] = (peer, writer);
            OnPeerConnected?.Invoke(peer);

            // Send sync request upon session connection
            try
            {
                var syncReqPacket = JsonSerializer.Serialize(new { type = "chat_sync_req" });
                await writer.WriteLineAsync(syncReqPacket);
            }
            catch {}

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync();

                        if (line == null) break;
                        if (string.IsNullOrWhiteSpace(line) || line == "\0") continue;

                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            string type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "" : "";
                            string msgId = root.TryGetProperty("msgId", out var idEl) ? idEl.GetString() ?? "" : "";

                            if (type == "chat_msg")
                            {
                                string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
                                string sender = root.TryGetProperty("sender", out var sEl) ? sEl.GetString() ?? peer.Name : peer.Name;

                                Console.ForegroundColor = ConsoleColor.Cyan;
                                Console.WriteLine($"[{sender}]: {content}");
                                Console.ResetColor();

                                OnMessageReceived?.Invoke(peer, content, msgId);

                                // Send back ACK confirmation JSON packet
                                var ackPacket = JsonSerializer.Serialize(new
                                {
                                    type = "chat_ack",
                                    msgId = msgId,
                                    status = "delivered"
                                });
                                await writer.WriteLineAsync(ackPacket);
                            }
                            else if (type == "chat_ack")
                            {
                                Console.WriteLine($"[CHAT ACK] Message {msgId} confirmed by {peer.Name}");
                                OnMessageAckReceived?.Invoke(peer, msgId);
                            }
                            else if (type == "chat_sync_req")
                            {
                                var history = OnGetHistoryForPeer?.Invoke(peer.Id) ?? new();
                                var syncResPacket = JsonSerializer.Serialize(new
                                {
                                    type = "chat_sync_res",
                                    messages = history.Select(h => new
                                    {
                                        msgId = h.MsgId,
                                        sender = h.Sender,
                                        content = h.Content,
                                        timestamp = h.Timestamp.ToString("o")
                                    })
                                });
                                await writer.WriteLineAsync(syncResPacket);
                            }
                            else if (type == "chat_sync_res")
                            {
                                if (root.TryGetProperty("messages", out var msgsEl) && msgsEl.ValueKind == JsonValueKind.Array)
                                {
                                    var items = new List<(string MsgId, string Sender, string Content, DateTime Timestamp)>();
                                    foreach (var el in msgsEl.EnumerateArray())
                                    {
                                        string id = el.TryGetProperty("msgId", out var iEl) ? iEl.GetString() ?? "" : "";
                                        string snd = el.TryGetProperty("sender", out var sEl) ? sEl.GetString() ?? "" : "";
                                        string cnt = el.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
                                        DateTime ts = el.TryGetProperty("timestamp", out var tsEl) && DateTime.TryParse(tsEl.GetString(), out var parsedTs) ? parsedTs : DateTime.Now;
                                        if (!string.IsNullOrEmpty(id)) items.Add((id, snd, cnt, ts));
                                    }
                                    OnHistorySyncReceived?.Invoke(peer, items);
                                }
                            }
                            else
                            {
                                OnMessageReceived?.Invoke(peer, line, Guid.NewGuid().ToString());
                            }
                        }
                        catch
                        {
                            OnMessageReceived?.Invoke(peer, line, Guid.NewGuid().ToString());
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[QUIC] Error reading chat stream: {ex.Message}");
                }
                finally
                {
                    ActiveChats.TryRemove(peer.Id, out _);
                    OnPeerDisconnected?.Invoke(peer);
                    Console.WriteLine($"\n[QUIC] Chat session with {peer.Name} ended.");
                }
            }, ct);

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
            }
        }

        public static async Task<(bool Success, string MsgId)> SendMessageAsync(Guid peerId, string senderName, string message)
        {
            string msgId = Guid.NewGuid().ToString();
            if (ActiveChats.TryGetValue(peerId, out var chat))
            {
                try
                {
                    var packet = JsonSerializer.Serialize(new
                    {
                        type = "chat_msg",
                        msgId = msgId,
                        sender = senderName,
                        content = message,
                        timestamp = DateTime.UtcNow.ToString("o")
                    });
                    await chat.Writer.WriteLineAsync(packet);
                    return (true, msgId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[QUIC] Error sending chat message: {ex.Message}");
                }
            }
            return (false, msgId);
        }
    }
}
