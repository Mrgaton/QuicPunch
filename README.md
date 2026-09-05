# QuicPunch

A high-performance, decentralized P2P networking and virtual overlay mesh library for .NET 11.

**QuicPunch** enables secure, authenticated peer-to-peer tunnels across the Internet without dedicated infrastructure. It uses **dual-stack transport** combining **UDP Hole Punching + QUIC (TLS 1.3)** with automatic fallback to **Tor v3 Onion Services**, coordinated through **Nostr decentralized signaling** and **cascaded gateway port mapping (PCP / NAT-PMP / UPnP)**.

---

## Key Features

- **Dual-Stack P2P Transport**: Direct UDP hole punching using RFC 5389 STUN candidates with native `System.Net.Quic` (mTLS 1.3), paired with an embedded **Tor v3 Onion Service** multiplexer for symmetric NATs and firewalls.
- **MsQuic Low-Level Native Engine Tuning & Real-Time Telemetry**:
  - Runtime dynamic switching to **BBR Congestion Control** (`QuicCongestionAlgorithm.Bbr`).
  - **DSCP QoS prioritization** (Expedited Forwarding 46 for Voice, AF41 34 for Video).
  - Microsecond-precision **real-time telemetry** via native `QUIC_STATISTICS_V2` (RTT, loss ratio, CWND, path MTU, wire throughput).
  - **RFC 9221 Unreliable QUIC Datagrams** with configuration cache injection and zero head-of-line blocking.
  - Cross-platform native endpoint resolution (`AF_INET` and `AF_INET6` on both Linux and Windows).
- **Cascaded Gateway Port Mapping**: Automatic port forward negotiation with fallback cascade:
  1. **PCP** (Port Control Protocol - RFC 6887, sub-millisecond, IPv4/IPv6 & CGNAT aware)
  2. **NAT-PMP** (RFC 6886, sub-millisecond, Apple/OpenWrt/pfSense)
  3. **UPnP IGD** (SSDP discovery + SOAP XML WANIPConnection/WANPPPConnection)
- **Dynamic NAT Pinhole Coordination**: Burst STUN probing across 32+ servers for port-range learning, symmetric NAT classification, and periodic pinhole keepalives.
- **Serverless Nostr Signaling**: Decentralized discovery using Nostr relays (Kind 27227) with a pure C# **BIP-340 Schnorr signature engine on secp256k1**.
- **Anti-MitM & Zero-Trust Architecture**:
  - Auto-generated ECDSA (NIST P-256) X.509 certificates.
  - Strict Certificate Pinning (`CertHash`) embedded in compact shareable endpoint tokens.
  - Granular **`PeerAccessController`** with certificate whitelist/blacklist and conditional auto-acceptance.
  - Directional session encryption with **AES-GCM-256** and **2-Party HKDF-SHA256**.
  - **RFC 6479 128-Packet Sliding Anti-Replay Filter**.
- **Opus Real-Time Voice Streaming**: Concentus pure C# Opus audio encoding/decoding (60ms 48kHz frames) over QUIC Datagrams.
- **Asynchronous Rotating File Logger**: High-throughput non-blocking channel logger with 50MB size thresholds, date rolling, and background gzip compression.
- **Dynamic Subservice Controls**: Activate, stop, or rebind **WAN UDP** and **Tor Onion** services on the fly via code or the embedded dashboard.
- **Modular WebUI & Desktop GUI**: Built-in HTTP server, modular REST APIs, async WebSocket hub, real-time QUIC telemetry dashboard with polling, and native Photino window.
- **Multi-Protocol Multiplexing**: Register custom protocols (`IProtocolHandler`) on a single authenticated tunnel. Included out-of-the-box:
  - **Direct Chat**: Structured JSON messaging with delivery receipts.
  - **Voice Calls**: Low-latency encrypted Opus audio streaming plane over QUIC datagrams.
  - **Virtual LAN**: Wintun L3 TUN adapter for seamless virtual private networks.
  - **RelayDrive**: Chunked ephemeral file shelf with SHA-256 integrity validation.

---

## Documentation

For detailed architectural diagrams, protocol specs, cryptographic proofs, and API references, see the **[QuicPunch Documentation Hub](doc/README.md)**:

