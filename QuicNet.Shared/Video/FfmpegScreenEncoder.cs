using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QuicNet.Video;

internal sealed record ScreenEncoderOptions
{
    public string Codec { get; init; } = "h264";
    public string Encoder { get; init; } = "auto";
    public string Source { get; init; } = "desktop";
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int Fps { get; init; } = 60;
    public int Bitrate { get; init; } = 12_000_000;
    public string Preset { get; init; } = "balanced";
    public string? EncoderPreset { get; init; }
    public string RateControl { get; init; } = "cbr";
    public int Quality { get; init; } = 23;
    public int KeyFrameIntervalMs { get; init; } = 1000;
    public int BufferMs { get; init; } = 50;
    public Guid? TargetPeerId { get; init; }

    public byte CodecId => Codec switch { "h264" => 1, "hevc" => 5, "vp9" => 3, "av1" => 4, _ => throw new ArgumentException("Unsupported native codec.") };
    public void Validate()
    {
        _ = CodecId;
        if (Width < 128 || Width > 7680 || Height < 128 || Height > 4320 || Width % 2 != 0 || Height % 2 != 0 ||
            Fps < 5 || Fps > 144 || Bitrate < 500_000 || Bitrate > 100_000_000 ||
            KeyFrameIntervalMs < 250 || KeyFrameIntervalMs > 5000 || BufferMs < 16 || BufferMs > 250 || Quality < 0 || Quality > 51)
            throw new ArgumentException("Invalid video dimensions, FPS, bitrate or latency settings.");
        if (Preset is not ("speed" or "balanced" or "quality") || RateControl is not ("cbr" or "vbr"))
            throw new ArgumentException("Invalid encoder tuning.");
        if (Encoder != "auto" && !Candidates.Contains(Encoder)) throw new ArgumentException("Unknown video encoder.");
        if (Encoder != "auto" && CodecForEncoder(Encoder) != Codec)
            throw new ArgumentException("Encoder and codec do not match.");
        if (!string.IsNullOrEmpty(EncoderPreset) && !PresetsFor(Encoder).Contains(EncoderPreset))
            throw new ArgumentException("Preset is not supported by the selected encoder.");
    }
    internal static readonly string[] Candidates = ["h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_amf", "hevc_amf", "av1_amf", "libx264", "libx265", "libvpx-vp9", "libsvtav1", "h264_mediacodec", "hevc_mediacodec", "av1_mediacodec"];
    internal static string CodecForEncoder(string encoder) => encoder switch
    {
        "libx264" => "h264", "libx265" => "hevc", "libvpx-vp9" => "vp9", "libsvtav1" => "av1",
        _ => encoder.Split('_')[0]
    };
    internal static bool IsHardware(string encoder) => !encoder.StartsWith("lib", StringComparison.Ordinal);
    internal static string[] PresetsFor(string encoder) => encoder switch
    {
        "libx264" or "libx265" => ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow"],
        "libvpx-vp9" => ["8", "7", "6", "5", "4"],
        "libsvtav1" => ["12", "11", "10", "9", "8", "7", "6"],
        _ when encoder.EndsWith("_nvenc", StringComparison.Ordinal) => ["p1", "p2", "p3", "p4", "p5", "p6", "p7"],
        _ when encoder.EndsWith("_amf", StringComparison.Ordinal) => ["speed", "balanced", "quality"],
        _ => []
    };
}

internal sealed record ScreenEncoderCapability(string Id, string Codec, bool Available, string? Reason)
{
    public bool Hardware => ScreenEncoderOptions.IsHardware(Id);
    public string[] Presets => ScreenEncoderOptions.PresetsFor(Id);
}
internal sealed record ScreenCaptureSource(string Id, string Name, int? Width = null, int? Height = null, int? RefreshRate = null, string Kind = "display");
internal sealed record NativeScreenProfile(string Encoder, byte CodecId, int Width, int Height, int Fps, string Codec, string? Description, long Generation, string CaptureMode = "web");

