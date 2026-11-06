using System.Buffers.Binary;
using System.Text.Json;

namespace QuicNet.Video;

internal sealed record EncodedScreenFrame(byte[] Data, bool KeyFrame, long TimestampUs);

// Streaming, video-only Matroska demuxer. FFmpeg supplies packet boundaries, key
// flags, presentation timestamps and codec private data; pipe reads do not.
internal sealed class MatroskaVideoReader(Stream stream, int frameRate = 30) : IScreenVideoReader
{
    private const int MaxElementBytes = 10 * 1024 * 1024;
    public byte[] CodecPrivate { get; private set; } = [];
    public string CodecId { get; private set; } = "";
    public int Width { get; private set; }
    public int Height { get; private set; }
    private long _timecodeScale = 1_000_000;
    private long _clusterTimecode;
    private long _position;

    public async Task ReadAsync(Func<EncodedScreenFrame, Task> output, CancellationToken ct)
        => await ReadElementsAsync(long.MaxValue, output, ct).ConfigureAwait(false);

    private async Task ReadElementsAsync(long end, Func<EncodedScreenFrame, Task> output, CancellationToken ct)
    {
        while (_position < end)
        {
            var id = await ReadVintAsync(true, ct).ConfigureAwait(false);
            if (id == null) return;
            var size = await ReadVintAsync(false, ct).ConfigureAwait(false) ?? throw new EndOfStreamException();
            long elementEnd = size.Unknown ? long.MaxValue : checked(_position + (long)size.Value);
            if (elementEnd > end) throw new InvalidDataException("Matroska element exceeds its parent.");
            switch (id.Value.Value)
            {
                case 0x18538067: // Segment (unknown length for live output)
                case 0x1549A966: // Info
                case 0x1654AE6B: // Tracks
                case 0xAE:       // TrackEntry
                case 0xE0:       // Video
                case 0x1F43B675: // Cluster
                    await ReadElementsAsync(elementEnd, output, ct).ConfigureAwait(false);
                    break;
                default:
                    if (size.Unknown || size.Value > MaxElementBytes)
                        throw new InvalidDataException("Invalid or oversized Matroska element.");
                    byte[] data = new byte[(int)size.Value];
                    await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
                    _position += data.Length;
                    switch (id.Value.Value)
                    {
                        case 0x2AD7B1: _timecodeScale = checked((long)Unsigned(data)); break;
                        case 0x86: CodecId = System.Text.Encoding.UTF8.GetString(data); break;
                        case 0x63A2: CodecPrivate = data; break;
                        case 0xB0: Width = checked((int)Unsigned(data)); break;
                        case 0xBA: Height = checked((int)Unsigned(data)); break;
                        case 0xE7: _clusterTimecode = checked((long)Unsigned(data)); break;
                        case 0xA3: await output(ParseBlock(data)).ConfigureAwait(false); break;
                        case 0xA0: // BlockGroup (used by some FFmpeg builds)
                            await output(ParseBlockGroup(data)).ConfigureAwait(false); break;
                    }
                    break;
            }
        }
    }

    private EncodedScreenFrame ParseBlockGroup(byte[] data)
    {
        byte[]? block = null;
        bool key = true;
        int offset = 0;
        while (offset < data.Length)
        {
            ulong id = SpanVint(data, ref offset, true);
            int size = checked((int)SpanVint(data, ref offset, false));
            if (size > data.Length - offset) throw new InvalidDataException("Truncated BlockGroup.");
            if (id == 0xA1) block = data.AsSpan(offset, size).ToArray();
            if (id == 0xFB) key = false;
            offset += size;
        }
        return ParseBlock(block ?? throw new InvalidDataException("Missing video block.")) with { KeyFrame = key };
    }

