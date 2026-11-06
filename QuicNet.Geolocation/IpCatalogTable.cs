using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace QuicNet.Geolocation;

/// <summary>GLP2 reader. Keeps the dictionary binary and only a bounded cache of numeric pages/results.</summary>
internal sealed class IpCatalogTable
{
    private const int MaxRows = 4096, MaxPages = 100_000, CacheBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Func<Stream> _open;
    private readonly bool _asn;
    private readonly Entry[] _index;
    private readonly byte[] _dictionary;
    private readonly int[] _strings, _locations;
    private readonly string[] _countries;
    private readonly Dictionary<int, NumericPage> _pages = [];
    private readonly Queue<int> _pageOrder = [];
    private readonly Dictionary<uint, PublishedLocation> _results = [];
    private readonly Queue<uint> _resultOrder = [];
    private readonly object _gate = new();
    private int _cachedBytes;

    private readonly record struct Entry(byte Family, UInt128 First, UInt128 Last, long Offset, int Length, int Count);
    private sealed record NumericPage(uint[]? Starts4, uint[]? Ends4, UInt128[]? Starts6, UInt128[]? Ends6,
        byte[]? Ids8, ushort[]? Ids16, uint[] Palette)
    {
        public int Count => Ids8?.Length ?? Ids16!.Length;
        public int Bytes => Count * ((Starts4 is null ? 32 : 8) + (Ids8 is null ? 2 : 1)) + Palette.Length * 4;
        public UInt128 Start(int i) => Starts4 is not null ? Starts4[i] : Starts6![i];
        public UInt128 End(int i) => Ends4 is not null ? Ends4[i] : Ends6![i];
        public uint Value(int i) => Palette[Ids8 is not null ? Ids8[i] : Ids16![i]];
    }

