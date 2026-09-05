# Tor Peer Transport & QUIC Stream Emulation

> [`TorPeerTransportHub`](../../QuicPunch/Tor/TorPeerTransportHub.cs) and [`DummyQuicTransport`](../../QuicPunch/Tor/DummyQuicTransport.cs) provide full QUIC interface parity over Tor TCP streams.

---

### Transport Emulation Architecture

```
Application Protocol (IProtocolHandler)
       │
       ▼
QuicConnection / QuicStream (Unified API)
       │
   ┌───┴───────────────────────────────┐
   │                                   │
   ▼                                   ▼
Native WAN Transport               Tor Peer Transport
(System.Net.Quic over UDP)         (DummyQuicConnectionTransport)
                                       │
                                       ▼
                               TorQuicConnectionManager
                                       │
                                       ▼
                             TorPeerTransportHub
                                       │
                   ┌───────────────────┼───────────────────┐
                   ▼                   ▼                   ▼
             Message Lane        QuicStream Lane        RawTcp Lane
              (Frame-based)       (Stream-based)      (Unframed TCP)
```

---

## Multi-Lane Preface Protocol (`QPT1`)

Every new TCP connection opened through Tor begins with a fixed **122-byte preface** defined in [`TorPeerTransportProtocol.cs`](../../QuicPunch/Tor/TorPeerTransportProtocol.cs):

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|       Magic: 'Q' 'P' 'T' '1'          |Ver: 1 |Kind(1)|Type(1)|Flg(1) |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                 Connection ID (Guid, 16 Bytes)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                 Stream ID (long, 8 Bytes)                     |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                 Connection Token (32 Bytes)                   |
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                 Sender Service ID (ASCII Base32, 56 Bytes)    |
|                             ...                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|      Sender Virtual Port (2B) |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

### Lane Kinds (`TorLaneKind`)
1. **`Message` (`1`)**: Persistent framed control lane for datagrams and lifecycle signaling.
2. **`QuicStream` (`2`)**: Dedicated stream lane mapped to an individual `QuicStream` instance.
3. **`RawTcp` (`3`)**: Raw passthrough TCP lane for transparent socket tunneling.
