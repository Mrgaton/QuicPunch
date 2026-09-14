#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace QuicPunch;

public sealed class TorQuicConnectionManager : IDummyQuicLaneProvider, IDisposable, IAsyncDisposable
{
    private const int DefaultMaxMessageBytes = 4 * 1024 * 1024;

    private readonly TorManager _tor;
    private readonly TorPeerTransportHub _hub;
    private readonly TorPeerTransportHub.RouteState _route;
    private readonly SemaphoreSlim _messageWriteLock = new(1, 1);
    private readonly Channel<byte[]> _messages;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<TorTcpConnection, byte> _openLanes = new();
    private readonly Task _messagePump;
    private readonly int _maxMessageBytes;

    private long _remoteCloseErrorCode;
    private int _closed;
    private int _disposed;

    private TorQuicConnectionManager(
        TorManager tor,
        TorPeerTransportHub hub,
        TorPeerTransportHub.RouteState route,
        QuicConnectionRole role,
        int maxMessageBytes)
    {
        _tor = tor;
        _hub = hub;
        _route = route;
        Role = role;
        ConnectionId = route.ConnectionId;
        _maxMessageBytes = maxMessageBytes;

        _messages = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(1024)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

        _messagePump = Task.Run(MessagePumpAsync);
    }

    public Guid ConnectionId { get; }
    public QuicConnectionRole Role { get; }
    public string RemoteOnion => _route.RemoteOnion;
    public int RemoteVirtualPort => _route.RemotePort;
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public long RemoteCloseErrorCode => Interlocked.Read(ref _remoteCloseErrorCode);

    public EndPoint? LocalEndPoint => new DnsEndPoint(_hub.OnionAddress, _hub.VirtualPort);
    public EndPoint? RemoteEndPoint => new DnsEndPoint(RemoteOnion, RemoteVirtualPort);

    public static async ValueTask<TorQuicConnectionManager> ConnectAsync(
        TorManager tor,
        TorPeerTransportHub hub,
        string remoteOnion,
        int remoteVirtualPort,
        int maxMessageBytes = DefaultMaxMessageBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tor);
        ArgumentNullException.ThrowIfNull(hub);
        remoteOnion = TorManager.NormalizeOnion(remoteOnion);
        if (remoteVirtualPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(remoteVirtualPort));
        ValidateMessageLimit(maxMessageBytes);

        Guid connectionId = Guid.NewGuid();
        byte[] token = RandomNumberGenerator.GetBytes(32);
        TorPeerTransportHub.RouteState? route = null;
        TorTcpConnection? message = null;

        try
        {
            route = hub.RegisterOutgoing(connectionId, token, remoteOnion, remoteVirtualPort);

            message = await tor.ConnectOnionAsync(
                    remoteOnion,
                    remoteVirtualPort,
                    IsolationKey(connectionId),
                    cancellationToken)
                .ConfigureAwait(false);

            TorLanePreface preface = TorPeerTransportProtocol.Create(
                TorLaneKind.Message,
                connectionId,
                streamId: -1,
                QuicStreamType.Bidirectional,
                token,
                hub.OnionService);

            await TorPeerTransportProtocol
                .WritePrefaceAsync(message, preface, cancellationToken)
                .ConfigureAwait(false);

            if (!route.TryAttachMessageLane(message))
                throw new InvalidOperationException("Could not attach the outgoing Tor message lane.");

            message = null;
            return new TorQuicConnectionManager(
                tor,
                hub,
                route,
                QuicConnectionRole.Client,
                maxMessageBytes);
        }
        catch
        {
            message?.Dispose();
            if (route is not null)
                hub.Unregister(route);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    public static async ValueTask<TorQuicConnectionManager> AcceptAsync(
        TorPeerTransportHub hub,
        int maxMessageBytes = DefaultMaxMessageBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ValidateMessageLimit(maxMessageBytes);

        TorIncomingPeerConnection incoming =
            await hub.AcceptPeerConnectionAsync(cancellationToken).ConfigureAwait(false);

        return new TorQuicConnectionManager(
            hub.Tor,
            hub,
            incoming.Route,
            QuicConnectionRole.Server,
            maxMessageBytes);
    }

    public QuicConnection CreateQuicConnection(bool leaveOpen = true) =>
        QuicConnection.CreateDummy(this, Role, ConnectionId, leaveProviderOpen: leaveOpen);

    public static async ValueTask<(QuicConnection Connection, TorQuicConnectionManager Manager)>
        AcceptQuicConnectionAsync(
            TorPeerTransportHub hub,
            int maxMessageBytes = DefaultMaxMessageBytes,
            CancellationToken cancellationToken = default)
    {
        TorQuicConnectionManager manager =
            await AcceptAsync(hub, maxMessageBytes, cancellationToken).ConfigureAwait(false);
        return (manager.CreateQuicConnection(), manager);
    }

    public ValueTask SendDatagramAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        SendMessageAsync(payload, cancellationToken);

    public async ValueTask SendMessageAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (payload.Length > _maxMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Message exceeds {_maxMessageBytes} bytes.");

        await SendFrameAsync(
            TorMessageFrameType.Message,
            payload,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveDatagramAsync(
        CancellationToken cancellationToken = default) =>
        await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveMessageAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        byte[] message = await _messages.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return message;
    }

    public async ValueTask<TorTcpConnection> OpenTcpConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        TorTcpConnection connection = await OpenRoutedConnectionAsync(
            TorLaneKind.RawTcp,
            streamId: -1,
            QuicStreamType.Bidirectional,
            cancellationToken).ConfigureAwait(false);

        _openLanes.TryAdd(connection, 0);
        return connection;
    }

    public async ValueTask<TorTcpConnection> AcceptTcpConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        TorTcpConnection connection =
            await _route.AcceptRawLaneAsync(cancellationToken).ConfigureAwait(false);
        _openLanes.TryAdd(connection, 0);
        return connection;
    }

