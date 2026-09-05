# WebUI Backend & Native Photino GUI

> QuicPunch includes an embedded HTTP/WebSocket server and Photino native desktop window, featuring modern single-page apps (SPA) for real-time node administration.

---

### WebUI Architecture Modules

| Module | File | Responsibility |
| :--- | :--- | :--- |
| [`WebUiServer`](../../QuicPunchTests/WebUi/WebUiServer.cs) | `WebUi/WebUiServer.cs` | Local `HttpListener` web server, Photino native window manager, CSRF verification. |
| [`WebUiContext`](../../QuicPunchTests/WebUi/WebUiContext.cs) | `WebUi/WebUiContext.cs` | HTTP request/response serialization helpers, security headers, body size limiters. |
| [`WebUiWebSocketHub`](../../QuicPunchTests/WebUi/WebUiWebSocketHub.cs) | `WebUi/WebUiWebSocketHub.cs` | High-throughput async WebSocket hub for real-time log, peer, and notification broadcasting. |
| [`StatusApiModule`](../../QuicPunchTests/WebUi/Modules/StatusApiModule.cs) | `WebUi/Modules/StatusApiModule.cs` | System status, WAN endpoints, Tor status, Nostr connectivity, active peers. |
| [`PeerApiModule`](../../QuicPunchTests/WebUi/Modules/PeerApiModule.cs) | `WebUi/Modules/PeerApiModule.cs` | Trusting/blocking peers, token imports, handshake authorization, clipboard sync. |
| [`ChatApiModule`](../../QuicPunchTests/WebUi/Modules/ChatApiModule.cs) | `WebUi/Modules/ChatApiModule.cs` | Chat message posting and history endpoints. |
| [`VoiceApiModule`](../../QuicPunchTests/WebUi/Modules/VoiceApiModule.cs) | `WebUi/Modules/VoiceApiModule.cs` | Voice call start, stop, mute, audio frame streaming endpoints. |
| [`LanApiModule`](../../QuicPunchTests/WebUi/Modules/LanApiModule.cs) | `WebUi/Modules/LanApiModule.cs` | Wintun adapter state, IP configuration, MTU changes, peer LAN status. |
| [`FilesApiModule`](../../QuicPunchTests/WebUi/Modules/FilesApiModule.cs) | `WebUi/Modules/FilesApiModule.cs` | RelayDrive upload, download, materialize, and delete endpoints. |
| [`AppPreferencesStore`](../../QuicPunchTests/Settings/AppPreferences.cs) | `Settings/AppPreferences.cs` | Thread-safe persistent JSON store for UI settings (`ui-settings.json`). |

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
- **`POST /api/settings/auto-accept`**: Toggles global auto-acceptance of handshakes from trusted peers.
  - Body: `{ "autoAcceptAll": true }`

### Peer Management & Interrogation
- **`POST /api/connect-token`**: Imports a Base64 endpoint token (WAN or Tor) and starts peer interrogation.
- **`POST /api/connect-peer`**: Initiates protocol handshake for a specific application with an available peer.
- **`POST /api/disconnect-peer`**: Terminates active protocol sessions and sends disconnect frames.
- **`POST /api/peer/trust`**: Promotes or revokes peer trust in the local `PeerStore`.
- **`POST /api/peer/auto-accept`**: Configures per-peer auto-accept rules.
- **`POST /api/save-peer`**: Persists peer identity and cryptographic cert hash to disk.
- **`POST /api/save-all-peers`**: Batch saves all currently connected peers.
- **`POST /api/cancel-interrogation`**: Aborts an in-flight hole punching attempt.

### Protocol Modules API
- **Chat (`ChatApiModule.cs`)**: `GET /api/chat/history`, `POST /api/chat/send`, `POST /api/chat/mark-read`.
- **Voice (`VoiceApiModule.cs`)**: `POST /api/voice/start`, `POST /api/voice/stop`, `POST /api/voice/mute`, `POST /api/voice/frame`.
- **LAN (`LanApiModule.cs`)**: `GET /api/lan/status`, `POST /api/lan/toggle`, `POST /api/lan/config`, `POST /api/lan/ip`.
- **RelayDrive Files (`FilesApiModule.cs`)**: `GET /api/files/list`, `POST /api/files/upload`, `POST /api/files/download`, `DELETE /api/files/delete`.

---

## WebSocket Real-Time Events (`WebUiWebSocketHub`)

The frontend maintains a persistent WebSocket connection (`/ws`) for instant reactive UI updates:

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
