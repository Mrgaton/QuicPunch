using System.Buffers.Binary;
using System.Text.Json;

namespace QuicNet.Video;

internal interface IScreenVideoReader
{
    int Width { get; }
    int Height { get; }
    Task ReadAsync(Func<EncodedScreenFrame, Task> output, CancellationToken ct);
    string WebCodecString();
    string? WebDescription { get; }
    byte[] BuildWirePacket(EncodedScreenFrame frame, long generation);
}

// IVF is streamable without seeking or replacing the container's AV1 extradata.
// Parse OBUs to identify true random access frames and their actual profile/level.
internal sealed class Av1IvfVideoReader(Stream stream) : IScreenVideoReader
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? WebDescription => null;
    private string? _codec;
    private bool _reducedHeader;
    private byte[]? _sequenceHeader;

    public async Task ReadAsync(Func<EncodedScreenFrame, Task> output, CancellationToken ct)
    {
        byte[] header = new byte[32];
        await stream.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, 4).SequenceEqual("DKIF"u8) || !header.AsSpan(8, 4).SequenceEqual("AV01"u8) || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2)) != 32)
            throw new InvalidDataException("Expected AV1 IVF stream.");
        Width = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12, 2));
        Height = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14, 2));
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));
        uint scale = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
        if (rate == 0 || scale == 0) throw new InvalidDataException("Invalid IVF timebase.");
        byte[] frameHeader = new byte[12];
        while (true)
        {
            int first = await stream.ReadAsync(frameHeader.AsMemory(0, 1), ct);
            if (first == 0) return;
            await stream.ReadExactlyAsync(frameHeader.AsMemory(1), ct);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(frameHeader);
            if (size == 0 || size > 10 * 1024 * 1024) throw new InvalidDataException("Invalid IVF frame length.");
            byte[] frame = new byte[size];
            await stream.ReadExactlyAsync(frame, ct);
            long timestamp = checked((long)(BinaryPrimitives.ReadUInt64LittleEndian(frameHeader.AsSpan(4)) * scale * 1_000_000 / rate));
            bool key = ParseObus(frame, out bool containsSequence);
            if (key && !containsSequence && _sequenceHeader != null) frame = [.. _sequenceHeader, .. frame];
            await output(new(frame, key, timestamp));
        }
    }

    private bool ParseObus(byte[] data, out bool containsSequence)
    {
        bool key = false;
        containsSequence = false;
        int offset = 0;
        while (offset < data.Length)
        {
            int start = offset;
            byte header = data[offset++];
            if ((header & 0x81) != 0) throw new InvalidDataException("Invalid AV1 OBU.");
            int type = (header >> 3) & 15;
            if ((header & 4) != 0) { if (offset >= data.Length) throw new InvalidDataException("Truncated OBU extension."); offset++; }
            ulong size = 0;
            if ((header & 2) != 0)
            {
                for (int i = 0; ; i++)
                {
                    if (i >= 8 || offset >= data.Length) throw new InvalidDataException("Invalid OBU size.");
                    byte b = data[offset++]; size |= (ulong)(b & 127) << (i * 7);
                    if ((b & 128) == 0) break;
                }
            }
            else size = (ulong)(data.Length - offset);
            if (size > (ulong)(data.Length - offset)) throw new InvalidDataException("Truncated OBU.");
            if (type == 1)
            {
                containsSequence = true;
                _sequenceHeader = data.AsSpan(start, offset + (int)size - start).ToArray();
                ReadSequenceHeader(data.AsSpan(offset, (int)size));
            }
            if (type is 3 or 6 && size > 0 && _codec != null)
            {
                var bits = new Bits(data.AsSpan(offset, (int)size));
                if (_reducedHeader) key = true;
                else if (bits.Read(1) == 0) key = bits.Read(2) == 0;
            }
            offset += (int)size;
        }
        return key;
    }

    private void ReadSequenceHeader(ReadOnlySpan<byte> payload)
    {
        var bits = new Bits(payload);
        int profile = bits.Read(3);
        bits.Read(1); // still_picture
        _reducedHeader = bits.Read(1) != 0;
        int level;
        int tier = 0;
        if (_reducedHeader) level = bits.Read(5);
        else
        {
            if (bits.Read(1) != 0)
            {
                bits.Skip(64); // timing_info
                if (bits.Read(1) != 0) bits.Uvlc();
                if (bits.Read(1) != 0) bits.Skip(47); // decoder_model_info
            }
            bits.Read(1); // initial_display_delay_present_flag
            bits.Read(5); // operating_points_cnt_minus_1
            bits.Skip(12); // first operating_point_idc
            level = bits.Read(5);
            if (level > 7) tier = bits.Read(1);
        }
        // This backend deliberately outputs yuv420p (8-bit SDR).
        _codec = $"av01.{profile}.{level:D2}{(tier == 0 ? "M" : "H")}.08";
    }

    public string WebCodecString() => _codec ?? throw new InvalidDataException("Missing AV1 sequence header.");
    public byte[] BuildWirePacket(EncodedScreenFrame frame, long generation)
    {
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new QuicNet.Serialization.Av1FrameMetadata(WebCodecString(), frame.TimestampUs, generation, Width, Height), QuicNet.Serialization.SharedJson.Options);
        byte[] packet = new byte[3 + metadata.Length + frame.Data.Length];
        packet[0] = (byte)((frame.KeyFrame ? 1 : 0) | 2);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), checked((ushort)metadata.Length));
        metadata.CopyTo(packet, 3); frame.Data.CopyTo(packet, 3 + metadata.Length);
        return packet;
    }

    private ref struct Bits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;
        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++) { if (_offset >= _data.Length * 8) throw new InvalidDataException("Truncated AV1 header."); value = (value << 1) | ((_data[_offset / 8] >> (7 - _offset % 8)) & 1); _offset++; }
            return value;
        }
        public void Skip(int count) { if (count > _data.Length * 8 - _offset) throw new InvalidDataException("Truncated AV1 header."); _offset += count; }
        public void Uvlc() { int zeroes = 0; while (Read(1) == 0 && ++zeroes < 32) { } if (zeroes < 32) Skip(zeroes); }
    }
}
