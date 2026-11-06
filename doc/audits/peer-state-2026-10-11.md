# Discovery availability and QUIC connection state

Follow-up: the later paired logs identify a mailbox placeholder preventing authenticated Hello admission. See [connection recovery review](connection-recovery-2026-10-11.md) for the reproduced cause, promotion fix, and coordination of simultaneous startup during candidate preparation.

The attached console trace discovers GUARRIDONGO by UDP at 17:41:35.284. Two outgoing negotiations then finish after approximately 25 seconds with the old message `Handshake declined by peer.` The handshake manager previously returned the same negative decision on timeout and explicit rejection, so this message does not prove the remote peer declined. The trace lacks enough evidence to identify why those requests received no accepted response.

The subsequent incoming negotiation is accepted at 17:42:56.355. NAT connectivity succeeds at 17:42:56.386 and the native QUIC client connects at 17:42:56.717. The first QUIC bind attempt hits address-in-use and the existing 50 ms retry recovers. These timings locate the long wait before native QUIC establishment, in discovery/control negotiation.

## State contract

- `peers` and its existing compatibility alias `availablePeers` remain the directory of responsive authenticated identities. Applications can use these to initiate a connection.
- `connectedPeers` includes only identities whose QUIC multiplexer exists and is not disposed.
- Each peer exposes `connectionState`: `available`, `connecting`, or `connected`; `isQuicConnected` remains supported.
- `quicEndpoint` describes the data path separately from the discovery/control `endpoint`.
- `lastConnectionError` and `lastConnectionAttemptUtc` expose the latest outbound attempt. The error is hidden while connected.
- Cached QUIC telemetry is not shown as live RTT after disconnect. Missing RTT is displayed as unavailable.
- The dashboard has separate connected and available tables. Save-all in the connected section saves connected trusted identities only.

## Maintenance and negotiation fixes

The maintenance loop formerly transmitted unauthenticated UDP pings while the security handler intentionally ignored their responses. It now probes inactive discovery identities with a signed Hello challenge at most once per 20 seconds. Only authenticated replies refresh availability. Active QUIC tunnels continue using their own transport maintenance.

Discovery availability is named `IsAvailableAndResponsive`; the old public name is retained as a documented compatibility alias. The status endpoint does not schedule disconnects merely by being read; background maintenance owns expiry.

Outgoing negotiation timeout and local cancellation now carry distinct reasons instead of masquerading as remote rejection. A duplicate tunnel request received while a multiplexer is already active now gets an explicit signed decline, cached for retransmission, rather than silently forcing the requester to wait 25 seconds. Glare arbitration continues to preserve its existing winner/yield rules. Authenticated handshakes discarded because their source differs from the validated control endpoint produce routing diagnostics; source validation remains enforced.

## Validation

Run `dotnet run --project QuicPunchTests -c CodexVerification -- --test-peer-state` for timeout, cancellation, HTTP status, and signed keepalive tests over real UDP sockets. Run `node QuicPunchTests/Tests/PeerConnectionStateWebTests.mjs` for dashboard discovery/connection/disconnect transitions, pending buttons, data endpoint, and missing RTT.

Connectivity security, handshake cancellation, WebUI security, and the complete native QUIC multiplexer suite also pass. The multiplexer fixture now explicitly approves its three test application protocols, matching the existing separation between authenticated transport and application permissions. Desktop/test harness and Android builds succeed with existing warnings. This does not establish the cause of the unanswered WAN negotiations in the supplied trace; reproducing with logs from both peers is still necessary to identify that cause.
