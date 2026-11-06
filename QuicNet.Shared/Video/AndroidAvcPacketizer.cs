using System.Buffers.Binary;
using System.Text.Json;
using QuicNet.Serialization;

namespace QuicNet.Video;

// Android MediaCodec emits Annex B. WebCodecs with an AVC description expects length-prefixed NALs.
internal sealed class AndroidAvcPacketizer(int width, int height, long generation)
{
    private byte[]? _sps, _pps;
    internal string Codec => _sps is { Length: >= 4 } s ? $"avc1.{s[1]:X2}{s[2]:X2}{s[3]:X2}" : "avc1.42E01F";
    internal string? Description { get; private set; }

    internal byte[]? Packet(byte[] annexB, long timestampUs, bool keyFrame)
    {
        var nals = Split(annexB);
        foreach (var nal in nals)
        {
            if ((nal[0] & 31) == 7) _sps = nal.Length >= 4 ? nal : throw new InvalidDataException("Truncated H264 SPS.");
            if ((nal[0] & 31) == 8) _pps = nal;
        }
        var video = nals.Where(n => (n[0] & 31) is not (7 or 8 or 9)).ToArray();
        if (_sps == null || _pps == null) return null;
        keyFrame |= video.Any(n => (n[0] & 31) == 5);
        byte[] description = new byte[11 + _sps.Length + _pps.Length];
        description[0] = 1; description[1] = _sps[1]; description[2] = _sps[2]; description[3] = _sps[3];
        description[4] = 255; description[5] = 225;
        BinaryPrimitives.WriteUInt16BigEndian(description.AsSpan(6), checked((ushort)_sps.Length));
        _sps.CopyTo(description, 8); int pos = 8 + _sps.Length; description[pos++] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(description.AsSpan(pos), checked((ushort)_pps.Length)); pos += 2;
        _pps.CopyTo(description, pos);
        Description = Convert.ToBase64String(description);
        if (video.Length == 0) return null;
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new ScreenFrameMetadata(Codec, Description, timestampUs, generation, width, height), SharedJson.Options);
        byte[] packet = new byte[3 + metadata.Length + video.Sum(n => 4 + n.Length)];
        packet[0] = (byte)(2 | (keyFrame ? 1 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), checked((ushort)metadata.Length));
        metadata.CopyTo(packet, 3); pos = 3 + metadata.Length;
        foreach (var nal in video)
        {
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(pos), nal.Length); pos += 4;
            nal.CopyTo(packet, pos); pos += nal.Length;
        }
        return packet;
    }

    internal static List<byte[]> Split(byte[] data)
    {
        var starts = new List<(int Offset, int Prefix)>();
        for (int i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            int prefix = data[i + 2] == 1 ? 3 : i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0;
            if (prefix == 0) continue;
            starts.Add((i, prefix)); i += prefix - 1;
        }
        if (starts.Count == 0) throw new InvalidDataException("MediaCodec did not emit Annex B H264.");
        var result = new List<byte[]>();
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].Offset + starts[i].Prefix, end = i + 1 < starts.Count ? starts[i + 1].Offset : data.Length;
            if (end > start) result.Add(data[start..end]);
        }
        return result;
    }
}
