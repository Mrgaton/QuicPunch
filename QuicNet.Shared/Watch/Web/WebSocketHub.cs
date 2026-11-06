using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace QuicNet.Watch.Web;

internal sealed class WebSocketHub : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly JsonSerializerOptions _json;

    public WebSocketHub(JsonSerializerOptions json) => _json = json;

    public async Task AddAsync(WebSocket socket, CancellationToken ct)
    {
        Guid id = Guid.NewGuid();
        _clients[id] = socket;
        byte[] buffer = new byte[16384];
        int used = 0;
        var diagnostics = new QuicNet.WebUi.WebClientDiagnostics();
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, used, buffer.Length - used), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                used += result.Count;
                if (!result.EndOfMessage) { if (used == buffer.Length) { socket.Abort(); break; } continue; }
                if (result.MessageType == WebSocketMessageType.Text)
                    try
                    {
                        using var json = JsonDocument.Parse(buffer.AsMemory(0, used), new JsonDocumentOptions { MaxDepth = 8 });
                        var root = json.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                            type.GetString() == "client_log" && root.TryGetProperty("data", out var data)) diagnostics.Accept(data);
                    }
                    catch (JsonException) { }
                used = 0;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None).ConfigureAwait(false); } catch { }
            socket.Dispose();
        }
    }

    public async Task BroadcastAsync(string type, object? data)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new QuicNet.Serialization.WatchBroadcastMessage(type, data), QuicNet.Serialization.SharedJson.WithOptions(_json)));
        foreach ((Guid id, WebSocket socket) in _clients.ToArray())
        {
            if (socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }
            try { await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
            catch
            {
                _clients.TryRemove(id, out _);
                try { socket.Abort(); } catch { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (WebSocket socket in _clients.Values)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None).ConfigureAwait(false); } catch { }
            socket.Dispose();
        }
        _clients.Clear();
    }
}
