using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using CatalogTests.Baseline;
using QuicNet.Geolocation;

if (args.Length == 0) throw new ArgumentException("fixtures <dir> | verify <GLP1-dir> <GLP2-dir> | bench <legacy|compact> <data-dir> <samples-file>");
switch (args[0])
{
    case "fixtures": Fixtures(args[1]); break;
    case "verify": Verify(args[1], args[2]); break;
    case "samples": Samples(args[1], args[2]); break;
    case "bench": Bench(args[1], args[2], args[3]); break;
    default: throw new ArgumentException("Unknown command");
}

static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
static IpCatalogTable Open(string path, bool asn = false) => new(() => File.OpenRead(path), asn);
static string Text(BinaryReader reader) => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
static UInt128 Number(ReadOnlySpan<byte> key) => BinaryPrimitives.ReadUInt128BigEndian(key[1..]);

static void Fixtures(string directory)
{
    var city = Open(Path.Combine(directory, "city.bin"));
    using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "expected.json")));
    foreach (var row in expected.RootElement.EnumerateArray())
    {
        byte family = row.GetProperty("family").GetByte();
        UInt128 first = UInt128.Parse(row.GetProperty("first").GetString()!), last = UInt128.Parse(row.GetProperty("last").GetString()!);
        uint id = city.FindValue(family, first) ?? throw new Exception("Missing fixture start");
        Check(city.FindValue(family, last) == id, "Fixture end differs");
        var result = city.Location(id, "fixture");
        Check(BitConverter.SingleToInt32Bits((float)result.Position.Latitude) == row.GetProperty("latitudeBits").GetInt32() &&
            BitConverter.SingleToInt32Bits((float)result.Position.Longitude) == row.GetProperty("longitudeBits").GetInt32() &&
            result.City == row.GetProperty("city").GetString() && result.Country == row.GetProperty("country").GetString() &&
            result.Region == row.GetProperty("region").GetString(), "Fixture value/float bits differ");
        if (family == 4) Check(city.FindValue(family, last + 1) is null, "Gap matched a range");
    }
    var asn = Open(Path.Combine(directory, "asn.bin"), true);
    Check(asn.FindValue(4, 0) == uint.MaxValue && asn.FindValue(4, uint.MaxValue) == uint.MaxValue &&
        asn.FindValue(6, UInt128.MaxValue) == 0, "ASN maximum/zero or whole-family range lost");
    foreach (string path in Directory.GetFiles(directory, "*.bad"))
    {
        bool rejected = false;
        try { var bad = Open(path, true); _ = bad.FindValue(4, 0); _ = bad.FindValue(6, UInt128.MaxValue); }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { rejected = true; }
        Check(rejected, "Accepted malformed input: " + path);
    }
    Parallel.For(0, 20_000, i => Check(city.FindValue(4, (uint)(i % 300 * 3)) is not null, "Concurrent lookup failed"));
    var data = new EmbeddedCatalog();
    Check(data.Lookup(IPAddress.Parse("8.8.8.8")) is { Country: "US", City: "Mountain View" }, "Embedded GLP2 city lookup");
    Check(data.Lookup(IPAddress.Parse("::ffff:8.8.8.8")) == data.Lookup(IPAddress.Parse("8.8.8.8")), "Mapped IPv4 differs");
    Check(data.Lookup(IPAddress.Loopback) is null && data.LookupAsn(IPAddress.Parse("100.64.1.1")) is null, "Private exclusion lost");
    Check(data.Lookup(IPAddress.Parse("2001:218::")) is { Country: "JP", City: "", Region: "" }, "Country-only resolution lost");
    Check(data.LookupAsn(IPAddress.Parse("2001:4860:4860::8888")) == 15169, "Embedded IPv6 ASN");
    using var sources = JsonDocument.Parse(data.Manifest);
    foreach (string name in new[] { "city", "asn", "routers" })
        Check(sources.RootElement.GetProperty("tables").GetProperty(name).GetProperty("format").GetString() == "GLP2", "Embedded legacy resource");
    Console.WriteLine("PASS cross-language fixtures, 8/16-bit palettes, float bits, UTF-8, gaps, both IP families, overflow/corruption rejection, concurrent and embedded lookups");
}

