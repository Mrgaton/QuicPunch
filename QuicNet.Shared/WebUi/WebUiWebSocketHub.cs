using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using QuicNet.Protocols;

namespace QuicNet.WebUi;

internal sealed class WebUiWebSocketHub : IAsyncDisposable
{
    private sealed class ClientConnection : IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; }
        internal WebClientDiagnostics Diagnostics { get; } = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private int _disposed;
        private readonly Dictionary<string, string> _sentStates = new();

        public ClientConnection(WebSocket socket)
        {
            Socket = socket;
        }

        public async Task SendTextAsync(string message, CancellationToken ct = default, string? stateKey = null)
        {
            if (Socket.State != WebSocketState.Open) return;
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            bool locked = false;
            try
            {
                await _sendLock.WaitAsync(deadline.Token).ConfigureAwait(false);
                locked = true;
                if (Socket.State == WebSocketState.Open)
                {
                    if (stateKey != null && _sentStates.GetValueOrDefault(stateKey) == message) return;
                    await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
                    if (stateKey != null) _sentStates[stateKey] = message;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Socket.Abort(); throw; }
            finally
            {
                if (locked) _sendLock.Release();
            }
        }

        public async Task SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            if (Socket.State != WebSocketState.Open) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            bool locked = false;
            try
            {
                await _sendLock.WaitAsync(deadline.Token).ConfigureAwait(false);
                locked = true;
                if (Socket.State == WebSocketState.Open)
                {
                    await Socket.SendAsync(bytes, WebSocketMessageType.Binary, true, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // A closed or stalled preview must not stop the P2P encoder or
                // another viewer. Abort also prevents a partial message reuse.
                try { Socket.Abort(); } catch (ObjectDisposedException) { }
            }
            finally
            {
                if (locked) _sendLock.Release();
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
                // Pending senders still release this gate after socket shutdown.
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
    public Func<QuicNet.Updates.UpdateStatus?>? GetUpdateSnapshot { get; set; }
    public Func<string, JsonElement, Task<object>>? ExecuteCommand { get; set; }
    private readonly Channel<bool> _stateChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private int _statusDirty = 1, _updatesDirty = 1;
    internal void NotifyStatusChanged() { Interlocked.Exchange(ref _statusDirty, 1); _stateChanges.Writer.TryWrite(true); }
    internal void NotifyUpdatesChanged() { Interlocked.Exchange(ref _updatesDirty, 1); _stateChanges.Writer.TryWrite(true); }
    private readonly Task _periodicSyncTask;
    private readonly CancellationTokenSource _syncCts;
    private int _disposed;

    private readonly bool _shutdownWhenNoClients;
    internal Func<bool>? KeepRunning { get; set; }
    public WebUiWebSocketHub(CancellationTokenSource cts, bool shutdownWhenNoClients = true)
    {
        _cts = cts;
        _shutdownWhenNoClients = shutdownWhenNoClients;
        _syncCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _periodicSyncTask = PeriodicSyncLoopAsync(_syncCts.Token);
    }

    private async Task PeriodicSyncLoopAsync(CancellationToken token)
    {
        long lastSample = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (!_clients.IsEmpty) wake.CancelAfter(2500); // Sample changing telemetry on the server only.
                try { await _stateChanges.Reader.ReadAsync(wake.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { Interlocked.Exchange(ref _statusDirty, 1); }
                if (_clients.IsEmpty) continue;
                await Task.Delay(75, token); // Bound/coalesce bursts of events and download progress.
                while (_stateChanges.Reader.TryRead(out _)) { }
                if (System.Diagnostics.Stopwatch.GetElapsedTime(lastSample).TotalMilliseconds >= 2500)
                    Interlocked.Exchange(ref _statusDirty, 1);
                if (Interlocked.Exchange(ref _statusDirty, 0) != 0 && GetStatusSnapshot != null)
                {
                    lastSample = System.Diagnostics.Stopwatch.GetTimestamp();
                    var status = GetStatusSnapshot();
                    if (status != null)
                    {
                        await BroadcastAsync("status_updated", status).ConfigureAwait(false);
                    }
                }
                if (Interlocked.Exchange(ref _updatesDirty, 0) != 0 && GetUpdateSnapshot?.Invoke() is { } update)
                    await BroadcastAsync("update_status", update).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public void OnClientConnected()
    {
        NotifyStatusChanged(); NotifyUpdatesChanged();
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
        if (!_shutdownWhenNoClients || KeepRunning?.Invoke() == true || Volatile.Read(ref _hasHadClients) == 0) return;

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
                        if (!cts.IsCancellationRequested && _clients.IsEmpty && !_cts.IsCancellationRequested && KeepRunning?.Invoke() != true)
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
        ClientConnection? client = null;
        try
        {
            HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            client = new ClientConnection(wsContext.WebSocket);
            _clients[client.Id] = client;
            OnClientConnected();

            await client.SendTextAsync(JsonSerializer.Serialize(new global::QuicNet.Serialization.WebUiWebSocketHubMessage1("ws_connected", client.Id.ToString()),global::QuicNet.WebUi.WebUiContext.JsonOptions), _cts.Token).ConfigureAwait(false);

            if (GetStatusSnapshot != null)
            {
                try
                {
                    var snapshot = GetStatusSnapshot();
                    if (snapshot != null)
                    {
                        await client.SendTextAsync(JsonSerializer.Serialize(new global::QuicNet.Serialization.WebUiWebSocketHubMessage2("status_updated", snapshot),global::QuicNet.WebUi.WebUiContext.JsonOptions), _cts.Token, "status_updated").ConfigureAwait(false);
                    }
                }
                catch { }
            }

            if (GetUpdateSnapshot?.Invoke() is { } update)
                await client.SendTextAsync(JsonSerializer.Serialize(new global::QuicNet.Serialization.WebUiWebSocketHubMessage2("update_status", update), WebUiContext.JsonOptions), _cts.Token, "update_status");

            // The listener owns a bounded WebSocket reservation for this task.
            // Keep it until disconnect instead of detaching an unbounded reader.
            await HandleClientLoopAsync(client).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (client != null)
            {
                _clients.TryRemove(client.Id, out _);
                await client.DisposeAsync().ConfigureAwait(false);
                CheckAutoShutdown();
            }
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
                            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await client.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", deadline.Token).ConfigureAwait(false);
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
                    if (messageLength <= 16384) await HandleTextAsync(client, buffer.AsMemory(0, messageLength)).ConfigureAwait(false);
                }
                else if (result.MessageType == WebSocketMessageType.Binary && messageLength > 16)
                {
                    // Tagged or Legacy Binary:
                    // If Tag == 0x02: [Tag 0x02 (1B)][TargetPeerId (16B)][CodecId (1B)][Video Data]
                    // If Tag == 0x01: [Tag 0x01 (1B)][TargetPeerId (16B)][Audio Data]
                    // Legacy Audio: [TargetPeerId (16B)][Audio Data]
                    if (buffer[0] == 0x02 && messageLength > 18)
                    {
                        // Video is produced and consumed exclusively by the native FFmpeg pipeline.
                        continue;
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

    public Task BroadcastBinaryVideoAsync(Guid peerId, byte codecId, byte[] videoChunk)
    {
        if (_clients.IsEmpty || videoChunk == null || videoChunk.Length == 0) return Task.CompletedTask;
        // Format: [Tag 0x02 (1B)][PeerId (16B)][CodecId (1B)][Video Data]
        byte[] packet = new byte[18 + videoChunk.Length];
        packet[0] = 0x02;
        peerId.TryWriteBytes(packet.AsSpan(1, 16));
        packet[17] = codecId;
        Buffer.BlockCopy(videoChunk, 0, packet, 18, videoChunk.Length);
        var tasks = _clients.Values.Select(client => client.SendBinaryAsync(packet, _cts.Token));
        return Task.WhenAll(tasks);
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

    private async Task HandleTextAsync(ClientConnection client, ReadOnlyMemory<byte> bytes)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String) return;
            string? type = kind.GetString();
            if (type == "client_log") { if (root.TryGetProperty("data", out var diagnostic)) client.Diagnostics.Accept(diagnostic); return; }
            if (type == "ping") { await client.SendTextAsync("{\"type\":\"pong\"}", _cts.Token); return; }
            if (type is "sync" or "get_status")
            {
                if (GetStatusSnapshot?.Invoke() is { } status)
                    await client.SendTextAsync(JsonSerializer.Serialize(new global::QuicNet.Serialization.WebUiWebSocketHubMessage2("status_updated", status), WebUiContext.JsonOptions), _cts.Token);
                if (GetUpdateSnapshot?.Invoke() is { } update)
                    await client.SendTextAsync(JsonSerializer.Serialize(new global::QuicNet.Serialization.WebUiWebSocketHubMessage2("update_status", update), WebUiContext.JsonOptions), _cts.Token);
                return;
            }
            if (type != "request" || !root.TryGetProperty("id", out var requestId) || requestId.ValueKind != JsonValueKind.String ||
                requestId.GetString() is not { Length: > 0 and <= 64 } validId) return;
            id = validId;
            if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Falta el comando.");
            string name = command.GetString()!;
            object result = name == "status.get" ? GetStatusSnapshot?.Invoke() ?? throw new InvalidOperationException("Estado no disponible.")
                : ExecuteCommand != null ? await ExecuteCommand(name, root.TryGetProperty("data", out var data) ? data : default)
                : throw new ArgumentException("Comando desconocido.");
            await client.SendTextAsync(JsonSerializer.Serialize(new global::QuicNet.Serialization.WebSocketReply("response", id, true, result), WebUiContext.JsonOptions), _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (id != null)
                await client.SendTextAsync(JsonSerializer.Serialize(new global::QuicNet.Serialization.WebSocketReply("response", id, false, error: ex.Message), WebUiContext.JsonOptions), _cts.Token);
        }
    }

    public void Broadcast(string eventType, object data)
    {
        _ = BroadcastAsync(eventType, data);
    }

    public async Task BroadcastAsync(string eventType, object data)
    {
        if (_clients.IsEmpty) return;
        string json = JsonSerializer.Serialize(new global::QuicNet.Serialization.WebUiWebSocketHubMessage2(eventType, data),global::QuicNet.WebUi.WebUiContext.JsonOptions);

        string? stateKey = eventType is "status_updated" or "update_status" ? eventType : null;
        var tasks = _clients.Values.Select(client => client.SendTextAsync(json, _cts.Token, stateKey));
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _syncCts.Cancel();
        await _periodicSyncTask.ConfigureAwait(false);
        _syncCts.Dispose();
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        _clients.Clear();
    }
}
