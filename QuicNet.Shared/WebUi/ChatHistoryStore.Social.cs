using Microsoft.Data.Sqlite;
using QuicNet.Serialization;

namespace QuicNet.WebUi;

internal sealed partial class ChatHistoryStore
{
    internal static readonly string[] ReactionEmoji = ["👍", "❤️", "😂", "😮", "😢", "🎉"];

    private static void InitializeSocial(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS message_actions (
                peer_id TEXT NOT NULL, message_id TEXT NOT NULL, mine INTEGER NOT NULL,
                kind TEXT NOT NULL, action_key TEXT NOT NULL, active INTEGER NOT NULL, revision INTEGER NOT NULL,
                PRIMARY KEY(peer_id, message_id, mine, kind, action_key)
            );
            """;
        command.ExecuteNonQuery();
    }

    internal static bool IsValidAction(ChatActionUpdate update) => update.msgId is { Length: > 0 and <= 256 } &&
        update.revision > 0 && update.revision <= DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds() &&
        (update.kind == "pin" && update.key == "" || update.kind == "reaction" && ReactionEmoji.Contains(update.key));

    public ChatActionState[] GetActions(string peerId)
    {
        lock (_sync)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = """
                SELECT a.message_id, a.kind, a.action_key, a.active, a.revision, a.mine
                FROM message_actions a JOIN messages m ON m.peer_id = a.peer_id AND m.message_id = a.message_id
                WHERE a.peer_id = $peer AND m.delivery_state = 'sent' AND (m.expires_at_ms IS NULL OR m.expires_at_ms > $now)
                ORDER BY a.revision DESC LIMIT 8192
                """;
            command.Parameters.AddWithValue("$peer", peerId); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader(); var states = new List<ChatActionState>();
            while (reader.Read()) states.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(4), reader.GetBoolean(5)));
            return states.ToArray();
        }
    }

    public ChatActionUpdate SetAction(string peerId, string messageId, string kind, string key, bool active)
    {
        lock (_sync)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.Parameters.AddWithValue("$peer", peerId); command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.CommandText = "SELECT 1 FROM messages WHERE peer_id = $peer AND message_id = $id AND delivery_state = 'sent' AND (expires_at_ms IS NULL OR expires_at_ms > $now)";
            if (command.ExecuteScalar() == null) throw new InvalidDataException("El mensaje ya no está disponible o aún está pendiente.");
            command.Parameters.RemoveAt("$now");
            command.CommandText = "SELECT COALESCE(MAX(revision), 0) FROM message_actions WHERE peer_id = $peer AND message_id = $id";
            long revision = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Convert.ToInt64(command.ExecuteScalar()) + 1);
            var update = new ChatActionUpdate(messageId, kind, key, active, revision);
            if (!IsValidAction(update)) throw new InvalidDataException("Reacción o acción no válida.");
            ApplyActions(peerId, [update], mine: true);
            return update;
        }
    }

    public void ApplyActions(string peerId, ChatActionUpdate[] updates, bool mine)
    {
        lock (_sync)
        {
            if (updates.Length > 8192 || updates.Any(update => update == null || !IsValidAction(update))) throw new InvalidDataException("Acciones de mensaje no válidas.");
            using var db = Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO message_actions(peer_id, message_id, mine, kind, action_key, active, revision)
                SELECT $peer, $id, $mine, $kind, $key, $active, $revision
                WHERE EXISTS (SELECT 1 FROM messages WHERE peer_id = $peer AND message_id = $id AND delivery_state = 'sent' AND (expires_at_ms IS NULL OR expires_at_ms > $now))
                ON CONFLICT(peer_id, message_id, mine, kind, action_key)
                DO UPDATE SET active = excluded.active, revision = excluded.revision WHERE excluded.revision > message_actions.revision
                """;
            foreach (var update in updates)
            {
                command.Parameters.Clear(); command.Parameters.AddWithValue("$peer", peerId); command.Parameters.AddWithValue("$id", update.msgId);
                command.Parameters.AddWithValue("$mine", mine); command.Parameters.AddWithValue("$kind", update.kind); command.Parameters.AddWithValue("$key", update.key);
                command.Parameters.AddWithValue("$active", update.active); command.Parameters.AddWithValue("$revision", update.revision);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public WebUiServer.ChatMessage[] GetPinnedMessages(string peerId) => GetActions(peerId).Where(state => state.kind == "pin")
        .GroupBy(state => state.msgId).Select(group => group.OrderByDescending(state => state.revision).ThenByDescending(state => state.active).First())
        .Where(state => state.active).Take(50).Select(state => GetMessage(peerId, state.msgId)).OfType<WebUiServer.ChatMessage>().ToArray();
}
