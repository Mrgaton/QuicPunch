# QuicPunch

A decentralized, serverless P2P networking library for .NET that combines UDP hole punching with QUIC (TLS 1.3). 

QuicPunch allows two peers behind NATs to discover each other using public BitTorrent trackers, punch through their firewalls, and establish a highly secure, multiplexed QUIC connection without relying on a central signaling server.

## Features

- **Zero Infrastructure**: Uses public UDP BitTorrent trackers for peer discovery. No need to host your own signaling, STUN, or TURN servers.
- **QUIC / TLS 1.3**: All data is transported over QUIC, providing built-in encryption, forward secrecy, and bidirectional stream multiplexing.
- **Anti-MitM Security**: Cryptographic identity is tied to auto-generated X.509 certificates. Certificate hashes are bundled into shareable tokens to guarantee MITM-proof connections (Certificate Pinning).
- **Protocol Multiplexing**: Register multiple custom protocols (`IProtocolHandler`) on a single P2P connection. Handle chats, file transfers, or RPCs simultaneously.
- **Resilient**: Built-in per-IP rate limiting and robust socket lifecycle management to prevent deadlocks and CPU spikes.

## How it works

1. **Identity Generation**: On first run, QuicPunch generates a self-signed ECDSA X.509 certificate.
2. **Discovery**: Peers join a specific "pool" (infohash) on public BitTorrent trackers.
3. **Signaling & Hole Punching**: When peers find each other, they exchange UDP handshakes to negotiate ports and punch through NATs.
4. **QUIC Upgrade**: Once the NAT is open, a QUIC connection is established over the punched ports. TLS 1.3 mutual authentication ensures the peer's certificate matches the expected hash.

## Quick Start

### 1. Initialize the Core

```csharp
var cts = new CancellationTokenSource();

// Pool ID is a 20-byte hash used for discovery on trackers
byte[] poolId = Convert.FromHexString("1234567890ABCDEF1234567890ABCDEF12345678");

// Initialize with a dynamic port (0)
using var qcc = new QuicPunchCore(cts, poolId, 0);

Console.WriteLine($"My Token: {await qcc.GetToken()}");
```

### 2. Register a Protocol Handler

Define what happens when a QUIC connection is established.

```csharp
public class ChatHandler : QuicPunchCore.IProtocolHandler
{
    public Guid ProtocolId => Guid.Parse("11111111-1111-1111-1111-111111111111");
    public ushort PreferredPort => 0;
    public string ProtocolName => "Chat";

    public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
    {
        // Handle your bidirectional stream here
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        
        await writer.WriteLineAsync("Hello from P2P!");
    }
}

var chatHandler = new ChatHandler();
qcc.RegisterProtocol(chatHandler);
```

### 3. Connect to a Peer

When a peer is found on the tracker, initiate the connection.

```csharp
qcc.TrackerScanner.OnPeerFound += (peerEndpoint) =>
{
    // Start UDP hole punching
    _ = qcc.PeerInterrogation(peerEndpoint, cts.Token);
};

qcc.OnPeerAvailable += async (peer) =>
{
    // Once the peer responds to hole punching, establish QUIC
    await qcc.InitQuicConnection(
        chatHandler.ProtocolId, 
        peer, 
        localPort: (ushort)Random.Shared.Next(1024, 65535), 
        cancellationToken: cts.Token
    );
};
```

## Security Model & Trust Architecture

QuicPunch cleanly separates **Discovery** from **Authorization**:

1. **Discovery ≠ Trust**:
   - Trackers, LAN multicast, and Tor discovery only facilitate IP reachability and cryptographic session negotiation.
   - When a peer is discovered, its self-signed certificate and ECDSA signatures are cryptographically verified to establish secure, encrypted UDP signaling (`AvailablePeers`).
   - However, **discovery does not grant trust or protocol access**. An unknown peer discovered on a public tracker is considered an untrusted stranger.

2. **Authorization via Out-of-Band Tokens**:
   - High security is achieved by exchanging a **Token** out-of-band (e.g. via QR code, encrypted messenger, or direct configuration).
   - The token contains the peer's pinned `CertHash`. Importing a token (`SavePeer`, `PeerInterrogation`) registers the hash in `ExpectedPeerCerts` and designates the peer as **Trusted**.

3. **Trust-Gated Protocol Authorization**:
   - When a peer attempts to open an application protocol (e.g. Virtual LAN, Chat, File Share), incoming handshakes are gated:
     - **Trusted Peers** (those matching an expected token or saved record) are automatically accepted (`AutoAcceptConnections = true`).
     - **Untrusted Strangers** (discovered via trackers or LAN without a token) are **not** auto-accepted. Their connection requests trigger the `HandshakeRequested` event, requiring explicit user/application approval.
   - During TLS 1.3 QUIC connection establishment, `RemoteCertificateValidationCallback` strictly enforces certificate pinning against the expected hash, preventing Man-in-the-Middle (MitM) attacks.

## License
MIT License.