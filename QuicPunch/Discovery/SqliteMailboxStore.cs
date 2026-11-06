using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace QuicPunch;

/// <summary>Durable, indexed mailbox history and outbox backed by a local SQLite database.</summary>
public sealed class SqliteMailboxStore : IMailboxStore
{
    private readonly string _connectionString;
    private readonly byte[]? _encryptionKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteMailboxStore(string databasePath, byte[]? encryptionKey = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        if (encryptionKey is { Length: not 32 }) throw new ArgumentException("Mailbox storage encryption key must be 32 bytes.", nameof(encryptionKey));
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _encryptionKey = encryptionKey is null ? null : (byte[])encryptionKey.Clone();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS mailbox_messages (
                    message_id TEXT PRIMARY KEY,
                    protocol_id TEXT NOT NULL,
                    direction INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    peer_id TEXT NULL,
                    sender_pubkey TEXT NOT NULL,
                    recipient_pubkey TEXT NOT NULL,
                    sender_name TEXT NOT NULL,
                    payload BLOB NOT NULL,
                    payload_protected INTEGER NOT NULL DEFAULT 0,
                    content_type TEXT NOT NULL,
                    created_at INTEGER NOT NULL,
                    stored_at INTEGER NOT NULL,
                    expires_at INTEGER NULL,
                    is_global INTEGER NOT NULL,
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    last_attempt_at INTEGER NULL,
                    last_error TEXT NULL,
                    relay_event_id TEXT NULL,
                    correlation_id TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_mailbox_protocol_created
                    ON mailbox_messages(protocol_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS ix_mailbox_peer_created
                    ON mailbox_messages(peer_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS ix_mailbox_outbox
                    ON mailbox_messages(direction, state, last_attempt_at);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_mailbox_correlation
                    ON mailbox_messages(protocol_id, correlation_id)
                    WHERE correlation_id IS NOT NULL;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            bool hasProtectionColumn = false;
            await using (var schemaCommand = connection.CreateCommand())
            {
                schemaCommand.CommandText = "PRAGMA table_info(mailbox_messages)";
                await using var reader = await schemaCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (string.Equals(reader.GetString(1), "payload_protected", StringComparison.OrdinalIgnoreCase))
                    {
                        hasProtectionColumn = true;
                        break;
                    }
                }
            }
            if (!hasProtectionColumn)
            {
                await using var migration = connection.CreateCommand();
                migration.CommandText = "ALTER TABLE mailbox_messages ADD COLUMN payload_protected INTEGER NOT NULL DEFAULT 0";
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryAddAsync(MailboxStoredMessage message, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO mailbox_messages
            (message_id, protocol_id, direction, state, peer_id, sender_pubkey, recipient_pubkey,
             sender_name, payload, payload_protected, content_type, created_at, stored_at, expires_at, is_global,
             attempt_count, last_attempt_at, last_error, relay_event_id, correlation_id)
            VALUES
            ($id, $protocol, $direction, $state, $peer, $sender, $recipient,
             $name, $payload, $payloadProtected, $contentType, $created, $stored, $expires, $global,
             $attempts, $lastAttempt, $error, $relayEvent, $correlation);
            """;
        BindMessage(command, message);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task UpdateDeliveryAsync(string messageId, MailboxDeliveryState state, string? relayEventId = null, string? error = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mailbox_messages
            SET state = $state,
                relay_event_id = COALESCE($relayEvent, relay_event_id),
                last_error = $error,
                attempt_count = attempt_count + 1,
                last_attempt_at = $attempted
            WHERE message_id = $id;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$relayEvent", (object?)relayEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempted", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", messageId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MailboxStoredMessage>> QueryAsync(MailboxHistoryQuery query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        int limit = Math.Clamp(query.Limit, 1, 1000);
        int offset = Math.Max(0, query.Offset);
        var where = new List<string> { "protocol_id = $protocol" };
        if (query.PeerId.HasValue) where.Add("peer_id = $peer");
        if (query.Direction.HasValue) where.Add("direction = $direction");
        if (query.State.HasValue) where.Add("state = $state");
        if (!query.IncludeGlobal) where.Add("is_global = 0");
        if (!query.IncludeExpired) where.Add("(expires_at IS NULL OR expires_at > $now)");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM mailbox_messages WHERE {string.Join(" AND ", where)} ORDER BY created_at DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$protocol", query.ProtocolId.ToString("D"));
        if (!query.IncludeExpired) command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (query.PeerId.HasValue) command.Parameters.AddWithValue("$peer", query.PeerId.Value.ToString("D"));
        if (query.Direction.HasValue) command.Parameters.AddWithValue("$direction", (int)query.Direction.Value);
        if (query.State.HasValue) command.Parameters.AddWithValue("$state", (int)query.State.Value);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var messages = new List<MailboxStoredMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) messages.Add(ReadMessage(reader));
        return messages;
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mailbox_messages WHERE expires_at IS NOT NULL AND expires_at <= $now";
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void BindMessage(SqliteCommand command, MailboxStoredMessage message)
    {
        command.Parameters.AddWithValue("$id", message.MessageId);
        command.Parameters.AddWithValue("$protocol", message.ProtocolId.ToString("D"));
        command.Parameters.AddWithValue("$direction", (int)message.Direction);
        command.Parameters.AddWithValue("$state", (int)message.State);
        command.Parameters.AddWithValue("$peer", message.PeerId.HasValue ? message.PeerId.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$sender", message.SenderPubKeyHex);
        command.Parameters.AddWithValue("$recipient", message.RecipientPubKeyHex);
        command.Parameters.AddWithValue("$name", message.SenderName);
        command.Parameters.Add("$payload", SqliteType.Blob).Value = ProtectPayload(message.Payload);
        command.Parameters.AddWithValue("$payloadProtected", _encryptionKey is null ? 0 : 1);
        command.Parameters.AddWithValue("$contentType", message.ContentType);
        command.Parameters.AddWithValue("$created", message.CreatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$stored", message.StoredAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expires", message.ExpiresAt.HasValue ? message.ExpiresAt.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("$global", message.IsGlobal ? 1 : 0);
        command.Parameters.AddWithValue("$attempts", message.AttemptCount);
        command.Parameters.AddWithValue("$lastAttempt", message.LastAttemptAt.HasValue ? message.LastAttemptAt.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)message.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$relayEvent", (object?)message.RelayEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("$correlation", (object?)message.CorrelationId ?? DBNull.Value);
    }

    private MailboxStoredMessage ReadMessage(SqliteDataReader reader)
    {
        DateTimeOffset? ReadTime(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal(name)));
        Guid? peerId = reader.IsDBNull(reader.GetOrdinal("peer_id")) ? null : Guid.Parse(reader.GetString(reader.GetOrdinal("peer_id")));
        return new MailboxStoredMessage(
            reader.GetString(reader.GetOrdinal("message_id")),
            Guid.Parse(reader.GetString(reader.GetOrdinal("protocol_id"))),
            (MailboxMessageDirection)reader.GetInt32(reader.GetOrdinal("direction")),
            (MailboxDeliveryState)reader.GetInt32(reader.GetOrdinal("state")),
            peerId,
            reader.GetString(reader.GetOrdinal("sender_pubkey")),
            reader.GetString(reader.GetOrdinal("recipient_pubkey")),
            reader.GetString(reader.GetOrdinal("sender_name")),
            UnprotectPayload((byte[])reader["payload"], reader.GetInt32(reader.GetOrdinal("payload_protected")) != 0),
            reader.GetString(reader.GetOrdinal("content_type")),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_at"))),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("stored_at"))),
            ReadTime("expires_at"),
            reader.GetInt32(reader.GetOrdinal("is_global")) != 0,
            reader.GetInt32(reader.GetOrdinal("attempt_count")),
            ReadTime("last_attempt_at"),
            reader.IsDBNull(reader.GetOrdinal("last_error")) ? null : reader.GetString(reader.GetOrdinal("last_error")),
            reader.IsDBNull(reader.GetOrdinal("relay_event_id")) ? null : reader.GetString(reader.GetOrdinal("relay_event_id")),
            reader.IsDBNull(reader.GetOrdinal("correlation_id")) ? null : reader.GetString(reader.GetOrdinal("correlation_id")));
    }

    private byte[] ProtectPayload(ReadOnlySpan<byte> plaintext)
    {
        if (_encryptionKey is null) return plaintext.ToArray();
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] protectedPayload = new byte[12 + 16 + plaintext.Length];
        nonce.CopyTo(protectedPayload, 0);
        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Encrypt(nonce, plaintext, protectedPayload.AsSpan(28), protectedPayload.AsSpan(12, 16));
        return protectedPayload;
    }

    private byte[] UnprotectPayload(byte[] storedPayload, bool isProtected)
    {
        if (!isProtected) return storedPayload;
        if (_encryptionKey is null) throw new CryptographicException("This mailbox database contains protected payloads, but no storage key was supplied.");
        if (storedPayload.Length < 28) throw new CryptographicException("Invalid protected mailbox payload.");
        byte[] plaintext = new byte[storedPayload.Length - 28];
        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Decrypt(storedPayload.AsSpan(0, 12), storedPayload.AsSpan(28), storedPayload.AsSpan(12, 16), plaintext);
        return plaintext;
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        if (_encryptionKey is not null) CryptographicOperations.ZeroMemory(_encryptionKey);
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
