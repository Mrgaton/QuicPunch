# Connection Establishment & UDP Hole Punching

> QuicPunch achieves direct P2P connectivity through stateful NATs using an ICE-inspired candidate gathering and racing algorithm, followed by mTLS-secured QUIC tunnel establishment.

---

## Complete Hole Punching & QUIC Sequence

```mermaid
sequenceDiagram
    autonumber
    participant App as Application Layer
    participant QP as QuicPunch Engine
    participant Stun as STUN Servers
    participant Peer as Remote Peer

    Note over QP: Discovery authenticates identity, without granting trust
    QP->>QP: EnsureMultiplexerConnectedAsync(RemotePeer)
    QP->>Stun: GatherCandidatesAsync() (Host, ServerReflexive)
    Stun-->>QP: List<CandidateEndpoint> (e.g. 192.168.1.50:54321, 203.0.113.10:48900)
    QP->>Peer: NegotiateConnection(Guid.Empty, Candidates)
    Peer-->>QP: HandshakeDecision.Accepted(RemoteCandidates, Port)

    Note over QP,Peer: SIMULTANEOUS UDP HOLE PUNCHING (OpenPortCore)
    par Burst Probe to Remote Candidates
        QP->>Peer: Send FinalHandshake ('F') bursts to all candidates
    and Listen on Bound Socket
        Peer->>QP: Send FinalHandshake ('F') bursts
        QP-->>Peer: Send Ack ('A') bursts
    end
    Note over QP: First valid response nominates Peer-Reflexive Endpoint!

    Note over QP,Peer: ROLE RESOLUTION (AmIServer)
    alt Local Guid > Remote Guid (Local is SERVER)
        QP->>QP: Dispose UDP socket & Bind QuicListener on same port
        QP->>Peer: Send QuicReady ('Q') packet with listening port
        QP->>QP: QuicListener.AcceptConnectionAsync()
    else Local Guid < Remote Guid (Local is CLIENT)
        QP->>Peer: WaitForQuicReadyAsync()
        Peer-->>QP: QuicReady ('Q') signal received
        QP->>Peer: QuicConnection.ConnectAsync() with mTLS pinned cert
    end

    Note over QP,Peer: QUIC TLS 1.3 Handshake completes!
    Note over QP,Peer: Transport ready; applications still require trust and permission
    App->>QP: InitQuicConnection(ProtocolId, RemotePeer)
    QP->>Peer: Request authorized virtual application session
    QP->>App: handler.HandleAsync(virtualConnection, virtualStream, peer)
```

---

## Step-by-Step Code Walkthrough

### 1. Candidate Gathering in [`SimpleStunClient.GatherCandidatesAsync`](../../QuicPunch/Discovery/SimpleStunClient.cs)
- Binds a UDP socket with `SocketOptionName.ReuseAddress = true`.
- Concurrently queries multiple public STUN servers (e.g., `stun.l.google.com:19302`, `stun1.l.google.com:19302`).
- Assembles local host interfaces and server-reflexive public IP/ports, filtering out bogon/loopback IPs.

### 2. Simultaneous Hole Punching in [`QuicPunchConnection.OpenPortCore`](../../QuicPunch/QuicPunchConnection.cs)
- Spawns background task `SendLoopAsync`, sending `FinalHandshake` packets to all target endpoints every `125ms`.
- Awaits first matching packet in `ReceiveHoleLoopAsync`.
- Responds with 4 `Ack` bursts and nominates the winning `remoteEndpoint`.

### 3. Server Listener Handoff in [`QuicPunchConnection.TryRunServer`](../../QuicPunch/QuicPunchConnection.cs)
- Disposes the initial hole-punch UDP socket.
- Binds `QuicListener` on `IPAddress.Any` and `localPort`.
- Sends signed `QuicReady` message to remote peer via `SendQuicReadyAsync`.
- Accepts native inbound QUIC connection and bidirectional stream.

### 4. Client Connection in [`QuicPunchConnection.TryRunClient`](../../QuicPunch/QuicPunchConnection.cs)
- Awaits `WaitForQuicReadyAsync` notification from the server.
- Connects to server endpoint with exponential backoff (`50ms` -> `400ms`).
- Injects client certificate in `ClientAuthenticationOptions`.
- Validates server certificate hash in `RemoteCertificateValidationCallback`.

