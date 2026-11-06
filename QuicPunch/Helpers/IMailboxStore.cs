namespace QuicPunch;

public enum MailboxMessageDirection
{
    Incoming = 0,
    Outgoing = 1
}

public enum MailboxDeliveryState
{
    Queued = 0,
    RelayAccepted = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4
}

public sealed record MailboxSendOptions(
    string ContentType = "application/octet-stream",
    TimeSpan? TimeToLive = null,
    string? CorrelationId = null);

public sealed record MailboxHistoryQuery(
    Guid ProtocolId,
    Guid? PeerId = null,
    MailboxMessageDirection? Direction = null,
    MailboxDeliveryState? State = null,
    bool IncludeGlobal = true,
    bool IncludeExpired = true,
    int Limit = 200,
    int Offset = 0);

public sealed record MailboxStoredMessage(
    string MessageId,
    Guid ProtocolId,
    MailboxMessageDirection Direction,
    MailboxDeliveryState State,
    Guid? PeerId,
    string SenderPubKeyHex,
    string RecipientPubKeyHex,
    string SenderName,
    byte[] Payload,
    string ContentType,
    DateTimeOffset CreatedAt,
    DateTimeOffset StoredAt,
    DateTimeOffset? ExpiresAt,
    bool IsGlobal,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    string? RelayEventId,
    string? CorrelationId);

public interface IMailboxStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> TryAddAsync(MailboxStoredMessage message, CancellationToken cancellationToken = default);
    Task UpdateDeliveryAsync(string messageId, MailboxDeliveryState state, string? relayEventId = null, string? error = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailboxStoredMessage>> QueryAsync(MailboxHistoryQuery query, CancellationToken cancellationToken = default);
    Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