    private EncodedScreenFrame ParseBlock(byte[] data)
    {
        int offset = 0;
        ulong track = SpanVint(data, ref offset, false);
        if (track != 1 || data.Length - offset < 4) throw new InvalidDataException("Unexpected video track.");
        short relative = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset, 2));
        byte flags = data[offset + 2];
        if ((flags & 0x06) != 0) throw new InvalidDataException("Laced video is unsupported.");
        long timestamp = checked((_clusterTimecode + relative) * _timecodeScale / 1000);
        return new(data.AsSpan(offset + 3).ToArray(), (flags & 0x80) != 0, timestamp);
    }

    private async Task<(ulong Value, bool Unknown)?> ReadVintAsync(bool id, CancellationToken ct)
    {
        byte[] one = new byte[1];
        if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) return null;
        _position++;
        int length = VintLength(one[0]);
        ulong value = id ? one[0] : (ulong)(one[0] & (0xFF >> length));
        for (int i = 1; i < length; i++)
        {
            await stream.ReadExactlyAsync(one, ct).ConfigureAwait(false);
            _position++;
            value = (value << 8) | one[0];
        }
        return (value, !id && value == (1UL << (7 * length)) - 1);
    }

    private static int VintLength(byte first)
    {
        for (int i = 1; i <= 8; i++) if ((first & (1 << (8 - i))) != 0) return i;
        throw new InvalidDataException("Invalid EBML integer.");
    }

    private static ulong SpanVint(byte[] data, ref int offset, bool id)
    {
        if (offset >= data.Length) throw new InvalidDataException("Truncated EBML integer.");
        int length = VintLength(data[offset]);
        if (length > data.Length - offset) throw new InvalidDataException("Truncated EBML integer.");
        ulong value = id ? data[offset] : (ulong)(data[offset] & (0xFF >> length));
        offset++;
        for (int i = 1; i < length; i++) value = (value << 8) | data[offset++];
        return value;
    }

    private static ulong Unsigned(byte[] data)
    {
        if (data.Length > 8) throw new InvalidDataException("Oversized EBML integer.");
        ulong value = 0;
        foreach (byte b in data) value = (value << 8) | b;
        return value;
    }

    public string WebCodecString()
    {
        var d = CodecPrivate;
        if (CodecId == "V_MPEG4/ISO/AVC" && d.Length >= 4)
            return $"avc1.{d[1]:X2}{d[2]:X2}{d[3]:X2}";
        if (CodecId == "V_MPEGH/ISO/HEVC" && d.Length >= 13)
        {
            uint compatibility = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(2, 4));
            uint reversed = 0;
            for (int i = 0; i < 32; i++) { reversed = (reversed << 1) | (compatibility & 1); compatibility >>= 1; }
            string space = (d[1] >> 6) switch { 1 => "A", 2 => "B", 3 => "C", _ => "" };
            string constraints = string.Join(".", d.Skip(6).Take(6).Reverse().SkipWhile(b => b == 0).Reverse().Select(b => b.ToString("X2")));
            return $"hvc1.{space}{d[1] & 31}.{reversed:X}.{((d[1] & 32) != 0 ? "H" : "L")}{d[12]}" + (constraints.Length > 0 ? "." + constraints : "");
        }
        if (CodecId == "V_AV1" && d.Length >= 3)
            return $"av01.{d[1] >> 5}.{d[1] & 31:D2}{((d[2] & 128) != 0 ? "H" : "M")}.{((d[2] & 64) == 0 ? 8 : (d[2] & 32) == 0 ? 10 : 12):D2}";
        if (CodecId == "V_VP9")
        {
            long pixelsPerSecond = (long)Width * Height * frameRate;
            string level = (long)Width * Height <= 983040 && pixelsPerSecond <= 36_864_000 ? "31" : (long)Width * Height <= 2228224 && pixelsPerSecond <= 83_558_400 ? "40" :
                (long)Width * Height <= 2228224 && pixelsPerSecond <= 160_432_128 ? "41" : (long)Width * Height <= 8912896 && pixelsPerSecond <= 311_951_360 ? "50" :
                (long)Width * Height <= 8912896 && pixelsPerSecond <= 588_251_136 ? "51" : (long)Width * Height <= 8912896 && pixelsPerSecond <= 1_176_502_272 ? "52" : pixelsPerSecond <= 1_176_502_272 ? "60" : pixelsPerSecond <= 2_353_004_544 ? "61" : "62";
            return $"vp09.00.{level}.08";
        }
        throw new InvalidDataException("FFmpeg did not provide a supported codec configuration.");
    }

    public string? WebDescription => CodecId is "V_MPEG4/ISO/AVC" or "V_MPEGH/ISO/HEVC" ? Convert.ToBase64String(CodecPrivate) : null;

    public byte[] BuildWirePacket(EncodedScreenFrame frame, long generation)
    {
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new QuicNet.Serialization.ScreenFrameMetadata(WebCodecString(), WebDescription, frame.TimestampUs, generation, Width, Height), QuicNet.Serialization.SharedJson.Options);
        byte[] packet = new byte[3 + metadata.Length + frame.Data.Length];
        packet[0] = (byte)((frame.KeyFrame ? 1 : 0) | 2);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1, 2), checked((ushort)metadata.Length));
        metadata.CopyTo(packet, 3);
        frame.Data.CopyTo(packet, 3 + metadata.Length);
        return packet;
    }
}
