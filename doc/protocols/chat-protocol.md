# Direct P2P Chat Protocol

> [`ChatHandler`](../../QuicPunchTests/Protocols/ChatHandler.cs) implements a direct, end-to-end encrypted messaging protocol running over native QUIC streams.

---

### Protocol Specification

| Property | Value |
| :--- | :--- |
| **Protocol GUID** | `00000000-0000-0000-0000-000000000001` |
| **Framing Format** | 4-byte little-endian length prefix + UTF-8 JSON payload |
| **Max Frame Size** | `12 MiB` (Supports rich text and small media payloads) |
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
