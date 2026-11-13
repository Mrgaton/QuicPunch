using System.Text.Json.Serialization;

namespace QuicNet.VirtualDisk;

public sealed record DiskRequest(string Operation, Guid ShareId = default, string Path = "", string Recipient = "",
    bool Writable = false, bool AllowDelete = false, DateTime? ExpiresUtc = null, Guid TransferId = default,
    long Offset = 0, long Length = 0, string? Version = null, byte[]? Data = null, string? Sha256 = null, int Skip = 0, Guid LeaseId = default);
public sealed record DiskEntry(string Name, string Path, bool Directory, long Length = 0, DateTime? ModifiedUtc = null);
public sealed record DiskShare(Guid Id, string Name, string Recipient, bool Writable, bool AllowDelete,
    DateTime? ExpiresUtc, string? Root = null, bool AdministratorGrant = false);
internal sealed record StoredDiskShare(Guid Id, string Root, string Recipient, bool Writable, bool AllowDelete,
    DateTime? ExpiresUtc, bool AdministratorGrant);
public sealed record DiskReply(bool Success, string? Error = null, string? Code = null, bool CanExplore = false,
    DiskEntry[]? Entries = null, DiskShare[]? Shares = null, Guid TransferId = default, byte[]? Data = null,
    string? Version = null, long Length = 0, bool More = false, DiskAudit[]? Audit = null, string? MountUrl = null, string? NativePath = null, Guid LeaseId = default);
public sealed record DiskAudit(DateTime Utc, string Actor, string Operation, Guid ShareId, string Path, bool Success);
public sealed record DiskReceipt(string Actor, Guid ShareId, string Path, long Length);
internal sealed record DiskWire(Guid Id, bool Response, DiskRequest? Request = null, DiskReply? Reply = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiskRequest))]
[JsonSerializable(typeof(DiskReply))]
[JsonSerializable(typeof(StoredDiskShare[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(DiskWire))]
internal partial class DiskJsonContext : JsonSerializerContext { }
