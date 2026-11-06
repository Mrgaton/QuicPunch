using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using QuicPunch;
using QuicPunch.Helpers;
using QuicNet.Watch.Domain;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicNet.Watch.Protocols;

public sealed class WatchControlHandler : QuicPunch.QuicPunch.IProtocolHandler, IDisposable
{
    private const int MaxMessageBytes = 256 * 1024;

    public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000007");
    public string ProtocolName => "QuicWatch";
    public ushort StreamPriority => (ushort)QuicStreamPriority.Critical;

    private sealed class Session : IDisposable
    {
        public PeerInfo Peer { get; }
        public Stream Stream { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public Session(PeerInfo peer, Stream stream)
        {
            Peer = peer;
            Stream = stream;
        }

        public async Task SendAsync(WatchWireMessage message, CancellationToken ct = default)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, QuicNet.Serialization.SharedJson.Options);
            if (payload.Length is <= 0 or > MaxMessageBytes)
                throw new InvalidDataException("Invalid QuicWatch control message size.");

            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await WriteLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await Stream.WriteAsync(header, ct).ConfigureAwait(false);
                await Stream.WriteAsync(payload, ct).ConfigureAwait(false);
                await Stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                WriteLock.Release();
            }
        }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            // Senders can still be unwinding WaitAsync/Release after the stream closes.
        }
    }

    private Func<PeerInfo, PeerInfo>? _localIdentityForPeer;
    public void OnLocalIdentityProviderAttached(Func<PeerInfo, PeerInfo> provider) => _localIdentityForPeer = provider;

    internal Guid LocalIdFor(PeerInfo peer, Guid fallback)
    {
        bool tor = peer.ActiveTransport == QuicPunch.QuicPunch.TransportType.Tor || peer.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor ||
            !string.IsNullOrWhiteSpace(peer.OnionAddress);
        return tor ? (_localIdentityForPeer?.Invoke(peer) ?? throw new InvalidOperationException("Tor identity provider is unavailable.")).Id : fallback;
    }

    internal Guid LocalIdFor(Guid peerId, Guid fallback) => _sessions.TryGetValue(peerId, out var session)
        ? LocalIdFor(session.Peer, fallback) : fallback;

    private WatchWireMessage ForPeer(PeerInfo peer, WatchWireMessage message)
    {
        bool tor = peer.ActiveTransport == QuicPunch.QuicPunch.TransportType.Tor || peer.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor ||
            !string.IsNullOrWhiteSpace(peer.OnionAddress);
        if (!tor) return message;
        var local = _localIdentityForPeer?.Invoke(peer) ?? throw new InvalidOperationException("Register the watch protocol before sending over Tor.");
        return message with { SenderPeerId = local.Id,
            TargetPeerId = message.TargetPeerId != Guid.Empty && message.TargetPeerId == message.SenderPeerId ? local.Id : message.TargetPeerId };
    }

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    public IReadOnlyCollection<Guid> ConnectedPeerIds => _sessions.Keys.ToArray();

    public event Action<PeerInfo>? PeerConnected;
    public event Action<PeerInfo>? PeerDisconnected;
    public event Action<PeerInfo, WatchWireMessage>? MessageReceived;

    public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;

    public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
    {
        stream.SetQuicPriority(QuicStreamPriority.Critical);
        var session = new Session(peer, stream);
        if (_sessions.TryRemove(peer.Id, out var previous)) previous.Dispose();
        _sessions[peer.Id] = session;
        PeerConnected?.Invoke(peer);

        byte[] header = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaxMessageBytes)
                    throw new InvalidDataException("Invalid QuicWatch control frame.");

                byte[] payload = new byte[length];
                await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
                WatchWireMessage? message = JsonSerializer.Deserialize<WatchWireMessage>(payload, QuicNet.Serialization.SharedJson.Options);
                if (message != null) MessageReceived?.Invoke(peer, message);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        finally
        {
            if (_sessions.TryRemove(new KeyValuePair<Guid, Session>(peer.Id, session)))
                PeerDisconnected?.Invoke(peer);
            session.Dispose();
        }
    }

    public Task<bool> SendAsync(Guid peerId, WatchWireMessage message, CancellationToken ct = default) =>
        _sessions.TryGetValue(peerId, out var session)
            ? SendCoreAsync(session, message, ct)
            : Task.FromResult(false);

    private async Task<bool> SendCoreAsync(Session session, WatchWireMessage message, CancellationToken ct)
    {
        await session.SendAsync(ForPeer(session.Peer, message), ct).ConfigureAwait(false);
        return true;
    }

    public async Task BroadcastAsync(WatchWireMessage message, CancellationToken ct = default)
    {
        Session[] sessions = _sessions.Values.ToArray();
        await Task.WhenAll(sessions.Select(s => s.SendAsync(ForPeer(s.Peer, message), ct))).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (Session session in _sessions.Values) session.Dispose();
        _sessions.Clear();
    }
}