static void Verify(string legacy, string compact)
{
    long total = 0, queries = 0;
    foreach (string name in new[] { "city", "asn", "routers" })
    {
        bool asn = name == "asn";
        var table = Open(Path.Combine(compact, name + ".bin"), asn);
        using var file = File.OpenRead(Path.Combine(legacy, name + ".bin")); using var header = new BinaryReader(file);
        Check(Encoding.ASCII.GetString(header.ReadBytes(4)) == "GLP1", "Expected original GLP1");
        int pages = header.ReadInt32(); var entries = new (long Offset, int Length, int Count)[pages];
        for (int i = 0; i < pages; i++) { header.ReadBytes(34); entries[i] = (header.ReadInt64(), header.ReadInt32(), header.ReadInt32()); }
        long count = 0; byte previousFamily = 0; UInt128 previousLast = 0;
        foreach (var page in entries)
        {
            file.Position = page.Offset; using var source = new MemoryStream(header.ReadBytes(page.Length));
            using var gzip = new GZipStream(source, CompressionMode.Decompress); using var reader = new BinaryReader(gzip);
            for (int i = 0; i < page.Count; i++)
            {
                byte[] start = reader.ReadBytes(17), end = reader.ReadBytes(17); byte family = start[0];
                UInt128 first = Number(start), last = Number(end);
                uint id = table.FindValue(family, first) ?? throw new Exception($"{name} missing start at row {count}");
                Check(table.FindValue(family, last) == id && table.FindValue(family, first + (last - first) / 2) == id,
                    $"{name} end/middle mismatch at row {count}");
                queries += 3;
                if (family == previousFamily && previousLast < UInt128.MaxValue && first > previousLast + 1)
                {
                    Check(table.FindValue(family, previousLast + 1) is null && table.FindValue(family, first - 1) is null, "Gap collapsed");
                    queries += 2;
                }
                if (asn) Check(id == reader.ReadUInt32(), "ASN value mismatch");
                else
                {
                    int lat = reader.ReadInt32(), lon = reader.ReadInt32(); string country = Text(reader), region = Text(reader), city = Text(reader);
                    var actual = table.Location(id, "verification");
                    Check(BitConverter.SingleToInt32Bits((float)actual.Position.Latitude) == lat &&
                        BitConverter.SingleToInt32Bits((float)actual.Position.Longitude) == lon &&
                        actual.Country == country && actual.Region == region && actual.City == city, $"{name} value mismatch at row {count}");
                }
                previousFamily = family; previousLast = last; count++;
            }
        }
        total += count; Console.WriteLine($"PASS {name}: {count:N0} original ranges, every coordinate bit and UTF-8 field, endpoints/middles/gaps");
    }
    Console.WriteLine($"PASS ALL: {total:N0} ranges, {queries:N0} runtime queries");
}

static void Samples(string legacy, string output)
{
    using var file = File.OpenRead(Path.Combine(legacy, "city.bin")); using var reader = new BinaryReader(file);
    reader.ReadBytes(4); int pages = reader.ReadInt32(); var addresses = new List<string>();
    for (int i = 0; i < pages; i++)
    {
        byte[] key = reader.ReadBytes(17); reader.ReadBytes(17); reader.ReadInt64(); reader.ReadInt32(); reader.ReadInt32();
        var ip = new IPAddress(key.AsSpan(key[0] == 4 ? 13 : 1));
        if (EmbeddedCatalog.IsPublic(ip)) addresses.Add(ip.ToString());
    }
    File.WriteAllLines(output, addresses); Console.WriteLine($"Saved {addresses.Count} identical benchmark addresses");
}

static void Bench(string mode, string directory, string samples)
{
    var addresses = File.ReadAllLines(samples).Select(IPAddress.Parse).ToArray();
    Func<IPAddress, PublishedLocation?> lookup;
    object owner;
    if (mode == "legacy")
    {
        LegacyCatalog.DataDirectory = directory;
        var catalog = new LegacyCatalog(); owner = catalog; lookup = catalog.Lookup;
    }
    else
    {
        var table = new Lazy<IpCatalogTable>(() => Open(Path.Combine(directory, "city.bin")));
        owner = table;
        lookup = ip => !EmbeddedCatalog.IsPublic(ip) ? null : table.Value.FindValue(ip) is { } id ? table.Value.Location(id, "IPTrust geolocation 2026-10-22") : null;
    }
    long before = GC.GetTotalMemory(true), allocated = GC.GetAllocatedBytesForCurrentThread();
    var watch = Stopwatch.StartNew(); var result = lookup(addresses[0]); watch.Stop();
    Check(result is not null, "Missing cold result");
    double coldMs = watch.Elapsed.TotalMilliseconds;
    long coldAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated, coldRetained = GC.GetTotalMemory(true) - before;
    for (int i = 0; i < 2000; i++) _ = lookup(addresses[0]); // tiered JIT warmup
    allocated = GC.GetAllocatedBytesForCurrentThread(); watch.Restart();
    for (int i = 0; i < 100_000; i++) _ = lookup(addresses[0]);
    watch.Stop(); double warmUs = watch.Elapsed.TotalMicroseconds / 100_000;
    long warmAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
    for (int i = 0; i < 32; i++) Check(lookup(addresses[i]) is not null, "Missing cache-fill result");
    long cacheRetained = GC.GetTotalMemory(true) - before;
    var timings = new double[Math.Min(512, addresses.Length)];
    allocated = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < timings.Length; i++)
    {
        long start = Stopwatch.GetTimestamp();
        Check(lookup(addresses[(i * 37 + 64) % addresses.Length]) is not null, "Missing distributed result");
        timings[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
    }
    long spreadAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
    Array.Sort(timings);
    Console.WriteLine(JsonSerializer.Serialize(new { mode, coldMs, coldAllocated, coldRetained, cacheRetained,
        warmUs, warmAllocatedBytesPerQuery = warmAllocated / 100_000.0,
        distributedP50Us = timings[timings.Length/2], distributedP95Us = timings[(int)(timings.Length*.95)],
        distributedAllocatedBytesPerQuery = spreadAllocated / (double)timings.Length,
        workingSetBytes = Environment.WorkingSet, runtime = Environment.Version.ToString(), bitness = IntPtr.Size * 8 }));
    GC.KeepAlive(owner);
}
