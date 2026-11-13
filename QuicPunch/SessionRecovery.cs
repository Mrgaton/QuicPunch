using System.Diagnostics;

namespace QuicPunch;

public enum SessionState { Connected, Reconnecting, Recovered, Closed }

/// <summary>State of a logical application session across physical connections.</summary>
public sealed record SessionStateChange(Guid PeerId, Guid ProtocolId, Guid SessionId, SessionState State);

// Grants are scoped to one authenticated peer, protocol and logical session. They
// survive only transport loss, never an application close, revocation or restart.
internal sealed class SessionRecoveryRegistry
{
    private sealed record Grant(Guid Owner, long DetachedAt, TaskCompletionSource Cleanup);
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Peer, Guid Protocol, Guid Session), Grant> _grants = new();
    private readonly Dictionary<(Guid Peer, Guid Protocol, Guid Session), long> _denied = new();
    private readonly Dictionary<(Guid Peer, Guid Protocol, Guid Session), (long At, Lazy<Task<bool>> Decision)> _decisions = new();

    internal Task<bool> GetDecisionAsync(Guid peer, Guid protocol, Guid session, TimeSpan window, Func<Task<bool>> decide)
    {
        Lazy<Task<bool>> decision;
        lock (_gate)
        {
            foreach (var key in _decisions.Where(p => Stopwatch.GetElapsedTime(p.Value.At) >= window)
                         .Select(p => p.Key).ToArray()) _decisions.Remove(key);
            var keyForSession = (peer, protocol, session);
            if (_decisions.TryGetValue(keyForSession, out var existing)) decision = existing.Decision;
            else
            {
                if (_decisions.Count >= 4096 || _decisions.Keys.Count(k => k.Peer == peer) >= 128)
                    return Task.FromResult(false);
                decision = new Lazy<Task<bool>>(decide);
                _decisions.Add(keyForSession, (Stopwatch.GetTimestamp(), decision));
            }
        }
        return decision.Value;
    }

    internal bool HasGrant(Guid peer, Guid protocol, Guid session)
    { lock (_gate) return _grants.ContainsKey((peer, protocol, session)); }
    internal bool WasDenied(Guid peer, Guid protocol, Guid session, TimeSpan window)
    {
        lock (_gate)
        {
            foreach (var key in _denied.Where(p => Stopwatch.GetElapsedTime(p.Value) >= window).Select(p => p.Key).ToArray()) _denied.Remove(key);
            return _denied.ContainsKey((peer, protocol, session));
        }
    }
    internal void RememberDenied(Guid peer, Guid protocol, Guid session)
    {
        lock (_gate)
        {
            if (_denied.Count >= 4096) _denied.Remove(_denied.MinBy(p => p.Value).Key);
            _denied[(peer, protocol, session)] = Stopwatch.GetTimestamp();
        }
    }

    internal bool TryStart(Guid peer, Guid protocol, Guid session, Guid owner, bool resume, TimeSpan window)
    {
        lock (_gate)
        {
            foreach (var key in _grants.Where(x => x.Value.DetachedAt != 0 &&
                Stopwatch.GetElapsedTime(x.Value.DetachedAt) >= window).Select(x => x.Key).ToArray())
            { _grants[key].Cleanup.TrySetCanceled(); _grants.Remove(key); }
            var keyForSession = (peer, protocol, session);
            if (session == Guid.Empty || owner == Guid.Empty) return false;
            if (_denied.TryGetValue(keyForSession, out long deniedAt) && Stopwatch.GetElapsedTime(deniedAt) < window) return false;
            if (resume)
            {
                if (!_grants.TryGetValue(keyForSession, out var grant) || grant.DetachedAt == 0 || !grant.Cleanup.Task.IsCompletedSuccessfully) return false;
            }
            else if (_grants.ContainsKey(keyForSession) || _grants.Count >= 4096 ||
                _grants.Keys.Count(x => x.Peer == peer) >= 128) return false;
            _grants[keyForSession] = new(owner, 0, new(TaskCreationOptions.RunContinuationsAsynchronously));
            return true;
        }
    }

    internal bool Finish(Guid peer, Guid protocol, Guid session, Guid owner, bool transportLost)
    {
        lock (_gate)
        {
            var key = (peer, protocol, session);
            if (!_grants.TryGetValue(key, out var grant) || grant.Owner != owner) return false;
            if (transportLost)
                _grants[key] = grant with { DetachedAt = grant.DetachedAt == 0 ? Stopwatch.GetTimestamp() : grant.DetachedAt };
            else
            {
                grant.Cleanup.TrySetCanceled(); _grants.Remove(key); _decisions.Remove(key);
                RememberDenied(peer, protocol, session);
                return true;
            }
            return false;
        }
    }

    internal void Revoke(Guid peer, Guid? protocol = null)
    {
        lock (_gate)
        {
            foreach (var key in _decisions.Keys.Where(x => x.Peer == peer && (!protocol.HasValue || x.Protocol == protocol)).ToArray())
            {
                RememberDenied(key.Peer, key.Protocol, key.Session);
                _decisions.Remove(key);
            }
            foreach (var key in _grants.Keys.Where(x => x.Peer == peer && (!protocol.HasValue || x.Protocol == protocol)).ToArray())
            { RememberDenied(key.Peer, key.Protocol, key.Session); _grants[key].Cleanup.TrySetCanceled(); _grants.Remove(key); }
        }
    }

    internal void Clear() { lock (_gate) { foreach (var grant in _grants.Values) grant.Cleanup.TrySetCanceled(); _grants.Clear(); _denied.Clear(); _decisions.Clear(); } }
    internal Task WaitForCleanup(Guid peer, Guid protocol, Guid session)
    {
        lock (_gate) return _grants.TryGetValue((peer, protocol, session), out var grant)
            ? grant.Cleanup.Task : Task.CompletedTask;
    }
    internal void HandlerFinished(Guid peer, Guid protocol, Guid session, Guid owner)
    {
        lock (_gate)
            if (_grants.TryGetValue((peer, protocol, session), out var grant) && grant.Owner == owner)
                grant.Cleanup.TrySetResult();
    }
    internal bool Contains(Guid peer, Guid protocol, TimeSpan window)
    {
        lock (_gate) return _grants.Any(x => x.Key.Peer == peer && x.Key.Protocol == protocol &&
            (x.Value.DetachedAt == 0 || Stopwatch.GetElapsedTime(x.Value.DetachedAt) < window));
    }
    internal SessionStateChange[] Expire(TimeSpan window)
    {
        lock (_gate)
        {
            var expired = _grants.Where(x => x.Value.DetachedAt != 0 && Stopwatch.GetElapsedTime(x.Value.DetachedAt) >= window)
                .Select(x => x.Key).ToArray();
            foreach (var key in expired) { _grants[key].Cleanup.TrySetCanceled(); _grants.Remove(key); }
            return expired.Select(x => new SessionStateChange(x.Peer, x.Protocol, x.Session, SessionState.Closed)).ToArray();
        }
    }
}
