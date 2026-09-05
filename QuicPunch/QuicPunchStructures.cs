using System;
using System.Collections.Generic;
using System.Text;

namespace QuicPunch
{
    internal static class QuicPunchStructures
    {
        public enum MessageType : byte
        {
            Hello = (byte)('H'),
            Ping = (byte)('P'),
            Interrogation = (byte)('I'),
            Ack = (byte)('K'),
            Handshake = (byte)('S'),
            FinalHandshake = (byte)('F'),
            Data = (byte)('D'),
            Disconnect = (byte)('X'),
            QuicReady = (byte)('Q')
        }
        public enum HandShakeType : byte
        {
            Request = (byte)('R'),
            Accept = (byte)('A'),
            Decline = (byte)('D'),
            Unsupported = (byte)('U') //Peer doesnt support the requested protocol
        }
    }

    public enum CandidateType : byte
    {
        Host = 1,
        ServerReflexive = 2,
        PeerReflexive = 3
    }

    public sealed record CandidateEndpoint(System.Net.IPEndPoint EndPoint, CandidateType Type, uint Priority = 0);

    public enum HandshakeSessionState : byte
    {
        Pending = 0,
        Completed = 1,
        Rejected = 2
    }

    internal sealed class IncomingHandshakeSession
    {
        public Guid ConnectionGuid { get; }
        public Guid PeerId { get; }
        public Guid ProtocolId { get; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public long CreatedTimestampMonotonic { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public long CompletedTimestampMonotonic { get; private set; }
        public HandshakeSessionState State { get; private set; } = HandshakeSessionState.Pending;
        public TaskCompletionSource<byte[]> ResponsePayloadTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool ConnectionTaskStarted;

        public IncomingHandshakeSession(Guid connectionGuid, Guid peerId, Guid protocolId)
        {
            ConnectionGuid = connectionGuid;
            PeerId = peerId;
            ProtocolId = protocolId;
        }

        public void MarkCompleted()
        {
            State = HandshakeSessionState.Completed;
            CompletedTimestampMonotonic = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public void MarkRejected()
        {
            State = HandshakeSessionState.Rejected;
            CompletedTimestampMonotonic = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public bool IsExpired(TimeSpan pendingTtl, TimeSpan completedTtl, TimeSpan rejectedTtl)
        {
            return State switch
            {
                HandshakeSessionState.Pending => System.Diagnostics.Stopwatch.GetElapsedTime(CreatedTimestampMonotonic) > pendingTtl,
                HandshakeSessionState.Completed => System.Diagnostics.Stopwatch.GetElapsedTime(CompletedTimestampMonotonic) > completedTtl,
                HandshakeSessionState.Rejected => System.Diagnostics.Stopwatch.GetElapsedTime(CompletedTimestampMonotonic) > rejectedTtl,
                _ => true
            };
        }
    }

    internal sealed class IncomingHandshakeWorker
    {
        public Guid ConnectionGuid { get; }
        public long Generation { get; }
        public CancellationTokenSource Cts { get; }
        public Task Task { get; set; }

        public IncomingHandshakeWorker(Guid connectionGuid, long generation, CancellationTokenSource cts)
        {
            ConnectionGuid = connectionGuid;
            Generation = generation;
            Cts = cts;
            Task = Task.CompletedTask;
        }
    }

    /// <summary>
    /// Represents point-in-time telemetry and connection metadata for an active application protocol session.
    /// </summary>
    public sealed class QuicSessionTelemetryInfo
    {
        /// <summary>Gets the unique identifier of the remote peer.</summary>
        public Guid PeerId { get; init; }

        /// <summary>Gets the display name of the remote peer.</summary>
        public string? PeerName { get; init; }

        /// <summary>Gets the protocol identifier associated with the active session.</summary>
        public Guid ProtocolId { get; init; }

        /// <summary>Gets the human-readable registered name of the protocol.</summary>
        public string? ProtocolName { get; init; }

        /// <summary>Gets the underlying transport layer classification (WAN or Tor).</summary>
        public QuicPunch.TransportType TransportType { get; init; }

        /// <summary>Gets the comprehensive telemetry metrics queried directly from the native MsQuic engine.</summary>
        public Helpers.QuicConnectionTelemetry Telemetry { get; init; } = null!;

        /// <summary>Gets the UTC timestamp at which this telemetry snapshot was captured.</summary>
        public DateTime SampleTimeUtc { get; init; } = DateTime.UtcNow;
    }
}
