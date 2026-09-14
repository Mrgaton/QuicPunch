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
    public Func<object>? GetStatusSnapshot { get; set; }
    private readonly Task _periodicSyncTask;

    public WebUiWebSocketHub(CancellationTokenSource cts)
    {
        _cts = cts;
        _periodicSyncTask = Task.Run(PeriodicSyncLoopAsync);
    }

    private async Task PeriodicSyncLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2500, _cts.Token).ConfigureAwait(false);
                if (!_clients.IsEmpty && GetStatusSnapshot != null)
                {
                    var status = GetStatusSnapshot();
                    if (status != null)
                    {
                        await BroadcastAsync("status_updated", status).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
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

            if (GetStatusSnapshot != null)
            {
                try
                {
                    var snapshot = GetStatusSnapshot();
                    if (snapshot != null)
                    {
                        await client.SendTextAsync(JsonSerializer.Serialize(new
                        {
                            type = "status_updated",
                            data = snapshot
                        }), _cts.Token).ConfigureAwait(false);
                    }
                }
                catch { }
            }

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
        // 2MB buffer for high-bitrate, ultra-high refresh rate chunks (supporting up to 144fps and 30+ Mbps I-frames)
        byte[] buffer = new byte[2 * 1024 * 1024];
        int accumulated = 0;

        try
        {
            while (!_cts.Token.IsCancellationRequested && client.Socket.State == WebSocketState.Open)
            {
                var result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer, accumulated, buffer.Length - accumulated), _cts.Token).ConfigureAwait(false);
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

                accumulated += result.Count;
                if (!result.EndOfMessage)
                {
                    if (accumulated >= buffer.Length)
                    {
                        // Exceeded maximum frame size, drop
                        accumulated = 0;
                    }
                    continue;
                }

                int messageLength = accumulated;
                accumulated = 0;

                if (result.MessageType == WebSocketMessageType.Text && messageLength > 0)
                {
                    string text = Encoding.UTF8.GetString(buffer, 0, messageLength);
                    if (text.Contains("\"ping\"", StringComparison.OrdinalIgnoreCase))
                    {
                        await client.SendTextAsync("{\"type\":\"pong\"}", _cts.Token).ConfigureAwait(false);
                    }
                    else if ((text.Contains("\"sync\"", StringComparison.OrdinalIgnoreCase) || text.Contains("\"get_status\"", StringComparison.OrdinalIgnoreCase)) && GetStatusSnapshot != null)
                    {
                        try
                        {
                            var snapshot = GetStatusSnapshot();
                            if (snapshot != null)
                            {
                                await client.SendTextAsync(JsonSerializer.Serialize(new
                                {
                                    type = "status_updated",
                                    data = snapshot
                                }), _cts.Token).ConfigureAwait(false);
                            }
                        }
                        catch { }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary && messageLength > 16)
                {
                    // Tagged or Legacy Binary:
                    // If Tag == 0x02: [Tag 0x02 (1B)][TargetPeerId (16B)][CodecId (1B)][Video Data]
                    // If Tag == 0x01: [Tag 0x01 (1B)][TargetPeerId (16B)][Audio Data]
                    // Legacy Audio: [TargetPeerId (16B)][Audio Data]
                    if (buffer[0] == 0x02 && messageLength > 18)
                    {
                        Guid targetPeerId = new Guid(buffer.AsSpan(1, 16));
                        byte codecId = buffer[17];
                        int videoLen = messageLength - 18;
                        byte[] videoPayload = new byte[videoLen];
                        Buffer.BlockCopy(buffer, 18, videoPayload, 0, videoLen);

                        if (targetPeerId == Guid.Empty)
                        {
                            _ = VoiceCallHandler.SendScreenFrameAsync(videoPayload);
                        }
                        else
                        {
                            _ = VoiceCallHandler.SendScreenFrameToPeerAsync(targetPeerId, videoPayload);
                        }
                    }
                    else if (buffer[0] == 0x03 && messageLength > 1)
                    {
                        // Tag 0x03: Screen Share Audio PCM Int16 samples
                        int pcmBytes = messageLength - 1;
                        byte[] audioPayload = new byte[pcmBytes];
                        Buffer.BlockCopy(buffer, 1, audioPayload, 0, pcmBytes);
                        _ = VoiceCallHandler.SendScreenAudioAsync(audioPayload);
                    }
                    else if (buffer[0] == 0x01 && messageLength > 17)
                    {
                        Guid targetPeerId = new Guid(buffer.AsSpan(1, 16));
                        int audioLen = messageLength - 17;
                        byte[] audioPayload = new byte[audioLen];
                        Buffer.BlockCopy(buffer, 17, audioPayload, 0, audioLen);

                        if (targetPeerId == Guid.Empty)
                        {
                            _ = VoiceCallHandler.BroadcastAudioDatagramAsync(audioPayload);
                        }
                        else
                        {
                            _ = VoiceCallHandler.SendAudioDatagramAsync(targetPeerId, audioPayload);
                        }
                    }
                    else
                    {
                        // Fallback: Legacy 16-byte PeerId + audio
                        Guid targetPeerId = new Guid(buffer.AsSpan(0, 16));
                        int audioLen = messageLength - 16;
                        byte[] audioPayload = new byte[audioLen];
                        Buffer.BlockCopy(buffer, 16, audioPayload, 0, audioLen);

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

    public void BroadcastBinaryVideo(Guid peerId, byte codecId, byte[] videoChunk)
    {
        if (_clients.IsEmpty || videoChunk == null || videoChunk.Length == 0) return;
        // Format: [Tag 0x02 (1B)][PeerId (16B)][CodecId (1B)][Video Data]
        byte[] packet = new byte[18 + videoChunk.Length];
        packet[0] = 0x02;
        peerId.TryWriteBytes(packet.AsSpan(1, 16));
        packet[17] = codecId;
        Buffer.BlockCopy(videoChunk, 0, packet, 18, videoChunk.Length);
        var tasks = _clients.Values.Select(client => client.SendBinaryAsync(packet, _cts.Token));
        _ = Task.WhenAll(tasks);
    }

    public void BroadcastBinaryScreenAudio(Guid peerId, byte[] audioData)
    {
        if (_clients.IsEmpty || audioData == null || audioData.Length == 0) return;
        // Format: [Tag 0x04 (1B)][PeerId (16B)][Audio PCM Data]
        byte[] packet = new byte[17 + audioData.Length];
        packet[0] = 0x04;
        peerId.TryWriteBytes(packet.AsSpan(1, 16));
        Buffer.BlockCopy(audioData, 0, packet, 17, audioData.Length);
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
