using Microsoft.Data.Sqlite;

namespace QuicNet.WebUi;

internal sealed partial class ChatHistoryStore
{
    private readonly string _connectionString;
    private readonly object _sync = new();
    private IReadOnlyDictionary<string, long>? _activityCache;
    private DateTime _activityValidUntil;

    public ChatHistoryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS messages (
                    peer_id TEXT NOT NULL,
                    message_id TEXT NOT NULL,
                    sender TEXT NOT NULL,
                    content TEXT NOT NULL,
                    timestamp_ms INTEGER NOT NULL,
                    is_me INTEGER NOT NULL,
                    is_confirmed INTEGER NOT NULL,
                    expires_at_ms INTEGER NULL,
                    PRIMARY KEY (peer_id, message_id)
                );
                CREATE INDEX IF NOT EXISTS ix_messages_expiry ON messages(expires_at_ms);
                CREATE INDEX IF NOT EXISTS ix_messages_conversation_time ON messages(peer_id, timestamp_ms DESC);
                CREATE TABLE IF NOT EXISTS conversation_settings (
                    peer_id TEXT PRIMARY KEY,
                    disappear_after_seconds INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS hidden_messages (
                    peer_id TEXT NOT NULL,
                    message_id TEXT NOT NULL,
                    hidden_at_ms INTEGER NOT NULL,
                    PRIMARY KEY (peer_id, message_id)
                );
                """;
            command.ExecuteNonQuery();
            using var columns = db.CreateCommand();
            columns.CommandText = "PRAGMA table_info(messages)";
            bool hasDeliveryState = false;
            using (var reader = columns.ExecuteReader())
                while (reader.Read()) if (reader.GetString(1) == "delivery_state") hasDeliveryState = true;
            if (!hasDeliveryState)
            {
                columns.CommandText = "ALTER TABLE messages ADD COLUMN delivery_state TEXT NOT NULL DEFAULT 'sent'";
                columns.ExecuteNonQuery();
            }
            columns.CommandText = "UPDATE messages SET delivery_state = 'pending' WHERE delivery_state = 'sending' AND is_confirmed = 0";
            columns.ExecuteNonQuery();
            columns.CommandText = "CREATE INDEX IF NOT EXISTS ix_messages_outbox ON messages(delivery_state, timestamp_ms) WHERE is_me = 1 AND is_confirmed = 0";
            columns.ExecuteNonQuery();
            InitializeSocial(db);
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    public IReadOnlyList<WebUiServer.ChatMessage> LoadRecent(int limit)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT peer_id, message_id, sender, content, timestamp_ms, is_me, is_confirmed, expires_at_ms, delivery_state
                FROM messages
                WHERE expires_at_ms IS NULL OR expires_at_ms > $now
                ORDER BY timestamp_ms DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var messages = new List<WebUiServer.ChatMessage>();
            while (reader.Read())
            {
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).LocalDateTime;
                DateTime? expiry = reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)).UtcDateTime;
                messages.Add(new WebUiServer.ChatMessage(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), timestamp, reader.GetInt64(5) != 0, reader.GetInt64(6) != 0, expiry) { DeliveryState = reader.GetString(8) });
            }
            messages.Reverse();
            return messages;
        }
    }

    public sealed record MessagePage(IReadOnlyList<WebUiServer.ChatMessage> Messages, string? NextBefore);

    public IReadOnlyDictionary<string, long> GetConversationActivity()
    {
        lock (_sync)
        {
            if (_activityCache != null && DateTime.UtcNow < _activityValidUntil) return _activityCache;
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT peer_id, MAX(timestamp_ms) FROM messages WHERE expires_at_ms IS NULL OR expires_at_ms > $now GROUP BY peer_id";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            var activity = new Dictionary<string, long>(StringComparer.Ordinal);
            while (reader.Read()) activity[reader.GetString(0)] = reader.GetInt64(1);
            _activityValidUntil = DateTime.UtcNow.AddSeconds(10);
            return _activityCache = activity;
        }
    }

    public MessagePage LoadPage(string peerId, int limit, string? before)
    {
        long beforeTimestamp = 0;
        string beforeId = "";
        if (!string.IsNullOrEmpty(before))
        {
            string[] parts = before.Split(':', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], out beforeTimestamp) || beforeTimestamp < 0)
                throw new InvalidDataException("Invalid message cursor.");
            try { beforeId = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])); }
            catch (FormatException) { throw new InvalidDataException("Invalid message cursor."); }
            if (beforeId.Length is < 1 or > 256) throw new InvalidDataException("Invalid message cursor.");
        }

        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT peer_id, message_id, sender, content, timestamp_ms, is_me, is_confirmed, expires_at_ms, delivery_state
                FROM messages
                WHERE peer_id = $peer
                  AND (expires_at_ms IS NULL OR expires_at_ms > $now)
                  AND ($before_id = '' OR timestamp_ms < $before_time
                       OR (timestamp_ms = $before_time AND message_id < $before_id))
                ORDER BY timestamp_ms DESC, message_id DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$peer", peerId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$before_time", beforeTimestamp);
            command.Parameters.AddWithValue("$before_id", beforeId);
            command.Parameters.AddWithValue("$limit", limit + 1);

            using var reader = command.ExecuteReader();
            var rows = new List<(WebUiServer.ChatMessage Message, long Timestamp)>();
            while (reader.Read())
            {
                long timestampMs = reader.GetInt64(4);
                DateTime timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).LocalDateTime;
                DateTime? expiry = reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)).UtcDateTime;
                rows.Add((new WebUiServer.ChatMessage(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), timestamp, reader.GetInt64(5) != 0, reader.GetInt64(6) != 0, expiry) { DeliveryState = reader.GetString(8) }, timestampMs));
            }

            bool hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            string? nextBefore = hasMore && rows.Count > 0
                ? $"{rows[^1].Timestamp}:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(rows[^1].Message.MsgId))}"
                : null;
            rows.Reverse();
            return new MessagePage(rows.Select(row => row.Message).ToArray(), nextBefore);
        }
    }

    public void Save(WebUiServer.ChatMessage message)
    {
        lock (_sync)
        {
            _activityCache = null;
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO messages(peer_id, message_id, sender, content, timestamp_ms, is_me, is_confirmed, expires_at_ms, delivery_state)
                VALUES($peer, $id, $sender, $content, $timestamp, $me, $confirmed, $expires, $state)
                ON CONFLICT(peer_id, message_id) DO UPDATE SET is_confirmed = excluded.is_confirmed
                """;
            command.Parameters.AddWithValue("$peer", message.PeerId);
            command.Parameters.AddWithValue("$id", message.MsgId);
            command.Parameters.AddWithValue("$sender", message.Sender);
            command.Parameters.AddWithValue("$content", message.Message);
            command.Parameters.AddWithValue("$timestamp", new DateTimeOffset(message.Timestamp).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$me", message.IsMe ? 1 : 0);
            command.Parameters.AddWithValue("$confirmed", message.IsConfirmed ? 1 : 0);
            command.Parameters.AddWithValue("$state", message.DeliveryState);
            command.Parameters.AddWithValue("$expires", message.ExpiresAtUtc.HasValue
                ? new DateTimeOffset(message.ExpiresAtUtc.Value.ToUniversalTime()).ToUnixTimeMilliseconds()
                : DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public void Confirm(string messageId)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE messages SET is_confirmed = 1, delivery_state = 'sent' WHERE message_id = $id";
            command.Parameters.AddWithValue("$id", messageId);
            command.ExecuteNonQuery();
        }
    }

    private static WebUiServer.ChatMessage ReadMessage(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).LocalDateTime,
        reader.GetInt64(5) != 0, reader.GetInt64(6) != 0,
        reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)).UtcDateTime)
        { DeliveryState = reader.GetString(8) };

    public WebUiServer.ChatMessage? GetMessage(string peerId, string messageId)
    {
        lock (_sync)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT peer_id, message_id, sender, content, timestamp_ms, is_me, is_confirmed, expires_at_ms, delivery_state FROM messages WHERE peer_id = $peer AND message_id = $id";
            command.Parameters.AddWithValue("$peer", peerId); command.Parameters.AddWithValue("$id", messageId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMessage(reader) : null;
        }
    }

    public IReadOnlyList<WebUiServer.ChatMessage> LoadPending()
    {
        lock (_sync)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT peer_id, message_id, sender, content, timestamp_ms, is_me, is_confirmed, expires_at_ms, delivery_state FROM messages WHERE is_me = 1 AND is_confirmed = 0 AND delivery_state IN ('pending', 'sending') ORDER BY timestamp_ms, message_id LIMIT 512";
            using var reader = command.ExecuteReader();
            var messages = new List<WebUiServer.ChatMessage>();
            while (reader.Read()) messages.Add(ReadMessage(reader));
            return messages;
        }
    }

    public WebUiServer.ChatMessage? SetDeliveryState(string peerId, string messageId, string state, DateTime? expiresAtUtc = null)
    {
        lock (_sync)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "UPDATE messages SET delivery_state = $state, expires_at_ms = COALESCE(expires_at_ms, $expiry) WHERE peer_id = $peer AND message_id = $id AND is_me = 1 AND (is_confirmed = 0 OR $state = 'sent')";
            command.Parameters.AddWithValue("$peer", peerId); command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$expiry", expiresAtUtc.HasValue ? new DateTimeOffset(expiresAtUtc.Value).ToUnixTimeMilliseconds() : DBNull.Value);
            command.ExecuteNonQuery();
            return GetMessage(peerId, messageId);
        }
    }

    public WebUiServer.ChatMessage? DeleteMessage(string peerId, string messageId)
    {
        lock (_sync)
        {
            var message = GetMessage(peerId, messageId);
            using var db = Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO hidden_messages(peer_id, message_id, hidden_at_ms)
                SELECT peer_id, message_id, $now FROM messages WHERE peer_id = $peer AND message_id = $id;
                DELETE FROM messages WHERE peer_id = $peer AND message_id = $id;
                DELETE FROM message_actions WHERE peer_id = $peer AND message_id = $id;
                """;
            command.Parameters.AddWithValue("$peer", peerId); command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery(); transaction.Commit(); _activityCache = null;
            return message;
        }
    }

    public void DeletePeer(string peerId)
    {
        lock (_sync)
        {
            _activityCache = null;
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO hidden_messages(peer_id, message_id, hidden_at_ms)
                SELECT peer_id, message_id, $now FROM messages WHERE peer_id = $peer;
                DELETE FROM messages WHERE peer_id = $peer;
                DELETE FROM message_actions WHERE peer_id = $peer;
                """;
            command.Parameters.AddWithValue("$peer", peerId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
    }

    public int DeleteExpired(DateTime nowUtc)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO hidden_messages(peer_id, message_id, hidden_at_ms)
                SELECT peer_id, message_id, $now FROM messages
                WHERE expires_at_ms IS NOT NULL AND expires_at_ms <= $now;
                DELETE FROM message_actions WHERE EXISTS (SELECT 1 FROM messages m WHERE m.peer_id = message_actions.peer_id AND m.message_id = message_actions.message_id AND m.expires_at_ms <= $now);
                DELETE FROM messages WHERE expires_at_ms IS NOT NULL AND expires_at_ms <= $now;
                """;
            command.Parameters.AddWithValue("$now", new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds());
            int affected = command.ExecuteNonQuery();
            if (affected > 0) _activityCache = null;
            return affected;
        }
    }

    public bool IsHidden(string peerId, string messageId)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT 1 FROM hidden_messages WHERE peer_id = $peer AND message_id = $id LIMIT 1";
            command.Parameters.AddWithValue("$peer", peerId);
            command.Parameters.AddWithValue("$id", messageId);
            return command.ExecuteScalar() != null;
        }
    }

    public IReadOnlyDictionary<string, int> GetDurations()
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT peer_id, disappear_after_seconds FROM conversation_settings";
            using var reader = command.ExecuteReader();
            var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) values[reader.GetString(0)] = reader.GetInt32(1);
            return values;
        }
    }

    public void SetDuration(string peerId, int seconds)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO conversation_settings(peer_id, disappear_after_seconds) VALUES($peer, $seconds)
                ON CONFLICT(peer_id) DO UPDATE SET disappear_after_seconds = excluded.disappear_after_seconds
                """;
            command.Parameters.AddWithValue("$peer", peerId);
            command.Parameters.AddWithValue("$seconds", seconds);
            command.ExecuteNonQuery();
        }
    }
}
