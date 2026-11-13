# Test Harness & Integration Suite

> QuicPunch includes automated unit, security, and integration test suites covering sliding window anti-replay protection, native MsQuic tuning, RFC 9221 datagrams, port mapping cascades, STUN burst probing, Opus voice compression, and Tor mesh networking.

---

### Test Modules Directory

| Test Class / Suite | Source File | CLI Flag | Focus Area |
| :--- | :--- | :--- | :--- |
| [`AutomaticContactReconnectTests`](../../QuicPunchTests/Tests/AutomaticContactReconnectTests.cs) | `Tests/AutomaticContactReconnectTests.cs` | `--test-contact-reconnect --loopback-only` | 48-hour eligibility, real exponential UDP intervals capped at 25 seconds beyond 45 seconds, signed reappearance and rejection of invalid announcements, opt-out, WAN stop/restart. Runs for approximately one minute. |
| [Linux VPN kernel and broker](../../QuicPunch.Vpn.Tests/Program.cs) | `QuicPunch.Vpn.Tests/Program.cs` | `bash scripts/test-vpn-linux.sh` | Isolated real TUN UDP capture/injection, broadcast, MTU, routes/bypasses, forwarding restoration, cancellation/disposal, zero-allocation I/O and unprivileged Unix-socket broker reconnects. No QUIC/GUI dependencies. |
| [Linux VPN distribution matrix](../audits/vpn-linux-distros-2026-10-18.md) | `scripts/vpn-distro.Dockerfile` | `bash scripts/test-vpn-distros.sh` | Ubuntu 24.04, Arch rolling and Fedora 44 userspace, installer smoke checks and real TUN packet/broker tests in disposable Docker network namespaces. Native desktop security policies need separate VM tests. |
| [`ConnectionRecoveryTests`](../../QuicPunchTests/Tests/ConnectionRecoveryTests.cs) | `Tests/ConnectionRecoveryTests.cs` | `--test-connection-recovery` | Mailbox/Hello ordering, authenticated placeholder promotion, cancellation, and simultaneous native QUIC startup. |
| [`AntiReplayTests`](../../QuicPunchTests/Tests/AntiReplayTests.cs) | `Tests/AntiReplayTests.cs` | `--test-antireplay` | In-order, out-of-order, duplicate replay, window boundaries, multithreaded stress, and AEAD AAD binding. |
| [`SecurityDiscoveryTests`](../../QuicPunchTests/Tests/SecurityDiscoveryTests.cs) | `Tests/SecurityDiscoveryTests.cs` | `--test-security` | Untrusted candidate isolation, certificate pinning gates, token validation, and subservice lifecycles. |
| [`SecurityDiscoveryTests`](../../QuicPunchTests/Tests/SecurityDiscoveryTests.cs) | `Tests/SecurityDiscoveryTests.cs` | `--test-singleflight` | Concurrency single-flight deduplication under high-load parallel connection requests. |
| [`HandshakeCancellationLifecycleTests`](../../QuicPunchTests/Tests/HandshakeCancellationLifecycleTests.cs) | `Tests/HandshakeCancellationLifecycleTests.cs` | `--test-handshake-cancellation` | Timeout cancellation and resource cleanup during aborted handshakes. |
| [`WebUiSecurityTests`](../../QuicPunchTests/Tests/WebUiSecurityTests.cs) | `Tests/WebUiSecurityTests.cs` | `--test-webui-security` | CSRF token validation, Origin header checks, and CSP enforcement. |
| [`RotatingLoggerTests`](../../QuicPunchTests/Tests/RotatingLoggerTests.cs) | `Tests/RotatingLoggerTests.cs` | `--test-rotating-logger` | Delayed IO and disk failure, concurrent flush barriers, queue saturation, rotation, directory changes, graceful disposal and Zstandard integrity. Supports `--loopback-only`. |
| [`WatchReliabilityTests`](../../QuicPunchTests/Tests/WatchReliabilityTests.cs) | `Tests/WatchReliabilityTests.cs` | `--test-watch-reliability` | Bounded per-session/global media work, EOF and replacement cleanup, duplex range integrity and cancellation, cache quotas and room lifecycle, WAN/Tor identity separation. Uses loopback TCP and controlled streams; supports `--loopback-only`. |
| [`AutomaticPeerTransportTests`](../../QuicPunchTests/Tests/AutomaticPeerTransportTests.cs) | `Tests/AutomaticPeerTransportTests.cs` | `--test-automatic-transport` | Native QUIC without trust, application denial and permissions, tunnel reuse, capacity, retry, idle parking, revocation and lifecycle cleanup. |
| [`SessionRecoveryTests`](../../QuicPunchTests/Tests/SessionRecoveryTests.cs) | `Tests/SessionRecoveryTests.cs` | `--test-session-recovery` | General session continuation across native QUIC replacement, scoped consent, expiry, revocation during transport loss, isolation of protocols, and correlated heartbeat failure detection. |
| [`PeerAccessControllerTests`](../../QuicPunchTests/Tests/PeerAccessControllerTests.cs) | `Tests/PeerAccessControllerTests.cs` | `--test-peer-acl` | Application trust, revocation, ExpectedCertSet, and saved-identity trust. |
| [`NatPinCoordinatorTests`](../../QuicPunchTests/Tests/NatPinCoordinatorTests.cs) | `Tests/NatPinCoordinatorTests.cs` | `--test-nat-pin-coordinator` | STUN burst sampling across 32+ servers, port delta variance learning, and pinhole keepalive logic. |
| [`VirtualLanPoolingTests`](../../QuicPunchTests/Tests/VirtualLanPoolingTests.cs) | `Tests/VirtualLanPoolingTests.cs` | `--test-lan-pooling` | Deterministic IP assignment collision avoidance, subnet hashing, and frame switching. |
| [`SpeedTestTests`](../../QuicPunchTests/Tests/SpeedTestTests.cs) | `Tests/SpeedTestTests.cs` | `--test-speedtest` | Isolated and simultaneous goodput, ordered All phases, cancellation, concurrency, native/stream quality probes, actual injected loss/duplicates/rejections and legacy-peer compatibility. |
| [`QuicPerformanceTests`](../../QuicPunchTests/Tests/QuicPerformanceTests.cs) | `Tests/QuicPerformanceTests.cs` | `--benchmark-quic` / `--benchmark-quic-cubic` / `--benchmark-quic-default` | Diagnostic 10-second duplex benchmark with 80 ms added RTT and one UDP packet dropped per 500 in each direction. Prepared transport includes the real multiplexer; run each case in a fresh process. No hardware-independent speed assertion. |
| Native MsQuic Congestion Control | `Program.cs` / `MsQuicTuner.cs` | `--test-quic-bbr` | Native controller inspection; rejects live changes that would only modify the reported setting. BBR must be selected before the handshake. |
| [`QuicDatagramTests`](../../QuicPunchTests/Tests/QuicDatagramTests.cs) | `Tests/QuicDatagramTests.cs` | `--test-quic-datagrams` | RFC 9221 unbuffered datagram channel negotiation and send/receive verification. |
| [`QuicAdvancedNativeTests`](../../QuicPunchTests/Tests/QuicAdvancedNativeTests.cs) | `Tests/QuicAdvancedNativeTests.cs` | `--test-quic-telemetry` | Microsecond `QuicStatisticsV2` telemetry querying, DSCP QoS voice/video tagging, and socket address mapping. |
| [`UpnpTests`](../../QuicPunchTests/Tests/UpnpTests.cs) | `Tests/UpnpTests.cs` | `--test-upnp` | UPnP IGD SSDP M-SEARCH discovery, XML root description parsing, and SOAP port mapping. |
| [`PortOpenerTests`](../../QuicPunchTests/Tests/PortOpenerTests.cs) | `Tests/PortOpenerTests.cs` | `--test-port-openers` | Cascaded port mapping coordinator: PCP (RFC 6887) -> NAT-PMP (RFC 6886) -> UPnP IGD. |
| [`OpusCodecTests`](../../QuicPunchTests/Tests/OpusCodecTests.cs) | `Tests/OpusCodecTests.cs` | `--test-opus` | Concentus pure C# Opus 48kHz encoding/decoding, bitrate compression ratio, and in-band FEC loss concealment. |
| [`TestTorFileShare`](../../QuicPunchTests/Tests/TestTorFileShare.cs) | `Tests/TestTorFileShare.cs` | `--test-tor` | End-to-end multi-peer file transfer over Tor v3 hidden services. |

