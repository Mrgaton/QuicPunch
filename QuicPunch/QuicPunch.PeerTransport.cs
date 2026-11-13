using System.Diagnostics;
using QuicPunch.Helpers;

namespace QuicPunch;

public partial class QuicPunch
{
    private PeerTransportOptions _peerTransport = new();
    /// <summary>Applies to discovered peers in this node's discovery scope. Never grants trust or application permission.</summary>
    public PeerTransportOptions PeerTransport
    {
        get => _peerTransport;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            _peerTransport = value;
        }
    }

    private readonly Dictionary<Guid, int> _transportAttempts = new();
    private Task? _peerTransportTask;
    internal bool HasActiveIncomingWorker(Guid id) => _activeIncomingWorkers.ContainsKey(id);
    internal int ReservedPeerTransportCount
    {
        get { lock (HandshakeCoordinationLock) return _transportAttempts.Count; }
    }

    // Called under the same gate as handshake glare and outgoing single flight.
    internal bool TryReservePeerTransport(Guid peerId, long generation)
    {
        lock (HandshakeCoordinationLock)
        {
            if (!IsStarted || generation != LifecycleGeneration || LifecycleToken.IsCancellationRequested) return false;
            if (!_transportAttempts.ContainsKey(peerId) && !_peerMultiplexers.ContainsKey(peerId) &&
                _transportAttempts.Keys.Union(_peerMultiplexers.Keys).Count() >= PeerTransport.MaxConnectedPeers)
                return false;
            _transportAttempts.TryGetValue(peerId, out int owners);
            _transportAttempts[peerId] = owners + 1;
            return true;
        }
    }

    internal void ReleasePeerTransport(Guid peerId, long generation)
    {
        lock (HandshakeCoordinationLock)
        {
            if (generation != LifecycleGeneration || !_transportAttempts.TryGetValue(peerId, out int owners)) return;
            if (owners <= 1) _transportAttempts.Remove(peerId);
            else _transportAttempts[peerId] = owners - 1;
        }
    }

    private sealed class TransportRetry
    {
        public PeerInfo Peer = null!;
        public int Failures;
        public double RetryJitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        public long FailedAt;
        public bool Parked;
        public Multiplexer.QuicPeerMultiplexer? LastMultiplexer;
        public byte[]? SessionNonce;
    }

    private async Task PeerTransportLoopAsync(long generation, CancellationToken token)
    {
        var retries = new Dictionary<Guid, TransportRetry>();
        var attempts = new Dictionary<Guid, Task<Exception?>>();
        long networkGeneration = Volatile.Read(ref _networkGeneration);
        try
        {
            while (IsLifecycleWorkerCurrent(generation, token))
            {
                foreach (var expired in _sessionRecovery.Expire(PeerTransport.SessionRecoveryWindow)) RaiseSessionState(expired);
                if (networkGeneration != Volatile.Read(ref _networkGeneration))
                {
                    networkGeneration = Volatile.Read(ref _networkGeneration);
                    foreach (var retry in retries.Values) { retry.Failures = 0; retry.FailedAt = 0; }
                }
                foreach (var entry in attempts.Where(x => x.Value.IsCompleted).ToArray())
                {
                    var error = await entry.Value.ConfigureAwait(false);
                    if (retries.TryGetValue(entry.Key, out var completed))
                    {
                        if (error == null) completed.Failures = 0;
                        else
                        {
                            completed.FailedAt = Stopwatch.GetTimestamp();
                            completed.Failures++;
                        }
                    }
                    attempts.Remove(entry.Key);
                }
                foreach (var id in retries.Keys.Where(id => !AvailablePeers.ContainsKey(id)).ToArray())
                    if (!attempts.ContainsKey(id)) retries.Remove(id);

                foreach (var peer in AvailablePeers.Values)
                {
                    if (!retries.TryGetValue(peer.Id, out var retry) || !ReferenceEquals(retry.Peer, peer) ||
                        !(retry.SessionNonce ?? []).AsSpan().SequenceEqual(peer.SessionNonce ?? []))
                        retries[peer.Id] = retry = new TransportRetry { Peer = peer, SessionNonce = peer.SessionNonce?.ToArray() };

                    bool keepConnected = peer.TryGetCertificateHash(out var savedHash) && PeerStore?.TryGet(savedHash, out var savedContact) == true && savedContact?.AutoConnect == true;
                    if (keepConnected) retry.Parked = false;
                    if (TryGetPeerMultiplexer(peer.Id, out var mux) && mux != null && !mux.IsDisposed)
                    {
                        retry.LastMultiplexer = mux;
                        retry.Failures = 0;
                        if (!keepConnected && !mux.HasApplicationSessions &&
                            Stopwatch.GetElapsedTime(mux.LastApplicationActivityTimestamp) >= PeerTransport.IdleTimeout)
                        {
                            // Park until rediscovery creates a new peer or explicit application intent
                            // reconnects it. Periodic Hello/QUIC heartbeat must not keep idle tunnels alive.
                            retry.Parked = true;
                            await mux.CloseIdleTransportAsync(token).ConfigureAwait(false);
                        }
                        else retry.Parked = false;
                        continue;
                    }
                    if (!keepConnected && retry.LastMultiplexer?.IdleCloseRequested == true) retry.Parked = true;

                    bool automaticSelected = peer.TryGetCertificateHash(out var hash) && PeerStore?.TryGet(hash, out var saved) == true && saved != null
                        ? IsAutomaticContactEligible(saved) : PeerTransport.AutoConnectDiscoveredPeers;
                    bool selected = _sessionIntents.Keys.Any(x => x.Peer == peer.Id) || automaticSelected;
                    if (!selected || retry.Parked || attempts.ContainsKey(peer.Id) ||
                        IsPeerMultiplexerActiveOrConnecting(peer.Id) || !peer.HasCertificate || !peer.IsSessionReady ||
                        peer.ActiveEndPoint == null || !peer.IsAvailableAndResponsive() ||
                        (peer.ActiveTransport == TransportType.Tor ? !IsTorStarted : !IsWanStarted || QuicPunchConnection.AndroidQuicUnavailableReason != null))
                        continue;
                    double delayMs = Math.Min(PeerTransport.MaxRetryDelay.TotalMilliseconds,
                        PeerTransport.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(retry.Failures - 1, 16)));
                    if (retry.Failures > 0 && Stopwatch.GetElapsedTime(retry.FailedAt).TotalMilliseconds < delayMs * retry.RetryJitter)
                        continue;
                    if (attempts.Count >= PeerTransport.MaxConcurrentAttempts) continue;
                    attempts[peer.Id] = ConnectDiscoveredPeerAsync(peer, token);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            try { await Task.WhenAll(attempts.Values).ConfigureAwait(false); } catch { }
        }
    }

    private async Task<Exception?> ConnectDiscoveredPeerAsync(PeerInfo peer, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PeerTransport.AttemptTimeout);
        try
        {
            await EnsureMultiplexerConnectedAsync(peer, cancellationToken: deadline.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            QuicPunchLog.Info($"[PEER TRANSPORT] Connection to {peer.Name} deferred: {ex.Message}");
            return ex;
        }
    }
}
