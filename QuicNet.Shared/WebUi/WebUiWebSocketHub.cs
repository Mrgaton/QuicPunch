using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using QuicNet.Protocols;

namespace QuicNet.WebUi;

internal sealed class WebUiWebSocketHub : IAsyncDisposable
{
    private readonly record struct MetricKey(string Type, string Source, string? Series);
    private readonly record struct Metric(object Data, long RecordedAt, long Version = 0);
    private readonly record struct LiveRequest(int Id, bool Refresh);

    private sealed class ClientConnection : IAsyncDisposable
    {
        private const int MaxTrackedMetrics = 64;
        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; }
        internal WebClientDiagnostics Diagnostics { get; } = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private int _disposed;
        private readonly object _liveLock = new();
        private readonly Dictionary<MetricKey, Metric> _metrics = new();
        private LiveRequest? _liveRequest;
        private long _statusVersion, _updateVersion, _metricVersion, _sentMetricVersion;
        private string? _lastStatus, _lastUpdate;

        public bool HasLiveRequest { get { lock (_liveLock) return _liveRequest.HasValue; } }

        public ClientConnection(WebSocket socket)
        {
            Socket = socket;
        }

        public void SetLiveRequest(LiveRequest? request)
        {
            lock (_liveLock) _liveRequest = request;
        }

        public bool QueueMetric(MetricKey key, Metric metric)
        {
            lock (_liveLock)
            {
                if (_metrics.Count == MaxTrackedMetrics && !_metrics.ContainsKey(key))
                    _metrics.Remove(_metrics.MinBy(entry => entry.Value.Version).Key);
                _metrics[key] = metric with { Version = ++_metricVersion };
                return _liveRequest.HasValue;
            }
        }

        public void DiscardMetrics(string type, string source)
        {
            lock (_liveLock)
                foreach (var key in _metrics.Keys)
                    if (key.Type == type && key.Source == source) _metrics.Remove(key);
        }

        public async Task SendLiveAsync(long statusVersion, Lazy<string?> status, long updateVersion, Lazy<string?> update, CancellationToken ct)
        {
            lock (_liveLock)
            {
                if (_liveRequest is not { } request ||
                    (!request.Refresh && _sentMetricVersion == _metricVersion &&
                     _statusVersion == statusVersion && _updateVersion == updateVersion)) return;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            bool locked = false;
            try
            {
                await _sendLock.WaitAsync(deadline.Token).ConfigureAwait(false);
                locked = true;
                if (Socket.State != WebSocketState.Open) return;
                LiveRequest request;
                lock (_liveLock)
                {
                    if (_liveRequest is not { } pending) return;
                    request = pending;
                }
                string? nextStatus = request.Refresh || _statusVersion != statusVersion ? status.Value : _lastStatus;
                string? nextUpdate = request.Refresh || _updateVersion != updateVersion ? update.Value : _lastUpdate;
                bool sendStatus, sendUpdate;
                List<global::QuicNet.Serialization.WebUiWebSocketHubMessage2> metrics = [];
                lock (_liveLock)
                {
                    if (_liveRequest != request) return;
                    sendStatus = nextStatus != null && (request.Refresh || nextStatus != _lastStatus);
                    sendUpdate = nextUpdate != null && (request.Refresh || nextUpdate != _lastUpdate);
                    _lastStatus = nextStatus;
                    _lastUpdate = nextUpdate;
                    _statusVersion = statusVersion;
                    _updateVersion = updateVersion;
                    foreach (var (key, metric) in _metrics)
                    {
                        if (!request.Refresh && metric.Version <= _sentMetricVersion) continue;
                        if (key.Type is "voice_vad" or "chat_typing" && Stopwatch.GetElapsedTime(metric.RecordedAt).TotalSeconds > 2) continue;
                        metrics.Add(new(key.Type, metric.Data));
                    }
                    _sentMetricVersion = _metricVersion;
                    if (!sendStatus && !sendUpdate && metrics.Count == 0) return;
                    // Each browser request grants exactly one batch, even if its event loop stops.
                    _liveRequest = null;
                }

                var bytes = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(bytes))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "live_updates");
                    writer.WriteNumber("id", request.Id);
                    writer.WriteStartArray("events");
                    if (sendStatus) writer.WriteRawValue(nextStatus!, skipInputValidation: true);
                    if (sendUpdate) writer.WriteRawValue(nextUpdate!, skipInputValidation: true);
                    foreach (var metric in metrics) JsonSerializer.Serialize(writer, metric, WebUiContext.JsonOptions);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                await Socket.SendAsync(bytes.WrittenMemory, WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Socket.Abort(); throw; }
            finally { if (locked) _sendLock.Release(); }
        }

