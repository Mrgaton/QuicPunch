namespace QuicNet.Vpn;

public sealed class VpnPeerPolicy
{
    public string Identity { get; }
    public uint Address { get; }
    public bool AutoConnect { get; }
    public bool AutoAccept { get; }
    public bool Paused { get; }
    public bool Broadcast { get; }
    public bool Forwarding { get; }
    public Ipv4Prefix[] Sources { get; }
    public Ipv4Prefix[] Destinations { get; }
    public Ipv4Prefix[] Routes { get; }
    public VpnPeerPolicy(VpnPeerConfiguration configuration, string automaticAddress, uint localAddress, bool simple, bool autoConnectEnabled = true)
    {
        Identity = configuration.CertHash.ToUpperInvariant();
        Address = Ipv4Prefix.Address((simple && configuration.Mode == "simple") || string.IsNullOrEmpty(configuration.VirtualAddress) ? automaticAddress : configuration.VirtualAddress);
        AutoConnect = configuration.AutoConnect && autoConnectEnabled;
        AutoAccept = configuration.AutoAccept;
        Paused = configuration.Paused;
        Broadcast = simple || configuration.AllowBroadcast;
        Forwarding = !simple && configuration.AllowForwarding;
        Sources = simple ? [new(Address, 32)] : configuration.AllowedSources.Select(Ipv4Prefix.Parse).Append(new(Address, 32)).Distinct().ToArray();
        Destinations = simple ? [new(localAddress, 32)] : configuration.AccessibleDestinations.Select(Ipv4Prefix.Parse).ToArray();
        Routes = simple ? [new(Address, 32)] : configuration.Routes.Select(Ipv4Prefix.Parse).Append(new(Address, 32)).Distinct().OrderByDescending(p => p.Length).ToArray();
    }
}

/// <summary>One immutable policy is published per revision. Routing ownership also authenticates packet sources.</summary>
public sealed class VpnPolicy
{
    private readonly Dictionary<string, VpnPeerPolicy> _peers;
    private readonly (Ipv4Prefix Prefix, string Identity)[] _owners;
    private readonly (Ipv4Prefix Prefix, string Identity)[] _routes;
    public uint LocalAddress { get; }
    public IEnumerable<VpnPeerPolicy> Peers => _peers.Values;
    public VpnPolicy(uint localAddress, IEnumerable<VpnPeerPolicy> peers)
    {
        LocalAddress = localAddress;
        _peers = peers.ToDictionary(p => p.Identity, StringComparer.OrdinalIgnoreCase);
        if (_peers.Values.Any(p => p.Address == localAddress) || _peers.Values.GroupBy(p => p.Address).Any(g => g.Count() > 1))
            throw new InvalidDataException("VPN address collision. Assign distinct virtual addresses to these identities.");
        var owners = _peers.Values.SelectMany(p => p.Routes.Concat(p.Sources).Distinct().Select(prefix => (Prefix: prefix, Identity: p.Identity))).ToArray();
        if (owners.GroupBy(o => o.Prefix).Any(g => g.Select(o => o.Identity).Distinct().Count() > 1))
            throw new InvalidDataException("Ambiguous IP ownership between peers.");
        _owners = owners.OrderByDescending(o => o.Prefix.Length).ToArray();
        _routes = _peers.Values.SelectMany(p => p.Routes.Select(r => (Prefix: r, Identity: p.Identity))).OrderByDescending(r => r.Prefix.Length).ToArray();
    }
    public VpnPeerPolicy? Peer(string identity) => _peers.GetValueOrDefault(identity);
    public string? Route(uint destination)
    {
        if (destination == LocalAddress) return null;
        // A configured, offline, more-specific owner must never fall through to a connected default gateway.
        foreach (var route in _routes) if (route.Prefix.Contains(destination)) return route.Identity;
        return null;
    }
    public bool AcceptSource(string identity, uint source)
    {
        if (source == LocalAddress || source >> 24 is 0 or 127 || source >= 0xE0000000) return false;
        var peer = Peer(identity);
        if (peer == null || peer.Paused) return false;
        bool permitted = false;
        foreach (var prefix in peer.Sources) if (prefix.Contains(source)) { permitted = true; break; }
        if (!permitted) return false;
        foreach (var owner in _owners) if (owner.Prefix.Contains(source)) return owner.Identity == peer.Identity;
        return false;
    }
    public bool AcceptDestination(string identity, uint destination, bool broadcast)
    {
        var peer = Peer(identity);
        if (peer == null || peer.Paused) return false;
        if (broadcast) return peer.Broadcast;
        foreach (var prefix in peer.Destinations) if (prefix.Contains(destination)) return true;
        return false;
    }
}

public interface IVpnControl
{
    VpnConfiguration Configuration { get; }
    string ConfigurationPath { get; }
    string? ConfigurationError { get; }
    object OrchestrationStatus { get; }
    object PeerDefaults { get; }
    void SaveConfiguration(VpnConfiguration configuration, long expectedRevision);
    void PausePeer(Guid peerId);
    void ResumePeer(Guid peerId);
}
