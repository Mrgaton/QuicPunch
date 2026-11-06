using System.Buffers.Binary;
using System.Diagnostics;

namespace QuicNet.Vpn;

/// <summary>Bounded return-path permissions for locally initiated, unfragmented TCP/UDP/ICMP traffic.</summary>
public sealed class VpnReplyTracker
{
    private readonly record struct Flow(string Identity, uint Local, uint Remote, byte Protocol, ushort LocalPort, ushort RemotePort);
    private readonly Dictionary<Flow, long> _flows = new();
    private readonly object _sync = new();
    private readonly int _capacity;
    public VpnReplyTracker(int capacity = 8192) => _capacity = capacity;
    public void Clear() { lock (_sync) _flows.Clear(); }
    public void Record(string identity, ReadOnlySpan<byte> packet, Ipv4Packet ip)
    {
        if (!TryFlow(identity, packet, ip, false, out var flow)) return;
        lock (_sync)
        {
            if (_flows.Count >= _capacity)
            {
                long now = Stopwatch.GetTimestamp();
                foreach (var key in _flows.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _flows.Remove(key);
                if (_flows.Count >= _capacity && !_flows.ContainsKey(flow)) return;
            }
            int seconds = ip.Protocol == 6 ? 120 : 30;
            _flows[flow] = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        }
    }
    public bool IsReply(string identity, ReadOnlySpan<byte> packet, Ipv4Packet ip)
    {
        if (!TryFlow(identity, packet, ip, true, out var flow)) return false;
        lock (_sync) return _flows.TryGetValue(flow, out long expires) && expires > Stopwatch.GetTimestamp();
    }
    private static bool TryFlow(string identity, ReadOnlySpan<byte> packet, Ipv4Packet ip, bool incoming, out Flow flow)
    {
        flow = default;
        if ((BinaryPrimitives.ReadUInt16BigEndian(packet[6..8]) & 0x3FFF) != 0) return false;
        var payload = packet[ip.HeaderLength..];
        ushort sourcePort, destinationPort;
        if (ip.Protocol is 6 or 17)
        {
            if (payload.Length < (ip.Protocol == 6 ? 20 : 8)) return false;
            sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload);
            destinationPort = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        }
        else if (ip.Protocol == 1)
        {
            if (payload.Length < 8 || payload[0] != (incoming ? 0 : 8) || payload[1] != 0) return false;
            sourcePort = destinationPort = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        }
        else return false;
        flow = incoming ? new(identity, ip.Destination, ip.Source, ip.Protocol, destinationPort, sourcePort)
            : new(identity, ip.Source, ip.Destination, ip.Protocol, sourcePort, destinationPort);
        return true;
    }
}