        public async Task SendTextAsync(string message, CancellationToken ct = default)
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
                    await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
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
    private long _statusVersion = 1, _updateVersion = 1;
    internal void NotifyStatusChanged() { Interlocked.Increment(ref _statusVersion); _stateChanges.Writer.TryWrite(true); }
    internal void NotifyUpdatesChanged() { Interlocked.Increment(ref _updateVersion); _stateChanges.Writer.TryWrite(true); }
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
        long lastSample = Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (_clients.Values.Any(client => client.HasLiveRequest)) wake.CancelAfter(2500);
                try { await _stateChanges.Reader.ReadAsync(wake.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                if (!_clients.Values.Any(client => client.HasLiveRequest)) continue;
                await Task.Delay(75, token);
                while (_stateChanges.Reader.TryRead(out _)) { }
                if (Stopwatch.GetElapsedTime(lastSample).TotalMilliseconds >= 2500)
                {
                    lastSample = Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref _statusVersion);
                }
                long statusVersion = Volatile.Read(ref _statusVersion), updateVersion = Volatile.Read(ref _updateVersion);
                var status = new Lazy<string?>(() => GetStatusSnapshot?.Invoke() is { } value ? SerializeEvent("status_updated", value) : null);
                var update = new Lazy<string?>(() => GetUpdateSnapshot?.Invoke() is { } value ? SerializeEvent("update_status", value) : null);
                await Task.WhenAll(_clients.Values.Select(client => client.SendLiveAsync(statusVersion, status, updateVersion, update, token))).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
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
            if (type == "live_pause") { client.SetLiveRequest(null); return; }
            if (type == "live_request")
            {
                if (root.TryGetProperty("id", out var number) && number.ValueKind == JsonValueKind.Number &&
                    number.TryGetInt32(out int liveId) && liveId > 0)
                {
                    bool refresh = root.TryGetProperty("refresh", out var flag) && flag.ValueKind == JsonValueKind.True;
                    client.SetLiveRequest(new LiveRequest(liveId, refresh));
                    _stateChanges.Writer.TryWrite(true);
                }
                return;
            }
            if (type is "sync" or "get_status")
            {
                if (GetStatusSnapshot?.Invoke() is { } status)
                    await client.SendTextAsync(SerializeEvent("status_updated", status), _cts.Token);
                if (GetUpdateSnapshot?.Invoke() is { } update)
                    await client.SendTextAsync(SerializeEvent("update_status", update), _cts.Token);
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
        string json = SerializeEvent(eventType, data);
        var tasks = _clients.Values.Select(client => client.SendTextAsync(json, _cts.Token));
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch { }
    }

    private static string SerializeEvent(string type, object data) =>
        JsonSerializer.Serialize(new global::QuicNet.Serialization.WebUiWebSocketHubMessage2(type, data), WebUiContext.JsonOptions);

    public void BroadcastMetric(string type, string source, object data, string? series = null)
    {
        if (_clients.IsEmpty) return;
        var key = new MetricKey(type, source, series);
        var metric = new Metric(data, Stopwatch.GetTimestamp());
        bool wake = false;
        foreach (var client in _clients.Values) wake |= client.QueueMetric(key, metric);
        if (wake) _stateChanges.Writer.TryWrite(true);
    }

    public void DiscardMetrics(string type, string source)
    {
        foreach (var client in _clients.Values) client.DiscardMetrics(type, source);
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
