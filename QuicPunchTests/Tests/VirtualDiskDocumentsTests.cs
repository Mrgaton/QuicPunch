using System.Text;
using QuicNet.VirtualDisk;

namespace QuicPunchTests.Tests;

internal static class VirtualDiskDocumentsTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static DiskReply Ok(DiskReply value) { Check(value.Success, value.Error ?? "Document operation failed"); return value; }
    internal static void Run(string directory)
    {
        var provider = new Documents();
        string store = Path.Combine(directory, "documents");
        using var disk = new VirtualDiskService(store, p => p is "alice" or "bob", p => p == "bob") { Documents = provider };
        var receipts = new List<DiskReceipt>(); disk.FileReceived += receipts.Add;
        var inbox = Ok(disk.Execute(null, new("inbox", Recipient: "alice", ExpiresUtc: DateTime.UtcNow.AddHours(24)))).Shares![0];
        var inboxAgain = Ok(disk.Execute(null, new("inbox", Recipient: "alice", ExpiresUtc: DateTime.UtcNow.AddHours(24)))).Shares![0];
        Check(inbox.Id == inboxAgain.Id, "Repeated reception created an unrevokable duplicate grant");
        var otherInbox = Ok(disk.Execute(null, new("inbox", Recipient: "bob"))).Shares![0];
        Check(inbox.Root != otherInbox.Root && inbox.Writable && !inbox.AllowDelete, "Inboxes are not isolated per sender");
        Check(!disk.Execute("bob", new("list", ShareId: inbox.Id)).Success, "Another sender read an inbox");
        Check(!disk.Execute("alice", new("inbox", Recipient: "alice")).Success, "Remote sender enabled their own inbox");
        var outbox = Ok(disk.Execute(null, new("outbox", Recipient: "alice"))).Shares![0];
        Check(!outbox.Writable, "Outbox grants recipients write permission");
        var write = Ok(disk.Execute(null, new("begin-write", ShareId: outbox.Id, Path: "hello.txt", Length: 5)));
        Ok(disk.Execute(null, new("write", TransferId: write.TransferId, Data: Encoding.UTF8.GetBytes("hello"))));
        Ok(disk.Execute(null, new("commit", TransferId: write.TransferId)));
        var sent = Ok(disk.Execute("alice", new("open-read", ShareId: outbox.Id, Path: "hello.txt")));
        Check(Encoding.UTF8.GetString(Ok(disk.Execute("alice", new("read", TransferId: sent.TransferId))).Data!) == "hello", "Recipient could not download an outbox file");
        Ok(disk.Execute("alice", new("close", TransferId: sent.TransferId)));
        Check(!disk.Execute("alice", new("begin-write", ShareId: outbox.Id, Path: "evil", Length: 0)).Success, "Recipient wrote to an outbox");
        Check(!disk.Execute("bob", new("grant", Path: store)).Success, "Android administrator granted private app files");
        Check(Ok(disk.Execute(null, new("explore"))).Entries!.Single().Path == Documents.Root, "Android exposed raw filesystem roots");
        var share = Ok(disk.Execute(null, new("grant", Path: Documents.Root, Recipient: "alice", Writable: true, AllowDelete: true))).Shares![0];
        Check(share.Name == "Phone documents", "SAF name was treated as filesystem basename");
        Check(Ok(disk.Execute("alice", new("shares"))).Shares!.All(s => s.Root == null), "SAF root leaked to remote peer");
        Check(!disk.Execute("alice", new("delete", ShareId: share.Id)).Success, "Shared document tree root was deleted");
        foreach (string path in new[] { "../escape", "a/../../escape", "content://other", "a\\b", "a\0b", ".quicnet-upload-private" })
            Check(!disk.Execute("alice", new("open-read", ShareId: share.Id, Path: path)).Success, "Unsafe document path reached provider");
        Ok(disk.Execute("alice", new("mkdir", ShareId: share.Id, Path: "nested")));
        var upload = Ok(disk.Execute("alice", new("begin-write", ShareId: share.Id, Path: "nested/empty.txt", Length: 0)));
        Ok(disk.Execute("alice", new("commit", TransferId: upload.TransferId)));
        Check(provider.Files["nested/empty.txt"].Length == 0, "Empty document could not be published");
        var opened = Ok(disk.Execute("alice", new("open-read", ShareId: share.Id, Path: "original.txt")));
        Check(Encoding.UTF8.GetString(Ok(disk.Execute("alice", new("read", TransferId: opened.TransferId, Offset: 2))).Data!) == "iginal", "SAF snapshot offset failed");
        Check(!disk.Execute("alice", new("begin-write", ShareId: share.Id, Path: "original.txt", Version: opened.Version)).Success, "SAF unsafe replacement accepted");
        Check(!disk.Execute("alice", new("lock", ShareId: share.Id, Path: "original.txt")).Success, "SAF advertised unsupported atomic locks");
        var pending = Ok(disk.Execute("alice", new("begin-write", ShareId: share.Id, Path: "revoked.txt", Length: 1)));
        provider.Enabled = false;
        Check(!disk.Execute("alice", new("read", TransferId: opened.TransferId)).Success, "OS revocation left snapshot readable");
        Check(!disk.Execute("alice", new("write", TransferId: pending.TransferId, Data: [1])).Success, "OS revocation left upload writable");
        provider.Enabled = true;
        provider.FailPublish = true;
        var failed = Ok(disk.Execute("alice", new("begin-write", ShareId: share.Id, Path: "failed.txt", Length: 0)));
        Check(!disk.Execute("alice", new("commit", TransferId: failed.TransferId)).Success, "Provider failure reported success");
        Check(!provider.Files.ContainsKey("failed.txt") && !Directory.EnumerateFiles(store, ".quicnet-upload-*").Any(), "Failed or revoked upload leaked staging files");
        Check(receipts.Count == 1 && receipts[0].Path == "nested/empty.txt", "Receipt emitted before publication or for a local staging upload");
        using var reload = new VirtualDiskService(store, _ => true, _ => false) { Documents = provider };
        Check(Ok(reload.Execute("alice", new("list", ShareId: share.Id))).Entries!.Any(e => e.Name == "original.txt"), "SAF share did not survive restart");
    }

    internal sealed class Download : IVirtualDiskDownload
    {
        internal readonly MemoryStream Buffer = new();
        public Stream Stream => Buffer;
        internal bool Completed, Disposed;
        public void Complete() => Completed = true;
        public void Dispose() { Disposed = true; Buffer.Dispose(); }
    }
    internal sealed class Documents : IVirtualDiskDocuments
    {
        internal const string Root = "content://test/tree/chosen%3AFolder";
        internal bool Enabled = true, FailPublish, CancelDownload;
        internal Download? LastDownload;
        internal Dictionary<string, byte[]> Files = new() { ["original.txt"] = Encoding.UTF8.GetBytes("original") };
        private readonly HashSet<string> _directories = [""];
        public Task<string?> PickFolderAsync(CancellationToken ct) => Task.FromResult<string?>(Root);
        public Task<IVirtualDiskDownload?> CreateDownloadAsync(string name, CancellationToken ct)
            => Task.FromResult<IVirtualDiskDownload?>(CancelDownload ? null : LastDownload = new Download());
        public string ValidateRoot(string root, bool writable) { if (!Enabled || root != Root) throw new UnauthorizedAccessException(); return root; }
        public string GetName(string root) => "Phone documents";
        public DiskEntry[] GetRoots() => [new("Phone documents", Root, true)];
        public DiskEntry? Stat(string root, string path)
        {
            ValidateRoot(root, false);
            return _directories.Contains(path) ? new(Path.GetFileName(path), path, true) : Files.TryGetValue(path, out var bytes) ? new(Path.GetFileName(path), path, false, bytes.Length) : null;
        }
        public DiskReply List(string root, string path, int skip) => new(true, Entries: Files.Keys.Select(p => Stat(root, p)!).ToArray());
        public Stream OpenRead(string root, string path) { ValidateRoot(root, false); return new MemoryStream(Files[path], writable: false); }
        public void CreateDirectory(string root, string path) => _directories.Add(path);
        public void Delete(string root, string path) { Files.Remove(path); _directories.Remove(path); }
        public void Publish(string root, string path, string stagingFile, string? expectedVersion)
        {
            ValidateRoot(root, true);
            if (FailPublish) throw new IOException("Provider unavailable");
            Files.Add(path, File.ReadAllBytes(stagingFile));
        }
    }
}
