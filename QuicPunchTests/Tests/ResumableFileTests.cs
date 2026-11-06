using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using QuicNet.Protocols;
using QuicNet.WebUi;
using QuicNet.WebUi.Modules;

namespace QuicPunchTests.Tests;

internal static class ResumableFileTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    private static ChatTransferRecord Manifest(string root, byte[] data) => new(Guid.NewGuid(), Guid.NewGuid(),
        "large.bin", data.LongLength, "application/octet-stream", Hash(data), Path.Combine(root, Guid.NewGuid() + ".part"),
        Path.Combine(root, Guid.NewGuid() + ".bin"), 0, Hash([]));

    private static ChatFileDownload Open(ChatTransferStore store, ChatTransferRecord record) => new(record.PeerId,
        record.FileId, record.Name, record.Size, record.Sha256, record.TempPath, record.FinalPath,
        new FileStream(record.TempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            ChatHandler.FileChunkBytes, FileOptions.Asynchronous), store, record);

    public static async Task RunAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "resume-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await PauseAndRestart(root);
            await AutomaticCheckpoint(root);
            await RecoverCheckpoint(root);
            await RejectCorruption(root);
            await RestartModule(root);
            LongOffsets(root);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped the build directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task PauseAndRestart(string root)
    {
        string database = Path.Combine(root, "resume.db");
        var store = new ChatTransferStore(database);
        byte[] data = RandomNumberGenerator.GetBytes(3 * ChatHandler.FileChunkBytes + 17);
        var record = Manifest(root, data);
        store.Save(record);
        await using (var first = Open(store, record))
        {
            await first.RestoreAsync(default);
            await first.WriteChunkAsync(record.PeerId, 0, data.AsMemory(0, ChatHandler.FileChunkBytes), false, default);
            await first.PauseAsync();
        }
        store = new ChatTransferStore(database);
        record = store.Find(record.PeerId, record.FileId)!;
        Check(record.Received == ChatHandler.FileChunkBytes && File.Exists(record.TempPath), "pause persists flushed bytes and offset across database restart");
        await using (var resumed = Open(store, record))
        {
            await resumed.RestoreAsync(default);
            Check(resumed.ReceivedBytes == record.Received, "resume reconstructs the hash and starts at the saved offset");
            Check(!await resumed.WriteChunkAsync(record.PeerId, 0, data.AsMemory(0, ChatHandler.FileChunkBytes), false, default),
                "a late duplicate from the previous upload cannot append bytes twice");
            for (long offset = resumed.ReceivedBytes; offset < data.Length; offset += ChatHandler.FileChunkBytes)
                await resumed.WriteChunkAsync(record.PeerId, offset, data.AsMemory((int)offset, (int)Math.Min(ChatHandler.FileChunkBytes, data.Length - offset)), false, default);
            Check(await resumed.WriteChunkAsync(record.PeerId, data.Length, ReadOnlyMemory<byte>.Empty, true, default), "resumed file passes the original whole-file SHA-256");
        }
        Check(File.ReadAllBytes(record.FinalPath).SequenceEqual(data), "resumed output is byte-for-byte identical");
        Check(new ChatTransferStore(database).Find(record.PeerId, record.FileId)!.Completed, "verified completion survives another restart");
        Check(store.Find(Guid.NewGuid(), record.FileId) == null, "checkpoints are isolated by authenticated peer");
    }

    private static async Task RecoverCheckpoint(string root)
    {
        var store = new ChatTransferStore(Path.Combine(root, "crash.db"));
        byte[] data = RandomNumberGenerator.GetBytes(2 * ChatHandler.FileChunkBytes);
        var record = Manifest(root, data) with { Received = ChatHandler.FileChunkBytes, PrefixHash = Hash(data.AsSpan(0, ChatHandler.FileChunkBytes)) };
        store.Save(record);
        // Simulate a process dying after writing a tail but before committing its offset.
        File.WriteAllBytes(record.TempPath, data);
        await using (var resumed = Open(store, record))
        {
            await resumed.RestoreAsync(default);
            Check(new FileInfo(record.TempPath).Length == record.Received, "crash recovery discards only the uncommitted tail");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await resumed.WriteChunkAsync(record.PeerId, record.Received, data.AsMemory(ChatHandler.FileChunkBytes), false, cancelled.Token); }
            catch (OperationCanceledException) { }
            await resumed.PauseAsync();
        }
        Check(store.Find(record.PeerId, record.FileId)!.Received == record.Received, "connection cancellation preserves the last valid prefix");
        await using (var discarded = Open(store, record)) await discarded.RestoreAsync(default);
        Check(!File.Exists(record.TempPath) && store.Find(record.PeerId, record.FileId) == null, "explicit discard removes partial bytes and resumable state");
    }

    private static async Task AutomaticCheckpoint(string root)
    {
        var store = new ChatTransferStore(Path.Combine(root, "automatic.db"));
        byte[] chunk = RandomNumberGenerator.GetBytes(ChatHandler.FileChunkBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < 1024; i++) hash.AppendData(chunk);
        var record = Manifest(root, []) with { Size = 64L * 1024 * 1024, Sha256 = Convert.ToHexString(hash.GetCurrentHash()) };
        store.Save(record);
        await using var download = Open(store, record);
        await download.RestoreAsync(default);
        for (int i = 0; i < 1024; i++)
            await download.WriteChunkAsync(record.PeerId, (long)i * chunk.Length, chunk, false, default);
        var checkpoint = new ChatTransferStore(Path.Combine(root, "automatic.db")).Find(record.PeerId, record.FileId)!;
        Check(checkpoint.Received == record.Size && checkpoint.PrefixHash == record.Sha256 && !checkpoint.Completed,
            "64 MiB checkpoint is committed automatically before pause or shutdown");
        await download.PauseAsync();
        await using var resumed = Open(store, checkpoint);
        await resumed.RestoreAsync(default);
        Check(await resumed.WriteChunkAsync(record.PeerId, record.Size, ReadOnlyMemory<byte>.Empty, true, default),
            "fully saved download resumes with an empty terminal frame");
    }

    private static async Task RejectCorruption(string root)
    {
        var store = new ChatTransferStore(Path.Combine(root, "corruption.db"));
        foreach (bool truncate in new[] { false, true })
        {
            byte[] data = RandomNumberGenerator.GetBytes(ChatHandler.FileChunkBytes);
            var record = Manifest(root, data) with { Received = data.Length, PrefixHash = Hash(data) };
            store.Save(record);
            if (truncate) File.WriteAllBytes(record.TempPath, data[..10]);
            else { data[0] ^= 1; File.WriteAllBytes(record.TempPath, data); }
            bool rejected = false;
            await using (var download = Open(store, record))
                try { await download.RestoreAsync(default); }
                catch (InvalidDataException) { rejected = true; }
            Check(rejected && !File.Exists(record.FinalPath) && store.Find(record.PeerId, record.FileId) == null,
                truncate ? "truncated checkpoint is rejected" : "corrupted checkpoint is rejected before requesting more bytes");
        }
    }

    private static async Task RestartModule(string root)
    {
        string appData = Path.Combine(root, "node");
        using var node = new QuicPunch.QuicPunch(appDataPath: appData);
        using var lifetime = new CancellationTokenSource();
        await using var hub = new WebUiWebSocketHub(lifetime);
        var handler = new ChatHandler();
        using var peer = new QuicPunch.PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        node.AvailablePeers[peer.Id] = peer;
        node.TrustPeer(peer.CertHash);
        node.ReputationFilter?.Dispose();
        node.ReputationFilter = null;
        byte[] data = RandomNumberGenerator.GetBytes(2 * ChatHandler.FileChunkBytes);
        var record = Manifest(root, data) with { PeerId = peer.Id };
        var store = new ChatTransferStore(Path.Combine(appData, "chat-transfers.db"));
        store.Save(record);
        var module = new ChatApiModule(node, handler, hub, root, Path.Combine(root, "outbox"));
        var download = Open(store, record);
        await download.RestoreAsync(default);
        await download.WriteChunkAsync(record.PeerId, 0, data.AsMemory(0, ChatHandler.FileChunkBytes), false, default);
        var active = (IDictionary)typeof(ChatApiModule).GetField("_activeDownloads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        active.Add((record.PeerId, record.FileId), download);
        module.Dispose();
        record = store.Find(record.PeerId, record.FileId)!;
        Check(record.Received == ChatHandler.FileChunkBytes && File.Exists(record.TempPath), "closing the application checkpoints active downloads");
        var upload = record with { FileId = Guid.NewGuid(), IsUpload = true, FinalPath = record.TempPath };
        store.Save(upload);
        using var restarted = new ChatApiModule(node, handler, hub, root, Path.Combine(root, "outbox"));
        var status = restarted.GetDownloadStatuses(record.PeerId).Single();
        Check(status.status == "paused" && status.received == record.Received, "reopened chat restores resumable progress without recent message history");
        var shared = (IDictionary)typeof(ChatApiModule).GetField("_sharedFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(restarted)!;
        Check(shared.Contains(upload.FileId), "sender restores persisted offers after restarting");
        bool offline = false;
        try { await restarted.BeginDownloadAsync(record.PeerId, record.FileId); }
        catch (IOException) { offline = true; }
        Check(offline && store.Find(record.PeerId, record.FileId)!.Received == record.Received && File.Exists(record.TempPath),
            "retry while the sender is offline preserves saved progress");
        // Simulate a crash after rename but before saving Completed=true.
        File.WriteAllBytes(record.FinalPath, data);
        File.Delete(record.TempPath);
        Check(await restarted.RecoverCompletedDownloadAsync(record.PeerId, record.FileId, default) == record.FinalPath &&
            store.Find(record.PeerId, record.FileId)!.Completed, "verified rename is recovered if the process died before committing completion");
    }

    private static void LongOffsets(string root)
    {
        var store = new ChatTransferStore(Path.Combine(root, "large.db"));
        var record = Manifest(root, []) with { Size = 300L * 1024 * 1024 * 1024, Received = 237L * 1024 * 1024 * 1024 };
        store.Save(record);
        var saved = new ChatTransferStore(Path.Combine(root, "large.db")).Find(record.PeerId, record.FileId)!;
        Check(saved.Size == record.Size && saved.Received == record.Received, "300 GiB manifest and 237 GiB offset survive SQLite without 32-bit overflow");
        byte[] frame = ChatHandler.BuildFileChunkFrame(record.FileId, record.Received, [1], 1, false);
        Check(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(17, 8)) == record.Received,
            "wire protocol retains offsets above 2 GiB");
        Check(WebUiContext.GetRequestBodyLimit("/api/chat/share-file") >= record.Size, "local upload accepts a 300 GiB offered file");
    }
}
