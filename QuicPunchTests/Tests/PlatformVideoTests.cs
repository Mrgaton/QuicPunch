using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using QuicNet.Serialization;
using QuicNet.Video;

namespace QuicPunchTests.Tests;

public static class PlatformVideoTests
{
    public static async Task RunAsync()
    {
        var codec = new AndroidAvcPacketizer(320, 180, 42);
        byte[] sps = [0, 0, 0, 1, 0x67, 0x42, 0, 0x1F, 0xAA];
        byte[] pps = [0, 0, 1, 0x68, 0xBB];
        if (codec.Packet([0, 0, 1, 0x65, 1], 0, true) != null) throw new Exception("Video sent before codec configuration.");
        if (codec.Packet(sps, 0, false) != null || codec.Packet(pps, 0, false) != null) throw new Exception("Codec config was sent as a video frame.");
        byte[] packet = codec.Packet([0, 0, 1, 0x65, 0x11, 0x22, 0, 0, 0, 1, 0x06, 0x33], 12345, false)!;
        if (packet[0] != 3) throw new Exception("IDR NAL was not marked as a keyframe.");
        int metadataLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(1));
        using (var json = JsonDocument.Parse(packet.AsMemory(3, metadataLength)))
        {
            var metadata = json.RootElement;
            if (metadata.GetProperty("c").GetString() != "avc1.42001F" || metadata.GetProperty("t").GetInt64() != 12345 || metadata.GetProperty("g").GetInt64() != 42)
                throw new Exception("AVC frame metadata was lost.");
            byte[] description = Convert.FromBase64String(metadata.GetProperty("d").GetString()!);
            if (description[0] != 1 || description[4] != 255 || description[5] != 225 || description[13] != 1)
                throw new Exception("Invalid AVC decoder configuration.");
        }
        int pos = 3 + metadataLength;
        if (BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(pos)) != 3 || BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(pos + 7)) != 2)
            throw new Exception("NAL length prefixes are not in network byte order.");
        foreach (byte[] invalid in new byte[][] { [], [1, 2, 3], [0, 0, 1, 0x67] })
        {
            try { codec.Packet(invalid, 0, false); throw new Exception("Malformed codec data was accepted."); }
            catch (InvalidDataException) { }
        }

        // Generate a genuine, synthetic H264 access unit; no screen or camera is opened.
        var start = new ProcessStartInfo(FfmpegScreenEncoder.Executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=green:s=320x180:r=30", "-frames:v", "1", "-c:v", "libx264", "-profile:v", "baseline", "-preset", "ultrafast", "-tune", "zerolatency", "-f", "h264", "pipe:1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var encoded = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(encoded);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (process.ExitCode != 0) throw new Exception(await errors);
        byte[] realPacket = new AndroidAvcPacketizer(320, 180, 73).Packet(encoded.ToArray(), 1000, true)!;
        if (realPacket == null || realPacket.Length < 50) throw new Exception("Real Annex B frame was not packetized.");
        await VerifyDecodeAsync(realPacket);
        if (OperatingSystem.IsWindows()) await VerifyNativePacketizerAsync();
        var fixture = new AndroidVideoFixture(Convert.ToBase64String(realPacket));
        Directory.CreateDirectory(".artifacts");
        await File.WriteAllTextAsync(".artifacts/android-video-fixture.json", JsonSerializer.Serialize(fixture));
        Console.WriteLine("PASS Android AVC: SPS/PPS, AVCC config, NAL framing, keyframes, timestamps, malformed input and real H264 encode/packetize/decode roundtrip.");
    }

    private static async Task VerifyNativePacketizerAsync()
    {
        foreach (var (id, encoder, format) in new[] { (1, "libx264", "h264"), (5, "libx265", "hevc"), (4, "libaom-av1", "obu") })
        {
            var start = new ProcessStartInfo(FfmpegScreenEncoder.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=green:s=320x180:r=30", "-frames:v", "1", "-c:v", encoder, "-threads", "2" };
            if (id == 4) arguments.AddRange(["-cpu-used", "8"]);
            else arguments.AddRange(["-preset", "ultrafast", "-tune", "zerolatency"]);
            arguments.AddRange(["-f", format, "pipe:1"]);
            foreach (string arg in arguments) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            using var encoded = new MemoryStream();
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(encoded), process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(20));
            if (process.ExitCode != 0) throw new Exception(await error);
            var packetizer = new NativeCapturePacketizer((byte)id, 320, 180, 74);
            byte[] packet = packetizer.Packet(encoded.ToArray(), 1000, true) ?? throw new Exception("Native sequence configuration missing: " + format);
            var parsed = NativeVideoRuntime.Parse(packet);
            nint decoder = NativeMedia.qpv_open(id, 0, 320, 180, parsed.Extra, parsed.Extra!.Length, 0, null);
            if (decoder == 0) throw new Exception(NativeMedia.Error);
            try
            {
                if (NativeMedia.Decode(decoder, packet.AsSpan(parsed.Offset), parsed.Timestamp, parsed.Key, false) < 0) throw new Exception(NativeMedia.Error);
                NativeMedia.qpv_stats(decoder, out var stats);
                if (stats.Decoded != 1 || stats.Width != 320 || stats.Height != 180) throw new Exception("Native capture roundtrip failed: " + format);
            }
            finally { NativeMedia.qpv_close(decoder); }
        }
        Console.WriteLine("PASS native capture packetizer: real H264/HEVC/AV1 sequence configuration and bundled FFmpeg decode roundtrip.");
    }

    private static async Task VerifyDecodeAsync(byte[] packet)
    {
        int metadataLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(1));
        using var metadata = JsonDocument.Parse(packet.AsMemory(3, metadataLength));
        byte[] description = Convert.FromBase64String(metadata.RootElement.GetProperty("d").GetString()!);
        using var annexB = new MemoryStream();
        void Nal(byte[] bytes, int offset, int length) { annexB.Write([0, 0, 0, 1]); annexB.Write(bytes, offset, length); }
        int pos = 6;
        for (int i = 0; i < (description[5] & 31); i++)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(description.AsSpan(pos)); pos += 2;
            Nal(description, pos, length); pos += length;
        }
        int ppsCount = description[pos++];
        for (int i = 0; i < ppsCount; i++)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(description.AsSpan(pos)); pos += 2;
            Nal(description, pos, length); pos += length;
        }
        pos = 3 + metadataLength;
        while (pos < packet.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(pos)); pos += 4;
            Nal(packet, pos, length); pos += length;
        }
        var start = new ProcessStartInfo(FfmpegScreenEncoder.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "h264", "-i", "pipe:0", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var rgb = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(rgb);
        await process.StandardInput.BaseStream.WriteAsync(annexB.ToArray()); process.StandardInput.Close();
        await Task.WhenAll(read, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        byte[] pixels = rgb.ToArray();
        if (process.ExitCode != 0 || pixels.Length != 320 * 180 * 3 || pixels[1] < pixels[0] + 30 || pixels[1] < pixels[2] + 30)
            throw new Exception("AVC decoder configuration or packetized image is corrupt: " + await errors);
    }

    private sealed record AndroidVideoFixture(string packet);
}
