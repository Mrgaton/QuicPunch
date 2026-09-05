---
title: MsQuic Native Telemetry and Dynamic Congestion Control
description: Technical architecture, native parameter definitions, and programming reference for MsQuic protocol telemetry and runtime congestion algorithm switching in QuicPunch.
ms.date: 09/05/2026
ms.topic: conceptual
ms.service: quicpunch
---

# MsQuic Native Telemetry and Dynamic Congestion Control

This document describes the design, native interop architecture, and runtime programming model for extracting low-level QUIC telemetry and dynamically managing congestion control algorithms in QuicPunch.

## Overview

QuicPunch utilizes `System.Net.Quic` backed by the native MsQuic library. While the high-level .NET managed abstractions provide stream and datagram I/O, enterprise diagnostics, quality of service (QoS) enforcement, and congestion control tuning require direct access to the underlying `HQUIC` connection handle and native MsQuic API tables.

The `MsQuicTuner` component establishes a reflection bridge into `System.Net.Quic` internals, exposing:
- Microsecond-precision round-trip time (RTT), minimum RTT, maximum RTT, and RTT variance.
- Comprehensive transmission statistics, including packet loss ratios, retransmissions, spurious losses, and congestion window (CWND) sizes.
- Reception diagnostics, including packet reordering, drops, duplicate frames, and decryption failures.
- Cryptographic handshake metrics, TLS cipher suites, ALPN tokens, and key rotation counts.
- Dynamic, in-flight switching of congestion control algorithms between CUBIC and BBR.

---

## Native Architecture and Interop Mechanism

MsQuic exposes its functionality via a C function table (`QUIC_API_TABLE`). `MsQuicTuner` dynamically resolves the internal managed pointers to this table and the connection handle:

```
+-------------------------------------------------------------+
|                      System.Net.Quic                        |
|                                                             |
|  QuicConnection                                             |
|    └─ _handle (MsQuicContextSafeHandle)                    |
|         └─ DangerousGetHandle() ──> HQUIC Connection Handle |
+-------------------------------------------------------------+
                               │
                               ▼
+-------------------------------------------------------------+
|                  MsQuic Native API Table                    |
|                                                             |
|  GetParam(HQUIC, Level, Param, BufferLength, Buffer)        |
|  SetParam(HQUIC, Level, Param, BufferLength, Buffer)        |
+-------------------------------------------------------------+
```

### Parameter Level and Identifiers

MsQuic parameters are queried and assigned using specific level constants and parameter flags:

| Parameter Constant | Numeric Value | Scope | Description |
| :--- | :--- | :--- | :--- |
| `QUIC_PARAM_CONN_STATISTICS_V2` | `0x05000016` | Connection | Queries comprehensive connection telemetry using the `QUIC_STATISTICS_V2` structure. |
| `QUIC_PARAM_CONN_SETTINGS` | `0x05000004` | Connection | Gets or sets connection-specific settings, including the active congestion control algorithm. |
| `QUIC_PARAM_CONN_LOCAL_ADDRESS` | `0x05000000` | Connection | Returns the native local IP endpoint (`sockaddr`). |
| `QUIC_PARAM_CONN_REMOTE_ADDRESS` | `0x05000001` | Connection | Returns the native remote IP endpoint (`sockaddr`). |
| `QUIC_PARAM_STREAM_PRIORITY` | `0x08000001` | Stream | Gets or sets a 16-bit stream priority (0 to 65535). |

---

## The QUIC_STATISTICS_V2 Structure

The `QUIC_STATISTICS_V2` structure is populated directly by MsQuic when querying `QUIC_PARAM_CONN_STATISTICS_V2`. It represents an unmanaged memory layout containing protocol telemetry:

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct QUIC_STATISTICS_V2
{
    public uint StructSize;
    public ulong CorrelationId;
    public uint VersionNegotiationDurationUs;
    public uint HandshakeDurationUs;
    public uint ConnectedDurationUs;
    public uint DisconnectedDurationUs;

    public uint SendTotalPackets;
    public uint SendRetransmittablePackets;
    public uint SendSuspectedLostPackets;
    public uint SendSpuriousLostPackets;

    public uint RecvTotalPackets;
    public uint RecvReorderedPackets;
    public uint RecvDroppedPackets;
    public uint RecvDuplicatePackets;
    public uint RecvDecryptionFailures;
    public uint RecvValidAckFrames;

    public ulong SendTotalBytes;
    public ulong SendTotalStreamBytes;
    public ulong RecvTotalBytes;
    public ulong RecvTotalStreamBytes;

    public uint SendCongestionCount;
    public uint SendPersistentCongestionCount;
    public uint SendEcnCongestionCount;

    public uint RttUs;
    public uint MinRttUs;
    public uint MaxRttUs;
    public uint RttVarianceUs;

    public uint SendCongestionWindow;
    public ushort PathMtu;

    public byte KeyUpdateCount;
    public byte DestCidUpdateCount;
    public byte HandshakeHopLimitTtl;

    public byte HandshakeClientFlight1Bytes;
    public byte HandshakeServerFlight1Bytes;
    public byte HandshakeClientFlight2Bytes;
}
```

### Key Derived Metrics

From the raw native structure, `MsQuicTuner` calculates higher-level operational metrics:
- **Round-Trip Time (ms)**: `RttUs / 1000.0`
- **Minimum / Maximum RTT (ms)**: `MinRttUs / 1000.0` and `MaxRttUs / 1000.0`
- **Packet Loss Ratio**: Calculated as `SendSuspectedLostPackets / (double)SendTotalPackets`.
- **Transmission Efficiency**: Evaluated via total bytes vs. total stream payload bytes.

---

## Dynamic Congestion Control Switching

QuicPunch allows runtime algorithm switching between CUBIC and BBR without closing or reconnecting the QUIC session.

### Native Setting Layout

In MsQuic, the congestion control algorithm is stored within `QUIC_SETTINGS`:
- **Byte Offset**: Offset 92 within `QUIC_SETTINGS`.
- **Bitfield Flag**: The `IsSet.CongestionControlAlgorithm` bit at offset 132 indicates that the algorithm field is actively specified (bitmask `0x00020000`).
- **Enumeration Values**:
  - `0`: CUBIC (Loss-based congestion control).
  - `1`: BBR (Bottleneck Bandwidth and Round-trip propagation time, model-based).

### Programmatic Invocation

```csharp
using QuicPunch.Helpers;

