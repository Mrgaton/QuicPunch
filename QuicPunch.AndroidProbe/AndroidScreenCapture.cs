using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using QuicNet.Protocols;
using QuicNet.Video;
using Environment = System.Environment;
using OperationCanceledException = System.OperationCanceledException;

namespace QuicPunch.AndroidProbe;

internal sealed class AndroidScreenCapture(Context context) : IPlatformScreenCapture
{
    internal static AndroidScreenCapture? Pending;
    internal ScreenEncoderOptions Options = new();
    internal Func<byte[], Task> Output = _ => Task.CompletedTask;
    internal Action<string?> Stopped = _ => { };
    internal TaskCompletionSource<NativeScreenProfile> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ResumeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationTokenSource Lifetime = new();
    private readonly SemaphoreSlim _start = new(1);

    public async Task<NativeScreenProfile> StartAsync(ScreenEncoderOptions options, Func<byte[], Task> output, Action<string?> stopped)
    {
        if (!await _start.WaitAsync(0)) throw new InvalidOperationException("Ya se está solicitando la pantalla.");
        try
        {
            await StopAsync();
            Lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            Options = options; Output = output; Stopped = stopped;
            Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ResumeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var activity = MainActivity.Foreground ?? throw new InvalidOperationException("Abre QuicNet para compartir la pantalla.");
            var permission = await activity.RequestProjectionAsync().WaitAsync(Lifetime.Token);
            Lifetime.Token.ThrowIfCancellationRequested();
            Pending = this;
            context.StartForegroundService(new Intent(context, typeof(AndroidScreenCaptureService)).PutExtra("permission", permission));
            var profile = await Ready.Task.WaitAsync(TimeSpan.FromSeconds(15), Lifetime.Token);
            Lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
            return profile;
        }
        catch { await StopAsync(); throw; }
        finally { _start.Release(); }
    }

