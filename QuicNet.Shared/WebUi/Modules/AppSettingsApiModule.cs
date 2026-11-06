using System.Net;
using System.Text.Json;
using QuicNet.Notifications;
using QuicNet.Settings;

namespace QuicNet.WebUi.Modules;

internal sealed record AppBehaviorSettings(bool StartWithSystem, bool RunInBackground, NotificationPreferences Notifications);
internal sealed record AppSettingsResponse(bool success, AppBehaviorSettings settings, bool startupSupported, string platformHelp);
internal sealed class AppSettingsApiModule(AppPreferencesStore preferences, AppNotificationService notifications, QuicNet.Presence.UserPresenceService presence)
{
    private readonly SemaphoreSlim _edits = new(1, 1);
    public async Task<bool> HandleAsync(string path, HttpListenerRequest request, HttpListenerResponse response)
    {
        if (path == "/api/profile/activity" && request.HttpMethod == "POST")
        {
            presence.Activity(); response.StatusCode = 204; return true;
        }
        if (path == "/api/profile/presence")
        {
            if (request.HttpMethod == "POST")
            {
                using var body = await WebUiContext.ReadJsonAsync(request, path);
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("doNotDisturb", out var dnd) ||
                    dnd.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || !root.TryGetProperty("idleAfterMinutes", out var idle) ||
                    idle.ValueKind != JsonValueKind.Number || !idle.TryGetInt32(out int minutes) || minutes is < 1 or > 60) throw new InvalidDataException("Estado o tiempo de inactividad no válido.");
                await _edits.WaitAsync();
                try { preferences.Update(p => { p.Notifications.DoNotDisturb = dnd.GetBoolean(); p.Notifications.DoNotDisturbUntil = null; p.PresenceIdleMinutes = minutes; }); }
                finally { _edits.Release(); }
                WebUiServer.BroadcastStatusUpdate();
            }
            else if (request.HttpMethod != "GET") { response.StatusCode = 405; response.Headers["Allow"] = "GET, POST"; return true; }
            await WebUiContext.WriteJsonAsync(response, presence.Snapshot()); return true;
        }
        if (path == "/api/notifications/presence" && request.HttpMethod == "POST")
        {
            using var body = await WebUiContext.ReadJsonAsync(request, path);
            notifications.Presence(body.RootElement.GetProperty("client").GetString() ?? "", body.RootElement.GetProperty("focused").GetBoolean());
            response.StatusCode = 204; return true;
        }
        if (path == "/api/notifications/test" && request.HttpMethod == "POST")
        {
            await WebUiContext.WriteJsonAsync(response, notifications.TestNotification()); return true;
        }
        if (path == "/api/notifications/ringtone" && request.HttpMethod == "GET")
        {
            int volume = preferences.Snapshot().Notifications.Volume;
            if (request.QueryString["volume"] is { } value && (!int.TryParse(value, out volume) || volume is < 0 or > 100))
                throw new InvalidDataException("El volumen debe estar entre 0 y 100.");
            byte[] wave = NotificationAudio.Ringtone(volume);
            response.ContentType = "audio/wav"; response.ContentLength64 = wave.Length;
            response.Headers["Cache-Control"] = "no-store";
            await response.OutputStream.WriteAsync(wave); return true;
        }
        if (path != "/api/app/preferences") return false;
        if (request.HttpMethod == "POST")
        {
            using var body = await WebUiContext.ReadJsonAsync(request, path);
            var next = WebUiContext.ReadJsonValue<AppBehaviorSettings>(body.RootElement, new(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Faltan los ajustes.");
            if (next.Notifications == null) throw new InvalidDataException("Faltan los ajustes de notificación.");
            next.Notifications.Validate();
            await _edits.WaitAsync();
            try
            {
                var old = preferences.Snapshot();
                bool startupChanged = old.StartWithSystem != next.StartWithSystem;
                if (startupChanged)
                {
                    if (notifications.Platform is not { StartupSupported: true } platform) throw new InvalidDataException("El inicio automático no está disponible en este sistema.");
                    platform.SetStartWithSystem(next.StartWithSystem);
                }
                try { preferences.Update(p => { p.StartWithSystem = next.StartWithSystem; p.RunInBackground = next.RunInBackground; p.Notifications = next.Notifications.Clone(); }); }
                catch { if (startupChanged) notifications.Platform?.SetStartWithSystem(old.StartWithSystem); throw; }
            }
            finally { _edits.Release(); }
            WebUiServer.BroadcastStatusUpdate();
        }
        else if (request.HttpMethod != "GET") { response.StatusCode = 405; response.Headers["Allow"] = "GET, POST"; return true; }
        var current = preferences.Snapshot();
        var result = new AppSettingsResponse(true, new(current.StartWithSystem, current.RunInBackground, current.Notifications),
            notifications.Platform?.StartupSupported == true, notifications.Platform?.Description ?? "El host no ha configurado las notificaciones del sistema.");
        await WebUiContext.WriteJsonAsync(response, JsonSerializer.SerializeToElement(result, WebUiContext.JsonOptionsFor(new(JsonSerializerDefaults.Web))));
        return true;
    }
}
