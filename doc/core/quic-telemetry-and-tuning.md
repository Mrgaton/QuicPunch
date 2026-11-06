---
title: MsQuic Native Telemetry and Transport Tuning
description: Native MsQuic telemetry, XDP policy, and configuration before connection startup.
ms.date: 10/23/2026
ms.topic: conceptual
ms.service: quicpunch
---

# MsQuic Native Telemetry and Transport Tuning

This document describes native QUIC telemetry and configuration before connection startup.

## XDP datapath

Windows x64 now requests XDP for the QUIC connection itself, including streams and RFC 9221 datagrams. The pinned MsQuic implements **XDP for Windows**, not Linux AF_XDP. Linux and Android retain their OS UDP datapaths. See the pinned upstream [XDP documentation](https://github.com/microsoft/msquic/blob/80a065112426bce68c1da42d026478d3e40fd45e/docs/XDP.md).

`MsQuicNativeLoader` initializes the tuner after loading the native library. The tuner sets `QUIC_PARAM_GLOBAL_SETTINGS.XdpEnabled` before the first listener binds: listeners select their datapath from global settings, before the server configuration callback. Client configurations also explicitly receive the XDP policy before connection startup. In the pinned ABI, `IsSet.XdpEnabled` is bit 43 at offset 0 and `XdpEnabled` is bit 6 in `Flags` at offset 120. Active-connection tuning does not change the datapath.

MsQuic retains its normal socket fallback when the driver, permissions, adapter, or route cannot use XDP. It still reserves an OS UDP port even when XDP carries packets. An ordinary socket's existence alone therefore does not prove that bypass is inactive.

Install the production XDP runtime from an **Administrator PowerShell 7**:

```powershell
./scripts/install-quic-xdp.ps1
```

The installer pins Microsoft's production runtime 1.4.0 to a SHA-256 digest and validates the driver/catalog signatures. `-PrepareOnly` downloads and verifies without installing. It does not enable test signing or change Secure Boot. Downloads are disposable under `.artifacts/`; Windows installs the runtime in its own driver store/system directories. Restart QuicNet after installation so MsQuic initializes against the new driver. Direct rule programming also requires elevated permission for QuicNet; separating rule programming into a privileged XDP maps broker is not implemented here.

Set `$env:QUICNET_XDP='0'` **before launching the process** to disable the request. The default on Windows is enabled. No machine-wide MsQuic registry values are changed.

`MsQuicTuner.IsXdpRequested`, `TryGetXdpEnabled`, and the `xdpRequested` field in `/api/status` and `/api/telemetry` report the native **request setting**, not actual bypass. A true value can coexist with socket fallback. Validate an active XDP route with MsQuic/XDP tracing and compare throughput, loss, latency, and CPU on the same physical path; loopback tests cannot establish NIC bypass or an XDP speedup.

## Performance review (2026-10-23)

- Existing configuration already enables BBR before connection startup, pacing, ECN, PMTU discovery, 16 MiB connection and 4 MiB stream receive windows, and round-robin scheduling. These are preserved. The [earlier performance audit](../audits/quic-performance-2026-10-15.md) explains why setting BBR after connecting was ineffective.
- Native QUIC datagram sends now place `QUIC_BUFFER` and payload in one allocation, reducing native allocation/free calls from two to one per datagram. The buffer is still tracked until MsQuic's send callback; failed sends and callbacks share a removal path to prevent double frees. This reduces allocator work; no end-to-end speedup is claimed without comparative measurement.
- Busy polling, forced CPU affinity, larger windows, larger MTUs, disabled send buffering, and crypto offload are not enabled speculatively. Measure CPU saturation, bandwidth-delay product, route MTU, queueing, and hardware support first. Keep pacing and bounded queues so bulk transfers do not overwhelm interactive traffic.
- `--test-quic-optimizations` checks global policy before listener binding, first-client/server native settings, status serialization, and an 8 MiB SHA-256-verified stream transfer. Run it in fresh processes both with the default environment and with `QUICNET_XDP=0`. `--test-quic-datagrams` verifies concurrent variable-size payloads and completion cleanup. `--benchmark-quic` exercises the real multiplexor over an artificial 80 ms path with periodic loss; it is not a physical-NIC XDP benchmark.

## Overview

QuicPunch utilizes `System.Net.Quic` backed by the native MsQuic library. While the high-level .NET managed abstractions provide stream and datagram I/O, enterprise diagnostics, quality of service (QoS) enforcement, and congestion control tuning require direct access to the underlying `HQUIC` connection handle and native MsQuic API tables.

The `MsQuicTuner` component establishes a reflection bridge into `System.Net.Quic` internals, exposing:
- Microsecond-precision round-trip time (RTT), minimum RTT, maximum RTT, and RTT variance.
- Comprehensive transmission statistics, including packet loss ratios, retransmissions, spurious losses, and congestion window (CWND) sizes.
- Reception diagnostics, including packet reordering, drops, duplicate frames, and decryption failures.
- Cryptographic handshake metrics, TLS cipher suites, ALPN tokens, and key rotation counts.
- Congestion control selected before connection startup, with readback on live connections.

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
| `QUIC_PARAM_CONN_LOCAL_ADDRESS` | `0x05000001` | Connection | Returns the native local IP endpoint (`sockaddr`). |
| `QUIC_PARAM_CONN_REMOTE_ADDRESS` | `0x05000002` | Connection | Returns the native remote IP endpoint (`sockaddr`). |
| `QUIC_PARAM_STREAM_PRIORITY` | `0x08000003` | Stream | Gets or sets a 16-bit stream priority (0 to 65535). |

---

## The QUIC_STATISTICS_V2 Structure

The native layout is defined by `QuicStatisticsV2` in `QuicPunch/Helpers/MsQuicTuner.cs`, matching the pinned `msquic.h`. Use that definition rather than copying an abbreviated structure: native field widths, ordering, alignment, and returned buffer length must agree. `TryGetTelemetry` maps the raw values to `QuicConnectionTelemetry`.

### Key Derived Metrics

From the raw native structure, `MsQuicTuner` calculates higher-level operational metrics:
- **Round-Trip Time (ms)**: `RttUs / 1000.0`
- **Minimum / Maximum RTT (ms)**: `MinRttUs / 1000.0` and `MaxRttUs / 1000.0`
- **Packet Loss Ratio**: Calculated as `SendSuspectedLostPackets / (double)SendTotalPackets`.
- **Transmission Efficiency**: Evaluated via total bytes vs. total stream payload bytes.

---

## Congestion Control Selection

Select BBR or CUBIC on the configuration before connecting. A live `TrySetCongestionControl` call only accepts an idempotent request for the existing algorithm; changing it requires a new connection. Updating the stored native field after startup does not reinitialize MsQuic's active controller.

### Native Setting Layout

In MsQuic, the congestion control algorithm is stored within `QUIC_SETTINGS`:
- **Byte Offset**: Offset 92 within `QUIC_SETTINGS`.
- **Bitfield Flag**: Bit 17 in `IsSetFlags` at offset 0 marks the algorithm field as explicitly specified (bitmask `0x00020000`).
- **Enumeration Values**:
  - `0`: CUBIC (Loss-based congestion control).
  - `1`: BBR (Bottleneck Bandwidth and Round-trip propagation time, model-based).

### Programmatic Invocation

```csharp
using QuicPunch.Helpers;

// This succeeds only if the connection already uses BBR.
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
3. The table lists active QUIC links with endpoints, cryptographic parameters, and loss statistics. Changing a live connection's congestion algorithm is rejected; reconnect using a new configuration instead.
