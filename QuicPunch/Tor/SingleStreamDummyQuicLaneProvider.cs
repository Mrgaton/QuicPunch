namespace QuicPunch;

public sealed class SingleStreamDummyQuicLaneProvider : IDummyQuicLaneProvider
{
    private readonly IDummyQuicLane _lane;
    private readonly bool _streamWasOpenedByRemote;
    private readonly QuicStreamType _type;
    private readonly long _inboundStreamId;
    private int _taken;

    public SingleStreamDummyQuicLaneProvider(
        IDummyQuicLane lane,
        QuicConnectionRole localRole,
        bool streamWasOpenedByRemote,
        QuicStreamType type)
    {
        _lane = lane ?? throw new ArgumentNullException(nameof(lane));
        _streamWasOpenedByRemote = streamWasOpenedByRemote;
        _type = type;
        _inboundStreamId =
            DummyQuicConnectionTransport.GetFirstRemoteStreamId(localRole, type);
    }

    public ValueTask<IDummyQuicLane> OpenOutboundLaneAsync(
        DummyQuicOpenLaneRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_streamWasOpenedByRemote)
        {
            throw new InvalidOperationException(
                "This single Stream was configured as inbound and cannot open an outbound lane.");
        }

        TakeOnce();

        if (request.Type != _type)
        {
            throw new InvalidOperationException(
                $"This single Stream was configured as {_type}, not {request.Type}.");
        }

        return ValueTask.FromResult(_lane);
    }

    public ValueTask<DummyQuicInboundLane> AcceptInboundLaneAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_streamWasOpenedByRemote)
        {
            throw new InvalidOperationException(
                "This single Stream was configured as outbound and cannot accept an inbound lane.");
        }

        TakeOnce();

        return ValueTask.FromResult(
            new DummyQuicInboundLane(
                _inboundStreamId,
                _type,
                _lane));
    }

    public ValueTask DisposeAsync() =>
        _lane.DisposeAsync();

    private void TakeOnce()
    {
        if (Interlocked.Exchange(ref _taken, 1) != 0)
        {
            throw new InvalidOperationException(
                "A SingleStreamDummyQuicLaneProvider can provide exactly one QuicStream.");
        }
    }
}