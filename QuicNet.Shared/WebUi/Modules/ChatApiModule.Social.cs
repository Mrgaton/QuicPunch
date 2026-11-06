using System.Net;
using QuicNet.Protocols;
using QuicNet.Serialization;
using QuicPunch;

namespace QuicNet.WebUi.Modules;

internal sealed partial class ChatApiModule
{
    private ChatSocialSnapshot SocialSnapshot(string peerId) => new(true, peerId, _history.GetActions(peerId), _history.GetPinnedMessages(peerId)
        .Select(message => ToWebMessage(message with { Message = MessagePreview(message.Message) })).ToArray());

    private ChatActionUpdate[] GetActionsForPeer(Guid peerId) => _history.GetActions(peerId.ToString()).Where(state => state.mine)
        .Select(state => new ChatActionUpdate(state.msgId, state.kind, state.key, state.active, state.revision)).ToArray();

    private void ReceiveActions(PeerInfo peer, ChatActionUpdate[] updates)
    {
        if (!_qcc.IsTrustedPeer(peer)) return;
        try
        {
            _history.ApplyActions(peer.Id.ToString(), updates, mine: false);
            _hub.Broadcast("chat_social_changed", new ChatApiModuleMessage5(peer.Id.ToString()));
        }
        catch (InvalidDataException) { }
    }

    private async Task SendActionAsync(Guid peer, ChatActionUpdate update)
    {
        if (!ChatHandler.ActiveChats.TryGetValue(peer, out var session)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_fileLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await session.SendJsonAsync(new ChatActionFrame("chat_actions", [update]), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { if (Volatile.Read(ref _disposed) == 0) WebUiServer.LogEvent($"[CHAT] Action will sync on reconnect: {ex.Message}"); }
    }

    private async Task<bool> HandleSocialRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/chat/message" && req.HttpMethod == "GET")
        {
            string peerId = req.QueryString["peerId"] ?? "";
            if (peerId != "global" && !Guid.TryParse(peerId, out _)) throw new InvalidDataException("Conversación no válida.");
            if (peerId != "global") peerId = Guid.Parse(peerId).ToString();
            var message = _history.GetMessage(peerId, req.QueryString["msgId"] ?? "");
            if (message == null || message.ExpiresAtUtc <= DateTime.UtcNow) { resp.StatusCode = 404; return true; }
            await WebUiContext.WriteJsonAsync(resp, new ChatOutboxResponse(true, ToWebMessage(message))).ConfigureAwait(false);
            return true;
        }
        if (path != "/api/chat/interactions") return false;
        if (req.HttpMethod == "GET")
        {
            string peerId = req.QueryString["peerId"] ?? "";
            if (!Guid.TryParse(peerId, out var id)) throw new InvalidDataException("Conversación no válida.");
            await WebUiContext.WriteJsonAsync(resp, SocialSnapshot(id.ToString())).ConfigureAwait(false);
            return true;
        }
        if (req.HttpMethod != "POST") return false;
        using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
        Guid peer = WebUiContext.ParseGuid(doc.RootElement, "peerId");
        if (!IsTrustedConversation(peer)) { await WebUiContext.WriteJsonAsync(resp, new ChatApiModuleMessage18(false, "Elige un contacto de confianza."), 403).ConfigureAwait(false); return true; }
        string messageId = doc.RootElement.GetProperty("msgId").GetString() ?? "";
        string kind = doc.RootElement.GetProperty("kind").GetString() ?? "";
        string key = doc.RootElement.GetProperty("key").GetString() ?? "";
        bool active = doc.RootElement.GetProperty("active").GetBoolean();
        var update = _history.SetAction(peer.ToString(), messageId, kind, key, active);
        _hub.Broadcast("chat_social_changed", new ChatApiModuleMessage5(peer.ToString()));
        // Persist first. A disconnected peer receives this state during the next history sync.
        _ = SendActionAsync(peer, update);
        await WebUiContext.WriteJsonAsync(resp, SocialSnapshot(peer.ToString())).ConfigureAwait(false);
        return true;
    }
}
