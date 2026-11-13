// Frozen pre-GLP2 reader for reproducible build-time comparisons only.
// Only resource opening differs: both benchmark readers use the same FileStream access.
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using QuicNet.Geolocation;

namespace CatalogTests.Baseline;


/// <summary>Immutable embedded snapshot; never downloads, writes a cache or resolves DNS.</summary>
public sealed class LegacyCatalog
{
    private static readonly Lazy<(string Source, string? Date)> CityMetadata = new(() =>
    {
        using var stream = Resource("manifest.json");
        using var manifest = JsonDocument.Parse(stream);
        var city = manifest.RootElement.GetProperty("tables").GetProperty("city");
        return (city.GetProperty("source").GetString()!, city.GetProperty("databaseDate").GetString());
    });
    public static LegacyCatalog Default { get; } = new();
    private readonly Lazy<PagedTable?> _city = new(() => HasResource("city.bin") ? new("city.bin", false) : null);
    private readonly Lazy<PagedTable?> _asn = new(() => HasResource("asn.bin") ? new("asn.bin", true) : null);
    public string Manifest { get { using var reader = new StreamReader(Resource("manifest.json")); return reader.ReadToEnd(); } }
    public string Notice { get { using var reader = new StreamReader(Resource("NOTICE.txt")); return reader.ReadToEnd(); } }

    public PublishedLocation? Lookup(IPAddress ip) => IsPublic(ip) ? _city.Value?.Find(Normalize(ip))?.Location : null;
    public uint? LookupAsn(IPAddress ip) => IsPublic(ip) ? _asn.Value?.Find(Normalize(ip))?.Asn : null;

    internal static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    public static bool IsPublic(IPAddress address)
    {
        var ip = Normalize(address);
        var b = ip.GetAddressBytes();
        if (b.Length == 4)
            return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
                !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
                !(b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2)) &&
                !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) &&
                !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Global unicast only, excluding documentation ranges.
        return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8) &&
            !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
    }

    public bool HasIpLocationDatabase => HasResource("city.bin");
    public string IpLocationSource => CityMetadata.Value.Source;
    public string? IpLocationDatabaseDate => CityMetadata.Value.Date;
    public bool HasAsnDatabase => HasResource("asn.bin");
    internal static string DataDirectory { get; set; } = "";
    internal static bool HasResource(string name) => File.Exists(Path.Combine(DataDirectory, name));
    internal static Stream Resource(string name) => File.OpenRead(Path.Combine(DataDirectory, name));

    private sealed record Entry(byte[] Start, byte[] End, long Offset, int Length, int Count);
    private sealed record Row(byte[] Start, byte[] End, PublishedLocation? Location, uint? Asn);
    private sealed class PagedTable
    {
        private readonly string _name;
        private readonly bool _asn;
        private readonly Entry[] _index;
        private readonly Dictionary<int, Row[]> _cache = [];
        private readonly Queue<int> _order = [];
        private readonly object _gate = new();
        public PagedTable(string name, bool asn)
        {
            _name = name; _asn = asn;
            using var input = Resource(name);
            using var reader = new BinaryReader(input);
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "GLP1") throw new InvalidDataException("Unsupported catalogue format");
            int count = reader.ReadInt32();
            if (count is < 1 or > 100_000) throw new InvalidDataException("Invalid catalogue directory");
            _index = new Entry[count];
            for (int i = 0; i < count; i++)
                _index[i] = new(reader.ReadBytes(17), reader.ReadBytes(17), reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32());
        }
        public Row? Find(IPAddress ip)
        {
            var key = new byte[17]; var bytes = ip.GetAddressBytes();
            key[0] = (byte)(bytes.Length == 4 ? 4 : 6); bytes.CopyTo(key, 17 - bytes.Length);
            int lo = 0, hi = _index.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2; var page = _index[mid];
                if (Compare(key, page.Start) < 0) hi = mid - 1;
                else if (Compare(key, page.End) > 0) lo = mid + 1;
                else
                {
                    Row[] rows;
                    lock (_gate)
                    {
                        if (!_cache.TryGetValue(mid, out rows!))
                        {
                            rows = Read(page);
                            if (_cache.Count == 32) _cache.Remove(_order.Dequeue());
                            _cache.Add(mid, rows); _order.Enqueue(mid);
                        }
                    }
                    int left = 0, right = rows.Length - 1;
                    while (left <= right)
                    {
                        int center = (left + right) / 2; var row = rows[center];
                        if (Compare(key, row.Start) < 0) right = center - 1;
                        else if (Compare(key, row.End) > 0) left = center + 1;
                        else return row;
                    }
                    return null;
                }
            }
            return null;
        }
        private Row[] Read(Entry page)
        {
            using var stream = Resource(_name);
            stream.Seek(page.Offset, SeekOrigin.Begin);
            var bytes = new byte[page.Length]; stream.ReadExactly(bytes);
            using var source = new MemoryStream(bytes);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8);
            var result = new Row[page.Count];
            string Text() => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
            for (int i = 0; i < result.Length; i++)
            {
                var start = reader.ReadBytes(17); var end = reader.ReadBytes(17);
                if (_asn) result[i] = new(start, end, null, reader.ReadUInt32());
                else result[i] = new(start, end, new(new(reader.ReadSingle(), reader.ReadSingle()), Text(), Text(), Text(),
                    _name == "city.bin" ? CityMetadata.Value.Source + " " + CityMetadata.Value.Date : "RIPE IPmap 2026-10-18"), null);
            }
            return result;
        }
        private static int Compare(byte[] left, byte[] right) => left.AsSpan().SequenceCompareTo(right);
    }
}
