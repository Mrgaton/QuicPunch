# Core Engine: QuicPunch Facade & Data Structures

> The [`QuicPunch`](../../QuicPunch/QuicPunch.cs) class is the central orchestrator of the entire library. It unifies UDP socket I/O, cryptographic identities, peer discovery tables, protocol negotiation, and Tor hidden services.

---

### Key Classes & Structures Breakdown

| Class / Structure | File | Responsibility |
| :--- | :--- | :--- |
| [`QuicPunch`](../../QuicPunch/QuicPunch.cs) | `QuicPunch.cs` | Main engine facade, lifecycle manager, UDP/Tor transport router. |
| [`QuicPunchBuilder`](../../QuicPunch/QuicPunchBuilder.cs) | `QuicPunchBuilder.cs` | Fluent builder API for configuring relays, discovery, pool IDs, passwords, and ports. |
| [`PeerInfo`](../../QuicPunch/Structures/PeerInfo.cs) | `Structures/PeerInfo.cs` | Cryptographic peer representation, identity keys, directional AES-GCM ciphers, sliding window. |
| [`PackedFlags`](../../QuicPunch/Structures/PackedFlags.cs) | `Structures/PackedFlags.cs` | Bit-packed byte representation of NAT network types and capability flags. |
| [`CandidateEndpoint`](../../QuicPunch/QuicPunchStructures.cs) | `QuicPunchStructures.cs` | Represents an ICE candidate (`Host`, `ServerReflexive`, `PeerReflexive`, `Relayed`) with priority. |
| [`IncomingHandshakeSession`](../../QuicPunch/QuicPunchStructures.cs) | `QuicPunchStructures.cs` | State container for inbound handshake decisions with time-to-live tracking. |

---

## `PeerInfo` Cryptographic Memory Layout

```
PeerInfo Memory & State
├──  Identity: Guid Id, string Name, string OnionAddress, byte[] CertHash
├──  Cryptographic Keys:
│   ├── ECDsa Curve (Remote Node's Public NIST P-256 Key)
│   ├── ECDiffieHellman Static ECDH Key
│   └── ECDiffieHellman Ephemeral ECDH Key
├──  Directional Session Ciphers (derived via 2-Party HKDF):
│   ├── AesGcm TxCipher + 4-byte TxSalt + Outbound Sequence Counter
│   └── AesGcm RxCipher + 4-byte RxSalt + AntiReplayWindow InboundReplayFilter
└──  Network Endpoints:
    ├── IPEndPoint ActiveEndPoint
    ├── IPAddress[] Addresses
    └── TransportType ActiveTransport (Wan | Tor)
```

---

## Fluent Builder API (`QuicPunchBuilder`)

```csharp
// Example: Creating and starting a QuicPunch node via builder
var quicPunch = await QuicPunch.CreateBuilder()
    .UsePool("my-p2p-mesh-room")               // SHA1 derived pool ID for Nostr rendezvous
    .WithPort(5000)                            // Preferred UDP discovery port
    .WithPassword("SecretPassword123!")        // Optional HMAC challenge-response authentication
    .WithAutoDiscovery(enabled: true)          // Enables both WAN STUN and Nostr signaling
    .WithNostrRelays(new[] { "wss://purplerelay.com", "wss://relay.primal.net" })
    .BuildAndStartAsync();
```

---

## Discovery Admission & Memory Pruning

To protect against memory exhaustion from untrusted network scanners, [`QuicPunch.cs`](../../QuicPunch/QuicPunch.cs) implements bounded peer pruning:
- **`MaxDiscoveredPeerCount`**: Capped at `256` anonymous discovered peers.
- **`DiscoveredPeerTtl`**: Default `5 minutes`.
- **Protected Peer Filter (`IsPeerProtectedFromDiscoveryPrune`)**:
  - Peers in [`PeerStore`](../../QuicPunch/Helpers/PeerStore.cs) (Trusted).
  - Peers with active protocol sessions (`_activeProtocolSessions`).
  - Peers with in-flight negotiations or active interrogations.

---

## Dynamic Subservice & Discovery Controls

[`QuicPunch`](../../QuicPunch/QuicPunch.cs) exposes modular methods to control individual transports and signaling planes on demand:

| Method / Property | Description |
| :--- | :--- |
| `Task StartWanAsync(ushort port = 0)` | Dynamically opens the UDP socket, initializes STUN, and connects WAN Nostr relays. |
| `Task StopWanAsync()` | Closes UDP listener and STUN, clears WAN addresses, and disconnects WAN relays. |
| `Task StartTorAsync(int virtualPort = 443)` | Initializes the Tor daemon, publishes a v3 onion service, and starts Tor transport. |
| `Task StopTorAsync()` | Shuts down the Tor onion service and disconnects Tor transports. |
| `Task SetWanPeerDiscoveryEnabledAsync(bool)` | Enables or disables Nostr signaling for WAN candidates without touching the UDP socket. |
| `Task SetTorPeerDiscoveryEnabledAsync(bool)` | Enables or disables Nostr signaling for Tor `.onion` addresses. |
| `bool RebindListenerPort(ushort newPort)` | Dynamically rebinds the UDP socket to a new local port. |
| `string? GetWanToken()` | Encodes current public/local UDP endpoints and certificate fingerprint. |
| `string? GetTorToken()` | Encodes the node's `.onion` address and certificate fingerprint. |
| `string GetToken(TransportType transport)` | Retrives the token for the specified transport (`Wan` or `Tor`). |