---

## RFC 9221 Unbuffered Datagrams (`MsQuicDatagramChannel`)

For ultra-low-latency, real-time protocols such as Voice Calls, head-of-line blocking from reliable QUIC streams is unacceptable. QuicPunch integrates [`MsQuicDatagramChannel`](../../QuicPunch/Helpers/MsQuicDatagrams.cs), enabling **RFC 9221 Unreliable QUIC Datagrams** on top of `System.Net.Quic` in .NET 11:

- **Native MsQuic API Table Interception**: Hooks into `DatagramSend` and `NativeCallback` via native function pointers.
- **Dynamic Configuration Patching**: Patches `QUIC_SETTINGS.DatagramReceiveEnabled` directly in the native configuration cache so that both endpoints negotiate datagram capability during the initial TLS 1.3 handshake.
- **Unbuffered Async Channels**: Received datagrams are queued directly into high-throughput, lock-free `Channel<byte[]>` reader pipelines.
- **Zero Head-of-Line Blocking**: Packet loss drops the audio frame without stalling subsequent audio frames, providing instantaneous, glitch-free voice communications.


## LAN control ports and eight-port STUN advertisements (2026-10-15)

The host binds one UDP control socket. Its actual bound port is `PeerInfo.PreferredPort`
(the historical property name); it is independent of externally observed NAT ports.
No STUN query is needed for the host's LAN address. IPv4 private/link-local addresses
with a known host port are probed at that exact port. They do not inherit WAN port
lists or sampled ranges. For QUIC data sockets, explicit `CandidateType.Host`
endpoints retain their own address/port pairs instead of inheriting the control port
or another candidate's external port. Same-subnet destinations are tried first.

STUN observations use `Single` for one distinct port, `Multiple` for two through
eight distinct ports, and compact `Range` bounds starting at nine. Hit frequency
cannot discard a rare observed port or turn eight ports into a broad range.
Ranges do not allocate or scan every integer: endpoint budgets remain 32 for
control interrogation and 64 for transport probing. LAN beacons advertise the
bound control port directly. A broad WAN range remains an inference, not a router
reservation or a guarantee that an arbitrary port within it is reachable.

Control framing changes from `PNch` to `PNci`; both applications must be updated.
This prevents an old three-port reader from parsing the extended discovery format.
ConnectionFlags bit 4 (`HasHostPort`) appends a nonzero UInt16 host control port
after the public port advertisement, before the certificate hash in endpoint tokens
and ACK shared peers, or before the name in Hello packets. Bit 3 retains counted
Multiple framing in discovery. Endpoint tokens use bit 4 in all three port modes;
legacy Range tokens with bit 3 and a preferred port are still readable. Invalid
or truncated host ports are rejected. The signed Nostr token covers the host port;
no unauthenticated JSON field is used for routing. The Tor token layout is unchanged
and carries no host/LAN port. Stored contacts retain the host port separately and
do not merge a LAN endpoint into public NAT range bounds.

Android uses the same PCP ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ NAT-PMP ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ UPnP mapping stack as desktop and holds a
Wi-Fi multicast lock. UPnP availability depends on the connected network's router;
LAN unicast does not depend on successful port mapping. UPnP diagnostics preserve
HTTP status and SOAP error code/description instead of returning only a generic
mapping failure. Failed discovery/lease attempts appear in network event logs.

Validation: `--test-lan-ports`, `--test-nat-pin-coordinator`,
`--test-lan-discovery`, `--test-upnp`, `--test-connectivity-security`,
`--test-discovery-announcements`, `--test-critical-reliability` and `--test-aot-json`.
LAN socket tests use isolated identities and ports; the beacon regression also uses
an isolated shared discovery port to avoid stealing traffic from an installed app.


## Rotating probes and bounded birthday traversal (2026-10-15)

