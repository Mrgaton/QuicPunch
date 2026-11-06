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


## Private application identity

Tor uses its own certificate/signing key, ECDH key and Nostr identity. Its default display name is a stable `tor-` pseudonym derived solely from its Tor signing public key. Neither the operating-system username nor the machine hostname belongs in the Tor certificate, its SAN extension, discovery announcements or application sender metadata. The local UI labels Tor contacts even before they are trusted and never joins Tor and WAN contacts by name.

On loading a valid legacy Tor identity, `CertManager` reissues the public certificate metadata with the same signing and ECDH keys under the existing identity file lock and an atomic replacement. The public-key pin, peer ID, Nostr keys, saved trust and onion service are preserved. Already sanitized identities remain byte-stable on reload. WAN identity files are not migrated. This prevents subsequent metadata disclosures; it does not remove previously distributed names or break associations already learned from the old identity.

Protocol registration supplies `GetLocalIdentityForPeer` through `IProtocolHandler.OnLocalIdentityProviderAttached`. Chat sender names, file offers and synchronized history use Tor presentation on Tor sessions. Watch control messages send the local Tor peer ID instead of the WAN ID. Message contents, chosen room names and filenames remain user-controlled and are not anonymized automatically.

The existing offline mailbox belongs to the WAN identity. Known Tor recipients are refused there, including retries of old queued messages, rather than disclosing the WAN sender key/name. Offline Tor delivery requires a separately isolated Tor mailbox. Tor-scoped Nostr discovery also refuses missing or bypassed proxies and never ignores a proxy configuration error to connect directly.

Run `--test-tor-privacy` to verify legacy PFX and bundle migration, unchanged pins/keys/trust IDs, stable reload, independent WAN/Tor identity, handshake/discovery metadata, chat and Watch wire frames, blocked mailbox fallback/retries and mandatory relay proxy usage.
