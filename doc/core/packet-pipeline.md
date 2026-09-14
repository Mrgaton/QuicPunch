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
| `Ack` | `'K'` | `0x4B` | [`AckHandler`](../../QuicPunch/PacketHandler/AckHandler.cs) | Acknowledgment of Hello, clock drift check, peer exchange. |
| `Handshake` | `'S'` | `0x53` | [`HandshakeHandler`](../../QuicPunch/PacketHandler/HandshakeHandler.cs) | Application protocol request, accept, decline, or candidate exchange. |
| `FinalHandshake` | `'F'` | `0x46` | [`QuicPunchConnection`](../../QuicPunch/QuicPunchConnection.cs) | Hole punching burst packet to punch firewall pinholes. |
| `QuicReady` | `'Q'` | `0x51` | [`QuicPunch`](../../QuicPunch/QuicPunch.cs) | Server signals that `QuicListener` is bound and ready to accept TLS connection. |
| `Ping` | `'P'` | `0x50` | [`PingHandler`](../../QuicPunch/PacketHandler/PingHandler.cs) | Lightweight RTT latency measurement. |
| `Disconnect` | `'X'` | `0x58` | [`DisconnectHandler`](../../QuicPunch/PacketHandler/DisconnectHandler.cs) | Signed teardown notification with immediate session cleanup. |

---

## Application Data Plane (RFC 9221 QUIC Datagrams)

All application datagram communication is transmitted natively through **RFC 9221 QUIC Datagrams** over the established TLS 1.3 `QuicConnection`:

- **Zero Head-of-Line Blocking**: Unreliable, individual datagram frames without stream head-of-line stalls.
- **Hardware DSCP Prioritization**: Prioritized queuing with QoS flags (e.g., DSCP 46 Voice EF).
- **TLS 1.3 Encryption**: Authenticated and encrypted via the QUIC transport session keys.
- **Congestion Control**: Native BBR / Cubic congestion control and MTU path discovery.
