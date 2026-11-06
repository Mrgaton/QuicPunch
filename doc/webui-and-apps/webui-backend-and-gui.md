# WebUI Backend & Native Photino GUI

> QuicPunch includes an embedded HTTP/WebSocket server and Photino native desktop window, featuring modern single-page apps (SPA) for real-time node administration.

---

### Local network history

Settings → Network diagnostics → Telemetry shows active physical connections, average RTT, interval packet loss, aggregate congestion window (CWND), and upload/download rates. The selector displays the last 1, 5 or 10 minutes. Hover a graph or focus it and use Left/Right, Home/End to inspect a timestamp; Escape hides the value. Current numbers and per-connection details remain available.

`network-telemetry.js` consumes the existing status stream without additional polling or chart dependencies. History stays in the current tab, is capped at 600 samples / ten minutes, and resets on reload. SVG rendering is coalesced with animation frames and stops when the diagnostics, telemetry tab, settings view or browser document is hidden. Colors follow the application theme; mobile uses stacked charts.

Transport endpoint/handshake identity deduplicates protocols sharing a physical connection. Rates use byte counter differences divided by actual elapsed seconds; loss uses interval packet differences, while the detail table retains cumulative counters. New/reset connections establish a baseline instead of generating traffic spikes. Missing telemetry and gaps over fifteen seconds leave breaks in the curves. `NetworkTelemetryWebTests.mjs` covers these calculations; `NetworkTelemetryBrowserTests.mjs` checks rendering, keyboard inspection, themes and responsive layouts with simulated data.

### Browser diagnostics

`diagnostics.js` loads before the application scripts, including in Watch. It records uncaught JavaScript errors, unhandled promise rejections, resource/media load errors, `console.error`/`console.warn`, unsuccessful HTTP responses and network failures. API helpers also report invalid JSON and logical failures; interface error notices, update actions, event callbacks, capture/encoding and avatar worker failures include their operation and original stack when available. Routine capability probes and explicit cancellations remain expected control flow.

The original browser console remains available. Diagnostic messages also use the existing authenticated WebSocket (`client_log`) to reach the native console and the application's event log; no extra HTTP polling or connection is added. A disconnected browser keeps up to 100 pending entries and forwards them after reconnect. Native forwarding groups identical entries for two seconds, limits message/stack lengths, and respects transport backpressure. The server validates each record, caps traffic per socket, and strips control characters/credentials. Forwarded diagnostics omit request bodies and URL queries, and redact common credential fields. Watch serves the same script and forwards through its own protected socket.

`DiagnosticsBrowserTests.mjs` injects failures into the isolated fixture. `--test-webui-security` verifies actual native receipt, credential redaction, payload validation, and both WebSocket security boundaries.

### Backend modules

 | Module | File | Responsibility |
| :--- | :--- | :--- |
| [`WebUiServer`](../../QuicNet.Shared/WebUi/WebUiServer.cs) | `WebUi/WebUiServer.cs` | Local `HttpListener` web server, Photino native window manager, CSRF verification. |
| [`WebUiContext`](../../QuicNet.Shared/WebUi/WebUiContext.cs) | `WebUi/WebUiContext.cs` | HTTP request/response serialization helpers, security headers, body size limiters. |
| [`WebUiWebSocketHub`](../../QuicNet.Shared/WebUi/WebUiWebSocketHub.cs) | `WebUi/WebUiWebSocketHub.cs` | High-throughput async WebSocket hub for real-time log, peer, and notification broadcasting. |
| [`StatusApiModule`](../../QuicNet.Shared/WebUi/Modules/StatusApiModule.cs) | `WebUi/Modules/StatusApiModule.cs` | System status, WAN endpoints, Tor status, Nostr connectivity, active peers. |
| [`PeerApiModule`](../../QuicNet.Shared/WebUi/Modules/PeerApiModule.cs) | `WebUi/Modules/PeerApiModule.cs` | Trusting/blocking peers, token imports, handshake authorization, clipboard sync. |
| [`ChatApiModule`](../../QuicNet.Shared/WebUi/Modules/ChatApiModule.cs) | `WebUi/Modules/ChatApiModule.cs` | Chat messages, history and file sharing endpoints. |
| [`VoiceApiModule`](../../QuicNet.Shared/WebUi/Modules/VoiceApiModule.cs) | `WebUi/Modules/VoiceApiModule.cs` | Voice call start, stop, mute, audio frame streaming endpoints. |
| [`LanApiModule`](../../QuicNet.Shared/WebUi/Modules/LanApiModule.cs) | `WebUi/Modules/LanApiModule.cs` | Wintun adapter state, IP configuration, MTU changes, peer LAN status. |
| [`AppPreferencesStore`](../../QuicNet.Shared/Settings/AppPreferencesStore.cs) | `Settings/AppPreferencesStore.cs` | Thread-safe persistent JSON store for UI settings (`ui-settings.json`). |

