using System.Text.Json;
using QuicNet.Settings;

namespace QuicNet.Protocols;

public sealed partial class ChatHandler
{
    internal ProfileAvatarStore? Avatars { get; set; }
    internal async Task SendAvatarAsync(ChatSession session, CancellationToken cancellationToken = default)
    {
        if (session.IsTor || Avatars == null) return;
        try
        {
            byte[] bytes = Avatars.Read() ?? [];
            await session.SendJsonAsync(new PeerAvatarMessage("peer_avatar", Convert.ToBase64String(bytes)), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine("[PROFILE] Avatar sync failed: " + ex.Message); }
    }
    internal async Task SynchronizeAvatarAsync(ChatSession session, CancellationToken cancellationToken = default)
    {
        if (session.IsTor || Avatars == null) return;
        await SendAvatarAsync(session, cancellationToken).ConfigureAwait(false);
        try { await session.SendJsonAsync(new PeerAvatarMessage("peer_avatar_req", ""), cancellationToken).ConfigureAwait(false); }
        catch { /* A disconnected session synchronizes again on reconnect. */ }
    }
    internal Task RequestAvatarsAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(ActiveChats.Values.Select(session => SynchronizeAvatarAsync(session, cancellationToken)));

    internal Task BroadcastAvatarAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(ActiveChats.Values.Select(session => SendAvatarAsync(session, cancellationToken)));

    internal void ReceiveAvatar(ChatSession session, JsonElement root)
    {
        if (session.IsTor || Avatars == null || !root.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.String) return;
        string encoded = value.GetString()!;
        if (encoded.Length > ((ProfileAvatarStore.MaxBytes + 2) / 3) * 4) return;
        try { Avatars.Set(Convert.FromBase64String(encoded), session.Peer.Id); }
        catch (Exception error) when (error is FormatException or InvalidDataException or IOException) { }
    }
}

internal sealed record PeerAvatarMessage(string type, string data);
