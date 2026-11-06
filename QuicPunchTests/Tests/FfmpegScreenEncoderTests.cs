using System.Diagnostics;
using System.Text.Json;
using QuicNet.Video;

namespace QuicPunchTests.Tests;

public static class FfmpegScreenEncoderTests
{
    private static readonly List<object> BrowserFixtures = [];
    public static async Task RunAsync()
    {
        TestValidationAndTuning();
        string resolvedExecutable = FfmpegScreenEncoder.Executable;
        if (Path.IsPathRooted(resolvedExecutable))
        {
            string? oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", "");
                Assert(FfmpegScreenEncoder.Executable == resolvedExecutable && File.Exists(resolvedExecutable),
                    "FFmpeg resolution must not depend on Explorer inheriting an updated PATH.");
            }
            finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
        }
        foreach (var display in ScreenCaptureInventory.Displays())
        {
            Assert(display.Width > 0 && display.Height > 0 && display.RefreshRate >= 0, "Invalid active display mode.");
            Console.WriteLine($"[DISPLAY] {display.Width}x{display.Height} @ {display.RefreshRate} Hz");
            var source = FfmpegScreenEncoder.Sources().Single(s => s.Id == "display:" + display.Id);
            Assert(source.Width == display.Width && source.Height == display.Height, "Monitor source lost its physical bounds.");
            var args = FfmpegScreenEncoder.CaptureArguments(new() { Source = source.Id }, "h264_nvenc");
            Assert(args[args.IndexOf("-video_size") + 1] == $"{display.Width}x{display.Height}" &&
                args.IndexOf("-offset_x") < args.IndexOf("-i"), "Monitor capture must use input bounds before opening the desktop.");
        }
        await TestNoUpscalingAsync();
        await TestRealPacketsAsync("libvpx-vp9", ["-deadline", "realtime", "-cpu-used", "8", "-lag-in-frames", "0"], "vp09.", 2560, 1440, 15);
        await TestMalformedContainerAsync();
        await TestBoundedQueueAsync();
        // Synthetic frames only: these tests never capture the user's screen.
        await TestRealPacketsAsync("libx264", ["-preset", "ultrafast", "-tune", "zerolatency", "-bf", "0"], "avc1.");
        var manualTuning = FfmpegScreenEncoder.EncodingArguments(new() { Encoder = "libx264", EncoderPreset = "medium" }, "libx264");
        manualTuning.RemoveRange(manualTuning.IndexOf("-c:v"), 2);
        manualTuning[manualTuning.IndexOf("-g") + 1] = "4";
        await TestRealPacketsAsync("libx264", manualTuning.ToArray(), "avc1.");
        await TestRealPacketsAsync("libx265", ["-preset", "ultrafast", "-tune", "zerolatency", "-x265-params", "log-level=error:pools=1:frame-threads=1", "-bf", "0"], "hvc1.");
        await TestRealPacketsAsync("libaom-av1", ["-cpu-used", "8", "-lag-in-frames", "0"], "av01.");
        using var encoder = new FfmpegScreenEncoder();
        var capabilities = await encoder.CapabilitiesAsync(CancellationToken.None);
        foreach (var capability in capabilities)
        {
            Console.WriteLine($"[ENCODER] {capability.Id}: {capability.Available} {capability.Reason}");
            if (capability.Available)
            {
                var settings = new ScreenEncoderOptions { Codec = capability.Codec, Encoder = capability.Id };
                var tuning = FfmpegScreenEncoder.EncodingArguments(settings, capability.Id);
                // Remove -c:v: TestRealPackets already supplies the chosen encoder.
                int codecIndex = tuning.IndexOf("-c:v"); tuning.RemoveRange(codecIndex, 2);
                tuning[tuning.IndexOf("-g") + 1] = "4";
                await TestRealPacketsAsync(capability.Id, tuning.ToArray(), capability.Codec switch { "h264" => "avc1.", "hevc" => "hvc1.", "vp9" => "vp09.", _ => "av01." });
            }
        }
        if (capabilities.Any(c => c.Id == "h264_nvenc" && c.Available)) await TestCaptureLifecycleAsync();
        if (Environment.GetEnvironmentVariable("QUICNET_SCREEN_TEST_FIXTURES") is { Length: > 0 } fixturePath)
            await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(BrowserFixtures));
        Console.WriteLine("FFmpeg screen encoder tests passed.");
    }

    private static void TestValidationAndTuning()
    {
        foreach (var (encoder, codec) in new[] { ("libx264", "h264"), ("libx265", "hevc"), ("libvpx-vp9", "vp9"), ("libsvtav1", "av1") })
        {
            var cpu = new ScreenEncoderOptions { Encoder = encoder, Codec = codec };
            cpu.Validate();
            Assert(!ScreenEncoderOptions.IsHardware(encoder) && ScreenEncoderOptions.CodecForEncoder(encoder) == codec, "CPU encoder mapping is invalid.");
            Assert(FfmpegScreenEncoder.EncodingArguments(cpu, encoder).Contains(encoder), "CPU encoder was not selected.");
        }
        var options = new ScreenEncoderOptions { Codec = "av1", Encoder = "av1_nvenc", RateControl = "vbr", Quality = 19, Preset = "quality" };
        var args = FfmpegScreenEncoder.EncodingArguments(options, options.Encoder);
        Assert(args.Contains("ull") && args.Contains("p5") && args.Contains("-cq"), "NVENC realtime tuning missing.");
        args = FfmpegScreenEncoder.EncodingArguments(options with { Encoder = "av1_amf" }, "av1_amf");
        Assert(args.Contains("ultralowlatency") && args.Contains("vbr_latency") && !args.Contains("-cq"), "AMF tuning must differ from NVENC.");
        foreach (var (encoder, codec, preset, flag) in new[] {
            ("h264_nvenc", "h264", "p7", "-preset"), ("hevc_nvenc", "hevc", "p2", "-preset"),
            ("libx264", "h264", "medium", "-preset"), ("libx265", "hevc", "slow", "-preset"),
            ("libvpx-vp9", "vp9", "4", "-cpu-used"), ("libsvtav1", "av1", "6", "-preset"),
            ("av1_amf", "av1", "quality", "-quality") })
        {
            var manual = new ScreenEncoderOptions { Encoder = encoder, Codec = codec, EncoderPreset = preset };
            var manualArgs = FfmpegScreenEncoder.EncodingArguments(manual, encoder);
            Assert(manualArgs[manualArgs.IndexOf(flag) + 1] == preset, "The chosen encoder preset was not applied.");
            foreach (string invalidPreset in new[] { "-i", "unknown", encoder.EndsWith("_nvenc") ? "medium" : "p7" })
            {
                try { (manual with { EncoderPreset = invalidPreset }).Validate(); throw new Exception("Invalid preset accepted."); }
                catch (ArgumentException) { }
            }
        }
        foreach (var invalid in new[] { options with { Width = 1919 }, options with { Encoder = "h264_nvenc" }, options with { BufferMs = 1000 }, options with { Preset = "-i" } })
        {
            try { invalid.Validate(); throw new Exception("Invalid settings accepted."); }
            catch (ArgumentException) { }
        }
    }

    private static async Task TestNoUpscalingAsync()
    {
        foreach (var (width, height, expectedWidth, expectedHeight) in new[] { (800, 600, 800, 600), (720, 1280, 606, 1080), (3440, 1440, 1920, 802) })
        {
            var info = new ProcessStartInfo(FfmpegScreenEncoder.Executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
                $"testsrc2=size={width}x{height}:rate=1", "-frames:v", "1", "-vf", FfmpegScreenEncoder.ScaleFilter(new()),
                "-c:v", "libx264", "-preset", "ultrafast", "-f", "matroska", "pipe:1" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new Exception("FFmpeg missing.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                var reader = FfmpegScreenEncoder.CreateReader("h264", process.StandardOutput.BaseStream);
                await reader.ReadAsync(_ => Task.CompletedTask, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                Assert(process.ExitCode == 0, await stderr);
                Assert(reader.Width == expectedWidth && reader.Height == expectedHeight,
                    $"Source {width}x{height} became {reader.Width}x{reader.Height}; preserve aspect and never upscale.");
            }
            finally { try { if (!process.HasExited) process.Kill(true); } catch { } }
        }
        Console.WriteLine("[SCALING] Small, portrait and ultrawide sources preserve dimensions/aspect without upscaling.");
    }

    private static async Task TestMalformedContainerAsync()
    {
        try
        {
            await new MatroskaVideoReader(new MemoryStream([0])).ReadAsync(_ => Task.CompletedTask, CancellationToken.None);
            throw new Exception("Invalid EBML accepted.");
        }
        catch (InvalidDataException) { }
    }

    private static async Task TestBoundedQueueAsync()
    {
        var queue = new RealtimeScreenFrameQueue();
        queue.Enqueue(new([1], true, 0));
        queue.Enqueue(new([2], false, 1));
        queue.Enqueue(new([3], false, 2)); // overflow clears dependent frames
        queue.Enqueue(new([4], false, 3)); // wait for random access
        queue.Enqueue(new([5], true, 4));
        queue.Enqueue(new([6], false, 5));
        queue.Complete();
        var received = new List<byte>();
        await queue.DrainAsync(frame => { received.Add(frame.Data[0]); return Task.CompletedTask; }, CancellationToken.None);
        Assert(received.SequenceEqual(new byte[] { 5, 6 }), "Queue must recover at a fresh keyframe after overflow.");
    }

    private static async Task TestCaptureLifecycleAsync()
    {
        using var encoder = new FfmpegScreenEncoder((options, selected) =>
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30" };
            args.AddRange(FfmpegScreenEncoder.EncodingArguments(options, selected));
            args.AddRange(FfmpegScreenEncoder.ContainerArguments(options.Codec)); args.Add("pipe:1"); return args;
        });
        var options = new ScreenEncoderOptions { Width = 320, Height = 180, Fps = 30, Codec = "h264", Encoder = "h264_nvenc", KeyFrameIntervalMs = 250 };
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = await encoder.StartAsync(options, _ => Task.FromResult(true), (_, _) => { received.TrySetResult(); return Task.CompletedTask; }, CancellationToken.None);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(profile.Codec.StartsWith("avc1.") && profile.Width == 320, "Invalid capture profile.");
        await encoder.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert(encoder.Profile == null, "Stop must reset the capture state.");
        try
        {
            await encoder.StartAsync(options, _ => Task.FromResult(false), (_, _) => throw new Exception("Unapproved video leaked."), CancellationToken.None);
            throw new Exception("Unapproved configuration accepted.");
        }
        catch (InvalidOperationException) { }
        Assert(encoder.Profile == null, "Rejected capture must release process and state.");
        Console.WriteLine("[LIFECYCLE] Synthetic GPU capture, stop and decoder rejection passed.");
    }

    private static async Task TestRealPacketsAsync(string encoder, string[] tuning, string prefix, int width = 320, int height = 180, int frameRate = 30)
    {
        var info = new ProcessStartInfo(FfmpegScreenEncoder.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        string codec = prefix == "av01." ? "av1" : prefix == "hvc1." ? "hevc" : prefix == "vp09." ? "vp9" : "h264";
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={frameRate}", "-frames:v", "8", "-c:v", encoder, "-g", "4" }.Concat(tuning)
            .Concat(FfmpegScreenEncoder.ContainerArguments(codec)).Append("pipe:1")) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new Exception("FFmpeg missing.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var reader = FfmpegScreenEncoder.CreateReader(codec, new FragmentedReadStream(process.StandardOutput.BaseStream), frameRate);
            var frames = new List<EncodedScreenFrame>();
            await reader.ReadAsync(frame => { frames.Add(frame); return Task.CompletedTask; }, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert(process.ExitCode == 0, await stderr);
            Assert(frames.Count == 8 && frames[0].KeyFrame && frames.Count(f => f.KeyFrame) >= 2 && frames.Any(f => !f.KeyFrame), "Lost frame boundaries or key flags.");
            Assert(frames.Select(f => f.TimestampUs).SequenceEqual(frames.Select(f => f.TimestampUs).Order()), "Invalid timestamps.");
            Assert(reader.Width == width && reader.Height == height && reader.WebCodecString().StartsWith(prefix), "Invalid decoder configuration.");
            if (encoder == "libvpx-vp9" && width == 2560) Assert(reader.WebCodecString() == "vp09.00.50.08", "1440p15 VP9 must signal a level supporting the whole frame.");
            var packet = reader.BuildWirePacket(frames[0], 123);
            int metadataLength = (packet[1] << 8) | packet[2];
            using var metadata = JsonDocument.Parse(packet.AsMemory(3, metadataLength));
            Assert(metadata.RootElement.GetProperty("g").GetInt64() == 123 && packet.AsSpan(3 + metadataLength).SequenceEqual(frames[0].Data), "Wire packet corrupted.");
            BrowserFixtures.Add(new { encoder, codecId = codec == "av1" ? 4 : codec == "hevc" ? 5 : codec == "vp9" ? 3 : 1, codec = reader.WebCodecString(), width = reader.Width, height = reader.Height,
                description = reader.WebDescription, packets = frames.Select(frame => Convert.ToBase64String(reader.BuildWirePacket(frame, 123))).ToArray() });
            Console.WriteLine($"[PACKETS] {encoder}: {frames.Count} frames, {reader.WebCodecString()}");
        }
        finally { try { if (!process.HasExited) process.Kill(true); } catch { } }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class FragmentedReadStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 7));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], ct);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
