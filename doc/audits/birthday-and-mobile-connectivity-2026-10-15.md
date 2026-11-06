# Birthday traversal and mobile/PC connectivity audit â€” 2026-10-15

The installed Build 11 clients could establish a public QUIC path during this
session. A two-second simultaneous speed test completed: 186.65 Mbps upload,
243.53 Mbps download, 107,544,576 bytes moved. This was a short connectivity
check, not a sustained throughput benchmark. The speed-test request on the
user's own phone was accepted for this test only; saved application permissions
were not broadened.

Direct LAN probes failed outside QuicNet as well: phone â†’ PC UDP and PC â†’ phone
UDP test markers timed out, and ICMP did not succeed. Both devices resolved each
other's link-layer address. The PC has an active WireGuard tunnel with a default
IPv4 route. This is consistent with WireGuard's documented `/0` kill-switch
behavior, but the tunnel configuration was not inspected and causality was not
proved by disabling it. No VPN settings were changed. Reference:
[WireGuard for Windows networking behavior](https://git.zx2c4.com/wireguard-windows/about/docs/netquirk.md).

The phone's actual logs confirm successful UPnP control and QUIC port mappings.
The PC's UPnP discovery repeatedly times out. Candidate preparation was repeating
that failed discovery for data sockets under a shared mapping gate, adding delay.
The code now avoids that repeat when no control mapping has succeeded and prefers
an actual public router lease over an inferred STUN range.

The port search also reused the same deterministic grid. It now rotates through
ports with a bounded schedule and supports a bounded many-socket QUIC traversal
pool. Implementation, guarantees and limitations are documented in
[connection and hole punching](../core/connection-and-holepunch.md).

Local test distribution: 1.1.10, Build 12, Windows single-file NativeAOT and
same-signature Android ARM64 release APK. Both retain the original user identities.
Published to the CDN after both immutable artifacts and their signed manifest passed verification; see [release 1.1.10](../deployment/releases/1.1.10.md).

Artifacts under `.artifacts/`: `birthday-test.log`, `birthday-final-tests.log`,
`birthday-native-smoke.log`, `birthday-data-baseline.json`,
`birthday-device-connect.json`, `birthday-phone-restart.json` and build logs.


Additional restart findings: the initial Android network callback was cancelling
in-flight negotiations; duplicate route notifications are now filtered. A retained
old tunnel caused explicit handshake declines and 5/10-second backoff on the
fresh mobile instance. Replacement requests now test that old tunnel's actual
liveness and proceed on the same request if it is silent. The restart regression
and native birthday handoff test passed, along with connectivity security,
critical reliability, logical session recovery, automatic transport, connection
recovery, and AOT JSON checks. The broader security suite passed before this last
restart-specific change; the targeted recovery tests cover the final change.


Final device verification: paired cold startup connected in 8.93 seconds; restarting
only the Android application connected in 7.66 seconds. The final two-second
simultaneous test completed at 125.04 Mbps upload and 242.75 Mbps download,
91,947,008 bytes, with 16/16 datagram probes answered and no send failures.
These are short connectivity checks, not sustained throughput guarantees.
The user disconnected the phone after these tests; no later phone verification
is claimed. The final standalone AOT smoke, update tests, LAN port tests, LAN
discovery and web security/profile tests passed before publication. Full HTTPS
downloads matched both signed artifact hashes; older Windows and Android
update clients discovered Build 12.
