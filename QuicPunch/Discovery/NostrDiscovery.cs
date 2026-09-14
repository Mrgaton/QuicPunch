using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuicPunch.Helpers;

namespace QuicPunch
{
    /// <summary>
    /// Minimal Nostr rendezvous transport. Relays are untrusted signaling only:
    /// the event content is a normal QuicPunch endpoint token and trust is
    /// established locally after QuicPunch authenticates the peer certificate.
    /// </summary>
    public sealed class NostrDiscovery : IAsyncDisposable, IDisposable
    {
        public const int DiscoveryKind = 27227;
        private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan EventMaxAge = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan EventFutureTolerance = TimeSpan.FromMinutes(1);
        private const int MaxEventBytes = 64 * 1024;
        private const int MaxTokenLength = 16 * 1024;

        public static readonly string[] DefaultRelays =
        {
            "wss://purplerelay.com",
            "wss://nostr.oxtr.dev",
            "wss://relay.primal.net",
            "wss://offchain.pub",
            "wss://nostr.bitcoiner.social"
        };

        private readonly Func<string?> _tokenProvider;
        private readonly string? _scope;
        private readonly string _channelTag;
        private readonly byte[] _secretKey;
        private readonly string _publicKeyHex;
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly ConcurrentDictionary<string, RelayConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _seenEvents = new(StringComparer.Ordinal);
        private readonly Func<IWebProxy?>? _proxyProvider;
        private IWebProxy? _proxy;
        private CancellationTokenSource? _runCts;
        private Task[] _relayTasks = Array.Empty<Task>();
        private bool _disposed;

        public event Action<string>? OnTokenFound;
        public event Action<string, string>? OnEventDiscovered;

        public bool IsRunning => _runCts is { IsCancellationRequested: false };
        public int ConnectedRelayCount => _connections.Count;
        public string ChannelTag => _channelTag;
        public string? Scope => _scope;
        public string PublicKeyHex => _publicKeyHex;
        public IWebProxy? Proxy
        {
            get => _proxy;
            set => _proxy = value;
        }

        public NostrDiscovery(byte[]? poolId, Func<string?> tokenProvider, string? scope = null, IWebProxy? proxy = null, Func<IWebProxy?>? proxyProvider = null, byte[]? nostrPrivateKey = null)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _scope = scope;
            _proxy = proxy;
            _proxyProvider = proxyProvider;
            _channelTag = CreateChannelTag(poolId, scope);
            _secretKey = nostrPrivateKey is { Length: 32 } ? (byte[])nostrPrivateKey.Clone() : NostrSchnorr.CreatePrivateKey();
            _publicKeyHex = Convert.ToHexString(NostrSchnorr.GetPublicKeyX(_secretKey)).ToLowerInvariant();
        }

        public NostrDiscovery(byte[]? poolId, Func<string?> tokenProvider)
            : this(poolId, tokenProvider, scope: null, proxy: null, proxyProvider: null, nostrPrivateKey: null)
        {
        }

        public async Task StartAsync(IEnumerable<string>? relays = null, CancellationToken cancellationToken = default)
        {
            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (IsRunning)
                    return;

                string[] relayList = NormalizeRelays(relays).ToArray();
                if (relayList.Length == 0)
                    relayList = DefaultRelays;

                _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CancellationToken token = _runCts.Token;
                _relayTasks = relayList.Select(relay => Task.Run(() => RelayLoopAsync(relay, token), token)).ToArray();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task StopAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_runCts == null)
                    return;

                try { _runCts.Cancel(); } catch { }

                foreach (var connection in _connections.Values)
                    connection.Abort();