internal sealed partial class FfmpegScreenEncoder : IDisposable
{
    private readonly Func<ScreenEncoderOptions, string, List<string>> _captureArguments;
    private readonly bool _allowCaptureFallback;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private CancellationTokenSource? _captureCts;
    private Task? _pump;
    private ScreenEncoderCapability[]? _capabilities;
    private int _disposed;
    private static long _generation = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public NativeScreenProfile? Profile { get; private set; }
    public NativeScreenProfile? PendingProfile { get; private set; }
    public event Action<string>? Failed;
    public static string Executable => ResolveExecutable();

    internal static string ResolveExecutable() => FfmpegInstallation.ResolveExecutable();

    // Windows capture uses the CLI. Android uses FFmpeg libraries with MediaProjection/Camera2.
    public static bool CaptureAvailable => OperatingSystem.IsWindows();

    public FfmpegScreenEncoder() : this(CaptureArguments) { _allowCaptureFallback = true; }
    internal FfmpegScreenEncoder(Func<ScreenEncoderOptions, string, List<string>> captureArguments) => _captureArguments = captureArguments;

    public static IReadOnlyList<ScreenCaptureSource> Sources(IReadOnlyList<CaptureDisplay>? displays = null)
    {
        var result = new List<ScreenCaptureSource>();
        if (!CaptureAvailable) return result;
        displays ??= ScreenCaptureInventory.Displays();
        // DXGI output indices do not share Win32 display ordering. Only associate
        // a mode when there is a single display; never label another monitor's mode as exact.
        // Named physical monitors are unambiguous; legacy DXGI IDs remain accepted by the encoder.
        result.Add(new("desktop", "Escritorio completo (todos los monitores)",
            displays.Count == 0 ? null : displays.Max(d => d.X + d.Width) - displays.Min(d => d.X),
            displays.Count == 0 ? null : displays.Max(d => d.Y + d.Height) - displays.Min(d => d.Y)));
        foreach (var display in displays)
            result.Add(new("display:" + display.Id, $"Pantalla {displays.ToList().IndexOf(display) + 1}" +
                (display.Primary ? " · principal" : "") + $" · {display.Name}", display.Width, display.Height, display.RefreshRate));
        foreach (var window in ScreenCaptureInventory.Windows())
            result.Add(new($"hwnd:{window.Handle}", window.Title, window.Width, window.Height, Kind: "window"));
        return result;
    }

