using Microsoft.Data.Sqlite;
using System.Text.Json;
using QuicNet.Serialization;

namespace QuicNet.Protocols;

internal sealed record ChatTransferRecord(Guid PeerId, Guid FileId, string Name, long Size, string Mime,
    string Sha256, string TempPath, string FinalPath, long Received, string PrefixHash,
    bool Completed = false, bool IsUpload = false, DateTime? ExpiresAtUtc = null);

/// <summary>Durable manifests and flushed byte offsets, independent of the in-memory chat window.</summary>
internal sealed class ChatTransferStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public ChatTransferStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS chat_transfers (peer TEXT NOT NULL, file TEXT NOT NULL, upload INTEGER NOT NULL, manifest TEXT NOT NULL, PRIMARY KEY(peer, file, upload))";
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    public void Save(ChatTransferRecord record)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO chat_transfers VALUES ($peer, $file, $upload, $manifest)";
            command.Parameters.AddWithValue("$peer", record.PeerId.ToString());
            command.Parameters.AddWithValue("$file", record.FileId.ToString());
            command.Parameters.AddWithValue("$upload", record.IsUpload ? 1 : 0);
            command.Parameters.AddWithValue("$manifest", JsonSerializer.Serialize(record, SharedJsonContext.Default.ChatTransferRecord));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ChatTransferRecord> Load(Guid? peerId = null, bool includeExpired = false)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT manifest FROM chat_transfers WHERE $peer IS NULL OR peer = $peer";
            command.Parameters.AddWithValue("$peer", peerId.HasValue ? (object)peerId.Value.ToString() : DBNull.Value);
            using var reader = command.ExecuteReader();
            var result = new List<ChatTransferRecord>();
            while (reader.Read())
            {
                var record = JsonSerializer.Deserialize(reader.GetString(0), SharedJsonContext.Default.ChatTransferRecord);
                if (record != null && (includeExpired || record.ExpiresAtUtc == null || record.ExpiresAtUtc > DateTime.UtcNow)) result.Add(record);
            }
            return result;
        }
    }

    public ChatTransferRecord? Find(Guid peerId, Guid fileId, bool includeExpired = false) =>
        Load(peerId, includeExpired).FirstOrDefault(r => r.FileId == fileId && !r.IsUpload);

    public void Delete(Guid peerId, Guid fileId, bool upload = false)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM chat_transfers WHERE peer = $peer AND file = $file AND upload = $upload";
            command.Parameters.AddWithValue("$peer", peerId.ToString());
            command.Parameters.AddWithValue("$file", fileId.ToString());
            command.Parameters.AddWithValue("$upload", upload ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }
}