                Task[] tasks = _relayTasks;
                _relayTasks = Array.Empty<Task>();

                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }

                foreach (var connection in _connections.Values)
                    connection.Dispose();
                _connections.Clear();

                _runCts.Dispose();
                _runCts = null;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task PublishNowAsync(CancellationToken cancellationToken = default)
        {
            if (!IsRunning)
                return;

            string? token;
            try
            {
                token = _tokenProvider();
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
                return;

            var tasks = _connections.Values
                .Where(c => c.IsOpen)
                .Select(c => PublishAsync(c, token, cancellationToken))
                .ToArray();

            if (tasks.Length == 0)
                return;

            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        }

        private async Task RelayLoopAsync(string relay, CancellationToken token)
        {
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                RelayConnection? connection = null;
                ClientWebSocket? pendingSocket = null;
                try
                {
                    pendingSocket = new ClientWebSocket();
                    pendingSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    try { pendingSocket.Options.SetRequestHeader("User-Agent", "QuicPunch-Discovery/1.0"); } catch { }

                    var proxy = Proxy ?? _proxyProvider?.Invoke();
                    if (proxy != null)
                    {
                        try { pendingSocket.Options.Proxy = proxy; } catch { }
                    }

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await pendingSocket.ConnectAsync(new Uri(relay), connectCts.Token).ConfigureAwait(false);

                    connection = new RelayConnection(relay, pendingSocket);
                    pendingSocket = null;
                    _connections[relay] = connection;
                    failures = 0;

                    await SubscribeAsync(connection, token).ConfigureAwait(false);

                    string? initialToken = _tokenProvider();
                    if (!string.IsNullOrWhiteSpace(initialToken) && initialToken.Length <= MaxTokenLength)
                        await PublishAsync(connection, initialToken, token).ConfigureAwait(false);

                    Task receiver = ReceiveLoopAsync(connection, token);
                    while (!token.IsCancellationRequested && connection.IsOpen)
                    {
                        int jitterSeconds = RandomNumberGenerator.GetInt32(-10, 11);
                        TimeSpan randomizedInterval = PublishInterval + TimeSpan.FromSeconds(jitterSeconds);
                        if (randomizedInterval < TimeSpan.FromSeconds(15))
                            randomizedInterval = TimeSpan.FromSeconds(15);

                        Task delay = Task.Delay(randomizedInterval, token);
                        Task completed = await Task.WhenAny(receiver, delay).ConfigureAwait(false);
                        if (completed == receiver)
                        {
                            await receiver.ConfigureAwait(false);
                            break;
                        }

                        string? currentToken = _tokenProvider();
                        if (!string.IsNullOrWhiteSpace(currentToken) && currentToken.Length <= MaxTokenLength)
                            await PublishAsync(connection, currentToken, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        QuicPunchLog.Info($"[NOSTR] Relay {relay} disconnected: {ex.Message}");
                }
                finally
                {
                    if (connection != null)
                    {
                        if (_connections.TryGetValue(relay, out var current) && ReferenceEquals(current, connection))
                            _connections.TryRemove(relay, out _);
                        connection.Dispose();
                    }
                    else
                    {
                        pendingSocket?.Dispose();
                    }
                }

                if (token.IsCancellationRequested)
                    break;

                failures = Math.Min(failures + 1, 5);
                int delaySeconds = Math.Min(30, 1 << failures);
                try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task SubscribeAsync(RelayConnection connection, CancellationToken token)
        {
            string subscriptionId = "qp-" + Guid.NewGuid().ToString("N")[..16];
            long since = new DateTimeOffset(PreciseTime.GetCorrectTime()).Subtract(EventMaxAge).ToUnixTimeSeconds();

            object[] request =
            {
                "REQ",
                subscriptionId,
                new Dictionary<string, object>
                {
                    ["kinds"] = new[] { DiscoveryKind },
                    ["#x"] = new[] { _channelTag },
                    ["since"] = since,
                    ["limit"] = 128
                }
            };

            await connection.SendJsonAsync(request, token).ConfigureAwait(false);
        }

        private async Task PublishAsync(RelayConnection connection, string tokenValue, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _runCts?.Token ?? CancellationToken.None,
                cancellationToken);

            long createdAt = new DateTimeOffset(PreciseTime.GetCorrectTime()).ToUnixTimeSeconds();
            string[][] tags =
            {
                new[] { "x", _channelTag },
                new[] { "t", "quicpunch" }
            };

            byte[] serialized = SerializeForId(_publicKeyHex, createdAt, DiscoveryKind, tags, tokenValue);
            byte[] idBytes = SHA256.HashData(serialized);
            string id = Convert.ToHexString(idBytes).ToLowerInvariant();
            string sig = Convert.ToHexString(NostrSchnorr.Sign(idBytes, _secretKey)).ToLowerInvariant();

            var evt = new NostrEvent
            {
                Id = id,
                PubKey = _publicKeyHex,
                CreatedAt = createdAt,
                Kind = DiscoveryKind,
                Tags = tags,
                Content = tokenValue,
                Sig = sig
            };

            await connection.SendJsonAsync(new object[] { "EVENT", evt }, linked.Token).ConfigureAwait(false);
        }

        private async Task ReceiveLoopAsync(RelayConnection connection, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            using var message = new MemoryStream();

            while (!token.IsCancellationRequested && connection.IsOpen)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    if (result.MessageType != WebSocketMessageType.Text)
                        continue;

                    if (message.Length + result.Count > MaxEventBytes)
                        throw new InvalidDataException("Nostr relay message exceeded the maximum allowed size.");

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (message.Length == 0)
                    continue;

                ProcessRelayMessage(message.GetBuffer().AsMemory(0, (int)message.Length));
            }
        }

        private void ProcessRelayMessage(ReadOnlyMemory<byte> utf8)
        {
            try
            {
                using var document = JsonDocument.Parse(utf8);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3)
                    return;
                if (!string.Equals(root[0].GetString(), "EVENT", StringComparison.Ordinal))
                    return;

                JsonElement evt = root[2];
                if (!TryReadEvent(evt, out string id, out string pubKey, out long createdAt, out int kind, out string[][] tags, out string content, out string sig))
                    return;
                if (kind != DiscoveryKind || content.Length == 0 || content.Length > MaxTokenLength)
                    return;
                if (string.Equals(pubKey, _publicKeyHex, StringComparison.OrdinalIgnoreCase))
                    return;
                if (!tags.Any(tag => tag.Length >= 2 && tag[0] == "x" && tag[1] == _channelTag))
                    return;

                long now = new DateTimeOffset(PreciseTime.GetCorrectTime()).ToUnixTimeSeconds();
                if (createdAt < now - (long)EventMaxAge.TotalSeconds ||
                    createdAt > now + (long)EventFutureTolerance.TotalSeconds)
                    return;

                byte[] serialized = SerializeForId(pubKey, createdAt, kind, tags, content);
                byte[] expectedId = SHA256.HashData(serialized);
                string expectedIdHex = Convert.ToHexString(expectedId).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(id),
                        Encoding.ASCII.GetBytes(expectedIdHex)))
                    return;

                byte[] pubKeyBytes;
                byte[] signature;
                try
                {
                    pubKeyBytes = Convert.FromHexString(pubKey);
                    signature = Convert.FromHexString(sig);
                }
                catch
                {
                    return;
                }

                if (pubKeyBytes.Length != 32 || signature.Length != 64 ||
                    !NostrSchnorr.Verify(expectedId, pubKeyBytes, signature))
                    return;

                if (!_seenEvents.TryAdd(id, Stopwatch.GetTimestamp()))
                    return;
                PruneSeenEvents();

                OnEventDiscovered?.Invoke(pubKey, content);
                OnTokenFound?.Invoke(content);
            }
            catch
            {
                // Relays and received events are untrusted input.
            }
        }

        private void PruneSeenEvents()
        {
            if (_seenEvents.Count < 1024)
                return;

            foreach (var item in _seenEvents)
            {
                if (Stopwatch.GetElapsedTime(item.Value) > TimeSpan.FromMinutes(10))
                    _seenEvents.TryRemove(item.Key, out _);
            }

            if (_seenEvents.Count <= 4096)
                return;

            foreach (var item in _seenEvents.OrderBy(kvp => kvp.Value).Take(_seenEvents.Count - 4096))
                _seenEvents.TryRemove(item.Key, out _);
        }

        private static bool TryReadEvent(
            JsonElement evt,
            out string id,
            out string pubKey,
            out long createdAt,
            out int kind,
            out string[][] tags,
            out string content,
            out string sig)
        {
            id = pubKey = content = sig = string.Empty;
            createdAt = 0;
            kind = 0;
            tags = Array.Empty<string[]>();

            if (evt.ValueKind != JsonValueKind.Object ||
                !evt.TryGetProperty("id", out var idEl) ||
                !evt.TryGetProperty("pubkey", out var pubEl) ||
                !evt.TryGetProperty("created_at", out var createdEl) ||
                !evt.TryGetProperty("kind", out var kindEl) ||
                !evt.TryGetProperty("tags", out var tagsEl) ||
                !evt.TryGetProperty("content", out var contentEl) ||
                !evt.TryGetProperty("sig", out var sigEl))
                return false;

            id = idEl.GetString() ?? "";
            pubKey = pubEl.GetString() ?? "";
            content = contentEl.GetString() ?? "";
            sig = sigEl.GetString() ?? "";

            if (id.Length != 64 || pubKey.Length != 64 || sig.Length != 128 ||
                !createdEl.TryGetInt64(out createdAt) ||
                !kindEl.TryGetInt32(out kind) ||
                tagsEl.ValueKind != JsonValueKind.Array)
                return false;

            var parsedTags = new List<string[]>();
            foreach (JsonElement tagEl in tagsEl.EnumerateArray())
            {
                if (tagEl.ValueKind != JsonValueKind.Array || tagEl.GetArrayLength() == 0 || tagEl.GetArrayLength() > 8)
                    return false;

                var values = new List<string>();
                foreach (JsonElement valueEl in tagEl.EnumerateArray())
                {
                    if (valueEl.ValueKind != JsonValueKind.String)
                        return false;
                    string value = valueEl.GetString() ?? "";
                    if (value.Length > 256)
                        return false;
                    values.Add(value);
                }
                parsedTags.Add(values.ToArray());
            }

            tags = parsedTags.ToArray();
            return true;
        }

        internal static byte[] SerializeForId(string pubKey, long createdAt, int kind, string[][] tags, string content)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(0);
                writer.WriteStringValue(pubKey);
                writer.WriteNumberValue(createdAt);
                writer.WriteNumberValue(kind);
                writer.WriteStartArray();
                foreach (string[] tag in tags)
                {
                    writer.WriteStartArray();
                    foreach (string value in tag)
                        writer.WriteStringValue(value);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteStringValue(content);
                writer.WriteEndArray();
            }
            return stream.ToArray();
        }

        private static IEnumerable<string> NormalizeRelays(IEnumerable<string>? relays)
        {
            var source = relays ?? DefaultRelays;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string relay in source)
            {
                if (string.IsNullOrWhiteSpace(relay))
                    continue;

                string value = relay.Trim();
                if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                    (uri.Scheme != "wss" && uri.Scheme != "ws"))
                    continue;

                string normalized = uri.ToString().TrimEnd('/');
                if (seen.Add(normalized))
                    yield return normalized;
            }
        }

        public static string CreateChannelTag(byte[]? poolId, string? scope = null)
        {
            string prefixString = string.IsNullOrWhiteSpace(scope)
                ? "QuicPunch-Nostr-Discovery-v1"
                : $"QuicPunch-Nostr-Discovery-{scope.Trim().ToLowerInvariant()}-v1";
            byte[] prefix = Encoding.UTF8.GetBytes(prefixString);
            byte[] seed = poolId is { Length: > 0 }
                ? prefix.Concat(poolId).ToArray()
                : prefix.Concat(Encoding.UTF8.GetBytes("-public")).ToArray();
            return Convert.ToHexString(SHA256.HashData(seed)).ToLowerInvariant();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NostrDiscovery));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
            CryptographicOperations.ZeroMemory(_secretKey);
            _lifecycleLock.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await StopAsync().ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(_secretKey);
            _lifecycleLock.Dispose();
        }

        private sealed class RelayConnection : IDisposable
        {
            private readonly SemaphoreSlim _sendLock = new(1, 1);
            public string Relay { get; }
            public ClientWebSocket Socket { get; }
            public bool IsOpen => Socket.State == WebSocketState.Open;

            public RelayConnection(string relay, ClientWebSocket socket)
            {
                Relay = relay;
                Socket = socket;
            }

            public async Task SendJsonAsync(object payload, CancellationToken token)
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, NostrJson.Options);
                await _sendLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (Socket.State != WebSocketState.Open)
                        return;
                    await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            public void Abort()
            {
                try { Socket.Abort(); } catch { }
            }

            public void Dispose()
            {
                try { Socket.Dispose(); } catch { }
                _sendLock.Dispose();
            }
        }

        private sealed class NostrEvent
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = "";
            [JsonPropertyName("pubkey")]
            public string PubKey { get; set; } = "";
            [JsonPropertyName("created_at")]
            public long CreatedAt { get; set; }
            [JsonPropertyName("kind")]
            public int Kind { get; set; }
            [JsonPropertyName("tags")]
            public string[][] Tags { get; set; } = Array.Empty<string[]>();
            [JsonPropertyName("content")]
            public string Content { get; set; } = "";
            [JsonPropertyName("sig")]
            public string Sig { get; set; } = "";
        }

        private static class NostrJson
        {
            public static readonly JsonSerializerOptions Options = new()
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false
            };
        }
    }
}
