# Test Harness & Integration Suite

> QuicPunch includes 16 automated unit, security, and integration test suites covering sliding window anti-replay protection, native MsQuic tuning, RFC 9221 datagrams, port mapping cascades, STUN burst probing, Opus voice compression, and Tor mesh networking.

---

### Test Modules Directory

| Test Class / Suite | Source File | CLI Flag | Focus Area |
| :--- | :--- | :--- | :--- |
| [`AntiReplayTests`](../../QuicPunchTests/Tests/AntiReplayTests.cs) | `Tests/AntiReplayTests.cs` | `--test-antireplay` | In-order, out-of-order, duplicate replay, window boundaries, multithreaded stress, and AEAD AAD binding. |
| [`SecurityDiscoveryTests`](../../QuicPunchTests/Tests/SecurityDiscoveryTests.cs) | `Tests/SecurityDiscoveryTests.cs` | `--test-security` | Untrusted candidate isolation, certificate pinning gates, token validation, and subservice lifecycles. |
| [`SecurityDiscoveryTests`](../../QuicPunchTests/Tests/SecurityDiscoveryTests.cs) | `Tests/SecurityDiscoveryTests.cs` | `--test-singleflight` | Concurrency single-flight deduplication under high-load parallel connection requests. |
| [`HandshakeCancellationLifecycleTests`](../../QuicPunchTests/Tests/HandshakeCancellationLifecycleTests.cs) | `Tests/HandshakeCancellationLifecycleTests.cs` | `--test-handshake-cancellation` | Timeout cancellation and resource cleanup during aborted handshakes. |
| [`WebUiSecurityTests`](../../QuicPunchTests/Tests/WebUiSecurityTests.cs) | `Tests/WebUiSecurityTests.cs` | `--test-webui-security` | CSRF token validation, Origin header checks, and CSP enforcement. |
| [`RotatingLoggerTests`](../../QuicPunchTests/Tests/RotatingLoggerTests.cs) | `Tests/RotatingLoggerTests.cs` | `--test-rotating-logger` | Non-blocking async channel logging, 50MB file size rotation, and gzip compression. |
| [`PeerAccessControllerTests`](../../QuicPunchTests/Tests/PeerAccessControllerTests.cs) | `Tests/PeerAccessControllerTests.cs` | `--test-peer-acl` | Zero-trust admission control, auto-accept logic, ExpectedCertSet, and whitelist/blacklist behavior. |
| [`NatPinCoordinatorTests`](../../QuicPunchTests/Tests/NatPinCoordinatorTests.cs) | `Tests/NatPinCoordinatorTests.cs` | `--test-nat-pin-coordinator` | STUN burst sampling across 32+ servers, port delta variance learning, and pinhole keepalive logic. |
| [`VirtualLanPoolingTests`](../../QuicPunchTests/Tests/VirtualLanPoolingTests.cs) | `Tests/VirtualLanPoolingTests.cs` | `--test-lan-pooling` | Deterministic IP assignment collision avoidance, subnet hashing, and frame switching. |
| Native MsQuic Congestion Control | `Program.cs` / `MsQuicTuner.cs` | `--test-quic-bbr` | Deep reflection inspection and runtime switching from Cubic to BBR congestion control. |
| [`QuicDatagramTests`](../../QuicPunchTests/Tests/QuicDatagramTests.cs) | `Tests/QuicDatagramTests.cs` | `--test-quic-datagrams` | RFC 9221 unbuffered datagram channel negotiation and send/receive verification. |
| [`QuicAdvancedNativeTests`](../../QuicPunchTests/Tests/QuicAdvancedNativeTests.cs) | `Tests/QuicAdvancedNativeTests.cs` | `--test-quic-telemetry` | Microsecond `QuicStatisticsV2` telemetry querying, DSCP QoS voice/video tagging, and socket address mapping. |
| [`UpnpTests`](../../QuicPunchTests/Tests/UpnpTests.cs) | `Tests/UpnpTests.cs` | `--test-upnp` | UPnP IGD SSDP M-SEARCH discovery, XML root description parsing, and SOAP port mapping. |
| [`PortOpenerTests`](../../QuicPunchTests/Tests/PortOpenerTests.cs) | `Tests/PortOpenerTests.cs` | `--test-port-openers` | Cascaded port mapping coordinator: PCP (RFC 6887) -> NAT-PMP (RFC 6886) -> UPnP IGD. |
| [`OpusCodecTests`](../../QuicPunchTests/Tests/OpusCodecTests.cs) | `Tests/OpusCodecTests.cs` | `--test-opus` | Concentus pure C# Opus 48kHz encoding/decoding, bitrate compression ratio, and in-band FEC loss concealment. |
| [`TestTorFileShare`](../../QuicPunchTests/Tests/TestTorFileShare.cs) | `Tests/TestTorFileShare.cs` | `--test-tor` | End-to-end multi-peer file transfer over Tor v3 hidden services. |

