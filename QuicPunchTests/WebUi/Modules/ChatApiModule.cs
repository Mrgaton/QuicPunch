using System.Collections.Concurrent;
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

    public ConcurrentQueue<WebUiServer.ChatMessage> ChatMessages { get; } = new();

    public ChatApiModule(QuicPunch.QuicPunch qcc, ChatHandler chatHandler, WebUiWebSocketHub hub)
    {
        _qcc = qcc;
        _chatHandler = chatHandler;
        _hub = hub;

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
            while ((ChatMessages.Count > 500 || _chatHistoryBytes > WebUiContext.MaxChatHistoryBytes) && ChatMessages.TryDequeue(out var removed))
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

        return false;
    }
}
