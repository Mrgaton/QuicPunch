using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using QuicPunch.Helpers;

namespace QuicPunch;

public partial class QuicPunch
{
    private readonly SessionRecoveryRegistry _sessionRecovery = new();
    private readonly ConcurrentDictionary<(Guid Peer, Guid Protocol), SessionIntent> _sessionIntents = new();
    private readonly SemaphoreSlim _networkRefreshGate = new(1, 1);
    private long _networkGeneration;
    private TaskCompletionSource _networkChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<SessionStateChange>? OnSessionStateChanged;
    public bool HasLogicalProtocolSession(Guid peerId, Guid protocolId) =>
        _sessionIntents.ContainsKey((peerId, protocolId)) || _sessionRecovery.Contains(peerId, protocolId, PeerTransport.SessionRecoveryWindow);

    private sealed class SessionIntent(Guid sessionId, CancellationTokenSource lifetime)
    {
        internal Guid Id => sessionId;
        internal volatile bool Recovering;
        internal void Cancel() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
    }
    private bool HasRecoveringSession(Guid peerId) => _sessionIntents.Any(x => x.Key.Peer == peerId && x.Value.Recovering);

    internal async Task<bool> AuthorizeRecoverableSessionAsync(PeerInfo peer, Guid protocol, Guid session,
        Guid owner, bool resume, CancellationToken ct)
    {
        if (!IsStarted || !IsTrustedPeer(peer) || !ProtocolHandlers.TryGetValue(protocol, out var handler) ||
            !handler.SupportsSessionRecovery || !handler.CanStartSession || handler.IncomingSessionPolicy(peer) == false) return false;
        long generation = LifecycleGeneration;
        if (resume)
        {
            try { await _sessionRecovery.WaitForCleanup(peer.Id, protocol, session).WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        }
        if (!resume && !await AuthorizeIncomingSessionAsync(peer, protocol, peer.ActiveTransport, ct).WaitAsync(ct).ConfigureAwait(false)) return false;
        ct.ThrowIfCancellationRequested();
        if (!IsStarted || generation != LifecycleGeneration || !IsTrustedPeer(peer) || handler.IncomingSessionPolicy(peer) == false) return false;
        return _sessionRecovery.TryStart(peer.Id, protocol, session, owner, resume, PeerTransport.SessionRecoveryWindow);
    }

    internal void FinishRecoverableSession(PeerInfo peer, Guid protocol, Guid session, Guid owner, bool lost)
    {
        if (_sessionRecovery.Finish(peer.Id, protocol, session, owner, lost && IsStarted && IsTrustedPeer(peer)))
            RaiseSessionState(new(peer.Id, protocol, session, SessionState.Closed));
    }
    internal void RecoveryHandlerFinished(PeerInfo peer, Guid protocol, Guid session, Guid owner) =>
        _sessionRecovery.HandlerFinished(peer.Id, protocol, session, owner);

    internal void RaiseSessionState(SessionStateChange change)
    {
        foreach (Action<SessionStateChange> observer in OnSessionStateChanged?.GetInvocationList() ?? [])
            try { observer(change); } catch (Exception ex) { QuicPunchLog.Info($"[SESSION] Observer failed: {ex.Message}"); }
    }

    /// <summary>Ends an application session, including a suspended recovery attempt.</summary>
    public async Task CloseProtocolSessionAsync(Guid peerId, Guid protocolId)
    {
        _sessionRecovery.Revoke(peerId, protocolId);
        if (_sessionIntents.TryGetValue((peerId, protocolId), out var intent)) intent.Cancel();
        await UnregisterProtocolSessionAsync(peerId, protocolId).ConfigureAwait(false);
    }

    private void RevokeRecovery(Guid peerId)
    {
        _sessionRecovery.Revoke(peerId);
        foreach (var entry in _sessionIntents.Where(x => x.Key.Peer == peerId)) entry.Value.Cancel();
    }

    private async Task RunProtocolSessionAsync(IProtocolHandler handler, PeerInfo peer, ushort port,
        TaskCompletionSource<object?> connected, CancellationToken callerToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(LifecycleToken, callerToken);
        var intent = new SessionIntent(Guid.NewGuid(), lifetime);
        var key = (peer.Id, handler.ProtocolId);
        if (!_sessionIntents.TryAdd(key, intent)) throw new InvalidOperationException("A logical session already exists.");
        CancellationTokenSource? recoveryDeadline = null;
        bool recovering = false;
        int failures = 0;
        try
        {
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!IsTrustedPeer(peer) || handler.IncomingSessionPolicy(peer) == false)
                    throw new UnauthorizedAccessException("Session authority was revoked.");
                QuicConnection? connection = null;
                bool lost = false;
                try
                {
                    using var attempt = recoveryDeadline == null
                        ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)
                        : CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, recoveryDeadline.Token);
                    if (recovering) attempt.CancelAfter(PeerTransport.AttemptTimeout);
                    var mux = await EnsureMultiplexerConnectedAsync(peer, port, attempt.Token).ConfigureAwait(false);
                    peer = mux.Peer;
                    connection = await mux.OpenVirtualSessionAsync(handler.ProtocolId, intent.Id, recovering, attempt.Token).ConfigureAwait(false);
                    await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, attempt.Token).ConfigureAwait(false);
                    try { stream.Transport.Priority16 = handler.StreamPriority; } catch { }
                    if (!await RegisterProtocolSessionAsync(peer.Id, handler.ProtocolId, connection, stream, LifecycleGeneration).ConfigureAwait(false))
                        throw new OperationCanceledException("Session registration failed.", lifetime.Token);
                    attempt.CancelAfter(Timeout.InfiniteTimeSpan);
                    recoveryDeadline?.Dispose();
                    recoveryDeadline = null;
                    failures = 0;
                    connected.TrySetResult(null);
                    RaiseSessionState(new(peer.Id, handler.ProtocolId, intent.Id, recovering ? SessionState.Recovered : SessionState.Connected));
                    recovering = false;
                    intent.Recovering = false;
                    using var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, connection.ApplicationSessionToken);
                    try { await handler.HandleAsync(connection, stream, peer, session.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (session.IsCancellationRequested && !lifetime.IsCancellationRequested && !connection.TransportLost) { }
                    finally { await UnregisterProtocolSessionAsync(peer.Id, handler.ProtocolId, connection).ConfigureAwait(false); }
                    lost = connection.TransportLost;
                }
                catch (Exception ex) when (!lifetime.IsCancellationRequested &&
                    ex is not SessionRejectedException and not UnauthorizedAccessException &&
                    ((recovering && connection == null) || (handler.SupportsSessionRecovery && connection?.TransportLost == true)))
                {
                    lost = true;
                    QuicPunchLog.Info($"[SESSION RECOVERY] {peer.Name}/{handler.ProtocolName}: {ex.Message}");
                }
                finally
                {
                    if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
                }
                if (!lost || !handler.SupportsSessionRecovery) return;
                if (!recovering) RaiseSessionState(new(peer.Id, handler.ProtocolId, intent.Id, SessionState.Reconnecting));
                recovering = true;
                intent.Recovering = true;
                recoveryDeadline ??= new CancellationTokenSource(PeerTransport.SessionRecoveryWindow);
                using var retry = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, recoveryDeadline.Token);
                double delay = Math.Min(PeerTransport.SessionMaxRetryDelay.TotalMilliseconds,
                    PeerTransport.SessionRetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(failures++, 8)));
                Task wake = Volatile.Read(ref _networkChanged).Task;
                await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(delay * (0.8 + Random.Shared.NextDouble() * 0.4)), retry.Token), wake).ConfigureAwait(false);
                retry.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested &&
            !callerToken.IsCancellationRequested && IsStarted && !LifecycleToken.IsCancellationRequested) { }
        finally
        {
            recoveryDeadline?.Dispose();
            _sessionIntents.TryRemove(new KeyValuePair<(Guid Peer, Guid Protocol), SessionIntent>(key, intent));
            RaiseSessionState(new(peer.Id, handler.ProtocolId, intent.Id, SessionState.Closed));
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs args) => _ = ObserveNetworkChangeAsync();
    private async Task ObserveNetworkChangeAsync()
    {
        try { await NotifyNetworkChangedAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { QuicPunchLog.Info($"[NETWORK CHANGE] Refresh failed: {ex.Message}"); }
    }

    /// <summary>Host/mobile network adapters can report a route change without restarting the node or identity.</summary>
    public async Task NotifyNetworkChangedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsStarted) return;
        CancellationToken wanToken = _wanLoopCts?.Token ?? LifecycleToken;
        Interlocked.Increment(ref _networkGeneration);
        Interlocked.Exchange(ref _networkChanged, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        foreach (var interrogation in ActiveInterrogations.Keys) CancelInterrogation(interrogation);
        foreach (var flight in _activeOutboundNegotiations.Values) try { flight.Cts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var mux in _peerMultiplexers.Values) mux.TrySendTransportPing();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(LifecycleToken, cancellationToken);
        if (!await _networkRefreshGate.WaitAsync(0, lifetime.Token).ConfigureAwait(false)) return;
        try
        {
            if (!IsWanStarted) return;
            // Advertise immediately on the new LAN, before refreshing WAN mappings and relays.
            await Discovery.SendLocalLanDiscoveryAsync().ConfigureAwait(false);
            // Never aggregate mappings from different interfaces into one NAT range.
            long observed;
            do
            {
                observed = Volatile.Read(ref _networkGeneration);
                _advertisedMapping = null;
                IsUpnpMapped = false;
                ActivePortMappingProtocol = null;
                await NatCoordinator.RefreshForNetworkChangeAsync(lifetime.Token).ConfigureAwait(false);
            } while (observed != Volatile.Read(ref _networkGeneration) && !lifetime.IsCancellationRequested);
            lifetime.Token.ThrowIfCancellationRequested();
            // Recreate stale relay TCP sockets rather than waiting for TCP's old
            // interface to time out. Discovery retains its node/WAN lifetime.
            await Discovery.RestartWanNostrDiscoveryAsync(wanToken).ConfigureAwait(false);
            await Discovery.PublishNostrDiscoveryAsync(lifetime.Token).ConfigureAwait(false);
        }
        finally { _networkRefreshGate.Release(); }
    }
}

public sealed class SessionRejectedException(string message) : InvalidOperationException(message);