---

## Security & Discovery Test Coverage (`--test-security`)

`--test-critical-reliability` covers verified chat transfers over native QUIC (including zero-byte files), ordered writes during delayed disk I/O, sender and cancellation isolation, invalid offsets/end markers/digests, disk failures, destination collisions, persisted trust revocation, identity corruption preservation, safe native-handle lifetime, and shared incoming/outgoing session reservations. It also checks all fifteen fixed-message official BIP-340 vectors and compatibility of Nostr identity derivation.

The Ping liveness parser check uses nodes with WAN disabled, so live authenticated Hello responses cannot change `LastSeen` during its assertion. The CI workflow runs the managed reliability/security suites for managed-code changes on Windows and Linux as well as checking the native PQC handshake.

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

Each successful suite prints one line: `OK --test-sha3 (0.1s)`. A suite that only reports skips prints `SKIP`; partial skips add `skipped=N` to the result. Skip reasons and complete failure diagnostics are saved under `.artifacts/test-output/`, with the log path printed after the result. A skipped check is not evidence that the capability works.

Failures print `FAIL`, up to three distinct failure messages and a short exception message, and exit with code **1**. Unknown test flags exit with code **2**. Successful and skipped suites exit with code **0**. `--verbose` shows the complete captured log and exception stack trace. Logs preserve the original run, so debugging does not require rerunning a flaky test.