---

## Security & Discovery Test Coverage (`--test-security`)

The `SecurityDiscoveryTests` suite rigorously exercises over 47 distinct security properties, network edge cases, and dynamic lifecycles:

1. **Token Encoding & Signatures**:
   - Fixed-offset raw encoding & zero-allocation roundtrips.
   - Deterministic Nostr identity derivation (BIP-340 Schnorr on secp256k1).
   - Token stability across polls and invalidation refreshes.
   - Cross-transport token separation (WAN vs. Tor tokens).
2. **Untrusted Stranger Isolation & Replay Rejection**:
   - Denial of protocol handshakes from untrusted discovered peers.
   - Anti-replay validation for Hello packets with strict timestamp monotone requirements.
   - Immediate rejection of corrupted ECDSA signatures and spoofed certificate hashes.
   - Certificate handle leak prevention on invalid or malformed incoming packets.
3. **Session Cryptography & Nonce Collision Safety**:
   - AES-GCM-256 session key uniqueness across node restarts and peer reconnects.
   - Zero nonce reuse protection via 2-Party HKDF salt mixing.
4. **Dynamic Subservice Lifecycles**:
   - Dynamic WAN UDP socket start/stop (`StartWanAsync` / `StopWanAsync`) and address cache eviction.
   - Dynamic Tor onion service initialization and teardown (`StartTorAsync` / `StopTorAsync`).
   - Dynamic port rebinding on active listeners (`RebindListenerPort`).
   - Rebind background refresh isolation to prevent resurrection after node termination.
5. **Concurrency & Resilience**:
   - Single-flight deduplication under 50 simultaneous connection attempts.
   - STUN auto-recovery after temporary network loss.
   - Bounded admission and LRU eviction under memory scanner attacks.

---

## Running the Automated Test Suites

```bash
# 1. Anti-Replay Sliding Window RFC 6479
dotnet run --project QuicPunchTests -- --test-antireplay

# 2. Full Security, Discovery & Lifecycle Suite
dotnet run --project QuicPunchTests -- --test-security

# 3. Single-Flight Concurrency Deduplication
dotnet run --project QuicPunchTests -- --test-singleflight

# 4. Handshake Cancellation & Cleanup
dotnet run --project QuicPunchTests -- --test-handshake-cancellation

# 5. WebUI CSRF & Header Security
dotnet run --project QuicPunchTests -- --test-webui-security

# 6. Rotating Async File Logger & Compression
dotnet run --project QuicPunchTests -- --test-rotating-logger

# 7. Zero-Trust Peer Access Control & Admission
dotnet run --project QuicPunchTests -- --test-peer-acl

# 8. STUN Burst Sampling & Pinhole Keepalives
dotnet run --project QuicPunchTests -- --test-nat-pin-coordinator

# 9. Virtual LAN Deterministic IP Pooling
dotnet run --project QuicPunchTests -- --test-lan-pooling

# 10. Native MsQuic Dynamic BBR Tuning
dotnet run --project QuicPunchTests -- --test-quic-bbr

# 11. RFC 9221 QUIC Datagram Channels
dotnet run --project QuicPunchTests -- --test-quic-datagrams

# 12. Native MsQuic Telemetry & DSCP QoS
dotnet run --project QuicPunchTests -- --test-quic-telemetry

# 13. UPnP IGD Gateway Port Mapping
dotnet run --project QuicPunchTests -- --test-upnp

# 14. Cascaded Port Openers (PCP -> NAT-PMP -> UPnP)
dotnet run --project QuicPunchTests -- --test-port-openers

# 15. Concentus Opus 48kHz Audio Codec & FEC
dotnet run --project QuicPunchTests -- --test-opus

# 16. End-to-End Tor Multi-Lane File Transfer
dotnet run --project QuicPunchTests -- --test-tor
```