- **[Architecture Overview](doc/architecture/overview.md)**: Dual WAN/Tor transport layer & glare arbitration.
- **[MsQuic Native Telemetry & Dynamic Congestion Tuning](doc/core/quic-telemetry-and-tuning.md)**: `QUIC_STATISTICS_V2`, BBR/CUBIC switching, and Web UI polling.
- **[Cryptography & Security](doc/architecture/cryptography-and-security.md)**: Certificate pinning, key derivation, replay protection.
- **[Lifecycle & State Machine](doc/architecture/lifecycle-and-state-machine.md)**: Monotonic generational tracking & dynamic WAN/Tor lifecycles.
- **[Core Facade](doc/core/quicpunch-facade.md)**: `QuicPunch`, `QuicPunchBuilder`, `PeerInfo`, and memory limits.
- **[STUN & NAT Traversal](doc/discovery/stun-and-nat.md)**: Binary RFC 5389 STUN parser & ICE candidate racing.
- **[Nostr Signaling](doc/discovery/nostr-signaling.md)**: Pure C# BIP-340 Schnorr on secp256k1 & ephemeral rendezvous.
- **[Tor Subsystem](doc/tor/tor-subsystem.md)**: Managed Tor runtime, SOCKS5, and v3 hidden services.
- **[WebUI & REST APIs](doc/webui-and-apps/webui-backend-and-gui.md)**: Embedded web server, WebSocket hub, dashboard controls.
- **[Testing Suite](doc/testing/test-suite.md)**: 47+ automated security, anti-replay, and lifecycle test specs.

---

## Quick Start

### 1. Fluent Builder Initialization

```csharp
using QuicPunch;

// Create and start a node with Nostr discovery and dynamic UDP port
var quicPunch = await QuicPunch.CreateBuilder()
    .UsePool("my-p2p-room")                     // SHA-256 derived room tag on Nostr
    .WithPort(0)                               // Dynamic UDP listener port
    .WithAutoDiscovery(enabled: true)          // Enables STUN and Nostr signaling
    .WithNostrRelays(new[] { "wss://relay.damus.io", "wss://nos.lol" })
    .BuildAndStartAsync();

// Retrieve shareable Base64 tokens
string? wanToken = quicPunch.GetWanToken();
string? torToken = quicPunch.GetTorToken();
Console.WriteLine($"WAN Token: {wanToken}");
```

### 2. Register a Custom Protocol Handler

```csharp
public class EchoProtocol : QuicPunch.IProtocolHandler
{
    public Guid ProtocolId => Guid.Parse("11111111-2222-3333-4444-555555555555");
    public ushort PreferredPort => 0;
    public string ProtocolName => "Echo";

    public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        string? line = await reader.ReadLineAsync(ct);
        await writer.WriteLineAsync($"Echo: {line}");
    }
}

quicPunch.RegisterProtocol(new EchoProtocol());
```

### 3. Connect via Shareable Token

```csharp
// Import peer token and establish encrypted session
await quicPunch.ConnectTokenAsync(remoteToken);

// Connect protocol stream to available peer
await quicPunch.InitQuicConnection(
    protocolId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
    peer: targetPeer,
    cancellationToken: CancellationToken.None
);
```

---

## Running Tests

```bash
# Run Security, Discovery & Lifecycle Test Suite:
dotnet run --project QuicPunchTests -- --test-security

# Run RFC 6479 Anti-Replay Sliding Window Tests:
dotnet run --project QuicPunchTests -- --test-antireplay

# Run MsQuic Dynamic BBR Congestion Control Test:
dotnet run --project QuicPunchTests -- --test-quic-bbr

# Run MsQuic Unreliable Datagrams RFC 9221 Test:
dotnet run --project QuicPunchTests -- --test-quic-datagrams

# Run MsQuic Advanced Native Telemetry & DSCP Test:
dotnet run --project QuicPunchTests -- --test-quic-telemetry

# Run Cascaded Port Openers Test (PCP / NAT-PMP / UPnP):
dotnet run --project QuicPunchTests -- --test-port-openers

# Run Opus Voice Codec Test:
dotnet run --project QuicPunchTests -- --test-opus

# Run End-to-End Tor Multi-Lane File Transfer Test:
dotnet run --project QuicPunchTests -- --test-tor
```

---

## License

MIT License.
