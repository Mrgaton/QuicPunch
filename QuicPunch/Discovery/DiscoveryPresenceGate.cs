namespace QuicPunch.Discovery;

// One reply per remote discovery incarnation, independent of endpoint changes.
// Bounded by identities, and never consulted before certificate/author validation.
internal sealed class DiscoveryPresenceGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _incarnations = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    internal bool Observe(string identity, string? incarnation)
    {
        if (!Guid.TryParseExact(incarnation, "N", out _)) return false;
        lock (_sync)
        {
            if (_incarnations.TryGetValue(identity, out var previous) && previous == incarnation) return false;
            if (!_incarnations.ContainsKey(identity))
            {
                if (_incarnations.Count >= 256) _incarnations.Remove(_order.Dequeue());
                _order.Enqueue(identity);
            }
            _incarnations[identity] = incarnation!;
            return true;
        }
    }
}
