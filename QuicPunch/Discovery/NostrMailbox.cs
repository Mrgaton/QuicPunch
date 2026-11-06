using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch.Helpers;

namespace QuicPunch
{
    /// <summary>
    /// Asynchronous store-and-forward encrypted mailbox and global public chat using the decentralized Nostr relay network.
    /// Allows sending and receiving end-to-end encrypted messages to/from peers even when they are offline,
    /// as well as participating in a public decentralized global chat channel across all users.
    /// Supports protocol multiplexing so that any IProtocolHandler can send and receive offline messages.
    /// </summary>
    public sealed partial class NostrMailbox : IAsyncDisposable, IDisposable
    {
        private sealed record MailboxGlobalEvent(string id, string pubkey, long created_at, int kind, string[][] tags, string content, string sig);
        private sealed record MailboxSyncState(long lastDmSync, long lastGlobalSync);
        private static readonly JsonSerializerOptions MailboxJsonOptions = new() { TypeInfoResolver = MailboxJsonContext.Default };

        [System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
        [System.Text.Json.Serialization.JsonSerializable(typeof(RumorEvent))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(WireEvent))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(MailboxGlobalEvent))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(MailboxSyncState))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(object[]))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(int[]))]
        [System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
        private partial class MailboxJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
        public const int DirectMessageKind = 4;
        public const int SealKind = 13;
        public const int PrivateMessageKind = 14;
        public const int GiftWrapKind = 1059;
        public const int GlobalMessageKind = 1;
        public const int MaxApplicationPayloadBytes = 14 * 1024;
        public const string GlobalTopic = "quicpunch-global";
        public static readonly Guid DefaultProtocolId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        private const int MaxMessageBytes = 64 * 1024;

        private readonly byte[] _secretKey;
        private readonly string _publicKeyHex;
        private readonly string? _stateFilePath;
        private readonly Func<IWebProxy?>? _proxyProvider;
        private readonly IWebProxy? _proxy;
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly ConcurrentDictionary<string, ClientWebSocket> _connections = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _processedEvents = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _publishAcknowledgements = new(StringComparer.Ordinal);

        private CancellationTokenSource? _runCts;
        private Task[] _relayTasks = Array.Empty<Task>();
        private long _lastSyncTimestamp;
        private long _lastGlobalSyncTimestamp;
        private bool _disposed;

        public event Action<string /* senderPubKeyHex */, string /* senderName */, Guid /* protocolId */, ReadOnlyMemory<byte> /* payload */, string /* message */, DateTimeOffset /* timestamp */, string /* msgId */>? OnMessageReceived;
        public event Action<string /* senderPubKeyHex */, string /* senderName */, Guid /* protocolId */, ReadOnlyMemory<byte> /* payload */, string /* contentType */, string /* message */, DateTimeOffset /* timestamp */, string /* msgId */>? OnTypedMessageReceived;
        public event Action<string /* senderPubKeyHex */, string /* senderName */, Guid /* protocolId */, ReadOnlyMemory<byte> /* payload */, string /* message */, DateTimeOffset /* timestamp */, string /* eventId */>? OnGlobalMessageReceived;

        public string PublicKeyHex => _publicKeyHex;
        public bool IsRunning => _runCts is { IsCancellationRequested: false };
        public int ConnectedRelayCount => _connections.Count;
        public long LastSyncTimestamp => Interlocked.Read(ref _lastSyncTimestamp);
        public long LastGlobalSyncTimestamp => Interlocked.Read(ref _lastGlobalSyncTimestamp);

        public NostrMailbox(CertManager certManager, string? stateFilePath = null, IWebProxy? proxy = null, Func<IWebProxy?>? proxyProvider = null)
            : this(certManager?.NostrPrivateKey ?? throw new ArgumentNullException(nameof(certManager)), stateFilePath, proxy, proxyProvider)
        {
        }

        public NostrMailbox(byte[] nostrPrivateKey, string? stateFilePath = null, IWebProxy? proxy = null, Func<IWebProxy?>? proxyProvider = null)
        {
            if (nostrPrivateKey == null || nostrPrivateKey.Length != 32)
                throw new ArgumentException("Nostr private key must be exactly 32 bytes.", nameof(nostrPrivateKey));

            _secretKey = (byte[])nostrPrivateKey.Clone();
            _publicKeyHex = Convert.ToHexString(NostrSchnorr.GetPublicKeyX(_secretKey)).ToLowerInvariant();
            _stateFilePath = stateFilePath;
            _proxy = proxy;
            _proxyProvider = proxyProvider;

            // Load last sync timestamps from state file if present
            if (!string.IsNullOrWhiteSpace(_stateFilePath) && File.Exists(_stateFilePath))
            {
                try
                {
                    string content = File.ReadAllText(_stateFilePath).Trim();
                    if (content.StartsWith("{") && content.EndsWith("}"))
                    {
                        using var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.TryGetProperty("lastDmSync", out var dmEl))
                            _lastSyncTimestamp = dmEl.GetInt64();
                        if (doc.RootElement.TryGetProperty("lastGlobalSync", out var glEl))
                            _lastGlobalSyncTimestamp = glEl.GetInt64();
                    }
                    else if (long.TryParse(content, out long savedTs))
                    {
                        _lastSyncTimestamp = savedTs;
                    }
                }
                catch { }
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_lastSyncTimestamp == 0)
            {
                // Default to sync private messages from the last 7 days on initial run
                _lastSyncTimestamp = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();
            }

            if (_lastGlobalSyncTimestamp == 0)
            {
                // Default to sync global chat messages from the last 24 hours on initial run
                _lastGlobalSyncTimestamp = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            }
        }

        public async Task StartAsync(IEnumerable<string>? relays = null, CancellationToken cancellationToken = default)
        {
            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(NostrMailbox));
                if (IsRunning) return;

                string[] relayList = (relays ?? NostrDiscovery.DefaultRelays).ToArray();
                if (relayList.Length == 0) relayList = NostrDiscovery.DefaultRelays;

                _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CancellationToken token = _runCts.Token;

                _relayTasks = relayList.Select(relay => Task.Run(() => RelayWorkerLoopAsync(relay, token), token)).ToArray();
                QuicPunchLog.Info($"[NOSTR MAILBOX] Started mailbox for {_publicKeyHex[..8]}... across {relayList.Length} relays.");
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
                if (_runCts == null) return;

                try { _runCts.Cancel(); } catch { }

                foreach (var ws in _connections.Values)
                {
                    try { ws.Abort(); ws.Dispose(); } catch { }
                }
                _connections.Clear();

                Task[] tasks = _relayTasks;
                _relayTasks = Array.Empty<Task>();
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
                foreach (SemaphoreSlim sendLock in _sendLocks.Values) sendLock.Dispose();
                _sendLocks.Clear();

                _runCts.Dispose();
                _runCts = null;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        #region Send Encrypted Direct Message (Offline DM)

        /// <summary>
        /// Encrypts and publishes an authenticated direct message to Nostr relays. The recipient will receive
        /// and decrypt it automatically when they connect, even if they are currently offline.
        /// </summary>
        public async Task<string> SendMessageAsync(
            string recipientPubKeyHex,
            ReadOnlyMemory<byte> payload,
            Guid? protocolId = null,
            string? senderName = null,
            string? msgId = null,
            string? contentType = null,
            DateTimeOffset? expiresAt = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(recipientPubKeyHex) || recipientPubKeyHex.Length != 64 || !recipientPubKeyHex.All(Uri.IsHexDigit))
                throw new ArgumentException("Recipient public key must be a 64-character hex string.", nameof(recipientPubKeyHex));
            if (payload.Length == 0)
                throw new ArgumentException("Payload cannot be empty.", nameof(payload));
            if (payload.Length > MaxApplicationPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(payload), $"A mailbox payload cannot exceed {MaxApplicationPayloadBytes} bytes. Split larger data at the application protocol layer.");

            byte[] recipientPubKey = Convert.FromHexString(recipientPubKeyHex);

            Guid proto = protocolId ?? DefaultProtocolId;
            string id = msgId ?? Guid.NewGuid().ToString();

            // Keep a single binary representation; duplicating UTF-8 text would needlessly inflate
            // the nested NIP-44/NIP-59 event and can exceed relay event-size policies.
            var envelopeObj = new Dictionary<string, object>
            {
                ["v"] = 1,
                ["type"] = "dm",
                ["proto"] = proto.ToString(),
                ["sender"] = senderName ?? "",
                ["msgId"] = id,
                ["contentType"] = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                ["bin"] = Convert.ToBase64String(payload.Span),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            string envelopeJson = JsonSerializer.Serialize(envelopeObj, MailboxJsonOptions);
            WireEvent giftWrap = CreateGiftWrap(recipientPubKeyHex.ToLowerInvariant(), recipientPubKey, envelopeJson, expiresAt);
            string jsonMessage = JsonSerializer.Serialize(new object[] { "EVENT", giftWrap }, MailboxJsonOptions);
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(jsonMessage);

            var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_publishAcknowledgements.TryAdd(giftWrap.id, accepted))
                throw new InvalidOperationException($"A publish operation for event {giftWrap.id} is already pending.");

            int sentCount = 0;
            try
            {
                foreach (var kvp in _connections)
                {
                    if (kvp.Value.State == WebSocketState.Open)
                    {
                        try
                        {
                            await SendToRelayAsync(kvp.Key, kvp.Value, utf8Bytes, cancellationToken).ConfigureAwait(false);
                            sentCount++;
                        }
                        catch (Exception ex)
                        {
                            QuicPunchLog.Info($"[NOSTR MAILBOX] Failed to send message to relay {kvp.Key}: {ex.Message}");
                        }
                    }
                }

                if (sentCount == 0)
                    throw new InvalidOperationException("No Nostr mailbox relay is connected; the message must remain queued for retry.");

                bool relayAccepted;
                try
                {
                    relayAccepted = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new InvalidOperationException("The event was sent, but no Nostr relay confirmed that it stored it.", ex);
                }

                if (!relayAccepted)
                    throw new InvalidOperationException("Nostr relays rejected the mailbox event.");
            }
            finally
            {
                _publishAcknowledgements.TryRemove(giftWrap.id, out _);
            }

            QuicPunchLog.Info($"[NOSTR MAILBOX] Sent NIP-17 gift wrap to {recipientPubKeyHex[..8]}... (Protocol: {proto}, Event ID: {giftWrap.id[..8]}..., broadcasted to {sentCount} relays).");
            return giftWrap.id;
        }

        private WireEvent CreateGiftWrap(string recipientPubKeyHex, byte[] recipientPubKey, string envelopeJson, DateTimeOffset? expiresAt = null)
        {
            long canonicalTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string[][] rumorTags = { new[] { "p", recipientPubKeyHex } };
            var rumor = new RumorEvent
            {
                pubkey = _publicKeyHex,
                created_at = canonicalTimestamp,
                kind = PrivateMessageKind,
                tags = rumorTags,
                content = envelopeJson
            };
            rumor.id = ComputeEventId(rumor.pubkey, rumor.created_at, rumor.kind, rumor.tags, rumor.content);

            string encryptedRumor = Nip44.Encrypt(_secretKey, recipientPubKey, JsonSerializer.Serialize(rumor, MailboxJsonOptions));
            WireEvent seal = CreateSignedEvent(_secretKey, SealKind, Array.Empty<string[]>(), encryptedRumor, RandomPastTimestamp());

            byte[] ephemeralKey = NostrSchnorr.CreatePrivateKey();
            try
            {
                string encryptedSeal = Nip44.Encrypt(ephemeralKey, recipientPubKey, JsonSerializer.Serialize(seal, MailboxJsonOptions));
                string[][] wrapTags = expiresAt.HasValue
                    ? new[] { new[] { "p", recipientPubKeyHex }, new[] { "expiration", expiresAt.Value.ToUnixTimeSeconds().ToString() } }
                    : new[] { new[] { "p", recipientPubKeyHex } };
                return CreateSignedEvent(ephemeralKey, GiftWrapKind, wrapTags, encryptedSeal, RandomPastTimestamp());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ephemeralKey);
            }
        }

        private static WireEvent CreateSignedEvent(byte[] privateKey, int kind, string[][] tags, string content, long createdAt)
        {
            string publicKey = Convert.ToHexString(NostrSchnorr.GetPublicKeyX(privateKey)).ToLowerInvariant();
            string id = ComputeEventId(publicKey, createdAt, kind, tags, content);
            byte[] idBytes = Convert.FromHexString(id);
            return new WireEvent
            {
                id = id,
                pubkey = publicKey,
                created_at = createdAt,
                kind = kind,
                tags = tags,
                content = content,
                sig = Convert.ToHexString(NostrSchnorr.Sign(idBytes, privateKey)).ToLowerInvariant()
            };
        }

        private static string ComputeEventId(string pubkey, long createdAt, int kind, string[][] tags, string content) =>
            Convert.ToHexString(SHA256.HashData(NostrDiscovery.SerializeForId(pubkey, createdAt, kind, tags, content))).ToLowerInvariant();

        private static long RandomPastTimestamp()
        {
            int seconds = RandomNumberGenerator.GetInt32(0, 2 * 24 * 60 * 60 + 1);
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds;
        }

        private class RumorEvent
        {
            public string id { get; set; } = "";
            public string pubkey { get; set; } = "";
            public long created_at { get; set; }
            public int kind { get; set; }
            public string[][] tags { get; set; } = Array.Empty<string[]>();
            public string content { get; set; } = "";
        }

        private sealed class WireEvent : RumorEvent
        {
            public string sig { get; set; } = "";
        }

        public Task<string> SendMessageAsync(
            string recipientPubKeyHex,
            string message,
            Guid? protocolId = null,
            string? senderName = null,
            string? msgId = null,
            string? contentType = "text/plain; charset=utf-8",
            DateTimeOffset? expiresAt = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(message))
                throw new ArgumentException("Message cannot be empty.", nameof(message));

            return SendMessageAsync(recipientPubKeyHex, Encoding.UTF8.GetBytes(message), protocolId, senderName, msgId, contentType, expiresAt, cancellationToken);
        }

        public Task<string> SendMessageAsync(
            string recipientPubKeyHex,
            ReadOnlyMemory<byte> payload,
            Guid? protocolId,
            string? senderName,
            string? msgId,
            CancellationToken cancellationToken) =>
            SendMessageAsync(recipientPubKeyHex, payload, protocolId, senderName, msgId, null, null, cancellationToken);

        public Task<string> SendMessageAsync(
            string recipientPubKeyHex,
            string message,
            Guid? protocolId,
            string? senderName,
            string? msgId,
            CancellationToken cancellationToken) =>
            SendMessageAsync(recipientPubKeyHex, message, protocolId, senderName, msgId, "text/plain; charset=utf-8", null, cancellationToken);

        #endregion

        #region Send Global Chat Message

        /// <summary>
        /// Publishes a public, signed message to the decentralized QuicPunch global channel across Nostr relays.
        /// </summary>
        public async Task<string> SendGlobalMessageAsync(
            string senderName,
            ReadOnlyMemory<byte> payload,
            Guid? protocolId = null,
            CancellationToken cancellationToken = default)
        {
            if (payload.Length == 0)
                throw new ArgumentException("Payload cannot be empty.", nameof(payload));

            long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string safeName = string.IsNullOrWhiteSpace(senderName) ? _publicKeyHex[..8] : senderName;
            Guid proto = protocolId ?? DefaultProtocolId;

            string text = "";
            try { text = Encoding.UTF8.GetString(payload.Span); } catch { }
            string contentToSend = !string.IsNullOrEmpty(text) ? text : Convert.ToBase64String(payload.Span);

            string[][] tags =
            {
                new[] { "t", GlobalTopic },
                new[] { "name", safeName },
                new[] { "client", "QuicPunch" },
                new[] { "proto", proto.ToString() }
            };

            byte[] serialized = NostrDiscovery.SerializeForId(_publicKeyHex, createdAt, GlobalMessageKind, tags, contentToSend);
            byte[] idBytes = SHA256.HashData(serialized);
            string id = Convert.ToHexString(idBytes).ToLowerInvariant();
            string sig = Convert.ToHexString(NostrSchnorr.Sign(idBytes, _secretKey)).ToLowerInvariant();

            var evt = new MailboxGlobalEvent(id, _publicKeyHex, createdAt, GlobalMessageKind, tags, contentToSend, sig);

            string jsonMessage = JsonSerializer.Serialize(new object[] { "EVENT", evt }, MailboxJsonOptions);
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(jsonMessage);

            int sentCount = 0;
            foreach (var kvp in _connections)
            {
                if (kvp.Value.State == WebSocketState.Open)
                {
                    try
                    {
                        await SendToRelayAsync(kvp.Key, kvp.Value, utf8Bytes, cancellationToken).ConfigureAwait(false);
                        sentCount++;
                    }
                    catch (Exception ex)
                    {
                        QuicPunchLog.Info($"[NOSTR MAILBOX] Failed to send global message to relay {kvp.Key}: {ex.Message}");
                    }
                }
            }

            QuicPunchLog.Info($"[NOSTR MAILBOX] Sent global message from {safeName} (Proto: {proto}, Event ID: {id[..8]}..., broadcasted to {sentCount} relays).");
            return id;
        }

        public Task<string> SendGlobalMessageAsync(
            string senderName,
            string message,
            Guid? protocolId = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("Message cannot be empty.", nameof(message));

            return SendGlobalMessageAsync(senderName, Encoding.UTF8.GetBytes(message), protocolId, cancellationToken);
        }

        #endregion

        #region Encryption & Decryption (NIP-44 v2)

        internal string EncryptMessage(byte[] recipientPubKey, string plaintext)
        {
            return Nip44.Encrypt(_secretKey, recipientPubKey, plaintext);
        }

        internal bool TryDecryptMessage(byte[] senderPubKey, string encryptedPayloadBase64, out string? plaintext)
        {
            plaintext = null;
            try
            {
                plaintext = Nip44.Decrypt(_secretKey, senderPubKey, encryptedPayloadBase64);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Relay Connection & Message Processing

        private async Task RelayWorkerLoopAsync(string relay, CancellationToken token)
        {
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                ClientWebSocket? ws = null;
                try
                {
                    ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    var p = _proxyProvider?.Invoke() ?? _proxy;
                    if (p != null) ws.Options.Proxy = p;

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));

                    await ws.ConnectAsync(new Uri(relay), connectCts.Token).ConfigureAwait(false);
                    failures = 0;

                    // Subscribe to incoming direct messages addressed to our public key
                    await SubscribeToDirectMessagesAsync(ws, token).ConfigureAwait(false);

                    // Subscribe to public global chat channel
                    await SubscribeToGlobalChannelAsync(ws, token).ConfigureAwait(false);
                    _connections[relay] = ws;

                    // Receive loop
                    byte[] buffer = new byte[8192];
                    using var ms = new MemoryStream();

                    while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        ms.SetLength(0);
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            if (result.MessageType == WebSocketMessageType.Text)
                            {
                                if (ms.Length + result.Count > MaxMessageBytes)
                                    throw new InvalidDataException("Relay message exceeded max size.");
                                ms.Write(buffer, 0, result.Count);
                            }
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (ms.Length > 0)
                        {
                            ProcessRelayJson(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        QuicPunchLog.Info($"[NOSTR MAILBOX] Relay connection issue ({relay}): {ex.Message}");
                }
                finally
                {
                    if (ws != null)
                    {
                        _connections.TryRemove(relay, out _);
                        try { ws.Abort(); ws.Dispose(); } catch { }
                    }
                }

                if (token.IsCancellationRequested) break;
                failures = Math.Min(failures + 1, 5);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << failures)), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task SendToRelayAsync(string relay, ClientWebSocket socket, byte[] payload, CancellationToken cancellationToken)
        {
            SemaphoreSlim sendLock = _sendLocks.GetOrAdd(relay, static _ => new SemaphoreSlim(1, 1));
            await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private async Task SubscribeToDirectMessagesAsync(ClientWebSocket ws, CancellationToken token)
        {
            string subId = "mb-dm-" + Guid.NewGuid().ToString("N")[..8];
            // NIP-59 deliberately backdates gift wraps by up to two days for metadata privacy.
            // Always overlap that window; persistent message-id deduplication makes replay safe.
            long privacyWindowStart = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
            long since = Math.Min(LastSyncTimestamp, privacyWindowStart);

            object[] request =
            {
                "REQ",
                subId,
                new Dictionary<string, object>
                {
                    // NIP-17/NIP-59 persistent gift wraps plus the legacy kind-4
                    // reader for rolling upgrades from older QuicPunch versions.
                    ["kinds"] = new[] { GiftWrapKind, DirectMessageKind },
                    ["#p"] = new[] { _publicKeyHex },
                    ["since"] = since,
                    ["limit"] = 250
                }
            };

            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(request, MailboxJsonOptions);
            await ws.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }

        private async Task SubscribeToGlobalChannelAsync(ClientWebSocket ws, CancellationToken token)
        {
            string subId = "mb-gl-" + Guid.NewGuid().ToString("N")[..8];
            // Re-read a short overlap after reconnect. SQLite message IDs deduplicate replayed events.
            long since = Math.Max(0, LastGlobalSyncTimestamp - 7200);

            object[] request =
            {
                "REQ",
                subId,
                new Dictionary<string, object>
                {
                    ["kinds"] = new[] { GlobalMessageKind },
                    ["#t"] = new[] { GlobalTopic },
                    ["since"] = since,
                    ["limit"] = 100
                }
            };

            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(request, MailboxJsonOptions);
            await ws.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }

        internal void ProcessRelayJson(ReadOnlyMemory<byte> utf8)
        {
            try
            {
                using var doc = JsonDocument.Parse(utf8);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3)
                    return;

                string? verb = root[0].GetString();
                if (string.Equals(verb, "OK", StringComparison.Ordinal) && root.GetArrayLength() >= 4)
                {
                    string? acknowledgedEventId = root[1].GetString();
                    bool relayAccepted = root[2].ValueKind == JsonValueKind.True;
                    if (relayAccepted && !string.IsNullOrWhiteSpace(acknowledgedEventId) &&
                        _publishAcknowledgements.TryGetValue(acknowledgedEventId, out var acknowledgement))
                    {
                        acknowledgement.TrySetResult(true);
                    }
                    return;
                }

                if (!string.Equals(verb, "EVENT", StringComparison.Ordinal))
                    return;

                JsonElement evt = root[2];
                string? id = evt.GetProperty("id").GetString();
                string? senderPubKey = evt.GetProperty("pubkey").GetString();
                long createdAt = evt.GetProperty("created_at").GetInt64();
                int kind = evt.GetProperty("kind").GetInt32();
                string? content = evt.GetProperty("content").GetString();
                string? sig = evt.GetProperty("sig").GetString();

                if (string.IsNullOrEmpty(id) || id.Length != 64 || string.IsNullOrEmpty(senderPubKey) || senderPubKey.Length != 64 ||
                    string.IsNullOrEmpty(content) || string.IsNullOrEmpty(sig) || sig.Length != 128)
                    return;

                string[][] eventTags = ParseTags(evt.GetProperty("tags"));
                if (!string.Equals(ComputeEventId(senderPubKey, createdAt, kind, eventTags, content), id, StringComparison.OrdinalIgnoreCase))
                    return;

                string? expiration = eventTags.FirstOrDefault(tag => tag.Length >= 2 && string.Equals(tag[0], "expiration", StringComparison.Ordinal))?.ElementAtOrDefault(1);
                if (long.TryParse(expiration, out long expiresAt) && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    return;

                // Ignore own echoes
                if (string.Equals(senderPubKey, _publicKeyHex, StringComparison.OrdinalIgnoreCase)) return;

                // Verify Schnorr Signature
                byte[] idBytes = Convert.FromHexString(id);
                byte[] senderPubKeyBytes = Convert.FromHexString(senderPubKey);
                byte[] sigBytes = Convert.FromHexString(sig);

                if (!NostrSchnorr.Verify(idBytes, senderPubKeyBytes, sigBytes))
                    return;

                // Only remember an event after its signature has been validated;
                // otherwise an invalid copy could poison deduplication for a valid event.
                if (!_processedEvents.TryAdd(id, createdAt))
                    return;
                if (_processedEvents.Count > 10_000)
                {
                    long cutoff = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();
                    foreach (var processed in _processedEvents)
                    {
                        if (processed.Value < cutoff) _processedEvents.TryRemove(processed.Key, out _);
                    }
                }

                // 1. NIP-17 private message in a NIP-59 persistent gift wrap.
                if (kind == GiftWrapKind)
                {
                    if (!HasEventTag(evt, "p", _publicKeyHex)) return;
                    ProcessGiftWrap(senderPubKeyBytes, content, id);
                }
                // 2. Legacy direct message (kind 4), retained for rolling upgrades.
                else if (kind == DirectMessageKind)
                {
                    if (TryDecryptMessage(senderPubKeyBytes, content, out string? decrypted) && !string.IsNullOrEmpty(decrypted))
                    {
                        Guid resolvedProtoId = DefaultProtocolId;
                        string actualMessage = decrypted;
                        ReadOnlyMemory<byte> resolvedPayload = Encoding.UTF8.GetBytes(decrypted);
                        string resolvedSenderName = senderPubKey[..8];
                        string resolvedMsgId = id;
                        string resolvedContentType = "application/octet-stream";

                        try
                        {
                            using var dmDoc = JsonDocument.Parse(decrypted);
                            var dmRoot = dmDoc.RootElement;
                            if (dmRoot.ValueKind == JsonValueKind.Object)
                            {
                                if (dmRoot.TryGetProperty("proto", out var pEl) && Guid.TryParse(pEl.GetString(), out var pGuid))
                                    resolvedProtoId = pGuid;
                                if (dmRoot.TryGetProperty("sender", out var sEl) && !string.IsNullOrWhiteSpace(sEl.GetString()))
                                    resolvedSenderName = sEl.GetString()!;
                                if (dmRoot.TryGetProperty("msgId", out var mEl) && !string.IsNullOrWhiteSpace(mEl.GetString()))
                                    resolvedMsgId = mEl.GetString()!;
                                if (dmRoot.TryGetProperty("contentType", out var ctEl) && !string.IsNullOrWhiteSpace(ctEl.GetString()))
                                    resolvedContentType = ctEl.GetString()!;
                                if (dmRoot.TryGetProperty("bin", out var bEl) && !string.IsNullOrWhiteSpace(bEl.GetString()))
                                {
                                    resolvedPayload = Convert.FromBase64String(bEl.GetString()!);
                                    try { actualMessage = Encoding.UTF8.GetString(resolvedPayload.Span); } catch { }
                                }
                                else if (dmRoot.TryGetProperty("content", out var cEl))
                                {
                                    actualMessage = cEl.GetString() ?? actualMessage;
                                    resolvedPayload = Encoding.UTF8.GetBytes(actualMessage);
                                }
                            }
                        }
                        catch
                        {
                            // Plain text legacy fallback
                        }

                        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(createdAt);
                        if (createdAt > _lastSyncTimestamp)
                        {
                            Interlocked.Exchange(ref _lastSyncTimestamp, createdAt);
                            PersistStateAsync();
                        }

                        QuicPunchLog.Info($"[NOSTR MAILBOX] Decrypted incoming message from {resolvedSenderName} ({senderPubKey[..8]}...) (Proto: {resolvedProtoId})");
                        OnTypedMessageReceived?.Invoke(senderPubKey, resolvedSenderName, resolvedProtoId, resolvedPayload, resolvedContentType, actualMessage, timestamp, resolvedMsgId);
                        OnMessageReceived?.Invoke(senderPubKey, resolvedSenderName, resolvedProtoId, resolvedPayload, actualMessage, timestamp, resolvedMsgId);
                    }
                }
                // 3. GLOBAL CHAT MESSAGE (Kind 1 with topic tag)
                else if (kind == GlobalMessageKind)
                {
                    string? senderName = null;
                    bool isGlobalTopic = false;
                    Guid resolvedProtoId = DefaultProtocolId;

                    if (evt.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tagItem in tagsEl.EnumerateArray())
                        {
                            if (tagItem.ValueKind == JsonValueKind.Array && tagItem.GetArrayLength() >= 2)
                            {
                                string? tagKey = tagItem[0].GetString();
                                string? tagVal = tagItem[1].GetString();

                                if (string.Equals(tagKey, "t", StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(tagVal, GlobalTopic, StringComparison.OrdinalIgnoreCase))
                                {
                                    isGlobalTopic = true;
                                }
                                else if (string.Equals(tagKey, "name", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(tagVal))
                                {
                                    senderName = tagVal;
                                }
                                else if (string.Equals(tagKey, "proto", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(tagVal, out var g))
                                {
                                    resolvedProtoId = g;
                                }
                            }
                        }
                    }

                    if (isGlobalTopic)
                    {
                        senderName ??= senderPubKey[..8];
                        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(createdAt);
                        if (createdAt > _lastGlobalSyncTimestamp)
                        {
                            Interlocked.Exchange(ref _lastGlobalSyncTimestamp, createdAt);
                            PersistStateAsync();
                        }

                        ReadOnlyMemory<byte> rawPayload = Encoding.UTF8.GetBytes(content);
                        QuicPunchLog.Info($"[NOSTR GLOBAL] Message from {senderName} (Proto: {resolvedProtoId}): {content}");
                        OnGlobalMessageReceived?.Invoke(senderPubKey, senderName, resolvedProtoId, rawPayload, content, timestamp, id);
                    }
                }
            }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[NOSTR MAILBOX] Failed to parse event: {ex.Message}");
            }
        }

        private void ProcessGiftWrap(byte[] wrapperPubKey, string encryptedSeal, string wrapperEventId)
        {
            try
            {
                string sealJson = Nip44.Decrypt(_secretKey, wrapperPubKey, encryptedSeal);
                using var sealDoc = JsonDocument.Parse(sealJson);
                JsonElement seal = sealDoc.RootElement;
                if (!TryValidateSignedEvent(seal, SealKind, requireEmptyTags: true, out string sealAuthor, out string sealContent, out _))
                    return;

                byte[] sealAuthorBytes = Convert.FromHexString(sealAuthor);
                string rumorJson = Nip44.Decrypt(_secretKey, sealAuthorBytes, sealContent);
                using var rumorDoc = JsonDocument.Parse(rumorJson);
                JsonElement rumor = rumorDoc.RootElement;

                string? rumorId = rumor.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                string? rumorAuthor = rumor.TryGetProperty("pubkey", out var pubEl) ? pubEl.GetString() : null;
                string? rumorContent = rumor.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(rumorId) || string.IsNullOrWhiteSpace(rumorAuthor) || string.IsNullOrEmpty(rumorContent) ||
                    !string.Equals(rumorAuthor, sealAuthor, StringComparison.OrdinalIgnoreCase) ||
                    !rumor.TryGetProperty("kind", out var kindEl) || kindEl.GetInt32() != PrivateMessageKind ||
                    !rumor.TryGetProperty("created_at", out var timeEl) ||
                    !rumor.TryGetProperty("tags", out var tagsEl) || !HasTag(tagsEl, "p", _publicKeyHex))
                    return;

                string[][] tags = ParseTags(tagsEl);
                string calculatedRumorId = ComputeEventId(rumorAuthor, timeEl.GetInt64(), PrivateMessageKind, tags, rumorContent);
                if (!string.Equals(calculatedRumorId, rumorId, StringComparison.OrdinalIgnoreCase))
                    return;

                ProcessDirectEnvelope(sealAuthor, rumorContent, timeEl.GetInt64(), wrapperEventId);
            }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[NOSTR MAILBOX] Rejected invalid NIP-17 gift wrap: {ex.Message}");
            }
        }

        private bool TryValidateSignedEvent(JsonElement evt, int expectedKind, bool requireEmptyTags, out string author, out string content, out string id)
        {
            author = content = id = "";
            try
            {
                id = evt.GetProperty("id").GetString() ?? "";
                author = evt.GetProperty("pubkey").GetString() ?? "";
                content = evt.GetProperty("content").GetString() ?? "";
                string sig = evt.GetProperty("sig").GetString() ?? "";
                long createdAt = evt.GetProperty("created_at").GetInt64();
                int kind = evt.GetProperty("kind").GetInt32();
                string[][] tags = ParseTags(evt.GetProperty("tags"));
                if (kind != expectedKind || (requireEmptyTags && tags.Length != 0) || author.Length != 64 || id.Length != 64 || sig.Length != 128)
                    return false;
                if (!string.Equals(ComputeEventId(author, createdAt, kind, tags, content), id, StringComparison.OrdinalIgnoreCase))
                    return false;
                return NostrSchnorr.Verify(Convert.FromHexString(id), Convert.FromHexString(author), Convert.FromHexString(sig));
            }
            catch
            {
                return false;
            }
        }

        private void ProcessDirectEnvelope(string senderPubKey, string decrypted, long createdAt, string fallbackMessageId)
        {
            Guid protocolId = DefaultProtocolId;
            ReadOnlyMemory<byte> payload = Encoding.UTF8.GetBytes(decrypted);
            string message = decrypted;
            string senderName = senderPubKey[..8];
            string messageId = fallbackMessageId;
            string contentType = "application/octet-stream";

            using var dmDoc = JsonDocument.Parse(decrypted);
            JsonElement root = dmDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("proto", out var pEl) && Guid.TryParse(pEl.GetString(), out var parsedProtocol)) protocolId = parsedProtocol;
            if (root.TryGetProperty("sender", out var sEl) && !string.IsNullOrWhiteSpace(sEl.GetString())) senderName = sEl.GetString()!;
            if (root.TryGetProperty("msgId", out var mEl) && !string.IsNullOrWhiteSpace(mEl.GetString())) messageId = mEl.GetString()!;
            if (root.TryGetProperty("contentType", out var contentTypeElement) && !string.IsNullOrWhiteSpace(contentTypeElement.GetString())) contentType = contentTypeElement.GetString()!;
            if (root.TryGetProperty("bin", out var bEl) && !string.IsNullOrWhiteSpace(bEl.GetString()))
            {
                payload = Convert.FromBase64String(bEl.GetString()!);
                try { message = Encoding.UTF8.GetString(payload.Span); } catch { }
            }
            else if (root.TryGetProperty("content", out var cEl))
            {
                message = cEl.GetString() ?? message;
                payload = Encoding.UTF8.GetBytes(message);
            }

            if (createdAt > _lastSyncTimestamp)
            {
                Interlocked.Exchange(ref _lastSyncTimestamp, createdAt);
                PersistStateAsync();
            }
            DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(createdAt);
            OnTypedMessageReceived?.Invoke(senderPubKey, senderName, protocolId, payload, contentType, message, timestamp, messageId);
            OnMessageReceived?.Invoke(senderPubKey, senderName, protocolId, payload, message, timestamp, messageId);
        }

        private static bool HasEventTag(JsonElement evt, string name, string value) =>
            evt.TryGetProperty("tags", out var tags) && HasTag(tags, name, value);

        private static bool HasTag(JsonElement tags, string name, string value)
        {
            if (tags.ValueKind != JsonValueKind.Array) return false;
            foreach (JsonElement tag in tags.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.Array && tag.GetArrayLength() >= 2 &&
                    string.Equals(tag[0].GetString(), name, StringComparison.Ordinal) &&
                    string.Equals(tag[1].GetString(), value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string[][] ParseTags(JsonElement tags)
        {
            if (tags.ValueKind != JsonValueKind.Array) return Array.Empty<string[]>();
            return tags.EnumerateArray()
                .Where(tag => tag.ValueKind == JsonValueKind.Array)
                .Select(tag => tag.EnumerateArray().Select(value => value.GetString() ?? "").ToArray())
                .ToArray();
        }

        private void PersistStateAsync()
        {
            if (string.IsNullOrWhiteSpace(_stateFilePath)) return;
            _ = Task.Run(() =>
            {
                try
                {
                    string? dir = Path.GetDirectoryName(_stateFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var state = new MailboxSyncState(LastSyncTimestamp, LastGlobalSyncTimestamp);
                    File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, MailboxJsonOptions));
                }
                catch { }
            });
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopAsync().GetAwaiter().GetResult();
            _lifecycleLock.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await StopAsync().ConfigureAwait(false);
            _lifecycleLock.Dispose();
        }

        #endregion
    }
}
