using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using QuicNet.Video;
using QuicPunch;

namespace QuicNet.Protocols;

internal sealed record ScreenTransmissionSettings(string Codec = "h264", int Width = 1920, int Height = 1080,
    int Fps = 60, int Bitrate = 12_000_000, string Encoder = "auto", string Preset = "balanced",
    string RateControl = "cbr", int Quality = 23, int KeyFrameIntervalMs = 1000, int BufferMs = 50);
internal sealed record ScreenControlRequest(Guid RequestId, string Action, long Revision = 0, ScreenTransmissionSettings? Settings = null);
internal sealed record ScreenControlCapabilities(int Version, long Revision, string Backend, bool Sharing,
    bool CanStart, bool CanConfigure, string[] Codecs, string[] Encoders, int[] Heights, int[] FrameRates,
    int MinBitrate, int MaxBitrate, ScreenTransmissionSettings Current, string[] Presets, string[] RateControls,
    int MinQuality, int MaxQuality, int MinKeyFrameIntervalMs, int MaxKeyFrameIntervalMs, int MinBufferMs, int MaxBufferMs);
internal sealed record ScreenControlReply(Guid RequestId, bool Success, string Code, string? Error = null,
    ScreenControlCapabilities? Capabilities = null);

internal sealed partial class VoiceCallHandler
{
    private static readonly JsonSerializerOptions ScreenControlJson = new() { PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<(Guid Peer, Guid Request), TaskCompletionSource<ScreenControlReply>> _screenReplies = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _browserUpdates = new();
    private readonly SemaphoreSlim _screenChangeGate = new(1, 1);
    private readonly SemaphoreSlim _screenControlSlots = new(4, 4);
    private readonly ConcurrentDictionary<Guid, DateTime> _screenConsentRequests = new();
    private readonly CancellationTokenSource _screenControlLifetime = new();
    private readonly object _screenSettingsLock = new();
    private ScreenEncoderOptions? _nativeScreenOptions;
    private ScreenTransmissionSettings _browserScreenSettings = new();
    private string[] _browserScreenCodecs = [];
    private DateTime _browserScreenReportedAt;
    private long _screenSettingsRevision = 1;
    private PeerInfo? _remoteCaptureOwner;
    private int _remoteCaptureRevocationQueued;
    public event Action<Guid, Guid, ScreenTransmissionSettings>? OnBrowserScreenSettingsRequested;
    public event Action? OnLocalScreenControlChanged;
    public event Func<byte, byte[], Task>? OnLocalNativeScreenFrame;
    internal Func<IPlatformScreenCapture?>? GetPlatformScreenCapture { get; set; }
    internal Func<PeerInfo, bool, CancellationToken, Task>? StartRemotePlatformScreen { get; set; }
    internal NativeScreenProfile? PlatformScreenProfile { get; set; }
    public ScreenEncoderOptions? NativeScreenSettings { get { lock (_screenSettingsLock) return _nativeScreenOptions; } }

    internal bool HasScreenPermission(PeerInfo peer, string permission) => _qcc.IsTrustedPeer(peer) &&
        peer.TryGetCertificateHash(out var hash) && _qcc.PeerStore?.HasAutomaticPermission(hash, permission) == true;

    public void ReportBrowserScreenSettings(ScreenTransmissionSettings settings, string[] codecs)
    {
        ValidateScreenSettings(settings);
        lock (_screenSettingsLock)
        {
            var supported = codecs.Where(c => c is "h264" or "hevc" or "vp9" or "av1").Distinct().Order().ToArray();
            if (_browserScreenSettings != settings || !_browserScreenCodecs.SequenceEqual(supported)) _screenSettingsRevision++;
            _browserScreenSettings = settings;
            _browserScreenCodecs = supported;
            _browserScreenReportedAt = DateTime.UtcNow;
        }
    }

    internal static void ValidateScreenSettings(ScreenTransmissionSettings settings)
    {
        if (settings.Codec is not ("h264" or "hevc" or "vp9" or "av1") ||
            settings.Width is < 128 or > 7680 || settings.Height is < 128 or > 4320 ||
            settings.Width % 2 != 0 || settings.Height % 2 != 0 || settings.Fps is < 5 or > 144 ||
            settings.Bitrate is < 500_000 or > 100_000_000 || settings.Quality is < 0 or > 51 ||
            settings.KeyFrameIntervalMs is < 250 or > 5000 || settings.BufferMs is < 16 or > 250 ||
            settings.Preset is not ("speed" or "balanced" or "quality") || settings.RateControl is not ("cbr" or "vbr"))
            throw new ArgumentException("Invalid screen transmission settings.");
    }

    public bool CompleteBrowserScreenUpdate(Guid requestId, bool success) =>
        _browserUpdates.TryGetValue(requestId, out var completion) && completion.TrySetResult(success);

    public async Task<ScreenControlReply> RequestScreenControlAsync(Guid peerId, string action,
        long revision = 0, ScreenTransmissionSettings? settings = null, CancellationToken ct = default)
    {
        if (!ActiveCalls.TryGetValue(peerId, out var session)) throw new InvalidOperationException("No active call with this peer.");
        if (action is not ("capabilities" or "start" or "start-control" or "configure")) throw new ArgumentException("Unknown screen action.");
        if (session.RemoteScreenControlVersion == 0)
        {
            if (action != "start") throw new InvalidOperationException("Actualiza la aplicación del emisor para modificar su transmisión.");
            await session.SendFrameAsync(FrameTypeScreenShareRequest, ct: ct).ConfigureAwait(false);
            return new(Guid.NewGuid(), true, "Requested");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.CancellationToken, _screenControlLifetime.Token);
        linked.CancelAfter(IsScreenStartAction(action) ? global::QuicPunch.QuicPunch.SessionConsentTimeout + TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(70));
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource<ScreenControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _screenReplies[(peerId, id)] = completion;
        try
        {
            await session.SendFrameAsync(FrameTypeScreenControlRequest,
                JsonSerializer.SerializeToUtf8Bytes(new ScreenControlRequest(id, action, revision, settings), QuicNet.Serialization.SharedJson.Options), linked.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !session.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("El peer no respondió al control de pantalla; puede necesitar actualizar su aplicación.");
        }
        finally { _screenReplies.TryRemove((peerId, id), out _); }
    }

    private void ReceiveScreenControlReply(PeerInfo peer, byte[] payload)
    {
        if (payload.Length > 16_384) return;
        try
        {
            var reply = JsonSerializer.Deserialize<ScreenControlReply>(payload, QuicNet.Serialization.SharedJson.WithOptions(ScreenControlJson));
            if (reply != null && _screenReplies.TryGetValue((peer.Id, reply.RequestId), out var completion)) completion.TrySetResult(reply);
        }
        catch (JsonException) { }
    }

    private async Task ProcessScreenControlFrameAsync(VoiceCallSession session, byte[] payload)
    {
        if (!await _screenControlSlots.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (payload.Length > 16_384) return;
            var request = JsonSerializer.Deserialize<ScreenControlRequest>(payload, QuicNet.Serialization.SharedJson.WithOptions(ScreenControlJson));
            if (request == null || request.RequestId == Guid.Empty) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken, _screenControlLifetime.Token);
            timeout.CancelAfter(IsScreenStartAction(request.Action) ? global::QuicPunch.QuicPunch.SessionConsentTimeout + TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(65));
            var reply = await ProcessScreenControlRequestAsync(session.Peer, request, timeout.Token).ConfigureAwait(false);
            await session.SendFrameAsync(FrameTypeScreenControlReply, JsonSerializer.SerializeToUtf8Bytes(reply, QuicNet.Serialization.SharedJson.Options), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { QuicPunchLog.Info($"[SCREEN CONTROL] {ex.Message}"); }
        finally { _screenControlSlots.Release(); }
    }

    private async Task ProcessLegacyScreenRequestAsync(VoiceCallSession session)
    {
        try
        {
            var reply = await ProcessScreenControlRequestAsync(session.Peer,
                new(Guid.NewGuid(), "start"), session.CancellationToken).ConfigureAwait(false);
            if (!reply.Success && reply.Code != "ConsentRequired") await session.SendFrameAsync(FrameTypeScreenShareRequestDenied).ConfigureAwait(false);
        }
        catch (Exception ex) { QuicPunchLog.Info($"[SCREEN CONTROL] {ex.Message}"); }
    }

    private static bool IsScreenStartAction(string action) => action is "start" or "start-control";

    internal async Task<ScreenControlReply> ProcessScreenControlRequestAsync(PeerInfo peer, ScreenControlRequest request, CancellationToken ct)
    {
        ScreenControlReply Denied(string code, string message) => new(request.RequestId, false, code, message);
        if (!_qcc.IsTrustedPeer(peer)) return Denied("Denied", "Peer is not trusted.");
        if (!ActiveCalls.TryGetValue(peer.Id, out var session)) return Denied("Denied", "The call has ended.");
        if (request.Action == "capabilities")
            return new(request.RequestId, true, "Completed", Capabilities: await GetScreenControlCapabilitiesAsync(peer, ct).ConfigureAwait(false));
        bool start = IsScreenStartAction(request.Action);
        bool forControl = request.Action == "start-control";
        if (!start && request.Action != "configure") return Denied("Unsupported", "Unknown screen action.");
        if (start && !session.IsScreenShareAllowed)
            return Denied("Denied", "El emisor ha desactivado el vídeo para esta llamada.");
        if (start && !HasScreenPermission(peer, "screen-share-request"))
        {
            var now = DateTime.UtcNow;
            if (!_screenConsentRequests.TryGetValue(peer.Id, out var last) || now - last > TimeSpan.FromSeconds(10))
            {
                _screenConsentRequests[peer.Id] = now;
                OnScreenShareRequested?.Invoke(peer, forControl);
            }
            return Denied("ConsentRequired", forControl
                ? "Cuando el contacto comparta una pantalla compatible, vuelve a solicitar el control desde el visor."
                : "La otra persona debe aceptar compartir su pantalla.");
        }
        if (request.Action == "configure" && !HasScreenPermission(peer, "screen-settings"))
            return Denied("Denied", "No tienes permiso para cambiar esta transmisión.");
        if (!await _screenChangeGate.WaitAsync(0, ct).ConfigureAwait(false)) return Denied("Busy", "Hay otro cambio de transmisión en curso.");
        try
        {
            if (start && IsLocalScreenSharing)
            {
                if (LocalVideoKind != "screen") return Denied("Busy", "El otro dispositivo tiene la cámara activa.");
                if (!CanReceiveLocalScreen(session)) return Denied("Denied", "La captura actual está dirigida a otro destinatario.");
                await NotifySessionScreenShareStartAsync(session).ConfigureAwait(false);
            }
            else if (start)
            {
                if (GetPlatformScreenCapture?.Invoke() != null && StartRemotePlatformScreen is { } startPlatform)
                {
                    await startPlatform(peer, forControl, ct).ConfigureAwait(false);
                    return new(request.RequestId, true, "Completed");
                }
                if (!FfmpegScreenEncoder.CaptureAvailable)
                {
                    if (forControl)
                        return Denied("Unsupported", "El contacto necesita una captura nativa compatible con el control remoto.");
                    OnScreenShareRequested?.Invoke(peer, false);
                    return Denied("ConsentRequired", "El otro dispositivo debe elegir la pantalla que quiere compartir.");
                }
                if (!HasScreenPermission(peer, "screen-share-request")) return Denied("Denied", "Permission revoked.");
                if (forControl && WindowsRemoteInput.CaptureArea("desktop") == null && X11RemoteInput.CaptureArea("desktop") == null)
                    return Denied("Unsupported", "El contacto necesita un escritorio compatible con el control remoto.");
                string? codec = new[] { "h264", "hevc", "vp9", "av1" }.FirstOrDefault(c =>
                    session.RemoteScreenDecoderCodecMask == 0 ||
                    (session.RemoteScreenDecoderCodecMask & (1u << ScreenEncoderOptions.CodecIdFor(c))) != 0);
                if (codec == null) return Denied("Unsupported", "No hay un códec de pantalla compatible con el receptor.");
                await StartNativeScreenCoreAsync(new() { Codec = codec,
                    Source = "desktop", TargetPeerId = peer.Id }, peer, ct).ConfigureAwait(false);
            }
            else
            {
                if (!IsLocalScreenSharing || !CanReceiveLocalScreen(session)) return Denied("Denied", "No accessible screen transmission is active.");
                var caps = await GetScreenControlCapabilitiesAsync(peer, ct).ConfigureAwait(false);
                if (caps.Backend == "platform") return Denied("Unsupported", "Los ajustes de esta captura se cambian en el dispositivo emisor.");
                if (request.Revision != caps.Revision) return Denied("Conflict", "Los ajustes han cambiado. Vuelve a consultarlos.");
                var settings = request.Settings ?? throw new ArgumentException("Missing settings.");
                ValidateScreenSettings(settings);
                if (!caps.Codecs.Contains(settings.Codec) || !caps.Encoders.Contains(settings.Encoder) ||
                    !caps.Heights.Contains(settings.Height) || !caps.FrameRates.Contains(settings.Fps) ||
                    settings.Width != settings.Height * 16 / 9 || settings.Bitrate > caps.MaxBitrate)
                    return Denied("Unsupported", "El emisor no ofrece estos ajustes.");
                if (!HasScreenPermission(peer, "screen-settings")) return Denied("Denied", "Permission revoked.");
                if (ScreenEncoder.Profile != null && _nativeScreenOptions != null)
                {
                    var previous = _nativeScreenOptions;
                    await ReplaceNativeScreenAsync(previous, previous with { Codec = settings.Codec, Width = settings.Width, Height = settings.Height,
                            Fps = settings.Fps, Bitrate = settings.Bitrate, Encoder = settings.Encoder, Preset = settings.Preset, EncoderPreset = null,
                            RateControl = settings.RateControl, Quality = settings.Quality, KeyFrameIntervalMs = settings.KeyFrameIntervalMs,
                            BufferMs = settings.BufferMs }, _remoteCaptureOwner, ct).ConfigureAwait(false);
                }
                else
                {
                    if (OnBrowserScreenSettingsRequested == null) return Denied("Unsupported", "La interfaz de captura no está disponible.");
                    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _browserUpdates[request.RequestId] = completion;
                    try
                    {
                        OnBrowserScreenSettingsRequested.Invoke(peer.Id, request.RequestId, settings);
                        if (!await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
                            return Denied("Failed", "El navegador no pudo aplicar el perfil o el receptor no admite el códec.");
                    }
                    finally { _browserUpdates.TryRemove(request.RequestId, out _); }
                }
            }
            return new(request.RequestId, true, "Completed", Capabilities: request.Action == "configure"
                ? await GetScreenControlCapabilitiesAsync(peer, ct).ConfigureAwait(false) : null);
        }
        catch (UnauthorizedAccessException ex) { return Denied("Denied", ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Denied("Failed", ex.Message); }
        finally { _screenChangeGate.Release(); }
    }

    private async Task<ScreenControlCapabilities> GetScreenControlCapabilitiesAsync(PeerInfo peer, CancellationToken ct)
    {
        var platform = GetPlatformScreenCapture?.Invoke();
        bool native = ScreenEncoder.Profile != null || platform == null && !IsLocalScreenSharing && FfmpegScreenEncoder.CaptureAvailable;
        ActiveCalls.TryGetValue(peer.Id, out var session);
        uint receiverMask = session?.RemoteScreenDecoderCodecMask ?? 0;
        var encoders = native ? (await ScreenEncoder.CapabilitiesAsync(ct).ConfigureAwait(false)).Where(e => e.Available &&
            (receiverMask == 0 || (receiverMask & (1u << ScreenEncoderOptions.CodecIdFor(e.Codec))) != 0)).ToArray() : [];
        lock (_screenSettingsLock)
        {
            var options = _nativeScreenOptions ?? new ScreenEncoderOptions();
            var current = native ? new ScreenTransmissionSettings(options.Codec, options.Width, options.Height, options.Fps,
                options.Bitrate, options.Encoder, options.Preset, options.RateControl, options.Quality, options.KeyFrameIntervalMs, options.BufferMs) : _browserScreenSettings;
            if (native && ScreenEncoder.Profile is { } profile) current = current with { Width = profile.Width, Height = profile.Height, Fps = profile.Fps, Encoder = profile.Encoder };
            if (!native && IsLocalScreenSharing)
                current = current with { Width = LocalScreenWidth, Height = LocalScreenHeight, Fps = LocalScreenFps };
            var codecs = native ? encoders.Select(e => e.Codec).Distinct().ToArray() : platform != null ?
                platform.Encoders().Where(e => e.Available).Select(e => e.Codec).ToArray() :
                DateTime.UtcNow - _browserScreenReportedAt < TimeSpan.FromSeconds(30) ? _browserScreenCodecs : [];
            bool accessible = !IsLocalScreenSharing || session != null && CanReceiveLocalScreen(session);
            return new(1, _screenSettingsRevision, native ? "native" : platform != null ? "platform" : "browser", IsLocalScreenSharing && accessible,
                HasScreenPermission(peer, "screen-share-request"), platform == null && accessible && HasScreenPermission(peer, "screen-settings"), codecs,
                native ? encoders.Select(e => e.Id).ToArray() : ["auto"],
                [720, 1080, 1440, 2160], [15, 24, 30, 60, 120, 144], 500_000, native ? 100_000_000 : 30_000_000, current,
                native ? ["speed", "balanced", "quality"] : [], native ? ["cbr", "vbr"] : [],
                0, 51, 250, 5000, 16, 250);
        }
    }

    public async Task<NativeScreenProfile> StartNativeScreenAsync(ScreenEncoderOptions options, PeerInfo? remoteOwner = null, CancellationToken ct = default)
    {
        if (!await _screenChangeGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Hay otro cambio de transmisión en curso.");
        try
        {
            options.Validate();
            var previous = ScreenEncoder.Profile != null ? NativeScreenSettings : null;
            if (previous != null && previous.Source == options.Source && previous.TargetPeerId == options.TargetPeerId)
                return await ReplaceNativeScreenAsync(previous, options, remoteOwner ?? _remoteCaptureOwner, ct).ConfigureAwait(false);
            return await StartNativeScreenCoreAsync(options, remoteOwner, ct).ConfigureAwait(false);
        }
        finally { _screenChangeGate.Release(); }
    }

    private async Task<NativeScreenProfile> ReplaceNativeScreenAsync(ScreenEncoderOptions previous, ScreenEncoderOptions next,
        PeerInfo? owner, CancellationToken ct)
    {
        try { return await StartNativeScreenCoreAsync(next, owner, ct).ConfigureAwait(false); }
        catch
        {
            // A request can end while the encoder is being replaced. Restore
            // with the handler lifetime so a live call keeps its last profile.
            try { await StartNativeScreenCoreAsync(previous, owner, _screenControlLifetime.Token).ConfigureAwait(false); }
            catch
            {
                await StopScreenShareAsync().ConfigureAwait(false);
                OnLocalScreenControlChanged?.Invoke();
            }
            throw;
        }
    }

    private async Task<NativeScreenProfile> StartNativeScreenCoreAsync(ScreenEncoderOptions options, PeerInfo? remoteOwner, CancellationToken ct)
    {
        await StopSystemAudioAsync().ConfigureAwait(false);
        options.Validate();
        if (ActiveCalls.IsEmpty || options.TargetPeerId.HasValue && !ActiveCalls.ContainsKey(options.TargetPeerId.Value))
            throw new InvalidOperationException("The target call has ended.");
        var bandwidth = new ScreenBandwidthController(options.Bitrate, options.Fps);
        long lastFeedback = 0;
        var profile = await ScreenEncoder.StartAsync(options, async config =>
        {
            if (remoteOwner != null && !HasScreenPermission(remoteOwner, "screen-share-request")) return false;
            // Reception is advisory per peer; it must never veto local encoding.
            await ProbeScreenDecodersAsync(config, options.TargetPeerId, ct).ConfigureAwait(false);
            if (remoteOwner != null && !HasScreenPermission(remoteOwner, "screen-share-request")) return false;
            await StartScreenShareAsync(config.Width, config.Height, config.CodecId, (byte)config.Fps, options.TargetPeerId, options.Source.StartsWith("camera:", StringComparison.Ordinal) ? "camera" : "screen").ConfigureAwait(false);
            return true;
        }, async (codecId, packet) =>
        {
            if (remoteOwner != null && !HasScreenPermission(remoteOwner, "screen-share-request"))
            {
                await StopScreenShareAsync().ConfigureAwait(false);
                var revokedGeneration = ScreenEncoder.Profile?.Generation;
                if (Interlocked.Exchange(ref _remoteCaptureRevocationQueued, 1) == 0)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _screenChangeGate.WaitAsync(_screenControlLifetime.Token).ConfigureAwait(false);
                            try
                            {
                                if (_remoteCaptureOwner == remoteOwner && ScreenEncoder.Profile?.Generation == revokedGeneration)
                                {
                                    await ScreenEncoder.StopAsync().ConfigureAwait(false);
                                    OnLocalScreenControlChanged?.Invoke();
                                }
                            }
                            finally { _screenChangeGate.Release(); }
                        }
                        catch (Exception ex) { QuicPunchLog.Info($"[SCREEN CONTROL] {ex.Message}"); }
                    });
                return;
            }
            await SendScreenFrameAsync(packet).ConfigureAwait(false);
            if (ScreenEncoder.CanAdaptRate && (lastFeedback == 0 || Stopwatch.GetElapsedTime(lastFeedback).TotalSeconds >= 2))
            {
                lastFeedback = Stopwatch.GetTimestamp();
                var current = ActiveCalls.Values.Where(session => CanReceiveLocalScreen(session) && session.IsScreenDecoderApproved && session.Connection.IsNativeQuic)
                    .Select(session => { var sample = session.VideoNetworkCounts; return (session.Peer.Id, sample.Sent, sample.Lost); });
                var rate = bandwidth.Observe(current);
                ScreenEncoder.SetRealtimeRate(rate.Bitrate, rate.Fps);
            }
            if (OnLocalNativeScreenFrame != null) await OnLocalNativeScreenFrame.Invoke(codecId, packet).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        lock (_screenSettingsLock) { _nativeScreenOptions = options; _remoteCaptureOwner = remoteOwner; _screenSettingsRevision++; _remoteCaptureRevocationQueued = 0; }
        OnLocalScreenControlChanged?.Invoke();
        return profile;
    }

    private void DisposeScreenControl()
    {
        _screenControlLifetime.Cancel();
        foreach (var pending in _screenReplies.Values) pending.TrySetCanceled();
        foreach (var pending in _browserUpdates.Values) pending.TrySetCanceled();
        _screenReplies.Clear();
        _browserUpdates.Clear();
    }
}
