# Nostr offline mailbox

QuicPunch exposes one persistent mailbox channel per registered protocol. It uses Nostr only as an encrypted store-and-forward transport; the local SQLite database is the authoritative outbox, inbox, history, retry state, and deduplication index.

## Sending

Obtain the channel in `IProtocolHandler.OnMailboxAttached` and retain it:

```csharp
public void OnMailboxAttached(IMailboxChannel channel) => _mailbox = channel;

string messageId = await _mailbox.QueueOfflineAsync(
    recipient.NostrPubKey!,
    payload,
    new MailboxSendOptions(
        ContentType: "application/vnd.example.settings+json",
        TimeToLive: TimeSpan.FromDays(7),
        CorrelationId: operationId));
```

`QueueOfflineAsync` commits the message locally before attempting network delivery. Its returned message id remains stable across relay failures, retries, and process restarts. `CorrelationId` makes an application operation idempotent within that protocol.

Payloads are limited to 14 KiB so the nested NIP-44/NIP-59 event stays below a 64 KiB relay event target. Protocols should split larger objects into independently identified chunks and reassemble them after receipt.

## Receiving and history

Incoming messages are persisted before either `IMailboxChannel.OnMessageReceived` or `IProtocolHandler.OnOfflineMessageReceivedAsync` is invoked. A repeated relay event therefore does not repeat application delivery.

```csharp
IReadOnlyList<MailboxStoredMessage> history = await _mailbox.GetHistoryAsync(
    new MailboxHistoryQuery(
        _mailbox.ProtocolId,
        Direction: MailboxMessageDirection.Incoming,
        Limit: 100));
```

The durable delivery states mean:

- `Queued`: committed locally and waiting for a publish attempt.
- `RelayAccepted`: at least one relay returned a positive Nostr `OK`; this does not claim that the recipient has opened it.
- `Delivered`: received, authenticated, decrypted, and committed to the recipient's local database.
- `Read`: reserved for an application-level read receipt.
- `Failed`: the last publish attempt failed; the automatic outbox worker continues retrying it with backoff until it expires.

Expiration stops further delivery attempts but does not erase history. Set `IncludeExpired: false` in a history query to hide it, or call `IMailboxStore.DeleteExpiredAsync` when the application intentionally applies a retention policy.

## Security and wire format

Private messages use a kind `14` rumor, a signed kind `13` seal, and a kind `1059` gift wrap. Both encryption layers use NIP-44 v2. The gift-wrap key and timestamp hide the sender and exact send time from relays. Legacy kind `4` messages remain receive-only for rolling upgrades.

The SQLite payload column is protected with AES-256-GCM using a key derived from the node's persistent Nostr identity. Routing and history metadata remain queryable. Losing or replacing that identity intentionally makes the protected payload history unreadable.

Expiration is enforced both in SQLite and with the NIP-40 `expiration` tag. Because relay deletion is advisory, applications must still avoid putting unbounded or irreplaceable data into relay messages.
