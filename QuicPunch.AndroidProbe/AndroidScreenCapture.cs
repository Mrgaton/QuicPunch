using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Display;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
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

    public NativeCamera[] Cameras()
    {
        var manager = (CameraManager)context.GetSystemService(Context.CameraService)!;
        return (manager.GetCameraIdList() ?? []).Select(id => {
            using var info = manager.GetCameraCharacteristics(id);
            var lens = (Java.Lang.Integer?)info.Get(CameraCharacteristics.LensFacing);
            return new NativeCamera("camera:" + id, lens?.IntValue() == (int)LensFacing.Front ? "Cámara frontal" : "Cámara trasera " + id);
        }).ToArray();
    }
    public ScreenEncoderCapability[] Encoders() => new[] { (1, "h264"), (5, "hevc"), (4, "av1") }.Select(c => {
        string? component = AndroidVideoHost.FindComponent(c.Item1, 640, 360, true);
        return new ScreenEncoderCapability(c.Item2 + "_mediacodec", c.Item2, component != null,
            component == null ? "No hay codificador de hardware anunciado para 640×360." : "FFmpeg · " + component + ". El perfil final se valida al iniciar.");
    }).ToArray();

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
            bool camera = options.Source.StartsWith("camera:", StringComparison.Ordinal);
            Intent? permission = null;
            if (camera) await activity.RequestNativeCameraAsync().WaitAsync(Lifetime.Token);
            else permission = await activity.RequestProjectionAsync().WaitAsync(Lifetime.Token);
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

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection | ForegroundService.TypeCamera)]
public sealed class AndroidScreenCaptureService : Service
{
    internal static bool IsCapturing { get; private set; }
    private CancellationTokenSource? _lifetime;
    private AndroidScreenCapture? _capture;
    private MediaProjection? _projection;
    private MediaProjection.Callback? _callback;
    private nint _encoder;
    private CameraDevice? _camera;
    private CameraCaptureSession? _cameraSession;
    private HandlerThread? _cameraThread;
    private readonly TaskCompletionSource _cameraReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            bool camera = _capture.Options.Source.StartsWith("camera:", StringComparison.Ordinal);
            var notice = new Notification.Builder(this, Channel).SetContentTitle(camera ? "Compartiendo tu cámara" : "Compartiendo tu pantalla")
                .SetContentText("Los participantes de la llamada pueden verla.").SetSmallIcon(Android.Resource.Drawable.IcMenuView)
                .SetContentIntent(open).SetOngoing(true)
                .AddAction(new Notification.Action.Builder(null, "Dejar de compartir", stop).Build()).Build();
            StartForeground(5072, notice, camera ? (OperatingSystem.IsAndroidVersionAtLeast(30) ? ForegroundService.TypeCamera : (ForegroundService)0) : ForegroundService.TypeMediaProjection);
            var metrics = Resources!.DisplayMetrics!;
            var options = _capture.Options;
            int width, height;
            if (camera)
            {
                var managerCamera = (CameraManager)GetSystemService(CameraService)!;
                using var info = managerCamera.GetCameraCharacteristics(options.Source[7..]);
                var configurations = (StreamConfigurationMap?)info.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                var sizes = configurations?.GetOutputSizes(Java.Lang.Class.FromType(typeof(MediaCodec))) ?? [];
                var size = sizes.Where(s => s.Width <= options.Width && s.Height <= options.Height)
                    .OrderByDescending(s => s.Width * s.Height).FirstOrDefault()
                    ?? throw new InvalidOperationException("La cámara no ofrece una resolución dentro del límite seleccionado.");
                width = size.Width; height = size.Height;
            }
            else
            {
#pragma warning disable CA1422, CS0618
                var permission = (Intent?)intent!.GetParcelableExtra("permission") ?? throw new InvalidOperationException("Falta el permiso de captura.");
#pragma warning restore CA1422, CS0618
                var projectionManager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
                _projection = projectionManager.GetMediaProjection((int)Result.Ok, permission) ?? throw new InvalidOperationException("Android ha denegado la pantalla.");
                _callback = new ProjectionCallback(this);
                _projection.RegisterCallback(_callback, new Handler(Looper.MainLooper!));
                double scale = Math.Min(1, Math.Min((double)options.Width / metrics.WidthPixels, (double)options.Height / metrics.HeightPixels));
                width = Math.Max(128, (int)(metrics.WidthPixels * scale) / 2 * 2); height = Math.Max(128, (int)(metrics.HeightPixels * scale) / 2 * 2);
            }
            int fps = options.Fps;
            string component = AndroidVideoHost.FindComponent(options.CodecId, width, height, true)
                ?? throw new InvalidOperationException("No hay codificador FFmpeg/MediaCodec para este códec y resolución.");
            _surface = MediaCodec.CreatePersistentInputSurface();
            _encoder = NativeMedia.qpv_encoder_open(options.CodecId, width, height, fps, options.Bitrate,
                options.KeyFrameIntervalMs, _surface!.Handle, component, options.RateControl == "vbr" ? 1 : 0, options.BufferMs);
            if (_encoder == 0) throw new InvalidOperationException(NativeMedia.Error);
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(_capture.Lifetime.Token);
            if (camera)
            {
                _cameraThread = new HandlerThread("QuicNet camera"); _cameraThread.Start();
                var managerCamera = (CameraManager)GetSystemService(CameraService)!;
                managerCamera.OpenCamera(options.Source[7..], new CameraState(this), new Handler(_cameraThread.Looper!));
            }
            else _display = _projection!.CreateVirtualDisplay("QuicNet", width, height, (int)metrics.DensityDpi,
                (Android.Views.DisplayFlags)(int)VirtualDisplayFlags.AutoMirror, _surface, null, null);
            IsCapturing = true;
            long generation = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var packetizer = new NativeCapturePacketizer(options.CodecId, width, height, generation);
            _profile = new NativeScreenProfile("FFmpeg · " + component, options.CodecId, width, height, fps, packetizer.Codec, null, generation, "android");
            _pump = Task.Run(() => PumpAsync(packetizer, _lifetime.Token));
        }
        catch (Exception ex)
        {
            _capture.Ready.TrySetException(ex);
            Android.Util.Log.Error("QuicNet", $"[SCREEN] {ex}"); StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    private async Task PumpAsync(NativeCapturePacketizer packetizer, CancellationToken ct)
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
            byte[] output = new byte[2 * 1024 * 1024];
            if (_capture!.Options.Source.StartsWith("camera:", StringComparison.Ordinal))
                await _cameraReady.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            while (!ct.IsCancellationRequested)
            {
                if (_capture!.ResumeSignal.Task.IsCompleted && (!VoiceCallHandler.IsLocalScreenSharing || VoiceCallHandler.ActiveCalls.IsEmpty)) break;
                if (sender.IsFaulted) await sender;
                int length = NativeMedia.qpv_encoder_read(_encoder, output, output.Length, out long timestamp, out int key);
                if (length < 0) throw new InvalidOperationException(NativeMedia.Error);
                if (length == 0) { await Task.Delay(3, ct); continue; }
                byte[]? packet = packetizer.Packet(output.AsSpan(0, length).ToArray(), timestamp, key != 0);
                if (packetizer.Description != null) _capture.Ready.TrySetResult(_profile! with { Codec = packetizer.Codec, Description = packetizer.Description });
                if (packet != null && _capture.ResumeSignal.Task.IsCompleted)
                    queue.Enqueue(new EncodedScreenFrame(packet, (packet[0] & 1) != 0, timestamp));
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

    private sealed class CameraState(AndroidScreenCaptureService owner) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera)
        {
            if (owner._lifetime?.IsCancellationRequested != false) { camera.Close(); return; }
            owner._camera = camera;
#pragma warning disable CA1422, CS0618
            camera.CreateCaptureSession(new[] { owner._surface! }, new CameraSessionState(owner), new Handler(owner._cameraThread!.Looper!));
#pragma warning restore CA1422, CS0618
        }
        public override void OnDisconnected(CameraDevice camera) { owner._cameraReady.TrySetException(new IOException("Cámara desconectada.")); camera.Close(); owner.StopSelf(); }
        public override void OnError(CameraDevice camera, CameraError error) { owner._cameraReady.TrySetException(new IOException("Error de cámara: " + error)); camera.Close(); owner.StopSelf(); }
    }
    private sealed class CameraSessionState(AndroidScreenCaptureService owner) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            try
            {
                if (owner._lifetime?.IsCancellationRequested != false) { session.Close(); return; }
                owner._cameraSession = session;
                using var request = owner._camera!.CreateCaptureRequest(CameraTemplate.Record);
                request.AddTarget(owner._surface!);
                session.SetRepeatingRequest(request.Build(), null, new Handler(owner._cameraThread!.Looper!));
                owner._cameraReady.TrySetResult();
            }
            catch (Exception ex) { owner._cameraReady.TrySetException(ex); owner.StopSelf(); }
        }
        public override void OnConfigureFailed(CameraCaptureSession session) { owner._cameraReady.TrySetException(new IOException("La cámara rechazó la superficie del codificador.")); owner.StopSelf(); }
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
            try { _cameraSession?.StopRepeating(); _cameraSession?.Close(); _cameraSession?.Dispose(); _camera?.Close(); _camera?.Dispose(); } catch { }
            try { _cameraThread?.QuitSafely(); _cameraThread?.Join(1000); _cameraThread?.Dispose(); } catch { }
            try { if (_encoder != 0) { NativeMedia.qpv_encoder_close(_encoder); _encoder = 0; } } catch { }
            try { _surface?.Release(); _surface?.Dispose(); } catch { }
            try { if (_callback != null) _projection?.UnregisterCallback(_callback); _projection?.Stop(); _projection?.Dispose(); _callback?.Dispose(); } catch { }
            _lifetime?.Dispose();
            _capture?.Released.TrySetResult();
        });
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }
}