    public async Task<ScreenEncoderCapability[]> CapabilitiesAsync(CancellationToken ct, bool refresh = false)
    {
        if (_capabilities != null && !refresh) return _capabilities;
        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_capabilities != null && !refresh) return _capabilities;
            if (!CaptureAvailable) return _capabilities = [];
            var capabilities = new List<ScreenEncoderCapability>();
            string listing;
            try { listing = (await RunAsync(["-hide_banner", "-encoders"], TimeSpan.FromSeconds(8), ct)).Output; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return _capabilities = ScreenEncoderOptions.Candidates.Select(e => new ScreenEncoderCapability(e, ScreenEncoderOptions.CodecForEncoder(e), false, ex.Message)).ToArray();
            }
            foreach (string encoder in ScreenEncoderOptions.Candidates)
            {
                if (!Regex.IsMatch(listing, @"\b" + encoder + @"\b"))
                {
                    capabilities.Add(new(encoder, ScreenEncoderOptions.CodecForEncoder(encoder), false, "No incluido en esta compilación de FFmpeg."));
                    continue;
                }
                try
                {
                    var options = new ScreenEncoderOptions { Codec = ScreenEncoderOptions.CodecForEncoder(encoder), Encoder = encoder, Width = 320, Height = 180, Fps = 30, Bitrate = 1_000_000 };
                    var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30" };
                    args.AddRange(EncodingArguments(options, encoder));
                    args.AddRange(["-frames:v", "2"]);
                    args.AddRange(ContainerArguments(options.Codec));
                    args.Add("pipe:1");
                    await VerifyEncoderOutputAsync(args, options.Codec, ct).ConfigureAwait(false);
                    capabilities.Add(new(encoder, options.Codec, true, null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { capabilities.Add(new(encoder, ScreenEncoderOptions.CodecForEncoder(encoder), false, ex.Message)); }
            }
            return _capabilities = capabilities.ToArray();
        }
        finally { _probeGate.Release(); }
    }

    internal static List<string> EncodingArguments(ScreenEncoderOptions options, string encoder)
    {
        options.Validate();
        string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        var args = new List<string> { "-an", "-c:v", encoder, "-pix_fmt", "yuv420p", "-g", Number(Math.Max(1, options.Fps * options.KeyFrameIntervalMs / 1000)),
            "-b:v", Number(options.Bitrate), "-maxrate", Number(options.Bitrate), "-bufsize", Number(Math.Max(options.Bitrate / options.Fps, (long)options.Bitrate * options.BufferMs / 1000)), "-bf", "0" };
        if (options.Codec == "h264") args.AddRange(["-profile:v", "high"]);
        if (options.Codec == "hevc") args.AddRange(["-profile:v", "main"]);
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            args.AddRange(["-preset", options.EncoderPreset is { Length: > 0 } nvencPreset ? nvencPreset : options.Preset switch { "speed" => "p1", "quality" => "p5", _ => "p3" },
                "-tune", "ull", "-rc", options.RateControl, "-rc-lookahead", "0", "-zerolatency", "1", "-forced-idr", "1"]);
            if (options.RateControl == "vbr") args.AddRange(["-cq", Number(options.Quality)]);
        }
        else if (encoder == "libx264")
        {
            args.AddRange(["-preset", options.EncoderPreset is { Length: > 0 } x264Preset ? x264Preset : options.Preset switch { "quality" => "fast", "balanced" => "veryfast", _ => "ultrafast" },
                "-tune", "zerolatency"]);
            if (options.RateControl == "vbr") args.AddRange(["-crf", Number(options.Quality)]);
        }
        else if (encoder == "libx265")
        {
            args.AddRange(["-preset", options.EncoderPreset is { Length: > 0 } x265Preset ? x265Preset : options.Preset == "quality" ? "fast" : "ultrafast", "-tune", "zerolatency",
                "-x265-params", "log-level=error:pools=2:frame-threads=1"]);
            if (options.RateControl == "vbr") args.AddRange(["-crf", Number(options.Quality)]);
        }
        else if (encoder == "libvpx-vp9")
            args.AddRange(["-deadline", "realtime", "-cpu-used", options.EncoderPreset is { Length: > 0 } vp9Preset ? vp9Preset : options.Preset == "quality" ? "6" : "8", "-row-mt", "1", "-lag-in-frames", "0"]);
        else if (encoder == "libsvtav1")
            args.AddRange(["-preset", options.EncoderPreset is { Length: > 0 } av1Preset ? av1Preset : options.Preset switch { "speed" => "12", "quality" => "8", _ => "10" }, "-svtav1-params", options.RateControl == "cbr" ? "lp=2:rc=2:pred-struct=1" : "lp=2:rc=1:pred-struct=2"]);
        else if (encoder.EndsWith("_amf", StringComparison.Ordinal))
        {
            args.AddRange(["-usage", "ultralowlatency", "-quality", options.EncoderPreset is { Length: > 0 } amfPreset ? amfPreset : options.Preset, "-rc", options.RateControl == "cbr" ? "cbr" : "vbr_latency", "-preanalysis", "0"]);
            if (options.Codec == "hevc") args.AddRange(["-header_insertion_mode", "idr"]);
        }
        return args;
    }

