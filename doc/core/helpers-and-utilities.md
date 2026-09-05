# Helpers & Cryptographic Utilities

> Comprehensive reference of helper utilities, time synchronization engines, native MsQuic reflection tuners, datagram pipelines, audio codecs, and persistent peer databases.

---

### Helper Modules Directory

| Helper Class | File | Responsibility |
| :--- | :--- | :--- |
| [`MsQuicTuner`](../../QuicPunch/Helpers/MsQuicTuner.cs) | `Helpers/MsQuicTuner.cs` | Native MsQuic reflection tuner: dynamic BBR congestion control, DSCP QoS voice/video tagging, microsecond stats. |
| [`MsQuicDatagramChannel`](../../QuicPunch/Helpers/MsQuicDatagrams.cs) | `Helpers/MsQuicDatagrams.cs` | RFC 9221 unbuffered datagram channel and native configuration cache patcher for real-time audio/video. |
| [`RotatingFileLogger`](../../QuicPunch/Helpers/RotatingFileLogger.cs) | `Helpers/RotatingFileLogger.cs` | Asynchronous channel-backed file logger with 50MB limits, daily rolling, and gzip archiving. |
| [`OpusVoiceCodec`](../../QuicPunch/Helpers/OpusVoiceCodec.cs) | `Helpers/OpusVoiceCodec.cs` | Concentus pure C# Opus 48kHz encoder/decoder with adaptive bitrate and in-band Forward Error Correction (FEC). |
| [`AntiReplayWindow`](../../QuicPunch/Helpers/AntiReplayWindow.cs) | `Helpers/AntiReplayWindow.cs` | RFC 6479 128-packet sliding bitmap anti-replay filter. |
| [`CertManager`](../../QuicPunch/Helpers/CertManager.cs) | `Helpers/CertManager.cs` | ECDSA certificate lifecycle, PKCS#12 bundle storage (`QPID` format), Nostr secp256k1 keys. |
| [`CompressedTransparentStream`](../../QuicPunch/Helpers/CompressedTransparentStream.cs) | `Helpers/CompressedTransparentStream.cs` | Transparent two-way stream wrapper with Zstandard compression. |
| [`HandshakeManager`](../../QuicPunch/Helpers/HandshakeManager.cs) | `Helpers/HandshakeManager.cs` | Asynchronous task completion tracking for protocol requests. |
| [`IpRateLimiter`](../../QuicPunch/Helpers/IpRateLimiter.cs) | `Helpers/IpRateLimiter.cs` | Per-IP sliding time window (1-sec window) rate limiter with bounded dictionary capacity. |
| [`PeerStore`](../../QuicPunch/Helpers/PeerStore.cs) | `Helpers/PeerStore.cs` | Thread-safe, cross-process atomic JSON database with multi-index lookups. |
| [`PreciseTime`](../../QuicPunch/Helpers/PreciseTime.cs) | `Helpers/PreciseTime.cs` | NTP clock synchronization with `pool.ntp.org:123` and monotonic offset tracking. |
| [`QuicPunchLog`](../../QuicPunch/Helpers/QuicPunchLog.cs) | `Helpers/QuicPunchLog.cs` | Centralized logging subsystem with delegate redirection. |
| [`Utilities`](../../QuicPunch/Helpers/Utilities.cs) | `Helpers/Utilities.cs` | Token packing/unpacking, Base32/Base64Url, BigSendAsync UDP broadcast helper. |

---

## Native MsQuic Tuning (`MsQuicTuner`)

[`MsQuicTuner`](../../QuicPunch/Helpers/MsQuicTuner.cs) uses high-performance reflection into .NET 11 `System.Net.Quic` internals (`MsQuicApi`, `<ApiTable>k__BackingField`, and `_handle`) to manipulate native MsQuic parameters on both **Linux** and **Windows**:

