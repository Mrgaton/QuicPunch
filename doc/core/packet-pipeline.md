# Packet Pipeline & Protocol Handlers

> QuicPunch uses a compact binary framing format preceded by a 4-byte magic header:
> `public static byte[] MagicHeader = Encoding.UTF8.GetBytes("PNci");`
> Bytes: `0x50, 0x4E, 0x63, 0x69` (ASCII `"PNci"`).

---

### Magic Header & Message Types

| Message Type | Byte Char | Value | Handler Class | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Interrogation` | `'I'` | `0x49` | [`HelloHandler`](../../QuicPunch/PacketHandler/HelloHandler.cs) | Discovery probe seeking rendezvous endpoint and cert. |
| `Hello` | `'H'` | `0x48` | [`HelloHandler`](../../QuicPunch/PacketHandler/HelloHandler.cs) | Signed mutual authentication, ephemeral ECDH, nonce exchange. |
| `Ack` | `'K'` | `0x4B` | [`AckHandler`](../../QuicPunch/PacketHandler/AckHandler.cs) | Acknowledgment of Hello, clock drift check, peer exchange. |
| `Handshake` | `'S'` | `0x53` | [`HandshakeHandler`](../../QuicPunch/PacketHandler/HandshakeHandler.cs) | Application protocol request, accept, decline, or candidate exchange. |
| `FinalHandshake` | `'F'` | `0x46` | [`QuicPunchConnection`](../../QuicPunch/QuicPunchConnection.cs) | Hole punching burst packet to punch firewall pinholes. |
| `QuicReady` | `'Q'` | `0x51` | [`QuicPunch`](../../QuicPunch/QuicPunch.cs) | Server signals that `QuicListener` is bound and ready to accept TLS connection. |
| `Ping` | `'P'` | `0x50` | [`PingHandler`](../../QuicPunch/PacketHandler/PingHandler.cs) | Lightweight RTT latency measurement. |
| `Disconnect` | `'X'` | `0x58` | [`DisconnectHandler`](../../QuicPunch/PacketHandler/DisconnectHandler.cs) | Signed teardown notification with immediate session cleanup. |

---

## Connection flags

`ConnectionFlags` remains one byte. Bit 5 (`0x20`), `ExternalPortMayDiffer`, marks
an external port translated relative to the bound host port, or a Multiple/Range
advertisement. It may therefore be true even in Single mode. The bit survives
Hello, peer exchange, endpoint tokens, signed Nostr announcements and saved
contacts. In Single mode, QUIC hole punching uses this bit to enable the staged
neighbouring-port birthday search described in [connection and hole punching](connection-and-holepunch.md).
It does not filter auto-connect eligibility or alter existing list/range traversal.
False includes older senders and is not a promise that
an external port stays stable across restarts or destinations. The existing
port list/range payloads and their encoding are unchanged.

## Application Data Plane (RFC 9221 QUIC Datagrams)

Application protocols use virtual streams or datagrams from `QuicPeerMultiplexer`. WAN datagrams use **RFC 9221 QUIC Datagrams** over the established TLS 1.3 connection; Tor carries emulated datagrams over its stream transport and therefore has different delivery characteristics.

The former `PNch` data message (`'D'`, `0x44`) is reserved and discarded. `GetPacketReader` and `SendPayloadAsync` have been removed; application handlers use their virtual `QuicConnection` instead. Discovery and handshake messages still use the control packet pipeline above.

Native WAN datagrams provide:

- **Zero Head-of-Line Blocking**: Unreliable, individual datagram frames without stream head-of-line stalls.
- **Hardware DSCP Prioritization**: Prioritized queuing with QoS flags (e.g., DSCP 46 Voice EF).
- **TLS 1.3 Encryption**: Authenticated and encrypted via the QUIC transport session keys.
- **Congestion Control**: Native BBR / Cubic congestion control and MTU path discovery.
