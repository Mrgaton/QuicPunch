# RelayDrive: Ephemeral P2P File Shelf

> [`RelayDriveHandler`](../../QuicPunchTests/Protocols/RelayDriveHandler.cs) provides a decentralized, temporary file shelf. Peers exchange manifests and stream chunks on-demand without central servers.

---

### Protocol Specification

| Property | Value |
| :--- | :--- |
| **Protocol GUID** | `00000000-0000-0000-0000-000000000005` |
| **Max File Size** | `2 GiB` per file |
| **Max Published Files** | `128` files per node |
| **Chunk Size** | `64 KiB` streaming chunks |
| **Integrity** | SHA-256 Incremental Hash Verified at `FileEnd` |

---

## File Transfer Sequence & Framing

```mermaid
sequenceDiagram
    autonumber
    participant Alice as Node A (Publisher)
    participant Bob as Node B (Downloader)

    Alice->>Bob: Frame: Manifest (JSON List of Available Files)
    Note over Bob: User selects file to materialize
    Bob->>Alice: Frame: Request <FileGuid (16B)>
    
    Alice->>Bob: Frame: FileStart { "id": "...", "name": "doc.pdf", "size": 1048576 }
    loop Chunk Streaming (64KB chunks)
        Alice->>Bob: Frame: FileChunk <FileGuid (16B)> + <ChunkBytes>
        Note over Bob: Append to temp part file & update IncrementalHash
    end
    Alice->>Bob: Frame: FileEnd <FileGuid (16B)> + <SHA256 Hash (32B)>
    
    Note over Bob: Verify SHA-256 == Computed Hash
    Note over Bob: Move .part file to Cache/doc.pdf
```