### Capabilities
1. **Dynamic Congestion Control Algorithm (BBR / Cubic)**:
   - Queries current algorithm via `GetCongestionControlAlgorithm(connection)`.
   - Modifies algorithm in-flight using `SetCongestionControlAlgorithm(connection, CongestionControlAlgorithm.BBR)`.
   - Patches `QUIC_SETTINGS` struct (144 bytes, offset 92, bitfield mask `0x00020000`).
2. **Differentiated Services Code Point (DSCP) QoS Tagging**:
   - Sets IP DSCP flags on underlying UDP sockets for enterprise QoS:
     - `DscpTag.Voice_EF` (Expedited Forwarding, DSCP 46, hex `0xB8`).
     - `DscpTag.Video_AF41` (Assured Forwarding 41, DSCP 34, hex `0x88`).
3. **Microsecond Telemetry (`QuicStatisticsV2`)**:
   - Reads exact connection metrics: RTT, MinRTT, send/receive throughput, packet loss counts, retransmission rates, and congestion window (CWND) in bytes.
4. **Cross-Platform Socket Address Translation**:
   - Accurately converts `sockaddr_in` and `sockaddr_in6` between Linux (`AF_INET6 = 10`) and Windows (`AF_INET6 = 23`).

---

## Opus Audio Compression (`OpusVoiceCodec`)

[`OpusVoiceCodec`](../../QuicPunch/Helpers/OpusVoiceCodec.cs) provides real-time voice compression using Concentus (pure C# RFC 6716 Opus implementation):
- **48 kHz VoIP Processing**: Captures and renders at 48,000 Hz, 16-bit mono.
- **Bandwidth Reduction**: Yields 80-90% data reduction compared to raw PCM audio (~64 kbps vs 768 kbps).
- **Forward Error Correction (FEC)**: Encodes redundant voice packets in-band (`UseInbandFEC = true`), allowing the decoder to reconstruct dropped audio frames even with 10% packet loss.
- **Packet Loss Concealment (PLC)**: Interpolates waveform smoothing when consecutive datagrams are lost.

---

## Asynchronous Rotating File Logger (`RotatingFileLogger`)

[`RotatingFileLogger`](../../QuicPunch/Helpers/RotatingFileLogger.cs) provides non-blocking disk logging:
- **Unbounded Async Channel**: Worker thread pulls log messages from a memory channel without blocking network threads.
- **50 MB File Size Cap & Rolling**: Automatically rotates files upon reaching 50 MB or at midnight UTC (`quicpunch_YYYY-MM-DD_NN.log`).
- **Background Gzip Compression**: Automatically compresses previous day's inactive logs into `.log.gz` archives with `CompressionLevel.Optimal`.

---

## Atomic Peer Storage (`PeerStore`)

[`PeerStore.cs`](../../QuicPunch/Helpers/PeerStore.cs) provides persistent storage for trusted peers with multi-field indexing:

### Key Features
1. **Cross-Process File Locking**: Uses `.db.lock` with retry exponential backoff.
2. **Atomic Temp-File Swapping**: Writes to `.db.tmp`, flushes to disk, and calls `File.Replace`.
3. **Multi-Index Lookups**:
   - `ByCertHash`: Hex-encoded SHA3-256 certificate fingerprint.
   - `ByIpPort`: `IPEndPoint` string mapping.
   - `ByNostrKey`: 32-byte hex Nostr public key.
   - `ByOnion`: 56-character `.onion` service ID.
4. **Change Debouncing**: Debounces `FileSystemWatcher` events to reload cache on external process updates.

---

## ⏰ NTP Clock Synchronization (`PreciseTime`)

Because decentralized authentication packets require clock-skew verification (`±60s`), [`PreciseTime.cs`](../../QuicPunch/Helpers/PreciseTime.cs) synchronizes with NTP servers:
- Queries `pool.ntp.org` on UDP port `123` using standard 48-byte NTP binary packet.
- Computes monotonic time offset: `_offset = ntpTime - localTime`.
- `PreciseTime.GetCorrectTime()` returns `DateTime.UtcNow + _offset`.
