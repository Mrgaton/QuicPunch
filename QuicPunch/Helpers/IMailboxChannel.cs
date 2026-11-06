using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch
{
    /// <summary>
    /// Represents an asynchronous offline message received through the Nostr relay mailbox.
    /// </summary>
    public sealed record OfflineMailboxMessage(
        Guid ProtocolId,
        string SenderPubKeyHex,
        string SenderName,
        PeerInfo? Peer,
        ReadOnlyMemory<byte> Payload,
        DateTimeOffset Timestamp,
        string MessageId,
        bool IsGlobal);

    /// <summary>
    /// Direct channel for protocol handlers and P2P services to interact with the decentralized
    /// Nostr Mailbox. Allows sending and receiving store-and-forward messages between peers even
    /// when they are completely offline and disconnected from direct QUIC.
    /// </summary>
    public interface IMailboxChannel
    {
        /// <summary>
        /// The protocol identifier associated with this mailbox channel.
        /// </summary>
        Guid ProtocolId { get; }

        /// <summary>The durable store shared by all protocol mailbox channels.</summary>
        IMailboxStore Store { get; }

        /// <summary>
        /// Sends an end-to-end encrypted offline message to a peer identified by their GUID ID.
        /// Resolved automatically via AvailablePeers or PeerStore.
        /// </summary>
        Task<string> SendOfflineAsync(Guid peerId, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

        /// <summary>
        /// Sends an end-to-end encrypted offline text message to a peer identified by their GUID ID.
        /// </summary>
        Task<string> SendOfflineAsync(Guid peerId, string text, CancellationToken ct = default);

        /// <summary>
        /// Sends an end-to-end encrypted offline message to a peer.
        /// </summary>
        Task<string> SendOfflineAsync(PeerInfo peer, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

        /// <summary>
        /// Sends an end-to-end encrypted offline text message to a peer.
        /// </summary>
        Task<string> SendOfflineAsync(PeerInfo peer, string text, CancellationToken ct = default);

        /// <summary>
        /// Sends an end-to-end encrypted offline message directly using the recipient's Nostr public key in hex.
        /// </summary>
        Task<string> SendOfflineAsync(string recipientPubKeyHex, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

        /// <summary>
        /// Sends an end-to-end encrypted offline text message directly using the recipient's Nostr public key in hex.
        /// </summary>
        Task<string> SendOfflineAsync(string recipientPubKeyHex, string text, CancellationToken ct = default);

        /// <summary>
        /// Durably queues an arbitrary binary payload. The returned id is stable across retries and restarts.
        /// A temporary lack of connected relays does not lose the message.
        /// </summary>
        Task<string> QueueOfflineAsync(string recipientPubKeyHex, ReadOnlyMemory<byte> payload, MailboxSendOptions? options = null, CancellationToken ct = default);

        /// <summary>
        /// Publishes a public, signed message to the global Nostr channel under this protocol.
        /// </summary>
        Task<string> SendGlobalAsync(string senderName, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

        /// <summary>
        /// Publishes a public, signed text message to the global Nostr channel under this protocol.
        /// </summary>
        Task<string> SendGlobalAsync(string senderName, string text, CancellationToken ct = default);

        /// <summary>
        /// Retrieves the inbox of offline messages received for this protocol.
        /// </summary>
        IReadOnlyList<OfflineMailboxMessage> GetInbox(Guid? peerId = null);

        /// <summary>Queries persistent incoming and outgoing history for this protocol.</summary>
        Task<IReadOnlyList<MailboxStoredMessage>> GetHistoryAsync(MailboxHistoryQuery? query = null, CancellationToken ct = default);

        /// <summary>Retries queued or previously failed messages for this protocol.</summary>
        Task<int> RetryPendingAsync(CancellationToken ct = default);

        /// <summary>
        /// Event triggered when an offline direct message arrives for this protocol.
        /// </summary>
        event Action<PeerInfo, ReadOnlyMemory<byte>, DateTimeOffset, string>? OnMessageReceived;

        /// <summary>
        /// Event triggered when a public global message arrives for this protocol.
        /// </summary>
        event Action<string /* senderPubKey */, string /* senderName */, ReadOnlyMemory<byte>, DateTimeOffset, string /* messageId */>? OnGlobalMessageReceived;
    }
}
