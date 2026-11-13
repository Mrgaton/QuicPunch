namespace QuicNet.VirtualDisk;

/// <summary>Platform document trees. Paths are validated by the host before reaching this adapter.</summary>
public interface IVirtualDiskDocuments
{
    Task<string?> PickFolderAsync(CancellationToken cancellationToken);
    Task<IVirtualDiskDownload?> CreateDownloadAsync(string name, CancellationToken cancellationToken);
    string ValidateRoot(string root, bool writable);
    string GetName(string root);
    DiskEntry[] GetRoots();
    DiskEntry? Stat(string root, string path);
    DiskReply List(string root, string path, int skip);
    // Return a stable, seekable snapshot; dispose removes any private staging file.
    Stream OpenRead(string root, string path);
    void CreateDirectory(string root, string path);
    void Delete(string root, string path);
    // Publish a verified local staging file. Never silently overwrite an existing document.
    void Publish(string root, string path, string stagingFile, string? expectedVersion);
}

public interface IVirtualDiskDownload : IDisposable
{
    Stream Stream { get; }
    void Complete();
}
