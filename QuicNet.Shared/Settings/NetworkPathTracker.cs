namespace QuicNet.Settings;

/// <summary>Tracks material route changes, ignoring initial/repeated Android callbacks.</summary>
public sealed class NetworkPathTracker(string? initialNetwork, string? initialPath)
{
    private readonly object _gate = new();
    private string? _network = initialNetwork;
    private string? _path = initialPath;

    public bool Update(string network, string path)
    {
        lock (_gate)
        {
            if (_network == network && _path == path) return false;
            _network = network; _path = path;
            return true;
        }
    }

    public bool Lost(string network)
    {
        lock (_gate)
        {
            // Android may report loss of the previous default AFTER the next
            // network is available. Do not cancel the new path's negotiations.
            if (_network != network) return false;
            _network = null; _path = null;
            return true;
        }
    }
}