A fixed set of evenly spaced ports, retransmitted forever, does not gain birthday
collision probability over time. Production control and transport attempts now
use `PortProbeSchedule`: a random permutation within the advertised range, with
real observed bounds retained as exact retry anchors. After the first three
rounds, dynamic port advertisements also explore ports outside the observed
STUN list/range (1024Ã¢â‚¬â€œ65535). Different STUN destinations do not necessarily see
the mapping that a new peer will use. LAN host endpoints stay exact; stable
single-port peers without `ExternalPortMayDiffer` do not trigger random expansion. Per-round limits are 16
endpoints for dynamic transport rounds. QUIC socket traversal retains its fast
25/125 ms burst. Control Hello retries instead start immediately, then wait
0.5, 1, 2, 4, 8, 16 and at most 25 seconds. Manual interrogation expires at 45
seconds and bounded automatic discovery at 30. Eligible saved contacts use
persistent control retries without that deadline; see [peer transport](peer-transport.md).

For the QUIC data socket, a dynamic-port local peer facing a stable-port remote
peer can additionally open up to 64 temporary local UDP sockets toward the
remote endpoint. This begins after a 250 ms head start for ordinary candidates,
runs at at most 128 initial probes per second, and allows only one extra pool per
process. All paths use the existing signed/password-bound connectivity envelope
and fresh endpoint challenges. No unverified response can nominate a path. The
winning **socket and its local port** are retained for the native QUIC handoff;
losing sockets and their receive/send workers are cancelled, closed and awaited.
The primary sender is also awaited before native QUIC starts using the port.
This is the many-open-ports half of the birthday technique, paired with rotating
remote target ports. Two hard NATs do not cause an unbounded socket/packet search.
It is a probabilistic improvement, not a guarantee for two strict symmetric NATs.
See the [NAT traversal explanation from Tailscale](https://tailscale.com/blog/how-nat-traversal-works).

For `PortMode.Single` with `ExternalPortMayDiffer = true`, QUIC traversal also
uses a bounded neighbouring-port search: rounds 1-8 sample `P..P+512`, rounds
9-16 sample `P..P+1024`, and later rounds sample `P-1024..P+1024`, clipped to
valid UDP ports 1-65535. `P` is the public QUIC socket candidate when available,
then the requested tunnel port, then the sole advertised control port. Exact
candidates keep priority, private/LAN endpoints do not expand, and each round
retains the 16-endpoint budget. Random permutations continue exploring each
window instead of repeating a fixed grid. A translated Single local endpoint
may also use the existing 64-socket birthday pool toward a Single remote peer.
Single peers without the bit and existing Multiple/Range traversal are unchanged;
the published port array is never rewritten into a synthetic range.

A successful lease on a publicly addressed UPnP/PCP/NAT-PMP router takes precedence
over inconsistent STUN observations: advertise that exact public IP and port.
A lease on a private/CGNAT gateway does not override the outer STUN observations.
When no control-port router mapping has succeeded, new QUIC sockets use direct
and STUN candidates without serially repeating gateway discovery behind the
control mapping lock. Control mapping continues retrying independently; once it
succeeds, subsequent sockets can request their own mappings.

`--test-birthday-punch` verifies full port-space traversal without replacement,
bounded rounds, LAN exclusion, expansion beyond explicit lists, stable/public
router leases, the no-mapper fast path, authentication, cancellation, retaining
the last of 64 opened sockets, and cleanup of losing sockets. Existing
`--test-connectivity-security` checks endpoint-bound return proof and native QUIC.


### Mobile restart and duplicate network notifications

The Android observer seeds its route snapshot before callback registration and
uses `NetworkPathTracker` to ignore the initial `OnAvailable`, repeated
`OnLinkPropertiesChanged`, IPv6-only temporary-address changes and late loss
notifications for a previous default network. Actual default-network, IPv4
address and route changes still refresh discovery and NAT state. Previously,
registering the observer after startup cancelled negotiations that had just
begun, leading to unnecessary exponential retries.

An incoming replacement multiplexer request now checks an apparently active old
tunnel with a correlated QUIC control ping before declining it. A responding
tunnel retains its application sessions. An unresponsive old tunnel is disposed
and the **same incoming request** proceeds, rather than declining the restarted
phone and forcing another 5/10/20-second retry cycle. Idle tunnels use a one-second correlated liveness check; tunnels with application
sessions retain the existing heartbeat deadline. This is an authenticated QUIC
control ping, not an unauthenticated UDP ping. A regression
uses a transport whose writes succeed but which never returns a correlated pong
and verifies that the replacement request is accepted after liveness fails.
