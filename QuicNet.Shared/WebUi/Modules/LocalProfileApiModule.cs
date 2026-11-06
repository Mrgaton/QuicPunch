using System.Net;
using System.Text.Json;
using QuicNet.Settings;
using QuicNet.Protocols;

namespace QuicNet.WebUi.Modules;

internal sealed class LocalProfileApiModule
{
    private readonly QuicPunch.QuicPunch node;
    private readonly AppPreferencesStore preferences;
    private readonly CancellationToken cancellationToken;
    private readonly ChatHandler chat;
    internal ProfileAvatarStore Avatars { get; }
    public LocalProfileApiModule(QuicPunch.QuicPunch node, AppPreferencesStore preferences, ChatHandler chat, CancellationToken cancellationToken)
    {
        this.node = node; this.preferences = preferences; this.chat = chat; this.cancellationToken = cancellationToken;
        Avatars = new ProfileAvatarStore(Path.Combine(preferences.StorageDirectory, "avatars"));
        Avatars.Changed += () => WebUiServer.BroadcastStatusUpdate();
        chat.Avatars = Avatars;
        _ = chat.RequestAvatarsAsync(cancellationToken);
    }
    private LocalProfile Snapshot()
    {
        var saved = preferences.Snapshot();
        return new LocalProfile(true, LocalUserProfile.Resolve(saved.DisplayName), saved.DisplayName, LocalUserProfile.DeviceName,
            Avatars.LocalVersion.Length == 0 ? "" : "/api/profile/avatar?v=" + Avatars.LocalVersion);
    }
    private async Task<bool> HandleAvatarAsync(string path, HttpListenerRequest request, HttpListenerResponse response)
    {
        if (path != "/api/profile/avatar" && path != "/api/profile/avatar/remove" && !path.StartsWith("/api/profile/avatar/", StringComparison.Ordinal)) return false;
        if (request.HttpMethod == "GET" && path != "/api/profile/avatar/remove")
        {
            Guid? peerId = null;
            if (path != "/api/profile/avatar")
            {
                if (!Guid.TryParse(path["/api/profile/avatar/".Length..], out var id)) { response.StatusCode = 404; return true; }
                if (id != node.CurrentPeer.Id) peerId = id;
            }
            byte[]? bytes = Avatars.Read(peerId);
            if (bytes == null) { response.StatusCode = 404; return true; }
            response.ContentType = "image/avif";
            response.Headers["Cache-Control"] = "private, max-age=86400";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (request.HttpMethod == "POST" && path is "/api/profile/avatar" or "/api/profile/avatar/remove")
        {
            byte[] bytes = path.EndsWith("/remove", StringComparison.Ordinal) ? [] : await WebUiContext.ReadRequestBytesAsync(request, ProfileAvatarStore.MaxBytes).ConfigureAwait(false);
            if (path == "/api/profile/avatar" && !ProfileAvatarStore.IsValid(bytes))
            {
                await WebUiContext.WriteJsonAsync(response, new ProfileError(false, "El avatar debe ser un AVIF cuadrado de hasta 512 × 512 píxeles y 320 KB; las animaciones pueden durar hasta 10 segundos."), 400).ConfigureAwait(false);
                return true;
            }
            Avatars.Set(bytes);
            _ = chat.BroadcastAvatarAsync(cancellationToken);
            await WebUiContext.WriteJsonAsync(response, Snapshot()).ConfigureAwait(false);
            return true;
        }
        response.Headers["Allow"] = "GET, POST";
        await WebUiContext.WriteJsonAsync(response, new ProfileError(false, "Método no permitido."), 405).ConfigureAwait(false);
        return true;
    }

    // Serialize edits so preferences and the advertised name always agree.
    private readonly SemaphoreSlim _edits = new(1, 1);
    private async Task PublishWanNameAsync()
    {
        var discovery = node.Discovery.WanNostrDiscovery;
        if (discovery?.IsRunning != true || cancellationToken.IsCancellationRequested) return;
        try { await discovery.PublishNowAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { WebUiServer.LogEvent("Nombre guardado; el anuncio WAN se reintentará en el siguiente ciclo de descubrimiento."); }
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest request, HttpListenerResponse response)
    {
        if (await HandleAvatarAsync(path, request, response).ConfigureAwait(false)) return true;
        if (path != "/api/profile") return false;
        if (request.HttpMethod == "POST")
        {
            using var document = await WebUiContext.ReadJsonAsync(request, path).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("displayName", out var name) ||
                name.ValueKind != JsonValueKind.String || !LocalUserProfile.IsValid(name.GetString()))
            {
                await WebUiContext.WriteJsonAsync(response, new ProfileError(false, "Usa un nombre de hasta 64 caracteres, sin saltos de línea ni caracteres de control."), 400).ConfigureAwait(false);
                return true;
            }
            await _edits.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                preferences.Update(value => value.DisplayName = name.GetString()!.Trim());
                LocalUserProfile.Apply(node, preferences.Snapshot());
                WebUiServer.BroadcastStatusUpdate();
                // Only republish the public identity; Tor keeps its independent schedule.
                _ = PublishWanNameAsync();
            }
            finally { _edits.Release(); }
        }
        else if (request.HttpMethod != "GET")
        {
            response.Headers["Allow"] = "GET, POST";
            await WebUiContext.WriteJsonAsync(response, new ProfileError(false, "Método no permitido."), 405).ConfigureAwait(false);
            return true;
        }
        await WebUiContext.WriteJsonAsync(response, Snapshot()).ConfigureAwait(false);
        return true;
    }
}

internal sealed record LocalProfile(bool success, string displayName, string customName, string deviceName, string avatarUrl);
internal sealed record ProfileError(bool success, string error);
