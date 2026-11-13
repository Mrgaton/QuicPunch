# QuicPunch

A high-performance, decentralized P2P networking and virtual overlay mesh library for .NET 11.

**QuicPunch** enables secure, authenticated peer-to-peer tunnels across the Internet without dedicated infrastructure. It uses **dual-stack transport** combining **UDP Hole Punching + QUIC (TLS 1.3)** with automatic fallback to **Tor v3 Onion Services**, coordinated through **Nostr decentralized signaling** and **cascaded gateway port mapping (PCP / NAT-PMP / UPnP)**.

---

## Key Features

- **Dual-Stack P2P Transport**: Direct UDP hole punching using RFC 5389 STUN candidates with native `System.Net.Quic` (mTLS 1.3), paired with an embedded **Tor v3 Onion Service** multiplexer for symmetric NATs and firewalls.
- **MsQuic Low-Level Native Engine Tuning & Real-Time Telemetry**:
  - **BBR Congestion Control** (`QuicCongestionAlgorithm.Bbr`) selected before the QUIC handshake, with live inspection of the active controller.
  - **DSCP QoS prioritization** (Expedited Forwarding 46 for Voice, AF41 34 for Video).
  - Microsecond-precision **real-time telemetry** via native `QUIC_STATISTICS_V2` (RTT, loss ratio, CWND, path MTU, wire throughput).
  - **RFC 9221 Unreliable QUIC Datagrams** with configuration cache injection and zero head-of-line blocking.
  - Cross-platform native endpoint resolution (`AF_INET` and `AF_INET6` on both Linux and Windows).
- **Cascaded Gateway Port Mapping**: Automatic port forward negotiation with fallback cascade:
  1. **PCP** (Port Control Protocol - RFC 6887, sub-millisecond, IPv4/IPv6 & CGNAT aware)
  2. **NAT-PMP** (RFC 6886, sub-millisecond, Apple/OpenWrt/pfSense)
  3. **UPnP IGD** (SSDP discovery + SOAP XML WANIPConnection/WANPPPConnection)
- **Dynamic NAT Pinhole Coordination**: Burst STUN probing across 32+ servers for port-range learning, symmetric NAT classification, and periodic pinhole keepalives.
- **Serverless Nostr Signaling**: Decentralized discovery using Nostr relays (Kind 27227) with **BIP-340 Schnorr signatures backed by NBitcoin.Secp256k1**.
- **Anti-MitM & Zero-Trust Architecture**:
  - Auto-generated ECDSA (NIST P-256) X.509 certificates.
  - Strict Certificate Pinning (`CertHash`) embedded in compact shareable endpoint tokens.
  - Automatic authenticated QUIC transport, independent **`PeerAccessController`** trust, and application-specific permissions.
  - Authenticated discovery with per-session entropy and **2-Party HKDF-SHA256** derivation; application data uses the established QUIC/Tor transport.
- **Opus Real-Time Voice Streaming**: Concentus pure C# Opus audio encoding/decoding (20ms 48kHz frames) over QUIC Datagrams.
- **Asynchronous Rotating File Logger**: High-throughput non-blocking channel logger with 50MB size thresholds, date rolling, and background gzip compression.
- **Dynamic Subservice Controls**: Activate, stop, or rebind **WAN UDP** and **Tor Onion** services on the fly via code or the embedded dashboard.
- **Modular WebUI & Desktop GUI**: Built-in HTTP server, modular REST APIs, async WebSocket hub, real-time QUIC telemetry dashboard with polling, and native Photino window.
- **Multi-Protocol Multiplexing**: Register custom protocols (`IProtocolHandler`) on a single authenticated tunnel. Included out-of-the-box:
  - **Direct Chat**: Structured JSON messaging with delivery receipts.
  - **Voice Calls**: Low-latency encrypted Opus audio streaming plane over QUIC datagrams.
  - **Virtual LAN**: Wintun L3 TUN adapter for seamless virtual private networks.
  - **QuicWatch**: Synchronized media playback and on-demand media transfer; chat also supports file sharing.

---

## Documentation

For detailed architectural diagrams, protocol specs, cryptographic proofs, and API references, see the **[QuicPunch Documentation Hub](doc/README.md)**:

- **[Architecture Overview](doc/architecture/overview.md)**: Dual WAN/Tor transport layer & glare arbitration.
- **[MsQuic Native Telemetry & Dynamic Congestion Tuning](doc/core/quic-telemetry-and-tuning.md)**: `QUIC_STATISTICS_V2`, BBR/CUBIC switching, and Web UI polling.
- **[Cryptography & Security](doc/architecture/cryptography-and-security.md)**: Certificate pinning, key derivation, replay protection.
- **[Lifecycle & State Machine](doc/architecture/lifecycle-and-state-machine.md)**: Monotonic generational tracking & dynamic WAN/Tor lifecycles.
- **[Core Facade](doc/core/quicpunch-facade.md)**: `QuicPunch`, `QuicPunchBuilder`, `PeerInfo`, and memory limits.
- **[STUN & NAT Traversal](doc/discovery/stun-and-nat.md)**: Binary RFC 5389 STUN parser & ICE candidate racing.
- **[Nostr Signaling](doc/discovery/nostr-signaling.md)**: BIP-340 Schnorr on secp256k1 & ephemeral rendezvous.
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
    .UsePool("my-p2p-room")                     // SHA-1 derived pool identifier
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
    public string ProtocolName => "Echo";
    public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;

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

For compact agent output, run `./scripts/test.ps1 -Tests output,sha3,antireplay -Web` in PowerShell. It builds once and prints one line per successful suite. Failures and skips retain complete diagnostics in `.artifacts/test-output/`. Use `-Verbose` for full output or `-NoBuild` to reuse the build. See the [test runner documentation](doc/testing/test-suite.md#running-the-automated-test-suites) for exit codes and suite selection.

## Cleaning the workspace

Close running project applications and Visual Studio, then run `./scripts/clean-project.ps1 -WhatIf` to preview or `./scripts/clean-project.ps1` to remove regenerable build outputs, test artifacts and Python caches. Locked files are reported and retained. The script preserves tracked files, native libraries, nested repositories, backups and the historical `precached/` archive. Use standard `bin/` and `obj/` directories for builds; keep release signing keys outside the repository as described in [AGENTS.md](AGENTS.md).

For isolated browser tests, run `npm ci`, `npm run test:browser:install`, then `./scripts/test.ps1 -Browser` (or `npm run test:browser`). Shared web assets live in `QuicNet.Shared/wwwroot` and `QuicNet.Shared/Watch/wwwroot`. Offline IP location data and the compressed catalogue reader live in `QuicNet.Geolocation`.

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

Actualizaciones: [publicar EXE AOT y APK, firma y recuperación](doc/deployment/auto-updates.md). Las instrucciones también están en Ajustes → Actualizaciones → Cómo publicar actualizaciones.