    public void Resume() => ResumeSignal.TrySetResult();
    public async Task StopAsync()
    {
        Lifetime.Cancel();
        if (Pending == this) Pending = null;
        if (context.StopService(new Intent(context, typeof(AndroidScreenCaptureService))))
            await Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public sealed class AndroidScreenCaptureService : Service
{
    internal static bool IsCapturing { get; private set; }
    private CancellationTokenSource? _lifetime;
    private AndroidScreenCapture? _capture;
    private MediaProjection? _projection;
    private MediaProjection.Callback? _callback;
    private MediaCodec? _codec;
    private Android.Views.Surface? _surface;
    private VirtualDisplay? _display;
    private Task? _pump;
    private NativeScreenProfile? _profile;
    private const string Channel = "quicnet-screen-capture";
    public override IBinder? OnBind(Intent? intent) => null;

    private sealed class ProjectionCallback(AndroidScreenCaptureService owner) : MediaProjection.Callback
    {
        public override void OnStop() => owner.StopSelf();
        // Android scales changed capture bounds into the fixed encoder surface while keeping aspect ratio.
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "stop") { StopSelf(); return StartCommandResult.NotSticky; }
        if (_capture != null) return StartCommandResult.NotSticky;
        _capture = AndroidScreenCapture.Pending; AndroidScreenCapture.Pending = null;
        if (_capture == null || _capture.Lifetime.IsCancellationRequested) { StopSelf(); return StartCommandResult.NotSticky; }
        try
        {
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(Channel, "Pantalla compartida", NotificationImportance.Low));
            var open = PendingIntent.GetActivity(this, 10, new Intent(this, typeof(MainActivity)).PutExtra("quicnetRoute", "/call.html"), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var stop = PendingIntent.GetService(this, 11, new Intent(this, typeof(AndroidScreenCaptureService)).SetAction("stop"), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var notice = new Notification.Builder(this, Channel).SetContentTitle("Compartiendo tu pantalla")
                .SetContentText("Los participantes de la llamada pueden verla.").SetSmallIcon(Android.Resource.Drawable.IcMenuView)
                .SetContentIntent(open).SetOngoing(true)
                .AddAction(new Notification.Action.Builder(null, "Dejar de compartir", stop).Build()).Build();
            StartForeground(5072, notice, ForegroundService.TypeMediaProjection);
#pragma warning disable CA1422, CS0618
            var permission = (Intent?)intent!.GetParcelableExtra("permission") ?? throw new InvalidOperationException("Falta el permiso de captura.");
#pragma warning restore CA1422, CS0618
            var projectionManager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            _projection = projectionManager.GetMediaProjection((int)Result.Ok, permission) ?? throw new InvalidOperationException("Android ha denegado la pantalla.");
            _callback = new ProjectionCallback(this);
            _projection.RegisterCallback(_callback, new Handler(Looper.MainLooper!));
            var metrics = Resources!.DisplayMetrics!;
            var options = _capture.Options;
            double scale = Math.Min(1, Math.Min((double)options.Width / metrics.WidthPixels, (double)options.Height / metrics.HeightPixels));
            int width = Math.Max(128, (int)(metrics.WidthPixels * scale) / 2 * 2), height = Math.Max(128, (int)(metrics.HeightPixels * scale) / 2 * 2);
            int fps = Math.Clamp(options.Fps, 15, 60);
            using var format = MediaFormat.CreateVideoFormat("video/avc", width, height);
            format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
            format.SetInteger(MediaFormat.KeyBitRate, Math.Clamp(options.Bitrate, 500_000, 12_000_000));
            format.SetInteger(MediaFormat.KeyFrameRate, fps);
            format.SetInteger(MediaFormat.KeyIFrameInterval, 1);
            format.SetInteger("max-bframes", 0);
            format.SetInteger("repeat-previous-frame-after", 1_000_000 / fps);
            _codec = MediaCodec.CreateEncoderByType("video/avc")!;
            _codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
            _surface = _codec.CreateInputSurface(); _codec.Start();
            _display = _projection.CreateVirtualDisplay("QuicNet", width, height, (int)metrics.DensityDpi,
                (Android.Views.DisplayFlags)(int)VirtualDisplayFlags.AutoMirror, _surface, null, null);
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(_capture.Lifetime.Token);
            IsCapturing = true;
            long generation = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var packetizer = new AndroidAvcPacketizer(width, height, generation);
            _profile = new NativeScreenProfile("Android MediaCodec", 1, width, height, fps, packetizer.Codec, null, generation, "android");
            _pump = Task.Run(() => PumpAsync(packetizer, _lifetime.Token));
        }
        catch (Exception ex)
        {
            _capture.Ready.TrySetException(ex);
            Android.Util.Log.Error("QuicNet", $"[SCREEN] {ex}"); StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    private async Task PumpAsync(AndroidAvcPacketizer packetizer, CancellationToken ct)
    {
        var queue = new RealtimeScreenFrameQueue();
        var sender = Task.Run(async () =>
        {
            await _capture!.ResumeSignal.Task.WaitAsync(ct);
            await queue.DrainAsync(frame => _capture.Output(frame.Data).WaitAsync(ct), ct);
        }, ct);
        string? error = null;
        try
        {
            using var info = new MediaCodec.BufferInfo();
            long lastKey = Environment.TickCount64;
            while (!ct.IsCancellationRequested)
            {
                if (_capture!.ResumeSignal.Task.IsCompleted && (!VoiceCallHandler.IsLocalScreenSharing || VoiceCallHandler.ActiveCalls.IsEmpty)) break;
                if (sender.IsFaulted) await sender;
                if (Environment.TickCount64 - lastKey > 1000)
                {
                    using var request = new Bundle(); request.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 0); _codec!.SetParameters(request); lastKey = Environment.TickCount64;
                }
                int index = _codec!.DequeueOutputBuffer(info, 20_000);
                if (index == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    using var outputFormat = _codec.OutputFormat;
                    foreach (string key in new[] { "csd-0", "csd-1" })
                    {
                        using var csd = outputFormat.GetByteBuffer(key);
                        if (csd == null) continue;
                        byte[] config = new byte[csd.Remaining()]; csd.Get(config); packetizer.Packet(config, 0, false);
                    }
                    if (packetizer.Description != null) _capture.Ready.TrySetResult(_profile! with { Codec = packetizer.Codec, Description = packetizer.Description });
                }
                if (index < 0) continue;
                try
                {
                    using var buffer = _codec.GetOutputBuffer(index);
                    if (buffer == null || info.Size == 0) continue;
                    buffer.Position(info.Offset); buffer.Limit(info.Offset + info.Size);
                    byte[] bytes = new byte[info.Size]; buffer.Get(bytes);
                    bool key = (info.Flags & MediaCodecBufferFlags.KeyFrame) != 0;
                    byte[]? packet = packetizer.Packet(bytes, info.PresentationTimeUs, key);
                    if (packetizer.Description != null) _capture.Ready.TrySetResult(_profile! with { Codec = packetizer.Codec, Description = packetizer.Description });
                    if (packet != null && _capture.ResumeSignal.Task.IsCompleted) queue.Enqueue(new EncodedScreenFrame(packet, (packet[0] & 1) != 0, info.PresentationTimeUs));
                }
                finally { _codec.ReleaseOutputBuffer(index, false); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { error = ex.Message; Android.Util.Log.Error("QuicNet", $"[SCREEN] {ex}"); }
        finally
        {
            _capture!.Ready.TrySetException(new InvalidOperationException(error ?? "La captura se ha detenido."));
            queue.Complete(); _lifetime!.Cancel();
            try { await sender; } catch (OperationCanceledException) { } catch (Exception ex) { error ??= ex.Message; }
            _capture!.Stopped(error);
            new Handler(Looper.MainLooper!).Post(StopSelf);
        }
    }

    public override void OnDestroy()
    {
        IsCapturing = false; _lifetime?.Cancel();
        var pump = _pump;
        // Release only once dequeue has stopped; never race codec teardown with its worker.
        _ = Task.Run(async () =>
        {
            if (pump != null) try { await pump; } catch { }
            try { _display?.Release(); _display?.Dispose(); } catch { }
            try { _codec?.Stop(); _codec?.Release(); _codec?.Dispose(); } catch { }
            try { _surface?.Release(); _surface?.Dispose(); } catch { }
            try { if (_callback != null) _projection?.UnregisterCallback(_callback); _projection?.Stop(); _projection?.Dispose(); _callback?.Dispose(); } catch { }
            _lifetime?.Dispose();
            _capture?.Released.TrySetResult();
        });
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }
}
