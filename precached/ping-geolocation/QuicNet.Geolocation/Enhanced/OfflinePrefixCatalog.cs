using System.Net;

namespace QuicNet.Geolocation;

/// <summary>Application-supplied offline ASN/geofeed metadata. No reputation API or reverse DNS.</summary>
public sealed class OfflinePrefixCatalog
{
    private sealed record Entry(byte[] Network, int Bits, PrefixMetadata Value);
    private readonly Entry[] _entries;
    public OfflinePrefixCatalog(OfflineMetadata source)
    {
        if (source.Prefixes is null || source.Prefixes.Length > 200000) throw new InvalidDataException("Offline prefix catalog exceeds its configured limit.");
        var entries = new List<Entry>();
        foreach (var item in source.Prefixes)
        {
            var parts = item.Prefix.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out int length) ||
                length < 0 || length > address.GetAddressBytes().Length * 8 || string.IsNullOrWhiteSpace(item.Source))
                throw new InvalidDataException("Invalid offline prefix: " + item.Prefix);
            item.Location?.Position.Validate();
            entries.Add(new(address.GetAddressBytes(), length, item));
        }
        _entries = entries.OrderByDescending(e => e.Bits).ToArray();
    }
    public PrefixMetadata? Lookup(IPAddress ip)
    {
        byte[] bytes = EmbeddedCatalog.Normalize(ip).GetAddressBytes();
        foreach (var e in _entries)
        {
            if (bytes.Length != e.Network.Length) continue;
            int full = e.Bits / 8, remainder = e.Bits % 8;
            if (!bytes.AsSpan(0, full).SequenceEqual(e.Network.AsSpan(0, full))) continue;
            if (remainder > 0 && (bytes[full] & (255 << (8 - remainder))) != (e.Network[full] & (255 << (8 - remainder)))) continue;
            return e.Value;
        }
        return null;
    }
}
