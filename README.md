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
    _ = qcc.PeerInterogation(peerEndpoint, new CancellationTokenSource());
};

qcc.OnPeerAvilable += async (peer) =>
{
    // Once the peer responds to hole punching, establish QUIC
    await qcc.InitPeerConection(
        chatHandler.ProtocolId, 
        peer, 
        localPort: (ushort)Random.Shared.Next(1024, 65535), 
        mainCts: cts
    );
};
```

## Security Model

QuicPunch does not rely on the trackers for security. The trackers only facilitate IP discovery. 
Security is achieved by sharing the "Token" (which contains the `CertHash`) out-of-band. 
When the QUIC connection is established, the `RemoteCertificateValidationCallback` strictly enforces that the remote peer's TLS certificate hash matches the hash from the token, making active Man-in-the-Middle attacks cryptographically impossible.

## License
MIT License.