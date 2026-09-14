namespace QuicPunch;

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