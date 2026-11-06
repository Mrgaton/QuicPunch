namespace QuicPunch;

/// <summary>Controls authenticated peer transport independently of application trust.</summary>
public sealed record PeerTransportOptions
{
    public bool AutoConnectDiscoveredPeers { get; init; } = true;
    public int MaxConnectedPeers { get; init; } = 64;
    public int MaxConcurrentAttempts { get; init; } = 4;
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan SessionRecoveryWindow { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan SessionRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan SessionMaxRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (MaxConnectedPeers < 1 || MaxConcurrentAttempts < 1 ||
            AttemptTimeout <= TimeSpan.Zero || IdleTimeout <= TimeSpan.Zero ||
            InitialRetryDelay <= TimeSpan.Zero || MaxRetryDelay < InitialRetryDelay ||
            HeartbeatInterval <= TimeSpan.Zero || HeartbeatTimeout <= HeartbeatInterval ||
            SessionRecoveryWindow <= TimeSpan.Zero || SessionRetryDelay <= TimeSpan.Zero ||
            SessionMaxRetryDelay < SessionRetryDelay)
            throw new ArgumentOutOfRangeException(nameof(PeerTransportOptions));
    }
}