// Switch connection to BBR dynamically
bool success = MsQuicTuner.TrySetCongestionControl(quicConnection, QuicCongestionAlgorithm.Bbr);

// Verify active algorithm
if (MsQuicTuner.TryGetCongestionControl(quicConnection, out var currentAlgo))
{
    Console.WriteLine($"Active Congestion Algorithm: {currentAlgo}");
}
```

---

## QuicPunch High-Level Telemetry API

The core `QuicPunch` class exposes high-level telemetry models that abstract over underlying managed `QuicConnection` and `PeerInfo` instances:

```csharp
// Retrieve real-time telemetry snapshots for all active protocol sessions
IReadOnlyList<QuicSessionTelemetryInfo> sessions = quicPunch.GetActiveSessionsTelemetry();

foreach (var s in sessions)
{
    Console.WriteLine($"Session: {s.ProtocolName} with Peer: {s.PeerName}");
    if (s.Telemetry != null)
    {
        Console.WriteLine($"  RTT: {s.Telemetry.RttMs:F2} ms");
        Console.WriteLine($"  Algorithm: {s.Telemetry.CongestionAlgorithmName}");
        Console.WriteLine($"  CWND: {s.Telemetry.SendCongestionWindow} bytes");
        Console.WriteLine($"  Loss Ratio: {s.Telemetry.PacketLossRatio:P2}");
    }
}

// Dynamically change congestion control for a specific peer session
bool updated = quicPunch.TrySetSessionCongestionControl(peerId, protocolId, QuicCongestionAlgorithm.Bbr);
```

---

## Web API Reference

The embedded WebUI backend exposes REST endpoints for automated telemetry collection and monitoring dashboards:

### 1. Get Real-Time Telemetry Snapshot
- **Endpoint**: `GET /api/telemetry`
- **Response**: JSON array containing active sessions, per-peer transport metrics, and MsQuic counters.

```json
{
  "success": true,
  "timestamp": "2026-09-05T00:46:18.123Z",
  "sessionCount": 1,
  "sessions": [
    {
      "peerId": "e18bc25b-2405-4c07-aeef-bcad99385bf5",
      "peerName": "RemotePeerAlpha",
      "protocolId": "11111111-2222-3333-4444-555555555555",
      "protocolName": "Direct Chat",
      "transportType": "Wan",
      "telemetry": {
        "rttMs": 3.86,
        "minRttMs": 0.22,
        "maxRttMs": 5.61,
        "rttVarianceMs": 1.20,
        "pathMtu": 1500,
        "sendTotalPackets": 13,
        "sendRetransmittablePackets": 0,
        "sendSuspectedLostPackets": 0,
        "sendTotalBytes": 7072,
        "sendCongestionWindow": 12520,
        "recvTotalPackets": 13,
        "recvDroppedPackets": 0,
        "recvTotalBytes": 7072,
        "congestionAlgorithm": "BBR",
        "nativeLocalEndpoint": "127.0.0.1:47191",
        "nativeRemoteEndpoint": "127.0.0.1:33155",
        "cipherSuite": "TLS_AES_128_GCM_SHA256",
        "alpn": "qp-proto",
        "packetLossPercentage": 0.00
      }
    }
  ]
}
```

### 2. Update Congestion Control Algorithm
- **Endpoint**: `POST /api/quic/congestion-control`
- **Request Headers**: `Content-Type: application/json`
- **Request Body**:
```json
{
  "peerId": "e18bc25b-2405-4c07-aeef-bcad99385bf5",
  "protocolId": "11111111-2222-3333-4444-555555555555",
  "algorithm": "bbr"
}
```
- **Response**:
```json
{
  "success": true,
  "algorithm": "Bbr",
  "peerId": "e18bc25b-2405-4c07-aeef-bcad99385bf5",
  "protocolId": "11111111-2222-3333-4444-555555555555"
}
```

---

## Web Dashboard Polling Integration

The WebUI client executes automated polling against `/api/status` at 1.3-second intervals (`setInterval(refreshDashboard, 1300)`). On each tick:
1. Active session telemetry is unpacked from `quicTelemetry`.
2. The KPI cards display aggregated metrics: active connections, mean RTT, overall packet loss ratio, global congestion window volume, and total wire throughput.
3. The table lists every active QUIC link, displaying local and remote endpoints, cryptographic parameters (TLS 1.3 cipher suite and ALPN), loss statistics, and an interactive action button to toggle between BBR and CUBIC in real time.
