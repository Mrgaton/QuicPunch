using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using QuicNet.Settings;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunch.Multiplexer;

namespace QuicNet.Protocols;

internal sealed class ProfileAvatarSync : QuicPunch.QuicPunch.IProtocolHandler, IDisposable
{
    private enum Result { Retry, Synchronized, Unsupported }
    private sealed record Attempt(QuicPeerMultiplexer Transport, string Version, long StartedAt, Task<Result> Task);

    private readonly QuicPunch.QuicPunch _node;
    private readonly ProfileAvatarStore _avatars;
    private readonly CancellationTokenSource _stop;
    private readonly Task _worker;
    private readonly ConcurrentDictionary<Guid, QuicPeerMultiplexer> _supported = new();
    private int _disposed;

    public Guid ProtocolId { get; } = new("00000000-0000-0000-0000-00000000000a");
    public string ProtocolName => "Profile avatar";
    public bool SupportsMailbox => false;
    public ushort StreamPriority => (ushort)QuicStreamPriority.Low;

    internal ProfileAvatarSync(QuicPunch.QuicPunch node, ProfileAvatarStore avatars, CancellationToken lifetime)
    {
        _node = node;
        _avatars = avatars;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _node.RegisterProtocol(this);
        _worker = RunAsync(_stop.Token);
    }

    private bool CanExchange(PeerInfo peer) =>
        !_stop.IsCancellationRequested && _node.IsTrustedPeer(peer) &&
        peer.ActiveTransport != QuicPunch.QuicPunch.TransportType.Tor &&
        peer.NetworkType != QuicPunch.QuicPunch.NetworkType.Tor && string.IsNullOrEmpty(peer.OnionAddress);

    public bool? IncomingSessionPolicy(PeerInfo peer) => CanExchange(peer);
    public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;

    internal bool UsesProfileStream(Guid peerId) =>
        _supported.TryGetValue(peerId, out var mux) && !mux.IsDisposed &&
        _node.TryGetPeerMultiplexer(peerId, out var current) && ReferenceEquals(current, mux);

    private async Task RunAsync(CancellationToken ct)
    {
        var attempts = new Dictionary<Guid, Attempt>();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                foreach (var entry in attempts.Where(entry => entry.Value.Transport.IsDisposed && entry.Value.Task.IsCompleted).ToArray())
                    attempts.Remove(entry.Key);
                foreach (var entry in _supported.Where(entry => entry.Value.IsDisposed))
                    _supported.TryRemove(entry);

                int active = attempts.Values.Count(attempt => !attempt.Task.IsCompleted);
                string version = _avatars.LocalVersion;
                foreach (var peer in _node.AvailablePeers.Values)
                {
                    if (active >= _node.PeerTransport.MaxConcurrentAttempts) break;
                    if (!CanExchange(peer) || !_node.TryGetPeerMultiplexer(peer.Id, out var mux) ||
                        mux is null or { IsDisposed: true } || mux.PhysicalConnection.IsTor) continue;
                    if (attempts.TryGetValue(peer.Id, out var previous))
                    {
                        if (!previous.Task.IsCompleted) continue;
                        if (previous.Transport == mux)
                        {
                            if (previous.Task.Result == Result.Unsupported) continue;
                            if (previous.Version == version && (previous.Task.Result == Result.Synchronized ||
                                Stopwatch.GetElapsedTime(previous.StartedAt) < TimeSpan.FromSeconds(10))) continue;
                        }
                    }
                    attempts[peer.Id] = new(mux, version, Stopwatch.GetTimestamp(), PublishAsync(mux, version, ct));
                    active++;
                }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(attempts.Values.Select(attempt => attempt.Task)).ConfigureAwait(false); }
    }

    // The peer supplies the SHA-256 already cached for this identity. A length
    // of -1 keeps that version, zero removes it, and a positive length carries raw AVIF.
    private async Task<Result> PublishAsync(QuicPeerMultiplexer mux, string version, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ct = timeout.Token;
        try
        {
            if (mux.GetRemoteProtocolAvailability(ProtocolId) == false) return Result.Unsupported;
            await using var session = await mux.OpenVirtualSessionAsync(ProtocolId, ct).ConfigureAwait(false);
            await using var stream = await session.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
            stream.Priority16 = StreamPriority;
            byte[] cached = new byte[32];
            await stream.ReadExactlyAsync(cached, ct).ConfigureAwait(false);
            if (!CanExchange(mux.Peer)) return Result.Retry;
            _supported[mux.Peer.Id] = mux;
            byte[]? bytes = cached.AsSpan().SequenceEqual(Digest(version)) ? null : _avatars.ReadLocalVersion(version);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes?.Length ?? -1);
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            if (bytes is { Length: > 0 }) await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.ReadExactlyAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false);
            stream.CompleteWrites();
            return header[0] == 1 ? Result.Synchronized : Result.Retry;
        }
        catch (SessionRejectedException)
        {
            // A busy peer can reject a stream even when it supports the protocol.
            if (!ct.IsCancellationRequested)
                await mux.RefreshRemoteProtocolAvailabilityAsync(ProtocolId).ConfigureAwait(false);
            return Result.Retry;
        }
        catch (OperationCanceledException) { return Result.Retry; }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) QuicPunchLog.Info("[PROFILE] Avatar synchronization deferred: " + ex.Message);
            return Result.Retry;
        }
    }

    public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
    {
        if (connection.IsTor || !CanExchange(peer)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ct = timeout.Token;
        await stream.WriteAsync(Digest(_avatars.Version(peer.Id)), ct).ConfigureAwait(false);
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < -1 or > ProfileAvatarStore.MaxBytes) throw new InvalidDataException("Invalid profile avatar length.");
        if (size >= 0)
        {
            byte[] bytes = new byte[size];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            if (!CanExchange(peer)) return;
            _avatars.Set(bytes, peer.Id);
        }
        header[0] = 1;
        await stream.WriteAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false);
        // Keep the virtual session alive until the sender reads the confirmation.
        await stream.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false);
    }

    private static byte[] Digest(string version) => version.Length == 0 ? new byte[32] : Convert.FromHexString(version);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _node.RemoveProtocol(this);
        _worker.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
