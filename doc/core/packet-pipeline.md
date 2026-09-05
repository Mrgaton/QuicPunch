# Packet Pipeline & Protocol Handlers

> QuicPunch uses a compact binary framing format preceded by a 4-byte magic header:
> `public static byte[] MagicHeader = Encoding.UTF8.GetBytes("PNch");`
> Bytes: `0x50, 0x4E, 0x63, 0x68` (ASCII `"PNch"`).

---

### Magic Header & Message Types

| Message Type | Byte Char | Value | Handler Class | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Interrogation` | `'I'` | `0x49` | [`HelloHandler`](../../QuicPunch/PacketHandler/HelloHandler.cs) | Discovery probe seeking rendezvous endpoint and cert. |
| `Hello` | `'H'` | `0x48` | [`HelloHandler`](../../QuicPunch/PacketHandler/HelloHandler.cs) | Signed mutual authentication, ephemeral ECDH, nonce exchange. |
| `Ack` | `'A'` | `0x41` | [`AckHandler`](../../QuicPunch/PacketHandler/AckHandler.cs) | Acknowledgment of Hello, clock drift check, peer exchange. |
| `Handshake` | `'S'` | `0x53` | [`HandshakeHandler`](../../QuicPunch/PacketHandler/HandshakeHandler.cs) | Application protocol request, accept, decline, or candidate exchange. |
| `FinalHandshake` | `'F'` | `0x46` | [`QuicPunchConnection`](../../QuicPunch/QuicPunchConnection.cs) | Hole punching burst packet to punch firewall pinholes. |
| `QuicReady` | `'Q'` | `0x51` | [`QuicPunch`](../../QuicPunch/QuicPunch.cs) | Server signals that `QuicListener` is bound and ready to accept TLS connection. |
| `Ping` | `'P'` | `0x50` | [`PingHandler`](../../QuicPunch/PacketHandler/PingHandler.cs) | Lightweight RTT latency measurement. |
| `Disconnect` | `'D'` | `0x44` | [`DisconnectHandler`](../../QuicPunch/PacketHandler/DisconnectHandler.cs) | Signed teardown notification with immediate session cleanup. |
| `Data` | `'X'` | `0x58` | [`QuicPunch`](../../QuicPunch/QuicPunch.cs) | Authenticated, encrypted AEAD UDP datagram. |

---

## Binary Wire Format: `Data` Packet (`'X'`)

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|       MagicHeader: "PNch" (0x50, 0x4E, 0x63, 0x68)    |Type:'X'|PacketType...
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|...PacketType (2B) |        Sender Peer ID (Guid, 16 Bytes)   |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                 Sender Peer ID (Continued)                    |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                 Sequence Number (ulong, 8 Bytes)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                 AES-GCM Authentication Tag (16 Bytes)         |
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                 Encrypted Ciphertext Payload (N Bytes)       |
|                             ...                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

### Associated Authenticated Data (AAD) & Nonce Formulation
- **Header AAD Size**: Exactly `31 bytes` (`MagicHeader (4B) + MessageType.Data (1B) + packetType (2B) + SenderId (16B) + sequenceNumber (8B)`).
- **Nonce Formulation (12 Bytes)**:
  - Nonce[0..3] = 4-byte `TxSalt` or `RxSalt`.
  - Nonce[4..11] = 8-byte `sequenceNumber` encoded in **Big-Endian**.
