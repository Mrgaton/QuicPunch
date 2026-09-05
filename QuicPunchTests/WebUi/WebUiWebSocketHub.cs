using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace QuicPunchTests.WebUi;

internal sealed class WebUiWebSocketHub : IAsyncDisposable
{
    private sealed class ClientConnection : IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; }
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private int _disposed;

        public ClientConnection(WebSocket socket)
        {
            Socket = socket;
        }

        public async Task SendTextAsync(string message, CancellationToken ct = default)
        {
            if (Socket.State != WebSocketState.Open) return;
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            if (Socket.State != WebSocketState.Open) return;
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    await Socket.SendAsync(bytes, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cts.Token).ConfigureAwait(false);
                }
            }
            catch { }
            finally
            {
                Socket.Dispose();
                _sendLock.Dispose();
            }
        }
    }

    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private readonly CancellationTokenSource _cts;
    private int _hasHadClients;
    private CancellationTokenSource? _shutdownCts;
    private readonly object _shutdownLock = new();

    public int ConnectedClientsCount => _clients.Count;

    public WebUiWebSocketHub(CancellationTokenSource cts)
    {
        _cts = cts;
    }

    public void OnClientConnected()
    {
        Interlocked.Exchange(ref _hasHadClients, 1);
        lock (_shutdownLock)
        {
            if (_shutdownCts != null)
            {
                try { _shutdownCts.Cancel(); _shutdownCts.Dispose(); } catch { }
                _shutdownCts = null;
            }
        }
    }

    public void CheckAutoShutdown(TimeSpan? delay = null)
    {
        if (Volatile.Read(ref _hasHadClients) == 0) return;

        lock (_shutdownLock)
        {
            if (_clients.IsEmpty)
            {
                if (_shutdownCts != null) return;
                var cts = new CancellationTokenSource();
                _shutdownCts = cts;
                var waitTime = delay ?? TimeSpan.FromSeconds(3.5);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(waitTime, cts.Token).ConfigureAwait(false);
                        if (!cts.IsCancellationRequested && _clients.IsEmpty && !_cts.IsCancellationRequested)
                        {
                            Console.WriteLine("[WebUI] All web UI windows closed. Shutting down application...");
                            try { _cts.Cancel(); } catch { }
                            await Task.Delay(1200).ConfigureAwait(false);
                            Environment.Exit(0);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                });
            }
        }
    }

    public async Task AcceptSocketAsync(HttpListenerContext context)
    {
        try
        {
            HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            var client = new ClientConnection(wsContext.WebSocket);
            _clients[client.Id] = client;
            OnClientConnected();

            await client.SendTextAsync(JsonSerializer.Serialize(new
            {
                type = "ws_connected",
                clientId = client.Id.ToString()
            }), _cts.Token).ConfigureAwait(false);

            _ = Task.Run(() => HandleClientLoopAsync(client), _cts.Token);
        }
        catch (Exception ex)
        {
            WebUiServer.LogEvent($"[WS] Failed to accept WebSocket connection: {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { }
        }
    }

    private async Task HandleClientLoopAsync(ClientConnection client)
    {
        byte[] buffer = new byte[16384];
        try
        {
            while (!_cts.Token.IsCancellationRequested && client.Socket.State == WebSocketState.Open)
            {
                var result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (client.Socket.State == WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await client.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
                        }
                        catch { }
                    }
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                {
                    string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (text.Contains("\"ping\"", StringComparison.OrdinalIgnoreCase))
                    {
                        await client.SendTextAsync("{\"type\":\"pong\"}", _cts.Token).ConfigureAwait(false);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary && result.Count > 16)
                {
                    if (result.Count > 64 * 1024)
                    {
                        // Drop oversized frames to prevent memory exhaustion
                        continue;
                    }
                    Guid targetPeerId = new Guid(buffer.AsSpan(0, 16));
                    byte[] audioPayload = new byte[result.Count - 16];
                    Buffer.BlockCopy(buffer, 16, audioPayload, 0, result.Count - 16);
                    if (targetPeerId == Guid.Empty)
                    {
                        _ = VoiceCallHandler.BroadcastAudioDatagramAsync(audioPayload);
                    }
                    else
                    {
                        _ = VoiceCallHandler.SendAudioDatagramAsync(targetPeerId, audioPayload);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            WebUiServer.LogEvent($"[WS] Client loop ended: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            await client.DisposeAsync().ConfigureAwait(false);
            CheckAutoShutdown();
        }
    }

    public void BroadcastBinaryAudio(Guid peerId, byte[] audioData)
    {
        if (_clients.IsEmpty || audioData == null || audioData.Length == 0) return;
        byte[] packet = new byte[16 + audioData.Length];
        peerId.TryWriteBytes(packet.AsSpan(0, 16));
        Buffer.BlockCopy(audioData, 0, packet, 16, audioData.Length);
        var tasks = _clients.Values.Select(client => client.SendBinaryAsync(packet, _cts.Token));
        _ = Task.WhenAll(tasks);
    }

    public void Broadcast(string eventType, object data)
    {
        _ = BroadcastAsync(eventType, data);
    }

    public async Task BroadcastAsync(string eventType, object data)
    {
        if (_clients.IsEmpty) return;
        string json = JsonSerializer.Serialize(new
        {
            type = eventType,
            data
        });

        var tasks = _clients.Values.Select(client => client.SendTextAsync(json, _cts.Token));
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        _clients.Clear();
    }
}
