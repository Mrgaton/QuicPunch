using System.Collections.Concurrent;
using System.Net;

namespace QuicPunch;

public sealed class DummyQuicConnectionTransport : IQuicConnectionTransport
{
    private const long MaxQuicStreamId = (1L << 62) - 1;

    private readonly IDummyQuicLaneProvider _provider;
    private readonly QuicConnectionRole _role;
    private readonly bool _leaveProviderOpen;
    private readonly ConcurrentDictionary<long, byte> _seenInboundIds = new();

    private long _nextBidirectionalIndex = -1;
    private long _nextUnidirectionalIndex = -1;
    private int _disposed;
    private int _closed;

    public DummyQuicConnectionTransport(
        IDummyQuicLaneProvider provider,
        QuicConnectionRole role,
        Guid? connectionId = null,
        bool leaveProviderOpen = false)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _role = role;
        _leaveProviderOpen = leaveProviderOpen;
        ConnectionId = connectionId ?? Guid.NewGuid();
    }

    public Guid ConnectionId { get; }

    public QuicPunch.TransportType TransportType => QuicPunch.TransportType.Tor;

    public QuicConnectionRole Role => _role;

    public EndPoint? LocalEndPoint => _provider.LocalEndPoint;

    public EndPoint? RemoteEndPoint => _provider.RemoteEndPoint;

    public async ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(
        QuicStreamType type,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();

        long streamId = AllocateLocalStreamId(type);

        var request = new DummyQuicOpenLaneRequest(
            ConnectionId,
            streamId,
            type);

        IDummyQuicLane lane =
            await _provider
                .OpenOutboundLaneAsync(request, cancellationToken)
                .ConfigureAwait(false);

        ArgumentNullException.ThrowIfNull(lane);

        ValidateLaneSemantics(
            lane,
            type,
            openedLocally: true);

        return new DummyQuicStreamTransport(
            lane,
            streamId,
            type);
    }

    public async ValueTask<IQuicStreamTransport> AcceptInboundStreamAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();

        DummyQuicInboundLane inbound =
            await _provider
                .AcceptInboundLaneAsync(ConnectionId, cancellationToken)
                .ConfigureAwait(false);

        ArgumentNullException.ThrowIfNull(inbound.Lane);

        ValidateInboundStreamId(
            inbound.StreamId,
            inbound.Type);

        if (!_seenInboundIds.TryAdd(inbound.StreamId, 0))
        {
            throw new InvalidOperationException(
                $"Duplicate inbound QUIC-like stream id {inbound.StreamId}.");
        }

        ValidateLaneSemantics(
            inbound.Lane,
            inbound.Type,
            openedLocally: false);

        return new DummyQuicStreamTransport(
            inbound.Lane,
            inbound.StreamId,
            inbound.Type);
    }

    public async ValueTask CloseAsync(
        long errorCode,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        await _provider
            .CloseAsync(errorCode, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (!_leaveProviderOpen)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static long GetFirstRemoteStreamId(
        QuicConnectionRole localRole,
        QuicStreamType type)
    {
        int remoteInitiatorBit =
            localRole == QuicConnectionRole.Client ? 1 : 0;

        int directionBit =
            type == QuicStreamType.Unidirectional ? 0b10 : 0;

        return remoteInitiatorBit | directionBit;
    }

    private long AllocateLocalStreamId(
        QuicStreamType type)
    {
        long index = type switch
        {
            QuicStreamType.Bidirectional =>
                Interlocked.Increment(ref _nextBidirectionalIndex),

            QuicStreamType.Unidirectional =>
                Interlocked.Increment(ref _nextUnidirectionalIndex),

            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        if (index > (MaxQuicStreamId >> 2))
            throw new InvalidOperationException("QUIC-like stream id space exhausted.");

        long initiatorBit =
            _role == QuicConnectionRole.Client ? 0L : 1L;

        long directionBit =
            type == QuicStreamType.Unidirectional ? 2L : 0L;

        return (index << 2) | initiatorBit | directionBit;
    }

    private void ValidateInboundStreamId(
        long streamId,
        QuicStreamType type)
    {
        if (streamId < 0 || streamId > MaxQuicStreamId)
            throw new InvalidOperationException("Invalid 62-bit QUIC-like stream id.");

        int initiatorBit = (int)(streamId & 1L);
        int expectedRemoteInitiatorBit =
            _role == QuicConnectionRole.Client ? 1 : 0;

        if (initiatorBit != expectedRemoteInitiatorBit)
        {
            throw new InvalidOperationException(
                $"Inbound stream {streamId} has the wrong initiator bit for role {_role}.");
        }

        bool idSaysUnidirectional = (streamId & 2L) != 0;
        bool typeSaysUnidirectional =
            type == QuicStreamType.Unidirectional;

        if (idSaysUnidirectional != typeSaysUnidirectional)
        {
            throw new InvalidOperationException(
                $"Inbound stream {streamId} type does not match its QUIC stream-id direction bit.");
        }
    }

    private static void ValidateLaneSemantics(
        IDummyQuicLane lane,
        QuicStreamType type,
        bool openedLocally)
    {
        if (lane.Stream is null)
            throw new InvalidOperationException("Dummy lane returned a null Stream.");

        if (type == QuicStreamType.Bidirectional)
        {
            if (!lane.CanRead || !lane.CanWrite)
            {
                throw new InvalidOperationException(
                    "A bidirectional dummy lane must be readable and writable.");
            }

            return;
        }

        if (openedLocally)
        {
            if (lane.CanRead || !lane.CanWrite)
            {
                throw new InvalidOperationException(
                    "A locally opened unidirectional lane must be write-only.");
            }
        }
        else
        {
            if (!lane.CanRead || lane.CanWrite)
            {
                throw new InvalidOperationException(
                    "A remotely opened unidirectional lane must be read-only.");
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();

        if (Volatile.Read(ref _closed) != 0)
            throw new InvalidOperationException("The dummy QUIC connection is closed.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}