    public async ValueTask<IDummyQuicLane> OpenOutboundLaneAsync(
        DummyQuicOpenLaneRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (request.ConnectionId != ConnectionId)
            throw new InvalidOperationException("Dummy QUIC request belongs to a different Tor logical connection.");

        TorTcpConnection connection = await OpenRoutedConnectionAsync(
            TorLaneKind.QuicStream,
            request.StreamId,
            request.Type,
            cancellationToken).ConfigureAwait(false);

        _openLanes.TryAdd(connection, 0);

        return new StreamDummyQuicLane(
            connection,
            request.Type,
            openedByRemote: false,
            completeWrites: connection.ShutdownSend,
            abort: (direction, errorCode) => connection.Abort(direction, errorCode));
    }

    public async ValueTask<DummyQuicInboundLane> AcceptInboundLaneAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (connectionId != ConnectionId)
            throw new InvalidOperationException("Requested ConnectionId does not match this Tor peer connection.");

        HubInboundQuicLane inbound =
            await _route.AcceptQuicLaneAsync(cancellationToken).ConfigureAwait(false);

        _openLanes.TryAdd(inbound.Connection, 0);

        IDummyQuicLane lane = new StreamDummyQuicLane(
            inbound.Connection,
            inbound.StreamType,
            openedByRemote: true,
            completeWrites: inbound.Connection.ShutdownSend,
            abort: (direction, errorCode) => inbound.Connection.Abort(direction, errorCode));