For agents and repeated verification, use the PowerShell runner. It builds once, saves build output (including warnings) to `.artifacts/test-output/build-*.log`, and prints only the requested suite results when everything passes. It continues through selected suites after a test failure and returns **1** if any fail. Only the selected suites run; network, hardware and Tor tests remain explicit choices.

```powershell
./scripts/test.ps1 -Tests output,sha3,antireplay -Web
./scripts/test.ps1 -Tests security,handshake-cancellation -NoBuild
./scripts/test.ps1 -Tests quic-telemetry -Verbose
./scripts/test.ps1 -Web
./scripts/test.ps1 -Browser
./scripts/test.ps1 -Web -Browser -BrowserFilter Voice
```

`-Tests` accepts short names or existing `--test-*` flags. `-Web` runs every `*WebTests.mjs` suite with Node.js and one result line per file. The default `Test` build configuration keeps test binaries separate from a running Debug/Release app. `-NoBuild` uses the existing binaries; omit it after changing C# source. `-Configuration Release` selects a different build configuration. `--test-output` verifies compact reporting, failure exit codes, skips, full diagnostic logs and verbose mode.

### Maintaining web tests

`QuicNet.Shared/wwwroot/ui-values.js` contains pure byte/duration formatting and port parsing/formatting. Test these functions in an empty VM context without DOM mocks. The application exposes them through `QP`; `index.html` loads this file before `app.js`.

For view tests that need the application API, use `attachApp(context)` from `QuicPunchTests/Fixtures/WebAppRuntime.mjs`. It loads the real dependencies and preserves live getters such as `isWsConnected`; supply only the IO/DOM doubles needed by the test in `context.QP`. Use `loadApp(context)` for an application runtime without overrides. Do not copy formatting or connection-policy implementations into test doubles.

Port display summaries can span gaps in a large list. Editors must keep the exact port list and validate it with `QP.parsePorts`; never submit a display summary as the original set of ports. `PortValuesWebTests` checks the pure rules and `SavedPeerEditorWebTests` checks that saving an existing sparse list preserves its ports.

Validate cleanup-rule changes with `./scripts/test-clean-project.ps1`. It exercises the real cleaner in an isolated disposable repository, including preview mode and preservation of archives, nested repositories, tracked files and the recovered-key guard. Add directory names to the rule lists in `clean-project.ps1`; keep path validation and deletion separate from selection.

### Real browser suites

Install the pinned development dependency and its Chromium build once, from the repository root:

```sh
npm ci
npm run test:browser:install
npm run test:web
npm run test:browser
node scripts/test-web.mjs --browser --filter=Voice --list
```

`-Browser` (or `npm run test:browser`) runs every `*BrowserTests.mjs` suite explicitly. `-BrowserFilter` selects browser names without filtering `-Web`. These tests use isolated HTTP fixtures and synthetic media, never the user's camera or screen. Camera tests generate their synthetic AVC fixture through `--test-platform-video`; camera and FFmpeg decode suites build the managed harness when needed. `-NoBuild` reuses that harness but still regenerates the video fixture. FFmpeg must be installed separately for the synthetic video cases; `QUICNET_FFMPEG_PATH` overrides its location. On Windows, the viewer fixture also finds WinGet installations when the shell's PATH is stale.