---

## Local Node Dashboard Layout

The node administration dashboard organizes runtime identity and network services into a balanced, dual-column structure:

| Row | Primary Left Column | Secondary Right Column |
| :--- | :--- | :--- |
| **1** | **Node Name**: Local cryptographic moniker (`glacinefrox@SFOT1NQIKH`) | **Network Type**: NAT classification (`FullCone`, `RestrictedCone`, `Symmetric`) |
| **2** | **Listener Port**: Configurable UDP port with live `Apply` rebind | **Auto-accept peers**: Global toggle to accept trusted peers automatically |
| **3** | **WAN Service**: Dynamic UDP listener toggle & live port status | **WAN Nostr Discovery**: Decentralized WAN signaling toggle & active relay counter |
| **4** | **Tor Service**: Dynamic Onion hidden service toggle & bootstrap % | **Tor Nostr Discovery**: Onion signaling toggle over Tor SOCKS5 proxy |

---

## REST API Reference

### System & Service Management
- **`GET /api/status`**: Returns comprehensive node status including `node`, `wan`, `tor`, `lan`, `discovery`, `stun`, `peers`, `discoveredPeers`, and `savedPeers`.
- **`POST /api/wan-start`**: Dynamically binds the UDP listener, initializes STUN gathering, and starts WAN discovery.
  - Body: `{ "port": 0 }` (optional custom port)
- **`POST /api/wan-stop`**: Gracefully stops the WAN UDP socket, STUN loops, and WAN discovery without stopping the overall application.
- **`POST /api/tor-start`**: Starts the embedded Tor daemon, opens a v3 Hidden Service, and initializes Tor transport.
  - Body: `{ "port": 443 }` (optional virtual port)
- **`POST /api/tor-stop`**: Gracefully stops Tor hidden services and closes active Tor connections.
- **`POST /api/tor-newnym`**: Requests new Tor circuits from the Tor control port (`SIGNAL NEWNYM`).
- **`POST /api/tor-connect`**: Connects directly to a remote `.onion` address.
  - Body: `{ "onion": "<v3-address>", "port": 443 }`
- **`POST /api/discovery`**: Toggles Nostr signaling plane independently for WAN or Tor.
  - Body: `{ "enabled": true, "type": "wan" | "tor" }`
- **`POST /api/change-listener-port`**: Rebinds the active WAN listener socket to a new port on the fly.
  - Body: `{ "port": 54321 }`

### Peer Management & Interrogation
- **`POST /api/peer/connect`**: Explicitly connects or wakes a peer transport without granting application trust. Discovered peers normally connect automatically.
- **`POST /api/peer/trust`**: Changes application trust while retaining the existing transport. Revocation closes application sessions.
- **`POST /api/connect-token`**: Imports a Base64 endpoint token (WAN or Tor) and starts peer interrogation.
- **`POST /api/connect-peer`**: Initiates protocol handshake for a specific application with an available peer.
- **`POST /api/disconnect-peer`**: Terminates active protocol sessions and sends disconnect frames.
- **`POST /api/peer/trust`**: Promotes or revokes peer trust in the local `PeerStore`.
- **`POST /api/save-peer`**: Persists peer identity and cryptographic cert hash to disk.
- **`POST /api/save-all-peers`**: Batch saves all currently connected peers.
- **`POST /api/cancel-interrogation`**: Aborts an in-flight hole punching attempt.

### Protocol Modules API
- **Chat (`ChatApiModule.cs`)**: `GET /api/chat/history`, `POST /api/chat/send`, `POST /api/chat/mark-read`.
- **Voice (`VoiceApiModule.cs`)**: `POST /api/voice/start`, `POST /api/voice/stop`, `POST /api/voice/mute`, `POST /api/voice/frame`.
- **LAN (`LanApiModule.cs`)**: `GET /api/lan/status`, `POST /api/lan/toggle`, `POST /api/lan/config`, `POST /api/lan/ip`.
- **Chat files (`ChatApiModule.cs`)**: `POST /api/chat/share-file`, `POST /api/chat/download-file`, `GET /api/chat/view-file`.

