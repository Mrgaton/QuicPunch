# Direct P2P Chat Protocol

> [`ChatHandler`](../../QuicNet.Shared/Protocols/ChatHandler.cs) implements a direct, end-to-end encrypted messaging protocol running over native QUIC streams.

---

### Protocol Specification

| Property | Value |
| :--- | :--- |
| **Protocol GUID** | `00000000-0000-0000-0000-000000000001` |
| **Framing Format** | 4-byte little-endian length prefix + UTF-8 JSON payload |
| **Max Frame Size** | `32 MiB` for JSON; file frames contain at most `64 KiB` of data plus a 26-byte header |
| **Features** | Delivery receipts (`chat_ack`), History Synchronization (`chat_sync_req` / `chat_sync_item`) |

---

## JSON Message Types

### 1. New Message (`chat_msg`)
```json
{
  "type": "chat_msg",
  "msgId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "sender": "Alice",
  "content": "Hello via direct P2P QUIC!",
  "timestamp": "2026-09-01T17:00:00.0000000Z"
}
```

### 2. Delivery Receipt (`chat_ack`)
```json
{
  "type": "chat_ack",
  "msgId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "delivered"
}
```

### 3. History Synchronization Request & Response
- **Initiation**: When a chat session opens, nodes exchange `{"type": "chat_sync_req"}`.
- **Sync Items**: Nodes emit historical messages missing from the peer.
- **Completion**: `{"type": "chat_sync_done"}` completes the sync phase.

## Verified file transfers

`chat_file_offer` includes `fileId`, `name`, `size`, `mime` and the SHA-256 digest in `sha256`. The digest is computed while the local upload is staged. Each offer belongs to its authenticated recipient; knowing a file ID does not authorize another peer to request, write or cancel that file.

`chat_file_req` identifies the file and offset. Binary frames use marker `0x01`, a 16-byte file ID, an 8-byte little-endian offset, one end-marker byte, and up to 64 KiB of data. The sender finishes with an empty terminal frame, including for empty files. The receiver also accepts an end marker on a full final chunk.

The chat reader awaits each disk write. It validates the sender, exact next offset, advertised size and SHA-256 before flushing to disk and renaming the temporary file. It never overwrites an existing destination. Only then does it send `chat_file_complete` with the matching file ID and digest. The sender reports delivery after that receipt, rather than after writing to its network stream.

There are at most eight uploads and eight downloads per module. Downloads stream to a `.qp-part` file in the download folder. `chat-transfers.db` in the node's persistent application-data folder stores the authenticated peer, immutable manifest, partial/final paths, committed 64-bit byte offset and SHA-256 of the saved prefix. Every 64 MiB or five seconds of received data, the receiver flushes the file to disk before committing the checkpoint to SQLite. Pause, peer cancellation, disconnection, two minutes of inactivity and normal application shutdown retain the partial download. Explicit discard and failed integrity verification remove it. Following an abrupt process exit, bytes beyond the last committed checkpoint are discarded.

The chat restores progress from SQLite when loading history and shows **Pause**, **Resume** and **Discard** actions. Resume is explicit: it rereads and verifies the saved prefix with bounded memory, reconstructs the incremental whole-file hash, and requests only the remaining bytes with `chat_file_req.offset`. Rereading hundreds of gigabytes can take time; the UI displays a saved-data verification state. Corrupted or truncated checkpoints cannot resume. A verified rename interrupted before its completion commit is recovered by checking the final file's size and hash. Completed downloads and sender offers also survive restart, independently of the recent in-memory message window. The sender's staged outbox file must remain available. Expiring or deleting the attachment forgets its transfer state.

Local staged uploads accept up to 1 TiB, including files around 300 GB; data remains chunked rather than held in memory. This still requires disk space for the sender's staged copy and the receiver's file. Partial chat frames have a 30-second progress deadline. Uploads have a two-minute send-progress deadline and a 60-second receipt deadline. The wire format already supports 64-bit resume offsets, so no protocol change is needed.

Both peers must implement the current verified-transfer contract. There is no capability negotiation or downgrade to unverified transfers: missing or invalid SHA-256 digests reject the offer before acknowledgement. Completion receipts also require a valid file ID and digest. The sending API requires SHA-256. `OnFileChunkReceivedAsync` is the sole chunk event so consumers await writes and apply backpressure; the old synchronous transfer events have been removed. The chat protocol GUID and transport remain unchanged. QUIC uses only the current `h3` disguise ALPN, with no draft or legacy ALPN fallback.
