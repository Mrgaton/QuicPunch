using System.Runtime.InteropServices;
using System.Diagnostics;
using QuicNet.Protocols;

namespace QuicNet.Video;

internal sealed class WindowsScreenReader : IScreenVideoReader
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Region(int Index, int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct FrameTimings(long CaptureUs, long ComposeUs, long EncodeUs, long Captured);

    private const string Library = "nettt_media";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint qpv_screen_open([In] Region[] displays, int count, int codec, int width, int height,
        int fps, int bitrate, int gop, int bufferMs, [MarshalAs(UnmanagedType.LPUTF8Str)] string preset, int vbr, int quality);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int qpv_screen_read(nint handle, [Out] byte[] data, int capacity, out long pts, out int key,
        int forceKey, int bitrate, int fps, int bufferMs, out FrameTimings timings);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void qpv_screen_close(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint qpv_screen_error();

    private readonly ScreenEncoderOptions _options;
    private readonly Region[] _regions;
    private readonly NativeCapturePacketizer _packetizer;
    private readonly ScreenCaptureMetrics? _metrics;
    private readonly record struct Rate(int Bitrate, int Fps);
    private long _rate;
    private int _forceKey;
    public int Width { get; }
    public int Height { get; }
    public string WebCodecString() => _packetizer.Codec;
    public string? WebDescription => _packetizer.Description;

    internal WindowsScreenReader(ScreenEncoderOptions options, Region[] regions, int width, int height, long generation,
        ScreenCaptureMetrics? metrics = null)
    {
        (_options, _regions, Width, Height) = (options, regions, width, height);
        _packetizer = new(options.CodecId, width, height, generation);
        _metrics = metrics;
        SetRate(options.Bitrate, options.Fps);
    }

    internal static (int Width, int Height) Fit(int width, int height, ScreenEncoderOptions options)
    {
        int w = (int)Math.Min(width, Math.Min(options.Width, (long)options.Height * width / height));
        int h = (int)Math.Min(height, Math.Min(options.Height, (long)options.Width * height / width));
        return (w & ~1, h & ~1);
    }

    internal void RequestKeyFrame() => Interlocked.Exchange(ref _forceKey, 1);
    internal void SetRate(int bitrate, int fps) => Volatile.Write(ref _rate, ((long)bitrate << 32) | (uint)fps);
    private Rate RealtimeRate()
    {
        long value = Volatile.Read(ref _rate);
        return new((int)(value >> 32), (int)value);
    }

    public byte[] BuildWirePacket(EncodedScreenFrame frame, long generation) =>
        _packetizer.Packet(frame.Data, frame.TimestampUs, frame.KeyFrame, updateConfiguration: false)
        ?? throw new InvalidDataException("NVENC no ha entregado la configuración del vídeo.");

    public async Task ReadAsync(Func<EncodedScreenFrame, Task> output, CancellationToken ct)
    {
        string preset = _options.EncoderPreset is { Length: > 0 } explicitPreset ? explicitPreset :
            _options.Preset switch { "speed" => "p1", "quality" => "p5", _ => "p3" };
        // The hardware-only smoke test supplies no displays and generates its own GPU surface.
        var handle = qpv_screen_open(_regions, _regions.Length, _options.CodecId, Width, Height, _options.Fps,
            _options.Bitrate, _options.KeyFrameIntervalMs, _options.BufferMs, preset, _options.RateControl == "vbr" ? 1 : 0, _options.Quality);
        if (handle == 0) throw new IOException(Error());
        try
        {
            // Leave room for the common screen frame header and codec configuration.
            byte[] buffer = new byte[VoiceCallHandler.MaxMediaUnitBytes - 4096];
            long lastKey = 0;
            while (!ct.IsCancellationRequested)
            {
                bool force = (lastKey == 0 || Stopwatch.GetElapsedTime(lastKey).TotalMilliseconds >= 250) &&
                    Interlocked.Exchange(ref _forceKey, 0) != 0;
                var rate = RealtimeRate();
                int size = qpv_screen_read(handle, buffer, buffer.Length, out long pts, out int key,
                    force ? 1 : 0, rate.Bitrate, rate.Fps, _options.BufferMs, out var timings);
                if (size < 0) throw new IOException(Error());
                if (timings.Captured != 0) _metrics?.Capture(timings.CaptureUs, rate.Bitrate, rate.Fps);
                if (size == 0)
                {
                    if (force) RequestKeyFrame();
                    if (timings.Captured == 0) await Task.Delay(1, ct).ConfigureAwait(false);
                    continue;
                }
                byte[] encoded = buffer.AsSpan(0, size).ToArray();
                bool keyFrame = key != 0;
                if (keyFrame) lastKey = Stopwatch.GetTimestamp();
                if (WebDescription == null)
                {
                    if (!keyFrame) continue;
                    keyFrame = _packetizer.Configure(encoded, keyFrame);
                    if (WebDescription == null) throw new InvalidDataException("NVENC no ha entregado los parámetros AVC/HEVC.");
                }
                await output(new(encoded, keyFrame, pts, timings)).ConfigureAwait(false);
            }
        }
        finally { qpv_screen_close(handle); }
    }

    private static string Error() => Marshal.PtrToStringUTF8(qpv_screen_error()) ?? "Error de captura D3D11/NVENC.";
}