---

## WebSocket Real-Time Events (`WebUiWebSocketHub`)

The frontend maintains a persistent WebSocket connection (`/ws`) for instant reactive UI updates:

General status and updater control/progress share this authenticated connection. Each connection receives `status_updated` and (when available) `update_status` snapshots automatically. The frontend never polls `/api/status` or `/api/updates`, including while disconnected; reconnect uses one timer with backoff and receives fresh snapshots. Simultaneous explicit status refreshes share a single request. Updater revisions prevent an older response from overwriting newer progress.

Commands use `{type:"request", id, command, data}` and correlated `{type:"response", id, success, data, error}` replies. Supported commands are `status.get`, `updates.get`, `updates.check`, `updates.install`, `updates.automatic` (`enabled`) and `updates.channel` (`channel`). Errors remain explicit; disconnected mutation requests are rejected instead of replayed. The existing Origin/CSRF checks protect the WebSocket upgrade, and command payloads are validated and limited to 16 KiB. Replies use source-generated JSON metadata for Android and Native AOT.

One bounded state signal coalesces event bursts and download progress over 75 ms, reads the latest snapshot, and skips identical per-client state. Slow text sends expire after five seconds so one stalled tab cannot grow an unbounded status queue. General telemetry is still sampled on the server every 2.5 seconds while clients are connected; no browser requests are needed for those samples. Existing HTTP endpoints remain available for compatibility and diagnostics. CDN manifest discovery and binary downloads continue over HTTPS; a static CDN does not provide a WebSocket publication feed.

| Event Name | Payload Summary | Trigger Condition |
| :--- | :--- | :--- |
| `wan_started` | `{ "port": 54321 }` | WAN UDP listener successfully started. |
| `wan_stopped` | `{}` | WAN UDP listener stopped. |
| `tor_started` | `{ "onion": "abc...xyz.onion" }` | Tor hidden service published. |
| `tor_stopped` | `{}` | Tor service terminated. |
| `peer_discovered`| `{ "peer": {...} }` | New candidate discovered via Nostr or LAN. |
| `peer_connected` | `{ "peerId": "...", "protocol": "..." }` | QUIC or Tor session established. |
| `peer_disconnected` | `{ "peerId": "..." }` | Session closed or timed out. |
| `log_event` | `{ "message": "...", "level": "..." }` | Core diagnostic log emitted. |

---

## WebUI Security Architecture

Because the WebUI runs a local HTTP listener on `127.0.0.1`, strict security defenses prevent malicious websites from hijacking the local API:

1. **Anti-CSRF Tokens**: Every state-changing `POST` request must include an `X-QuicPunch-CSRF` header containing a 32-byte cryptographically secure token injected at page load.
2. **Origin Restriction**: Rejects any request whose `Origin` header does not strictly match `http://127.0.0.1:<port>`.
3. **Content Security Policy (CSP)**:
   ```http
   Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; media-src 'self' data: blob:; connect-src 'self' ws: wss:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'
   X-Content-Type-Options: nosniff
   Referrer-Policy: no-referrer
   ```
4. **URL Protocol Scheme (`QP://`)**: Registers custom Windows URI scheme `QP://<token>` allowing one-click P2P connections from web browsers.

## Peer speed and connection quality

`POST /api/speedtest/start` accepts `upload`, `download`, `both` (simultaneous), or `all`. The default `all` runs download, upload, then simultaneous traffic, with the selected duration applied to each phase. Results and session history retain each phase separately. `GET /api/speedtest/status` restores the current phase, completed phases, idle baseline, and last result after navigation. Cancellation covers connection setup, baseline measurement, and remaining phases.

The baseline uses ten probes before bulk traffic; probes continue during each traffic phase. RTT min/mean/max and jitter (mean absolute difference between successive measured RTTs) come from probe replies. Native QUIC DATAGRAM probes report round-trip loss as accepted probes without a reply within one second; local send rejection is counted separately, and duplicates do not inflate received counts. This cannot distinguish upload loss from download loss. Reliable-stream/Tor fallback measures latency with application echo frames and leaves loss unavailable. An initial reliable echo verifies support so older peers are not misreported as 100% loss. Insufficient samples remain unavailable instead of becoming fabricated zeroes.
