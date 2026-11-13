using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace QuicNet.Watch.Web;

internal sealed class WebSocketHub : IAsyncDisposable
{
    private sealed class Client(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly CancellationTokenSource _sendLifetime = new();
        private int _pendingSends;
        private int _disposed;

        public async Task SendAsync(byte[] payload)
        {
            if (Interlocked.Increment(ref _pendingSends) > 32)
            {
                Interlocked.Decrement(ref _pendingSends);
                Abort();
                throw new WebSocketException("Watch client cannot keep up with updates.");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_sendLifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            bool entered = false;
            try
            {
                await _sendGate.WaitAsync(deadline.Token).ConfigureAwait(false);
                entered = true;
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(payload, WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
            }
            catch
            {
                Abort();
                throw;
            }
            finally
            {
                if (entered) _sendGate.Release();
                Interlocked.Decrement(ref _pendingSends);
            }
        }

        private void Abort()
        {
            _sendLifetime.Cancel();
            try { Socket.Abort(); } catch { }
        }

        public async Task CloseAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            bool entered = false;
            try
            {
                await _sendGate.WaitAsync(deadline.Token).ConfigureAwait(false);
                entered = true;
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    // The receive loop owns reads; CloseAsync would start a second reader.
                    await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", deadline.Token).ConfigureAwait(false);
            }
            catch { Abort(); }
            finally
            {
                _sendLifetime.Cancel();
                Socket.Dispose();
                if (entered) _sendGate.Release();
            }
        }
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly JsonSerializerOptions _json;
    private int _disposed;

    public WebSocketHub(JsonSerializerOptions json) => _json = json;

    public async Task AddAsync(WebSocket socket, CancellationToken ct)
    {
        Guid id = Guid.NewGuid();
        var client = new Client(socket);
        _clients[id] = client;
        if (Volatile.Read(ref _disposed) != 0)
        {
            _clients.TryRemove(id, out _);
            await client.CloseAsync().ConfigureAwait(false);
            return;
        }
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
        catch (ObjectDisposedException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            await client.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task BroadcastAsync(string type, object? data)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new QuicNet.Serialization.WatchBroadcastMessage(type, data), QuicNet.Serialization.SharedJson.WithOptions(_json)));
        await Task.WhenAll(_clients.ToArray().Select(pair => SendAsync(pair.Key, pair.Value, payload))).ConfigureAwait(false);
    }

    private async Task SendAsync(Guid id, Client client, byte[] payload)
    {
        if (client.Socket.State != WebSocketState.Open)
        {
            _clients.TryRemove(id, out _);
            return;
        }
        try { await client.SendAsync(payload).ConfigureAwait(false); }
        catch
        {
            _clients.TryRemove(id, out _);
            await client.CloseAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await Task.WhenAll(_clients.Values.Select(client => client.CloseAsync())).ConfigureAwait(false);
        _clients.Clear();
    }
}
