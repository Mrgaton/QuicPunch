using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Numerics;
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

            string token;
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

                    string initialToken = _tokenProvider();
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

                        string currentToken = _tokenProvider();
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
                if (string.Equals(pubKey, _publicKeyHex, StringComparison.Ordinal))
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

    /// <summary>
    /// Small BIP-340 implementation for ephemeral Nostr discovery identities.
    /// It deliberately does not reuse the QuicPunch certificate identity.
    /// </summary>
    internal static class NostrSchnorr
    {
        private static readonly BigInteger P = BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F", System.Globalization.NumberStyles.HexNumber);
        private static readonly BigInteger N = BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", System.Globalization.NumberStyles.HexNumber);
        private static readonly Point G = new(
            BigInteger.Parse("079BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798", System.Globalization.NumberStyles.HexNumber),
            BigInteger.Parse("0483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8", System.Globalization.NumberStyles.HexNumber));

        public static byte[] CreatePrivateKey()
        {
            while (true)
            {
                byte[] bytes = RandomNumberGenerator.GetBytes(32);
                BigInteger d = FromBytes(bytes);
                if (d > 0 && d < N)
                    return bytes;
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public static byte[] DerivePrivateKey(byte[] masterKey, byte[] info)
        {
            byte[] derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt: null, info: info);
            try
            {
                BigInteger d = Mod(FromBytes(derived), N - 1) + 1;
                return ToBytes32(d);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derived);
            }
        }

        public static byte[] GetPublicKeyX(byte[] privateKey)
        {
            BigInteger d = FromBytes(privateKey);
            ValidateSecret(d);
            Point p = Multiply(G, d);
            return ToBytes32(p.X);
        }

        public static byte[] Sign(ReadOnlySpan<byte> message32, byte[] privateKey)
        {
            if (message32.Length != 32)
                throw new ArgumentException("BIP-340 messages must be exactly 32 bytes.", nameof(message32));

            BigInteger d0 = FromBytes(privateKey);
            ValidateSecret(d0);

            Point p = Multiply(G, d0);
            BigInteger d = IsEven(p.Y) ? d0 : N - d0;
            byte[] px = ToBytes32(p.X);
            byte[] dBytes = ToBytes32(d);
            byte[] aux = RandomNumberGenerator.GetBytes(32);

            try
            {
                byte[] auxHash = TaggedHash("BIP0340/aux", aux);
                byte[] t = new byte[32];
                for (int i = 0; i < 32; i++)
                    t[i] = (byte)(dBytes[i] ^ auxHash[i]);

                byte[] nonceInput = Utilities.Combine(t, px, message32.ToArray());
                BigInteger k0 = Mod(FromBytes(TaggedHash("BIP0340/nonce", nonceInput)), N);
                if (k0.IsZero)
                    throw new CryptographicException("BIP-340 generated an invalid zero nonce.");

                Point rPoint = Multiply(G, k0);
                BigInteger k = IsEven(rPoint.Y) ? k0 : N - k0;
                byte[] rx = ToBytes32(rPoint.X);

                BigInteger e = Mod(FromBytes(TaggedHash("BIP0340/challenge", Utilities.Combine(rx, px, message32.ToArray()))), N);
                BigInteger s = Mod(k + e * d, N);

                byte[] signature = new byte[64];
                rx.CopyTo(signature, 0);
                ToBytes32(s).CopyTo(signature, 32);
                return signature;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dBytes);
                CryptographicOperations.ZeroMemory(aux);
            }
        }

        public static bool Verify(ReadOnlySpan<byte> message32, ReadOnlySpan<byte> publicKeyX32, ReadOnlySpan<byte> signature64)
        {
            if (message32.Length != 32 || publicKeyX32.Length != 32 || signature64.Length != 64)
                return false;

            BigInteger px = FromBytes(publicKeyX32);
            Point? p = LiftX(px);
            if (p == null)
                return false;

            BigInteger r = FromBytes(signature64[..32]);
            BigInteger s = FromBytes(signature64[32..]);
            if (r >= P || s >= N)
                return false;

            BigInteger e = Mod(FromBytes(TaggedHash(
                "BIP0340/challenge",
                Utilities.Combine(signature64[..32].ToArray(), publicKeyX32.ToArray(), message32.ToArray()))), N);

            Point sG = Multiply(G, s);
            Point eP = Multiply(p.Value, Mod(N - e, N));
            Point rPoint = Add(sG, eP);

            return !rPoint.Infinity && IsEven(rPoint.Y) && rPoint.X == r;
        }

        private static Point? LiftX(BigInteger x)
        {
            if (x < 0 || x >= P)
                return null;

            BigInteger c = Mod(BigInteger.ModPow(x, 3, P) + 7, P);
            BigInteger y = BigInteger.ModPow(c, (P + 1) >> 2, P);
            if (Mod(y * y - c, P) != 0)
                return null;
            if (!IsEven(y))
                y = P - y;
            return new Point(x, y);
        }

        private static Point Multiply(Point point, BigInteger scalar)
        {
            scalar = Mod(scalar, N);
            Jacobian result = Jacobian.InfinityPoint;
            Jacobian addend = Jacobian.FromAffine(point);

            int bits = BitLength(scalar);
            for (int i = bits - 1; i >= 0; i--)
            {
                result = Double(result);
                if (!((scalar >> i) & BigInteger.One).IsZero)
                    result = Add(result, addend);
            }

            return result.ToAffine();
        }

        private static Point Add(Point a, Point b)
        {
            if (a.Infinity) return b;
            if (b.Infinity) return a;
            return Add(Jacobian.FromAffine(a), Jacobian.FromAffine(b)).ToAffine();
        }

        private static Jacobian Double(Jacobian p)
        {
            if (p.IsInfinity || p.Y.IsZero)
                return Jacobian.InfinityPoint;

            BigInteger xx = Mod(p.X * p.X, P);
            BigInteger yy = Mod(p.Y * p.Y, P);
            BigInteger yyyy = Mod(yy * yy, P);
            BigInteger s = Mod(2 * (Mod((p.X + yy) * (p.X + yy), P) - xx - yyyy), P);
            BigInteger m = Mod(3 * xx, P);
            BigInteger t = Mod(m * m - 2 * s, P);
            BigInteger x3 = t;
            BigInteger y3 = Mod(m * (s - t) - 8 * yyyy, P);
            BigInteger z3 = Mod(2 * p.Y * p.Z, P);
            return new Jacobian(x3, y3, z3);
        }

        private static Jacobian Add(Jacobian p, Jacobian q)
        {
            if (p.IsInfinity) return q;
            if (q.IsInfinity) return p;

            BigInteger z1z1 = Mod(p.Z * p.Z, P);
            BigInteger z2z2 = Mod(q.Z * q.Z, P);
            BigInteger u1 = Mod(p.X * z2z2, P);
            BigInteger u2 = Mod(q.X * z1z1, P);
            BigInteger s1 = Mod(p.Y * q.Z * z2z2, P);
            BigInteger s2 = Mod(q.Y * p.Z * z1z1, P);

            if (u1 == u2)
                return s1 == s2 ? Double(p) : Jacobian.InfinityPoint;

            BigInteger h = Mod(u2 - u1, P);
            BigInteger i = Mod((2 * h) * (2 * h), P);
            BigInteger j = Mod(h * i, P);
            BigInteger r = Mod(2 * (s2 - s1), P);
            BigInteger v = Mod(u1 * i, P);
            BigInteger x3 = Mod(r * r - j - 2 * v, P);
            BigInteger y3 = Mod(r * (v - x3) - 2 * s1 * j, P);
            BigInteger z3 = Mod((Mod((p.Z + q.Z) * (p.Z + q.Z), P) - z1z1 - z2z2) * h, P);
            return new Jacobian(x3, y3, z3);
        }

        private static byte[] TaggedHash(string tag, byte[] data)
        {
            byte[] tagBytes = Encoding.ASCII.GetBytes(tag);
            byte[] tagHash = SHA256.HashData(tagBytes);
            return SHA256.HashData(Utilities.Combine(tagHash, tagHash, data));
        }

        private static BigInteger FromBytes(ReadOnlySpan<byte> bytes) =>
            new(bytes, isUnsigned: true, isBigEndian: true);

        private static byte[] ToBytes32(BigInteger value)
        {
            byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (raw.Length > 32)
                throw new CryptographicException("Integer does not fit in 32 bytes.");
            byte[] result = new byte[32];
            raw.CopyTo(result, 32 - raw.Length);
            return result;
        }

        private static BigInteger Mod(BigInteger value, BigInteger modulus)
        {
            BigInteger result = value % modulus;
            return result.Sign < 0 ? result + modulus : result;
        }

        private static bool IsEven(BigInteger value) => value.IsEven;

        private static int BitLength(BigInteger value)
        {
            if (value.Sign <= 0)
                return 0;
            byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            int leading = BitOperations.LeadingZeroCount((uint)bytes[0]) - 24;
            return bytes.Length * 8 - leading;
        }

        private static void ValidateSecret(BigInteger d)
        {
            if (d <= 0 || d >= N)
                throw new CryptographicException("Invalid secp256k1 private key.");
        }

        private readonly record struct Point(BigInteger X, BigInteger Y, bool Infinity = false);

        private readonly record struct Jacobian(BigInteger X, BigInteger Y, BigInteger Z)
        {
            public static Jacobian InfinityPoint => new(BigInteger.Zero, BigInteger.One, BigInteger.Zero);
            public bool IsInfinity => Z.IsZero;
            public static Jacobian FromAffine(Point p) =>
                p.Infinity ? InfinityPoint : new Jacobian(p.X, p.Y, BigInteger.One);

            public Point ToAffine()
            {
                if (IsInfinity)
                    return new Point(BigInteger.Zero, BigInteger.Zero, true);

                BigInteger zInv = BigInteger.ModPow(Z, P - 2, P);
                BigInteger zInv2 = Mod(zInv * zInv, P);
                BigInteger x = Mod(X * zInv2, P);
                BigInteger y = Mod(Y * zInv2 * zInv, P);
                return new Point(x, y);
            }
        }
    }
}
