using System.Buffers.Binary;
using System.Text.Json;
using QuicNet.Serialization;

namespace QuicNet.Video;

// FFmpeg outputs Annex B for AVC/HEVC and low-overhead OBUs for AV1.
// Repeat the sequence configuration in metadata so a late viewer can start at any keyframe.
internal sealed class NativeCapturePacketizer(byte codecId, int width, int height, long generation)
{
    private readonly Dictionary<int, byte[]> _parameters = new();
    internal string Codec { get; private set; } = codecId switch { 1 => "avc1", 5 => "hvc1", 4 => "av01", _ => throw new ArgumentException("Unsupported capture codec") };
    internal string? Description { get; private set; }
    internal byte[]? Packet(byte[] encoded, long timestamp, bool key)
    {
        if (codecId is 1 or 5)
        {
            foreach (byte[] nal in AndroidAvcPacketizer.Split(encoded))
            {
                int type = codecId == 1 ? nal[0] & 31 : (nal[0] >> 1) & 63;
                if (codecId == 1 ? type is 7 or 8 : type is 32 or 33 or 34) _parameters[type] = nal;
                if (codecId == 1 && type == 7 && nal.Length >= 4) Codec = $"avc1.{nal[1]:X2}{nal[2]:X2}{nal[3]:X2}";
                key |= codecId == 1 ? type == 5 : type is >= 16 and <= 21;
            }
            int[] required = codecId == 1 ? [7, 8] : [32, 33, 34];
            if (required.All(_parameters.ContainsKey))
            {
                using var configuration = new MemoryStream();
                foreach (int type in required) { configuration.Write([0, 0, 0, 1]); configuration.Write(_parameters[type]); }
                Description = Convert.ToBase64String(configuration.ToArray());
            }
        }
        else
        {
            int pos = 0;
            while (pos < encoded.Length)
            {
                int start = pos; byte header = encoded[pos++];
                if ((header & 0x80) != 0 || (header & 2) == 0) throw new InvalidDataException("Invalid AV1 OBU");
                if ((header & 4) != 0) pos++;
                ulong size = 0; int shift = 0; byte value;
                do { if (pos >= encoded.Length || shift > 49) throw new InvalidDataException("Invalid AV1 length"); value = encoded[pos++]; size |= (ulong)(value & 127) << shift; shift += 7; } while ((value & 128) != 0);
                if (size > (ulong)(encoded.Length - pos)) throw new InvalidDataException("Truncated AV1 OBU");
                pos += (int)size;
                if (((header >> 3) & 15) == 1) Description = Convert.ToBase64String(encoded.AsSpan(start, pos - start));
            }
        }
        if (Description == null) return null;
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new ScreenFrameMetadata(Codec, Description, timestamp, generation, width, height), SharedJson.Options);
        byte[] packet = new byte[3 + metadata.Length + encoded.Length];
        packet[0] = (byte)(2 | (key ? 1 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), checked((ushort)metadata.Length));
        metadata.CopyTo(packet, 3); encoded.CopyTo(packet, 3 + metadata.Length);
        return packet;
    }
}
