using System.Net;
using System.Text.Json;
using QuicNet.Updates;

namespace QuicNet.WebUi.Modules;

internal static class UpdatesApiModule
{
    internal static async Task<object> ExecuteAsync(AutoUpdateService? updater, string command, JsonElement data)
    {
        if (updater == null) throw new InvalidOperationException("Actualizaciones no disponibles en este host.");
        switch (command)
        {
            case "updates.get": break;
            case "updates.check": _ = updater.CheckAsync(); break;
            case "updates.install": _ = updater.InstallAsync(); break;
            case "updates.automatic":
                if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("enabled", out var enabled) ||
                    enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Falta la preferencia automática.");
                updater.SetAutomatic(enabled.GetBoolean()); break;
            case "updates.channel":
                if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("channel", out var channel) ||
                    channel.ValueKind != JsonValueKind.String || !ReleaseIdentity.IsValidChannel(channel.GetString()))
                    throw new ArgumentException("Elige el canal estable o beta.");
                await updater.SetChannelAsync(channel.GetString()!);
                _ = updater.CheckAsync(); break;
            default: throw new ArgumentException("Comando desconocido.");
        }
        return updater.Status;
    }

    public static async Task<bool> HandleAsync(AutoUpdateService? updater, string path, HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!path.StartsWith("/api/updates", StringComparison.Ordinal)) return false;
        if (updater == null)
        {
            await WebUiContext.WriteJsonAsync(response, new global::QuicNet.Serialization.UpdatesApiModuleMessage1(false, "Actualizaciones no disponibles en este host."), 503);
            return true;
        }
        if (request.HttpMethod == "POST")
        {
            if (path == "/api/updates/check") _ = updater.CheckAsync();
            else if (path == "/api/updates/install") _ = updater.InstallAsync();
            else if (path == "/api/updates/automatic")
            {
                using var document = await WebUiContext.ReadJsonAsync(request, path);
                updater.SetAutomatic(document.RootElement.GetProperty("enabled").GetBoolean());
            }
            else if (path == "/api/updates/channel")
            {
                using var document = await WebUiContext.ReadJsonAsync(request, path);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("channel", out var value) || value.ValueKind != JsonValueKind.String ||
                    !ReleaseIdentity.IsValidChannel(value.GetString()))
                {
                    await WebUiContext.WriteJsonAsync(response, new global::QuicNet.Serialization.UpdatesApiModuleMessage1(false, "Elige el canal estable o beta."), 400);
                    return true;
                }
                try { await updater.SetChannelAsync(value.GetString()!); }
                catch (InvalidOperationException ex)
                {
                    await WebUiContext.WriteJsonAsync(response, new global::QuicNet.Serialization.UpdatesApiModuleMessage1(false, ex.Message), 409);
                    return true;
                }
                _ = updater.CheckAsync();
            }
            else return false;
        }
        else if (request.HttpMethod != "GET" || path != "/api/updates") return false;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(updater.Status, UpdateJsonContext.Default.UpdateStatus);
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        return true;
    }
}
