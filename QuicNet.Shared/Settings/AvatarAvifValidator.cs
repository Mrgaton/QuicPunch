using System.Buffers.Binary;

namespace QuicNet.Settings;

/// <summary>Bound the primary image and AVIF animation track before storing or serving peer media.</summary>
internal static class AvatarAvifValidator
{
    private readonly record struct Part(int Start, int Length, string Type);
    private static uint U32(ReadOnlySpan<byte> bytes, int at = 0) => BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]);
    private static ushort U16(ReadOnlySpan<byte> bytes, int at = 0) => BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]);
    private static List<Part> Parts(ReadOnlySpan<byte> bytes)
    {
        var parts = new List<Part>();
        for (int at = 0; at < bytes.Length;)
        {
            if (bytes.Length - at < 8 || parts.Count >= 512) throw new InvalidDataException();
            uint length = U32(bytes, at);
            if (length < 8 || length > bytes.Length - at) throw new InvalidDataException();
            parts.Add(new Part(at, (int)length, System.Text.Encoding.ASCII.GetString(bytes.Slice(at + 4, 4))));
            at += (int)length;
        }
        return parts;
    }
    private static ReadOnlySpan<byte> Child(ReadOnlySpan<byte> bytes, string type)
    {
        var matches = Parts(bytes).Where(p => p.Type == type).ToArray();
        if (matches.Length != 1) throw new InvalidDataException();
        return bytes.Slice(matches[0].Start + 8, matches[0].Length - 8);
    }
    private static bool Square(uint width, uint height) => width is > 0 and <= ProfileAvatarStore.MaxSide && width == height;
    internal static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 32 or > ProfileAvatarStore.MaxBytes) return false;
        try
        {
            var top = Parts(bytes);
            var ftyp = Child(bytes, "ftyp");
            if (ftyp.Length < 8 || (ftyp.Length & 3) != 0) return false;
            bool animated = ftyp[..4].SequenceEqual("avis"u8);
            if (!animated && !ftyp[..4].SequenceEqual("avif"u8)) return false;
            bool sequenceBrand = false;
            for (int i = 8; i < ftyp.Length; i += 4) sequenceBrand |= ftyp.Slice(i, 4).SequenceEqual("avis"u8);
            if (sequenceBrand != animated) return false;
            var meta = Child(bytes, "meta");
            if (meta.Length < 4 || U32(meta) != 0) return false;
            var properties = Child(Child(meta[4..], "iprp"), "ipco");
            uint side = 0;
            foreach (var property in Parts(properties))
            {
                var data = properties.Slice(property.Start + 8, property.Length - 8);
                if (property.Type == "ispe")
                {
                    if (data.Length != 12 || U32(data) != 0 || !Square(U32(data, 4), U32(data, 8))) return false;
                    if (side != 0 && side != U32(data, 4)) return false;
                    side = U32(data, 4);
                }
                if (animated && property.Type == "auxC") return false;
            }
            if (side == 0) return false;
            var media = Child(bytes, "mdat");
            if (media.IsEmpty) return false;
            if (!animated) return !top.Any(p => p.Type == "moov");
            var movie = Child(bytes, "moov");
            var track = Child(movie, "trak"); // Exactly one image track: no audio or auxiliary tracks.
            var mdia = Child(track, "mdia");
            var header = Child(mdia, "mdhd");
            if (header.Length != 24 || U32(header) != 0 || U32(header, 12) != 1000 || U32(header, 16) is < 1 or > 10000) return false;
            uint duration = U32(header, 16);
            var handler = Child(mdia, "hdlr");
            if (handler.Length < 12 || !handler.Slice(8, 4).SequenceEqual("pict"u8)) return false;
            var table = Child(Child(mdia, "minf"), "stbl");
            var description = Child(table, "stsd");
            if (description.Length < 8 || U32(description) != 0 || U32(description, 4) != 1) return false;
            var entry = Child(description[8..], "av01");
            if (entry.Length < 78 || !Square(U16(entry, 24), U16(entry, 26)) || U16(entry, 24) != side) return false;
            if (!Child(entry[78..], "av1C").SequenceEqual(Child(properties, "av1C"))) return false;
            var sizes = Child(table, "stsz");
            if (sizes.Length < 12 || U32(sizes) != 0 || U32(sizes, 4) != 0) return false;
            uint count = U32(sizes, 8);
            if (count is < 2 or > 120 || sizes.Length != 12 + count * 4) return false;
            ulong total = 0;
            for (int i = 0; i < count; i++) { uint size = U32(sizes, 12 + i * 4); if (size == 0) return false; total += size; }
            if (total != (ulong)media.Length) return false;
            var timing = Child(table, "stts");
            if (timing.Length < 8 || U32(timing) != 0 || U32(timing, 4) is < 1 or > 120 || timing.Length != 8 + U32(timing, 4) * 8) return false;
            ulong samples = 0, ticks = 0;
            for (int i = 8; i < timing.Length; i += 8)
            {
                uint frames = U32(timing, i), delta = U32(timing, i + 4);
                if (frames == 0 || delta < 33 || delta > 10000) return false;
                samples += frames; ticks += (ulong)frames * delta;
            }
            if (samples != count || ticks != duration) return false;
            var chunks = Child(table, "stsc");
            if (chunks.Length != 20 || U32(chunks) != 0 || U32(chunks, 4) != 1 || U32(chunks, 8) != 1 || U32(chunks, 12) != count || U32(chunks, 16) != 1) return false;
            int dataStart = top.Single(p => p.Type == "mdat").Start + 8;
            var offsets = Child(table, "stco");
            if (offsets.Length != 12 || U32(offsets) != 0 || U32(offsets, 4) != 1 || U32(offsets, 8) != dataStart) return false;
            var location = Child(meta[4..], "iloc");
            return location.Length == 22 && U32(location) == 0 && location[4] == 0x44 && location[5] == 0 && U16(location, 6) == 1 &&
                U16(location, 10) == 0 && U16(location, 12) == 1 && U32(location, 14) == dataStart && U32(location, 18) == U32(sizes, 12);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or InvalidOperationException) { return false; }
    }
}
