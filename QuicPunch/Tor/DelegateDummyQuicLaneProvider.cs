using System.Net;

namespace QuicPunch;

public sealed class DelegateDummyQuicLaneProvider : IDummyQuicLaneProvider
{
    private readonly Func<DummyQuicOpenLaneRequest, CancellationToken, ValueTask<IDummyQuicLane>> _open;
    private readonly Func<Guid, CancellationToken, ValueTask<DummyQuicInboundLane>> _accept;
    private readonly Func<long, CancellationToken, ValueTask>? _close;
    private readonly Func<ValueTask>? _dispose;

    public DelegateDummyQuicLaneProvider(
        Func<DummyQuicOpenLaneRequest, CancellationToken, ValueTask<IDummyQuicLane>> open,
        Func<Guid, CancellationToken, ValueTask<DummyQuicInboundLane>> accept,
        EndPoint? localEndPoint = null,
        EndPoint? remoteEndPoint = null,
        Func<long, CancellationToken, ValueTask>? close = null,
        Func<ValueTask>? dispose = null)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _accept = accept ?? throw new ArgumentNullException(nameof(accept));
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        _close = close;
        _dispose = dispose;
    }

    public EndPoint? LocalEndPoint { get; }

    public EndPoint? RemoteEndPoint { get; }

    public ValueTask<IDummyQuicLane> OpenOutboundLaneAsync(
        DummyQuicOpenLaneRequest request,
        CancellationToken cancellationToken = default) =>
        _open(request, cancellationToken);

    public ValueTask<DummyQuicInboundLane> AcceptInboundLaneAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default) =>
        _accept(connectionId, cancellationToken);

    public ValueTask CloseAsync(
        long errorCode,
        CancellationToken cancellationToken = default) =>
        _close is null
            ? ValueTask.CompletedTask
            : _close(errorCode, cancellationToken);

    public ValueTask DisposeAsync() =>
        _dispose is null
            ? ValueTask.CompletedTask
            : _dispose();
}