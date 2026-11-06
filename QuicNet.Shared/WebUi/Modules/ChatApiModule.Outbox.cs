using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using QuicNet.Protocols;
using QuicNet.Serialization;

namespace QuicNet.WebUi.Modules;

internal sealed partial class ChatApiModule
{
    private readonly SemaphoreSlim _outboxGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DateTime> _outboxConnections = new();

    private bool IsTrustedConversation(Guid peerId) =>
        _qcc.AvailablePeers.TryGetValue(peerId, out var peer) ? _qcc.IsTrustedPeer(peer) :
        _qcc.PeerStore?.SavedPeers.Any(saved => new Guid(System.IO.Hashing.XxHash128.Hash(saved.CertHash)) == peerId && _qcc.IsTrustedPeer(saved.CertHash)) == true;

    private static ChatApiModuleMessage16 ToWebMessage(WebUiServer.ChatMessage message) => new(
        message.PeerId, message.MsgId, message.Sender, message.Message, new DateTimeOffset(message.Timestamp).ToUnixTimeMilliseconds(),
        message.Timestamp.ToString("HH:mm:ss"), message.IsMe, message.IsConfirmed, message.PeerId == "global", ToUnixMs(message.ExpiresAtUtc))
        { deliveryState = message.DeliveryState };

    private void PublishStoredMessage(WebUiServer.ChatMessage message)
    {
        lock (_chatQueueLock)
        {
            var items = ChatMessages.ToArray();
            while (ChatMessages.TryDequeue(out _)) { }
            foreach (var item in items) ChatMessages.Enqueue(item.PeerId == message.PeerId && item.MsgId == message.MsgId ? message : item);
        }
        _hub.Broadcast("chat_msg", ToWebMessage(message));
    }

    private void RemoveStoredMessage(string peerId, string messageId)
    {
        lock (_chatQueueLock)
        {
            var remaining = ChatMessages.Where(message => message.PeerId != peerId || message.MsgId != messageId).ToArray();
            while (ChatMessages.TryDequeue(out _)) { }
            _chatHistoryBytes = 0;
            foreach (var message in remaining) { ChatMessages.Enqueue(message); _chatHistoryBytes += Encoding.UTF8.GetByteCount(message.Message); }
        }
    }

    private async Task<bool> HandleOutboxRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (req.HttpMethod != "POST" || path is not ("/api/chat/outbox" or "/api/chat/message/delete")) return false;
        using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
        string peerId = doc.RootElement.GetProperty("peerId").GetString() ?? "";
        if (peerId != "global") peerId = Guid.Parse(peerId).ToString();
        string messageId = doc.RootElement.GetProperty("msgId").GetString() ?? "";
        if (messageId.Length is < 1 or > 256) throw new InvalidDataException("Identificador de mensaje inválido.");
        await _outboxGate.WaitAsync(_fileLifetime.Token).ConfigureAwait(false);
        try
        {
            if (path.EndsWith("/delete", StringComparison.Ordinal))
            {
                if (doc.RootElement.TryGetProperty("pendingOnly", out var onlyPending) && onlyPending.GetBoolean() &&
                    _history.GetMessage(peerId, messageId) is { DeliveryState: "sent" })
                {
                    await WebUiContext.WriteJsonAsync(resp, new ChatApiModuleMessage18(false, "El mensaje ya se ha enviado. Puedes eliminar su copia local."), 409).ConfigureAwait(false);
                    return true;
                }
                var removed = _history.DeleteMessage(peerId, messageId);
                RemoveStoredMessage(peerId, messageId);
                if (removed != null) ForgetAttachment(removed);
                _hub.Broadcast("chat_message_deleted", new ChatMessageDeleted(peerId, messageId));
                WebUiServer.BroadcastStatusUpdate();
                await WebUiContext.WriteJsonAsync(resp, new ChatApiModuleMessage25(true)).ConfigureAwait(false);
                return true;
            }
            if (peerId == "global" || !IsTrustedConversation(Guid.Parse(peerId)))
            {
                await WebUiContext.WriteJsonAsync(resp, new ChatApiModuleMessage18(false, "Elige un contacto de confianza para enviar mensajes."), 403).ConfigureAwait(false);
                return true;
            }
            if (!Guid.TryParse(messageId, out _)) throw new InvalidDataException("Identificador de mensaje inválido.");
            string text = doc.RootElement.GetProperty("message").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 64 * 1024) throw new InvalidDataException("El mensaje debe contener texto y ocupar como máximo 64 KB.");
            if (_history.IsHidden(peerId, messageId)) throw new InvalidDataException("Este mensaje ya se ha eliminado.");
            var message = _history.GetMessage(peerId, messageId);
            if (message != null && (!message.IsMe || message.Message != text)) throw new InvalidDataException("El identificador ya pertenece a otro mensaje.");
            if (message == null)
            {
                if (_history.LoadPending().Count >= 512) throw new InvalidDataException("Hay demasiados mensajes pendientes. Envía o cancela algunos antes de continuar.");
                message = EnqueueChat(new WebUiServer.ChatMessage(peerId, messageId, "Me", text, DateTime.Now, true, false) { DeliveryState = "pending" });
            }
            _hub.Broadcast("chat_msg", ToWebMessage(message));
            await WebUiContext.WriteJsonAsync(resp, new ChatOutboxResponse(true, ToWebMessage(message))).ConfigureAwait(false);
        }
        finally { _outboxGate.Release(); }
        _ = FlushOutboxAsync();
        return true;
    }

    private async Task ConnectOutboxAsync(Guid peerId)
    {
        try
        {
            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer) || !_qcc.IsTrustedPeer(peer) || _qcc.IsConnectionInFlight(peerId, _chatHandler.ProtocolId)) return;
            if (_outboxConnections.TryGetValue(peerId, out var retry) && retry > DateTime.UtcNow) return;
            _outboxConnections[peerId] = DateTime.UtcNow.AddSeconds(30);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_fileLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            await _qcc.InitQuicConnection(_chatHandler.ProtocolId, peer, 0, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { WebUiServer.LogEvent($"[CHAT] Pending connection: {ex.Message}"); }
    }

    private async Task FlushOutboxAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || !await _outboxGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            foreach (var conversation in _history.LoadPending().GroupBy(message => message.PeerId))
            {
                if (!Guid.TryParse(conversation.Key, out var peerId) || !IsTrustedConversation(peerId)) continue;
                if (!ChatHandler.ActiveChats.ContainsKey(peerId)) { _ = ConnectOutboxAsync(peerId); continue; }
                foreach (var pending in conversation)
                {
                    var sending = _history.SetDeliveryState(pending.PeerId, pending.MsgId, "sending");
                    if (sending == null || sending.IsConfirmed) continue;
                    PublishStoredMessage(sending);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_fileLifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    int seconds = GetDisappearingSeconds(pending.PeerId);
                    var result = await _chatHandler.SendMessageAsync(peerId, LocalSenderName(peerId), pending.Message, timeout.Token,
                        seconds, messageId: pending.MsgId, directOnly: true).ConfigureAwait(false);
                    var updated = _history.SetDeliveryState(pending.PeerId, pending.MsgId, result.Success ? "sent" : "pending",
                        result.Success && seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : null);
                    if (updated != null) PublishStoredMessage(updated);
                    if (!result.Success) break;
                }
            }
        }
        catch (Exception ex) { if (Volatile.Read(ref _disposed) == 0) WebUiServer.LogEvent($"[CHAT] Outbox: {ex.Message}"); }
        finally { _outboxGate.Release(); }
    }
}