    internal static List<string> CaptureArguments(ScreenEncoderOptions options, string encoder)
    {
        if (!CaptureAvailable) throw new PlatformNotSupportedException("La captura requiere el adaptador FFmpeg nativo de este dispositivo.");
        if (options.Source.StartsWith("camera:", StringComparison.Ordinal))
        {
            string name = options.Source[7..];
            if (string.IsNullOrWhiteSpace(name) || name.Length > 2048 || name.Contains('\0')) throw new ArgumentException("Cámara inválida.");
            var camera = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin", "-f", "dshow", "-i", "video=" + name,
                "-vf", ScaleFilter(options), "-r", options.Fps.ToString(CultureInfo.InvariantCulture) };
            camera.AddRange(EncodingArguments(options, encoder)); camera.AddRange(ContainerArguments(options.Codec)); camera.Add("pipe:1"); return camera;
        }
        if (options.Source == "dxgi:0" && ScreenEncoderOptions.IsHardware(encoder))
        {
            var gpuArgs = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin", "-f", "lavfi", "-i",
                $"ddagrab=output_idx=0:framerate={options.Fps}:draw_mouse=1:output_fmt=bgra",
                "-vf", $"scale_d3d11=width='trunc(min(iw,min({options.Width},{options.Height}*iw/ih))/2)*2':height='trunc(min(ih,min({options.Height},{options.Width}*ih/iw))/2)*2':format=nv12" };
            gpuArgs.AddRange(EncodingArguments(options, encoder));
            gpuArgs[gpuArgs.IndexOf("-pix_fmt") + 1] = "d3d11";
            gpuArgs.AddRange(ContainerArguments(options.Codec));
            gpuArgs.Add("pipe:1");
            return gpuArgs;
        }
        string source = "desktop";
        CaptureDisplay? display = null;
        if (options.Source.StartsWith("window:", StringComparison.Ordinal) && int.TryParse(options.Source[7..], out int pid))
        {
            using var process = Process.GetProcessById(pid);
            if (process.MainWindowHandle == IntPtr.Zero) throw new ArgumentException("La ventana seleccionada ya no está disponible.");
            source = $"hwnd={process.MainWindowHandle.ToInt64()}";
        }
        else if (options.Source.StartsWith("hwnd:", StringComparison.Ordinal) &&
                 long.TryParse(options.Source[5..], out long hwnd) &&
                 ScreenCaptureInventory.Windows().Any(w => w.Handle == hwnd))
            source = $"hwnd={hwnd}";
        else if (options.Source.StartsWith("display:", StringComparison.Ordinal))
            display = ScreenCaptureInventory.Displays().FirstOrDefault(d => "display:" + d.Id == options.Source)
                ?? throw new ArgumentException("El monitor seleccionado ya no está disponible.");
        else if (options.Source != "desktop") throw new ArgumentException("Fuente de captura desconocida.");
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin", "-f", "gdigrab", "-framerate", options.Fps.ToString(CultureInfo.InvariantCulture),
            "-draw_mouse", "1", "-i", source, "-vf", ScaleFilter(options) };
        if (display != null)
            args.InsertRange(args.IndexOf("-i"), ["-offset_x", display.X.ToString(CultureInfo.InvariantCulture),
                "-offset_y", display.Y.ToString(CultureInfo.InvariantCulture), "-video_size", $"{display.Width}x{display.Height}"]);
        args.AddRange(EncodingArguments(options, encoder));
        // Each cluster is flushed as soon as possible. Container overhead stays
        // local: only demuxed packets travel over the existing P2P protocol.
        args.AddRange(ContainerArguments(options.Codec));
        args.Add("pipe:1");
        return args;
    }

    internal static async Task<NativeCamera[]> CamerasAsync()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var start=new ProcessStartInfo(Executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(string arg in new[]{"-hide_banner","-list_devices","true","-f","dshow","-i","dummy"})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var stderr=process.StandardError.ReadToEndAsync(timeout.Token);var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token),stderr,stdout).ConfigureAwait(false);
            var result=new List<NativeCamera>();string? label=null;
            foreach(string line in (await stderr).Split('\n'))
            {
                var video=Regex.Match(line,"\"(.*)\" \\(video\\)");
                if(video.Success){label=video.Groups[1].Value;continue;}
                if(label!=null){var alternative=Regex.Match(line,"Alternative name \"(.*)\"");if(alternative.Success){result.Add(new("camera:"+alternative.Groups[1].Value,label));label=null;}}
            }
            return result.ToArray();
        }
        finally{try{if(!process.HasExited)process.Kill();}catch{}}
    }

    internal static string[] ContainerArguments(string codec) => codec == "av1"
        ? ["-f", "ivf", "-flush_packets", "1"]
        : ["-f", "matroska", "-cluster_time_limit", "1", "-flush_packets", "1"];

    internal static IScreenVideoReader CreateReader(string codec, Stream stream, int frameRate = 30) => codec == "av1" ? new Av1IvfVideoReader(stream) : new MatroskaVideoReader(stream, frameRate);

    private static List<string> CompatibilityCaptureArguments(ScreenEncoderOptions options, string encoder)
    {
        var args = CaptureArguments(options, encoder);
        args[args.IndexOf("-vf") + 1] = "hwdownload,format=bgra," + ScaleFilter(options);
        args[args.IndexOf("-pix_fmt") + 1] = "yuv420p";
        return args;
    }

    internal static string ScaleFilter(ScreenEncoderOptions options) =>
        $"scale=w='trunc(min(iw,min({options.Width},{options.Height}*iw/ih))/2)*2':h='trunc(min(ih,min({options.Height},{options.Width}*ih/iw))/2)*2'";

    public async Task<NativeScreenProfile> StartAsync(ScreenEncoderOptions options,
        Func<NativeScreenProfile, Task<bool>> approve,
        Func<byte, byte[], Task> output, CancellationToken ct)
    {
        options.Validate();
        var capabilities = await CapabilitiesAsync(ct).ConfigureAwait(false);
        var encoders = capabilities.Where(c => c.Available && c.Codec == options.Codec && (options.Encoder == "auto" || c.Id == options.Encoder)).ToArray();
        if (encoders.Length == 0) throw new InvalidOperationException("No hay un codificador FFmpeg probado para este formato. Revisa las opciones disponibles.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            await StopCoreAsync().ConfigureAwait(false);
            Exception? last = null;
            var attempts = encoders.SelectMany(encoder => options.Source == "dxgi:0" && ScreenEncoderOptions.IsHardware(encoder.Id) && _allowCaptureFallback
                ? new[] { (Encoder: encoder, Compatibility: false), (Encoder: encoder, Compatibility: true) }
                : new[] { (Encoder: encoder, Compatibility: false) });
            foreach (var attempt in attempts)
            {
                var encoder = attempt.Encoder;
                try
                {
                    var captureCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _captureCts = captureCts;
                    _process = StartProcess(attempt.Compatibility ? CompatibilityCaptureArguments(options, encoder.Id) : _captureArguments(options, encoder.Id));
                    var process = _process;
                    Task<string> errors = ReadErrorsAsync(process, captureCts.Token);
                    var reader = CreateReader(options.Codec, process.StandardOutput.BaseStream, options.Fps);
                    var ready = new TaskCompletionSource<NativeScreenProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    long generation = Math.Max(Interlocked.Increment(ref _generation), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Interlocked.Exchange(ref _generation, generation);
                    _pump = Task.Run(async () =>
                    {
                        bool started = false;
                        var queue = new RealtimeScreenFrameQueue();
                        var sender = Task.Run(async () =>
                        {
                            try
                            {
                                await resume.Task.WaitAsync(captureCts.Token).ConfigureAwait(false);
                                await queue.DrainAsync(frame => output(options.CodecId, reader.BuildWirePacket(frame, generation)).WaitAsync(captureCts.Token), captureCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (captureCts.IsCancellationRequested) { }
                            catch (Exception ex)
                            {
                                Profile = null;
                                Failed?.Invoke(ex.Message);
                                captureCts.Cancel();
                                Kill(process);
                            }
                        });
                        try
                        {
                            await reader.ReadAsync(frame =>
                            {
                                if (!started)
                                {
                                    if (!frame.KeyFrame) return Task.CompletedTask;
                                    started = true;
                                    string captureMode = options.Source == "dxgi:0" ? attempt.Compatibility ? "DXGI + CPU scaling" : "DXGI + D3D11 scaling" : "GDI capture + CPU scaling";
                                    ready.TrySetResult(new(encoder.Id, options.CodecId, reader.Width, reader.Height, options.Fps, reader.WebCodecString(), reader.WebDescription, generation, captureMode));
                                }
                                queue.Enqueue(frame);
                                return Task.CompletedTask;
                            }, captureCts.Token).ConfigureAwait(false);
                            if (!captureCts.IsCancellationRequested) throw new IOException("FFmpeg dejó de producir vídeo. " + await errors.ConfigureAwait(false));
                        }
                        catch (Exception ex)
                        {
                            ready.TrySetException(ex);
                            if (!captureCts.IsCancellationRequested && resume.Task.IsCompletedSuccessfully)
                            {
                                Profile = null;
                                Failed?.Invoke(ex.Message);
                            }
                        }
                        finally
                        {
                            queue.Complete();
                            captureCts.Cancel();
                            try { await sender.ConfigureAwait(false); } catch { }
                        }
                    });
                    using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct, captureCts.Token);
                    var profile = await ready.Task.WaitAsync(TimeSpan.FromSeconds(12), startupCts.Token).ConfigureAwait(false);
                    PendingProfile = profile;
                    if (!await approve(profile).WaitAsync(startupCts.Token).ConfigureAwait(false)) throw new InvalidOperationException("No se ha autorizado el inicio de esta captura.");
                    Profile = profile;
                    PendingProfile = null;
                    resume.TrySetResult();
                    return profile;
                }
                catch (Exception ex)
                {
                    last = ex;
                    await StopCoreAsync().ConfigureAwait(false);
                    if (ct.IsCancellationRequested) throw;
                }
            }
            throw new InvalidOperationException("No se pudo iniciar la captura FFmpeg: " + last?.Message, last);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Profile = null;
        PendingProfile = null;
        _captureCts?.Cancel();
        Kill(_process);
        if (_pump != null) { try { await _pump.ConfigureAwait(false); } catch { } }
        _process?.Dispose(); _process = null;
        _captureCts?.Dispose(); _captureCts = null;
        _pump = null;
    }

    private static Process StartProcess(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("No se pudo ejecutar FFmpeg.");
    }

    private static async Task<string> ReadErrorsAsync(Process process, CancellationToken ct)
    {
        var tail = new StringBuilder();
        try
        {
            while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                tail.AppendLine(line);
                if (tail.Length > 8192) tail.Remove(0, tail.Length - 4096);
            }
        }
        catch (OperationCanceledException) { }
        return tail.ToString();
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        using var process = StartProcess(args);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).WaitAsync(timeout, ct).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
        }
        finally { Kill(process); }
    }

    // Verify the same muxer, parser and codec metadata used for transmission.
    // A successful encode to the null muxer does not establish a usable stream.
    private static async Task VerifyEncoderOutputAsync(IEnumerable<string> args, string codec, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = StartProcess(args);
        var errors = ReadErrorsAsync(process, timeout.Token);
        try
        {
            var reader = CreateReader(codec, process.StandardOutput.BaseStream);
            bool keyFrame = false;
            await reader.ReadAsync(frame =>
            {
                if (frame.KeyFrame)
                {
                    _ = reader.BuildWirePacket(frame, 0);
                    keyFrame = true;
                }
                return Task.CompletedTask;
            }, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || !keyFrame || reader.Width != 320 || reader.Height != 180)
                throw new IOException("FFmpeg no produjo vídeo utilizable. " + Tail(await errors.ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("El codificador FFmpeg no respondió en ocho segundos.");
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            // IVF can fail before its header exists when a driver rejects the
            // encoder. Preserve FFmpeg's actual reason in the capability table.
            Kill(process);
            string diagnostic = "";
            try { diagnostic = Tail(await errors.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false)); }
            catch (TimeoutException) { }
            throw new IOException(ex.Message + " " + diagnostic, ex);
        }
        finally
        {
            Kill(process);
            timeout.Cancel();
            try { await errors.ConfigureAwait(false); } catch { }
        }
    }

    private static void Kill(Process? process) { try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { } }
    private static string Tail(string text) => text.Length > 1500 ? text[^1500..] : text.Trim();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        Kill(_process);
        // Shutdown cancellation unblocks demuxing and negotiation. Cleanup owns
        // the same gate as start/stop, preventing a surviving capture process.
        _ = StopAsync();
    }
}