`BrowserFixture.mjs` owns preview startup, browser creation and shutdown. It defaults to the Chromium version installed for the locked Playwright dependency, with loopback permission for intercepted local test pages. `QUICNET_PLAYWRIGHT_MODULE`, `QUICNET_TEST_BROWSER` and `QUICNET_TEST_BROWSER_PATH` remain optional overrides. Neither microphone/camera permission nor application CSP is relaxed. The Node runner and PowerShell wrapper share discovery, output and failure logs in `.artifacts/test-output/`. No matching suites or invalid arguments return 2; a failing suite returns 1 and does not prevent the remaining selected suites from running.

Without PowerShell, build once and use `--no-build` for each subsequent run:

```bash
dotnet build QuicPunchTests --nologo --verbosity quiet
dotnet run --project QuicPunchTests --no-build -- --test-antireplay
dotnet run --project QuicPunchTests --no-build -- --test-security
```

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


## Speedtest: fases, cancelación e historial

`dotnet run --project QuicPunchTests -- --test-speedtest` comprueba subida, bajada,
simultánea y Todo sobre QUIC real, con datagramas y con sondas por stream fiable.
Incluye escrituras ralentizadas, cancelación con un frame en curso y respuestas a
sondas encoladas sin bloquear la lectura. Una cancelación de sonda solo puede
interrumpir la espera del escritor: un frame iniciado se termina con un plazo de
15 segundos para toda la escritura. Un fallo dentro del frame cierra la sesión,
porque continuar enviando corrompería su framing.

La prueba también interrumpe la conexión al empezar la tercera fase: comprueba
que las dos fases completadas, el RTT en reposo y el motivo del fallo permanecen
en el historial. Verifica por HTTP `/api/speedtest/status` y la limpieza mediante
`POST /api/speedtest/history/clear`. Se guardan hasta 50 planes en memoria durante
la sesión de la aplicación; sobreviven a recargas de la interfaz y a que el peer
desaparezca del descubrimiento. No se guardan mediciones inventadas de fases sin
completar.

`node QuicPunchTests/Tests/SpeedTestWebTests.mjs` verifica la media
`(subida + bajada) / 2` del indicador simultáneo, las velocidades independientes,
la aparición del RTT en reposo antes de terminar, los resultados parciales, la
recuperación del historial sin duplicados y su limpieza.

El RTT bajo carga incluye esperas de cola. Si las sondas comparten el stream
fiable con la transferencia, su retraso tampoco es una medida aislada del RTT
de red. Las sondas sin respuesta por este método no se presentan como pérdida
de paquetes; para ese cálculo se necesitan datagramas nativos.


## Anuncios de Android y Native AOT

`--test-discovery-announcements` arranca un proceso con
`System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false`, como la
APK Release. Genera y verifica los anuncios WAN y Tor con sus contextos
`JsonSerializable`, conserva sus cuatro propiedades originales y comprueba que
el receptor descubre y autentica cada identidad. El smoke test del EXE AOT
también genera un anuncio WAN real. Los fallos de creación del anuncio se
registran para no confundir relays conectados con publicación correcta.

## QUIC initialization and AOT regression checks

`--test-quic-optimizations` checks BBR on the **first** client and server handshake, before wrapping the server, without warming the configuration cache. It rejects live controller changes and verifies runtime settings do not attempt to change immutable ECN. It also covers unsigned loss-counter correction. `QuicNet --release-smoke-test` checks the same configuration inside the actual Native AOT single-file executable and exchanges real QUIC streams and datagrams in both directions. Merely preserving reflection metadata or reading a changed `CongestionControlAlgorithm` field is insufficient.

See [the performance audit](../audits/quic-performance-2026-10-15.md) for measurements, route limitations and reproduction commands.


`--test-mux` also verifies that stream priority reaches a QuicPunch transport without a System.Net.Quic handle. Android uses this path for the highest-priority control stream; a helper that requires .NET native handles first would silently skip it.


The native optimization suite forces a local ACK delay of 0 ms before an 8 MiB transfer with checksum verification. The outgoing ACK_FREQUENCY must still honor the peer's positive min_ack_delay. This regression fails with PROTOCOL_VIOLATION using the previous MsQuic binary and passes with msquic-ack-frequency.patch; it does not disable protocol validation at the receiver.
