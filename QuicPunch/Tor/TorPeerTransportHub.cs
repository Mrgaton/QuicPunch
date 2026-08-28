#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace QuicPunch;

public sealed class TorPeerTransportHub : IAsyncDisposable
{
    private readonly TorManager _tor;
    private readonly TorOnionService _service;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Guid, RouteState> _routes = new();
    private readonly Channel<TorIncomingPeerConnection> _incomingConnections;
    private readonly Task _acceptLoop;
    private int _disposed;

    private TorPeerTransportHub(TorManager tor, TorOnionService service)
    {
        _tor = tor;
        _service = service;

        _incomingConnections = Channel.CreateBounded<TorIncomingPeerConnection>(
            new BoundedChannelOptions(128)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public TorManager Tor => _tor;
    public TorOnionService OnionService => _service;
    public TorIdentity Identity => _service.Identity;
    public string OnionAddress => _service.OnionAddress;
    public string ServiceId => _service.ServiceId;
    public int VirtualPort => _service.VirtualPort;

    public static async ValueTask<TorPeerTransportHub> CreateAsync(
        TorManager tor,
        TorIdentity? identity = null,
        int virtualPort = 443,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tor);

        TorOnionService service = identity is null
            ? await tor.CreateOnionServiceAsync(virtualPort, cancellationToken).ConfigureAwait(false)
            : await tor.HostOnionAsync(identity, virtualPort, cancellationToken).ConfigureAwait(false);

        return new TorPeerTransportHub(tor, service);
    }

    public async ValueTask<TorIncomingPeerConnection> AcceptPeerConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _incomingConnections.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    internal RouteState RegisterOutgoing(
        Guid connectionId,
        ReadOnlySpan<byte> connectionToken,
        string remoteOnion,
        int remotePort)
    {
        ThrowIfDisposed();
        if (connectionId == Guid.Empty)
            throw new ArgumentException("ConnectionId cannot be empty.", nameof(connectionId));
        if (connectionToken.Length != 32)
            throw new ArgumentException("Connection token must be 32 bytes.", nameof(connectionToken));

        string remoteServiceId = TorManager.NormalizeServiceId(remoteOnion);
        var route = new RouteState(
            connectionId,
            connectionToken.ToArray(),
            remoteServiceId,
            remotePort,
            incoming: false);

        if (!_routes.TryAdd(connectionId, route))
            throw new InvalidOperationException($"ConnectionId {connectionId} is already registered in this Tor hub.");

        return route;
    }

    internal bool TryGetRoute(Guid connectionId, out RouteState? route) =>
        _routes.TryGetValue(connectionId, out route);

    internal void Unregister(RouteState route)
    {
        if (_routes.TryRemove(new System.Collections.Generic.KeyValuePair<Guid, RouteState>(route.ConnectionId, route)))
            route.Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }

        _incomingConnections.Writer.TryComplete();

        foreach (RouteState route in _routes.Values)
            route.Close();
        _routes.Clear();

        await _service.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TorTcpConnection? connection = null;
            try
            {
                TorTcpConnection incomingConn = await _service.AcceptConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleIncomingAsync(incomingConn, _shutdown.Token));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                connection?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                if (!_shutdown.IsCancellationRequested)
                    QuicPunchLog.Error("[TorPeerTransportHub ERROR] Accept loop failure", ex);
                connection?.Dispose();
                if (!_shutdown.IsCancellationRequested)
                {
                    try { await Task.Delay(100, _shutdown.Token).ConfigureAwait(false); }
                    catch { }
                }
            }
        }
    }

    private async Task HandleIncomingAsync(
        TorTcpConnection connection,
        CancellationToken cancellationToken)
    {
        bool transferred = false;
        try
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(20));

            TorLanePreface preface = await TorPeerTransportProtocol
                .ReadPrefaceAsync(connection, headerTimeout.Token)
                .ConfigureAwait(false);

            if (preface.Kind == TorLaneKind.Message)
            {
                transferred = HandleIncomingMessageLane(connection, preface);
                return;
            }

            RouteState? route = await WaitForRouteAsync(
                preface.ConnectionId,
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);

            if (route is null || !route.Matches(preface))
                return;

            switch (preface.Kind)
            {
                case TorLaneKind.QuicStream:
                    transferred = route.TryQueueQuicLane(
                        new HubInboundQuicLane(preface.StreamId, preface.StreamType, connection));
                    break;

                case TorLaneKind.RawTcp:
                    transferred = route.TryQueueRawLane(connection);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (!_shutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                QuicPunchLog.Error("[TorPeerTransportHub ERROR] HandleIncomingAsync failed", ex);
        }
        finally
        {
            if (!transferred)
                connection.Dispose();
        }
    }

    private bool HandleIncomingMessageLane(
        TorTcpConnection connection,
        TorLanePreface preface)
    {
        if (_routes.TryGetValue(preface.ConnectionId, out RouteState? existing))
        {
            if (!existing.Matches(preface) || !existing.TryAttachMessageLane(connection))
                return false;
            return true;
        }

        var route = new RouteState(
            preface.ConnectionId,
            preface.ConnectionToken,
            preface.SenderServiceId,
            preface.SenderVirtualPort,
            incoming: true);

        if (!route.TryAttachMessageLane(connection))
            return false;

        if (!_routes.TryAdd(preface.ConnectionId, route))
        {
            route.DetachMessageLaneWithoutDispose(connection);
            return false;
        }

        var incoming = new TorIncomingPeerConnection(
            preface.ConnectionId,
            preface.SenderServiceId + ".onion",
            preface.SenderVirtualPort,
            route);

        if (!_incomingConnections.Writer.TryWrite(incoming))
        {
            Unregister(route);
            return false;
        }

        return true;
    }

    private async ValueTask<RouteState?> WaitForRouteAsync(
        Guid connectionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_routes.TryGetValue(connectionId, out RouteState? route))
                return route;

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    internal sealed class RouteState
    {
        private readonly object _sync = new();
        private readonly byte[] _token;
        private readonly Channel<HubInboundQuicLane> _quicLanes;
        private readonly Channel<TorTcpConnection> _rawLanes;
        private TorTcpConnection? _messageLane;
        private int _closed;

        public RouteState(
            Guid connectionId,
            byte[] token,
            string remoteServiceId,
            int remotePort,
            bool incoming)
        {
            if (token.Length != 32)
                throw new ArgumentException("Token must be 32 bytes.", nameof(token));

            ConnectionId = connectionId;
            _token = (byte[])token.Clone();
            RemoteServiceId = remoteServiceId;
            RemoteOnion = remoteServiceId + ".onion";
            RemotePort = remotePort;
            IsIncoming = incoming;

            _quicLanes = Channel.CreateBounded<HubInboundQuicLane>(
                new BoundedChannelOptions(256)
                {
                    SingleReader = false,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });

            _rawLanes = Channel.CreateBounded<TorTcpConnection>(
                new BoundedChannelOptions(64)
                {
                    SingleReader = false,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });
        }

        public Guid ConnectionId { get; }
        public string RemoteServiceId { get; }
        public string RemoteOnion { get; }
        public int RemotePort { get; }
        public bool IsIncoming { get; }
        public ReadOnlyMemory<byte> Token => new((byte[])_token.Clone());

        public bool Matches(TorLanePreface preface)
        {
            if (Volatile.Read(ref _closed) != 0)
                return false;
            if (preface.ConnectionId != ConnectionId)
                return false;
            if (!string.Equals(preface.SenderServiceId, RemoteServiceId, StringComparison.Ordinal))
                return false;
            return CryptographicOperations.FixedTimeEquals(preface.ConnectionToken, _token);
        }

        public bool TryAttachMessageLane(TorTcpConnection connection)
        {
            lock (_sync)
            {
                if (_closed != 0 || _messageLane is not null)
                    return false;
                _messageLane = connection;
                return true;
            }
        }

        public void DetachMessageLaneWithoutDispose(TorTcpConnection connection)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_messageLane, connection))
                    _messageLane = null;
            }
        }

        public TorTcpConnection GetMessageLane()
        {
            lock (_sync)
            {
                return _messageLane ?? throw new InvalidOperationException("Message lane has not been attached.");
            }
        }

        public bool TryQueueQuicLane(HubInboundQuicLane lane) =>
            Volatile.Read(ref _closed) == 0 && _quicLanes.Writer.TryWrite(lane);

        public bool TryQueueRawLane(TorTcpConnection lane) =>
            Volatile.Read(ref _closed) == 0 && _rawLanes.Writer.TryWrite(lane);

        public ValueTask<HubInboundQuicLane> AcceptQuicLaneAsync(CancellationToken cancellationToken) =>
            _quicLanes.Reader.ReadAsync(cancellationToken);

        public ValueTask<TorTcpConnection> AcceptRawLaneAsync(CancellationToken cancellationToken) =>
            _rawLanes.Reader.ReadAsync(cancellationToken);

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            _quicLanes.Writer.TryComplete();
            _rawLanes.Writer.TryComplete();

            lock (_sync)
            {
                try { _messageLane?.Dispose(); } catch { }
                _messageLane = null;
            }

            while (_quicLanes.Reader.TryRead(out HubInboundQuicLane lane))
                lane.Connection.Dispose();
            while (_rawLanes.Reader.TryRead(out TorTcpConnection raw))
                raw.Dispose();

            CryptographicOperations.ZeroMemory(_token);
        }
    }
}

public sealed record TorIncomingPeerConnection
{
    internal TorIncomingPeerConnection(
        Guid connectionId,
        string remoteOnion,
        int remotePort,
        TorPeerTransportHub.RouteState route)
    {
        ConnectionId = connectionId;
        RemoteOnion = remoteOnion;
        RemotePort = remotePort;
        Route = route;
    }

    public Guid ConnectionId { get; }
    public string RemoteOnion { get; }
    public int RemotePort { get; }

    internal TorPeerTransportHub.RouteState Route { get; }
}

internal readonly record struct HubInboundQuicLane(
    long StreamId,
    QuicStreamType StreamType,
    TorTcpConnection Connection);