    internal IpCatalogTable(Func<Stream> open, bool asn)
    {
        _open = open; _asn = asn;
        using var input = open();
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("GLP2"u8))
            throw new InvalidDataException("Expected GLP2 catalogue. Run scripts/prepare-geolocation.py before building.");
        if (reader.ReadByte() != (asn ? 1 : 0)) throw Invalid("table kind");
        uint dictionaryLength = reader.ReadUInt32(), pageCount = reader.ReadUInt32();
        if (pageCount is 0 or > MaxPages || dictionaryLength > 64 * 1024 * 1024 || (asn && dictionaryLength != 0) || (!asn && dictionaryLength == 0))
            throw Invalid("header");
        _index = new Entry[pageCount];
        long nextOffset = 0;
        for (int i = 0; i < _index.Length; i++)
        {
            byte family = reader.ReadByte();
            UInt128 first = ReadAddress(reader, family), last = ReadAddress(reader, family);
            long offset = reader.ReadUInt32(); uint length = reader.ReadUInt32(); int count = reader.ReadUInt16();
            if (first > last || count is < 1 or > MaxRows || length is 0 or > 1024 * 1024 ||
                offset > input.Length - length || (i > 0 && offset != nextOffset)) throw Invalid("page bounds");
            if (i > 0 && (family < _index[i - 1].Family || family == _index[i - 1].Family && first <= _index[i - 1].Last))
                throw Invalid("page order");
            _index[i] = new(family, first, last, offset, (int)length, count);
            nextOffset = offset + length;
        }
        if (_index[0].Offset != input.Position + dictionaryLength || nextOffset != input.Length) throw Invalid("directory length");
        if (asn)
        {
            _dictionary = []; _strings = []; _locations = []; _countries = [];
            return;
        }
        _dictionary = Inflate(input, (int)dictionaryLength, 64 * 1024 * 1024);
        var data = new Cursor(_dictionary);
        int countries = data.Count(256); _countries = new string[countries];
        for (int i = 0; i < countries; i++) _countries[i] = Utf8.GetString(data.Text());
        _strings = new int[data.Count(2_000_000)];
        for (int i = 0; i < _strings.Length; i++) { _strings[i] = data.Position; data.Text(); }
        _locations = new int[data.Count(2_000_000)];
        for (int i = 0; i < _locations.Length; i++)
        {
            _locations[i] = data.Position;
            float lat = data.Single(), lon = data.Single();
            if (!float.IsFinite(lat) || !float.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180 || data.Byte() >= countries ||
                data.UInt32() >= _strings.Length || data.UInt32() >= _strings.Length) throw Invalid("location dictionary");
        }
        data.Finish();
    }

    internal uint? FindValue(IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!ip.TryWriteBytes(bytes, out int length)) throw Invalid("IP address");
        return FindValue((byte)(length == 4 ? 4 : 6), length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt128BigEndian(bytes));
    }

    internal uint? FindValue(byte family, UInt128 address)
    {
        int lo = 0, hi = _index.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2; var entry = _index[mid];
            if (family < entry.Family || family == entry.Family && address < entry.First) hi = mid - 1;
            else if (family > entry.Family || address > entry.Last) lo = mid + 1;
            else
            {
                lock (_gate)
                {
                    if (!_pages.TryGetValue(mid, out var page))
                    {
                        page = ReadPage(entry);
                        while (_pages.Count > 0 && (_pages.Count >= 32 || _cachedBytes + page.Bytes > CacheBytes))
                        {
                            int removed = _pageOrder.Dequeue(); _cachedBytes -= _pages[removed].Bytes; _pages.Remove(removed);
                        }
                        _pages.Add(mid, page); _pageOrder.Enqueue(mid); _cachedBytes += page.Bytes;
                    }
                    int left = 0, right = page.Count - 1;
                    while (left <= right)
                    {
                        int center = (left + right) / 2;
                        if (address < page.Start(center)) right = center - 1;
                        else if (address > page.End(center)) left = center + 1;
                        else return page.Value(center);
                    }
                    return null;
                }
            }
        }
        return null;
    }

    internal PublishedLocation Location(uint id, string source)
    {
        if (_asn || id >= _locations.Length) throw Invalid("location ID");
        lock (_gate)
        {
            if (_results.TryGetValue(id, out var result)) return result;
            var data = new Cursor(_dictionary, _locations[id]);
            var point = new GeoPoint(data.Single(), data.Single());
            string country = _countries[data.Byte()], region = Text(data.UInt32()), city = Text(data.UInt32());
            result = new(point, country, region, city, source);
            if (_results.Count >= 512) _results.Remove(_resultOrder.Dequeue());
            _results.Add(id, result); _resultOrder.Enqueue(id);
            return result;
        }
    }

    private string Text(uint id) { var data = new Cursor(_dictionary, _strings[id]); return Utf8.GetString(data.Text()); }

    private NumericPage ReadPage(Entry entry)
    {
        using var stream = _open(); stream.Seek(entry.Offset, SeekOrigin.Begin);
        var data = new Cursor(Inflate(stream, entry.Length, 1024 * 1024));
        var palette = new uint[data.Count(entry.Count)];
        for (int i = 0; i < palette.Length; i++)
        {
            palette[i] = data.UInt32();
            if (!_asn && palette[i] >= _locations.Length) throw Invalid("palette location ID");
        }
        uint[]? starts4 = entry.Family == 4 ? new uint[entry.Count] : null, ends4 = entry.Family == 4 ? new uint[entry.Count] : null;
        UInt128[]? starts6 = entry.Family == 6 ? new UInt128[entry.Count] : null, ends6 = entry.Family == 6 ? new UInt128[entry.Count] : null;
        byte[]? ids8 = palette.Length <= 256 ? new byte[entry.Count] : null;
        ushort[]? ids16 = ids8 is null ? new ushort[entry.Count] : null;
        UInt128 cursor = entry.First;
        for (int i = 0; i < entry.Count; i++)
        {
            UInt128 start, end;
            try { start = checked(cursor + data.Varint()); end = checked(start + data.Varint()); }
            catch (OverflowException e) { throw new InvalidDataException("GLP2 range overflow", e); }
            int id = ids8 is not null ? data.Byte() : data.UInt16();
            if (id >= palette.Length || end > entry.Last || i == 0 && start != entry.First) throw Invalid("range");
            if (ids8 is not null) ids8[i] = (byte)id; else ids16![i] = (ushort)id;
            if (starts4 is not null) { starts4[i] = (uint)start; ends4![i] = (uint)end; }
            else { starts6![i] = start; ends6![i] = end; }
            if (i == entry.Count - 1) { if (end != entry.Last) throw Invalid("last range"); }
            else
            {
                if (end == UInt128.MaxValue) throw Invalid("range overflow");
                cursor = end + 1;
            }
        }
        data.Finish();
        return new(starts4, ends4, starts6, ends6, ids8, ids16, palette);
    }

    private static UInt128 ReadAddress(BinaryReader reader, byte family)
    {
        int width = family switch { 4 => 4, 6 => 16, _ => throw Invalid("address family") };
        Span<byte> bytes = stackalloc byte[16]; reader.BaseStream.ReadExactly(bytes[..width]);
        return width == 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }

    private static byte[] Inflate(Stream stream, int length, int maximum)
    {
        byte[] compressed = new byte[length]; stream.ReadExactly(compressed);
        using var input = new MemoryStream(compressed, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8192];
        int read;
        while ((read = brotli.Read(buffer)) != 0)
        {
            if (output.Length + read > maximum) throw Invalid("decompressed size");
            output.Write(buffer[..read]);
        }
        return output.ToArray();
    }

    private static InvalidDataException Invalid(string detail) => new("Invalid GLP2 " + detail);

    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> bytes;
        public Cursor(ReadOnlySpan<byte> bytes, int position = 0) { this.bytes = bytes; Position = position; }
        public int Position { get; private set; }
        public byte Byte() { if (Position >= bytes.Length) throw Invalid("truncated payload"); return bytes[Position++]; }
        public ushort UInt16() { var data = Take(2); return BinaryPrimitives.ReadUInt16LittleEndian(data); }
        public float Single() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public UInt128 Varint()
        {
            UInt128 result = 0;
            for (int i = 0; i < 19; i++)
            {
                byte value = Byte();
                if (i == 18 && value > 3) throw Invalid("varint overflow");
                result |= (UInt128)(value & 127) << (i * 7);
                if ((value & 128) == 0) return result;
            }
            throw Invalid("unterminated varint");
        }
        public uint UInt32()
        {
            uint result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte value = Byte();
                if (shift == 28 && value > 15) throw Invalid("UInt32 overflow");
                result |= (uint)(value & 127) << shift;
                if ((value & 128) == 0) return result;
            }
            throw Invalid("unterminated UInt32 varint");
        }
        public int Count(int maximum) { uint n = UInt32(); return n is > 0 && n <= maximum ? (int)n : throw Invalid("count"); }
        public ReadOnlySpan<byte> Text() { uint n = UInt32(); return n <= 65535 ? Take((int)n) : throw Invalid("text length"); }
        private ReadOnlySpan<byte> Take(int n)
        {
            if (n > bytes.Length - Position) throw Invalid("truncated payload");
            var result = bytes.Slice(Position, n); Position += n; return result;
        }
        public void Finish() { if (Position != bytes.Length) throw Invalid("trailing payload"); }
    }
}