        return new DummyQuicInboundLane(
            inbound.StreamId,
            inbound.StreamType,
            lane);
    }

    public async ValueTask CloseAsync(
        long errorCode,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        byte[] closePayload = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(closePayload, errorCode);

        try
        {
            await SendFrameCoreAsync(
                TorMessageFrameType.ConnectionClose,
                closePayload,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort graceful signal; closing the TCP lanes is authoritative.
        }

        _shutdown.Cancel();
        _messages.Writer.TryComplete();

        foreach (TorTcpConnection lane in _openLanes.Keys)
        {
            try { lane.Dispose(); } catch { }
        }
        _openLanes.Clear();

        _hub.Unregister(_route);
    }

    public void Dispose()
    {
        try
        {
            _ = DisposeAsync().AsTask();
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (Volatile.Read(ref _closed) == 0)
                await CloseCoreDuringDisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Cancel();
            try { await _messagePump.ConfigureAwait(false); } catch { }
            _messages.Writer.TryComplete();
            _messageWriteLock.Dispose();
            _shutdown.Dispose();
        }
    }

    private async ValueTask CloseCoreDuringDisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        try
        {
            byte[] payload = new byte[8];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await SendFrameCoreAsync(TorMessageFrameType.ConnectionClose, payload, timeout.Token).ConfigureAwait(false);
        }
        catch { }

        _shutdown.Cancel();
        foreach (TorTcpConnection lane in _openLanes.Keys)
        {
            try { lane.Dispose(); } catch { }
        }
        _openLanes.Clear();
        _hub.Unregister(_route);
    }

    private async ValueTask<TorTcpConnection> OpenRoutedConnectionAsync(
        TorLaneKind kind,
        long streamId,
        QuicStreamType streamType,
        CancellationToken cancellationToken)
    {
        TorTcpConnection connection = await _tor.ConnectOnionAsync(
                RemoteOnion,
                RemoteVirtualPort,
                IsolationKey(ConnectionId),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            TorLanePreface preface = TorPeerTransportProtocol.Create(
                kind,
                ConnectionId,
                streamId,
                streamType,
                _route.Token.ToArray(),
                _hub.OnionService);

            await TorPeerTransportProtocol
                .WritePrefaceAsync(connection, preface, cancellationToken)
                .ConfigureAwait(false);

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task MessagePumpAsync()
    {
        try
        {
            TorTcpConnection stream = _route.GetMessageLane();
            byte[] header = new byte[5];

            while (!_shutdown.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _shutdown.Token).ConfigureAwait(false);

                TorMessageFrameType type = header[0] switch
                {
                    (byte)TorMessageFrameType.Message => TorMessageFrameType.Message,
                    (byte)TorMessageFrameType.ConnectionClose => TorMessageFrameType.ConnectionClose,
                    _ => throw new InvalidDataException("Unknown Tor message-lane frame type.")
                };

                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4)));
                if (length < 0 || length > _maxMessageBytes)
                    throw new InvalidDataException("Tor message-lane frame exceeds configured maximum.");

                byte[] payload = new byte[length];
                await stream.ReadExactlyAsync(payload, _shutdown.Token).ConfigureAwait(false);

                if (type == TorMessageFrameType.ConnectionClose)
                {
                    if (payload.Length != 8)
                        throw new InvalidDataException("Invalid connection-close frame length.");

                    Interlocked.Exchange(
                        ref _remoteCloseErrorCode,
                        BinaryPrimitives.ReadInt64BigEndian(payload));
                    CloseFromRemoteOrFailure(error: null);
                    break;
                }

                if (!_messages.Writer.TryWrite(payload))
                    throw new IOException("Tor message receive queue is full.");
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
            CloseFromRemoteOrFailure(error: null);
        }
        catch (Exception ex)
        {
            if (!_shutdown.IsCancellationRequested && Volatile.Read(ref _closed) == 0)
                QuicPunchLog.Info($"[TorQuicConnectionManager Notice] MessagePumpAsync closed on role {Role}: {ex.Message}");
            CloseFromRemoteOrFailure(ex);
        }
    }

    private void CloseFromRemoteOrFailure(Exception? error)
    {
        Interlocked.Exchange(ref _closed, 1);
        _shutdown.Cancel();

        if (error is null)
            _messages.Writer.TryComplete();
        else
            _messages.Writer.TryComplete(error);

        foreach (TorTcpConnection lane in _openLanes.Keys)
        {
            try { lane.Dispose(); } catch { }
        }
        _openLanes.Clear();

        _hub.Unregister(_route);
    }

    private ValueTask SendFrameAsync(
        TorMessageFrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return SendFrameCoreAsync(type, payload, cancellationToken);
    }

    private async ValueTask SendFrameCoreAsync(
        TorMessageFrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > _maxMessageBytes)
            throw new InvalidDataException("Message frame exceeds configured maximum.");

        await _messageWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TorTcpConnection stream = _route.GetMessageLane();
            byte[] header = new byte[5];
            header[0] = (byte)type;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1, 4), checked((uint)payload.Length));

            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            if (!payload.IsEmpty)
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _messageWriteLock.Release();
        }
    }

    private static string IsolationKey(Guid connectionId) =>
        "quicpunch:" + connectionId.ToString("N");

    private static void ValidateMessageLimit(int maxMessageBytes)
    {
        if (maxMessageBytes is < 1024 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(
                nameof(maxMessageBytes),
                "Message lane limit must be between 1 KiB and 64 MiB.");
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _closed) != 0)
            throw new IOException("The Tor logical peer connection is closed.");
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private enum TorMessageFrameType : byte
    {
        Message = 1,
        ConnectionClose = 2
    }
}