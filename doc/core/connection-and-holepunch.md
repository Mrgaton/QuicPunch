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

    App->>QP: InitQuicConnection(ProtocolId, RemotePeer)
    QP->>Stun: GatherCandidatesAsync() (Host, ServerReflexive)
    Stun-->>QP: List<CandidateEndpoint> (e.g. 192.168.1.50:54321, 203.0.113.10:48900)
    QP->>Peer: NegotiateConnection(ProtocolId, Candidates)
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
    QP->>App: handler.HandleAsync(connection, stream, peer)
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
