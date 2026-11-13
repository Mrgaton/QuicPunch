using System.Collections.Concurrent;
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
    public ScreenEncoderOptions? NativeScreenSettings { get { lock (_screenSettingsLock) return _nativeScreenOptions; } }

    private bool HasScreenPermission(PeerInfo peer, string permission) => _qcc.IsTrustedPeer(peer) &&
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
        if (action is not ("capabilities" or "start" or "configure")) throw new ArgumentException("Unknown screen action.");
        if (session.RemoteScreenControlVersion == 0)
        {
            if (action != "start") throw new InvalidOperationException("Actualiza la aplicación del emisor para modificar su transmisión.");
            await session.SendFrameAsync(FrameTypeScreenShareRequest, ct: ct).ConfigureAwait(false);
            return new(Guid.NewGuid(), true, "Requested");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.CancellationToken, _screenControlLifetime.Token);
        linked.CancelAfter(action == "start" ? global::QuicPunch.QuicPunch.SessionConsentTimeout + TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(70));
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
            timeout.CancelAfter(request.Action == "start" ? global::QuicPunch.QuicPunch.SessionConsentTimeout + TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(65));
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

    internal async Task<ScreenControlReply> ProcessScreenControlRequestAsync(PeerInfo peer, ScreenControlRequest request, CancellationToken ct)
    {
        ScreenControlReply Denied(string code, string message) => new(request.RequestId, false, code, message);
        if (!_qcc.IsTrustedPeer(peer)) return Denied("Denied", "Peer is not trusted.");
        if (!ActiveCalls.TryGetValue(peer.Id, out var session)) return Denied("Denied", "The call has ended.");
        if (request.Action == "capabilities")
            return new(request.RequestId, true, "Completed", Capabilities: await GetScreenControlCapabilitiesAsync(peer, ct).ConfigureAwait(false));
        if (request.Action is not ("start" or "configure")) return Denied("Unsupported", "Unknown screen action.");
        if (request.Action == "start" && !HasScreenPermission(peer, "screen-share-request"))
        {
            var now = DateTime.UtcNow;
            if (!_screenConsentRequests.TryGetValue(peer.Id, out var last) || now - last > TimeSpan.FromSeconds(10))
            {
                _screenConsentRequests[peer.Id] = now;
                OnScreenShareRequested?.Invoke(peer);
            }
            return Denied("ConsentRequired", "La otra persona debe aceptar compartir su pantalla.");
        }
        if (request.Action == "configure" && !HasScreenPermission(peer, "screen-settings"))
            return Denied("Denied", "No tienes permiso para cambiar esta transmisión.");
        if (!await _screenChangeGate.WaitAsync(0, ct).ConfigureAwait(false)) return Denied("Busy", "Hay otro cambio de transmisión en curso.");
        try
        {
            if (request.Action == "start" && IsLocalScreenSharing)
            {
                if (!CanReceiveLocalScreen(session)) return Denied("Denied", "La captura actual está dirigida a otro destinatario.");
                await NotifySessionScreenShareStartAsync(session).ConfigureAwait(false);
            }
            else if (request.Action == "start")
            {
                var caps = await GetScreenControlCapabilitiesAsync(peer, ct).ConfigureAwait(false);
                if (caps.Codecs.Length == 0 || caps.Backend != "native")
                {
                    OnScreenShareRequested?.Invoke(peer);
                    return Denied("ConsentRequired", "La captura disponible necesita una acción local en el navegador.");
                }
                if (!HasScreenPermission(peer, "screen-share-request")) return Denied("Denied", "Permission revoked.");
                await StartNativeScreenCoreAsync(new() { Codec = caps.Codecs.Contains("h264") ? "h264" : caps.Codecs[0],
                    Source = "desktop", TargetPeerId = peer.Id }, peer, ct).ConfigureAwait(false);
            }
            else
            {
                if (!IsLocalScreenSharing || !CanReceiveLocalScreen(session)) return Denied("Denied", "No accessible screen transmission is active.");
                var caps = await GetScreenControlCapabilitiesAsync(peer, ct).ConfigureAwait(false);
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
                    try
                    {
                        await StartNativeScreenCoreAsync(previous with { Codec = settings.Codec, Width = settings.Width, Height = settings.Height,
                            Fps = settings.Fps, Bitrate = settings.Bitrate, Encoder = settings.Encoder, Preset = settings.Preset, EncoderPreset = null,
                            RateControl = settings.RateControl, Quality = settings.Quality, KeyFrameIntervalMs = settings.KeyFrameIntervalMs,
                            BufferMs = settings.BufferMs }, _remoteCaptureOwner, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        try { await StartNativeScreenCoreAsync(previous, _remoteCaptureOwner, ct).ConfigureAwait(false); }
                        catch { await StopScreenShareAsync().ConfigureAwait(false); }
                        throw;
                    }
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
            return new(request.RequestId, true, "Completed", Capabilities: await GetScreenControlCapabilitiesAsync(peer, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Denied("Failed", ex.Message); }
        finally { _screenChangeGate.Release(); }
    }

    private async Task<ScreenControlCapabilities> GetScreenControlCapabilitiesAsync(PeerInfo peer, CancellationToken ct)
    {
        bool native = ScreenEncoder.Profile != null || !IsLocalScreenSharing && FfmpegScreenEncoder.CaptureAvailable;
        var encoders = native ? (await ScreenEncoder.CapabilitiesAsync(ct).ConfigureAwait(false)).Where(e => e.Available).ToArray() : [];
        lock (_screenSettingsLock)
        {
            var options = _nativeScreenOptions ?? new ScreenEncoderOptions();
            var current = native ? new ScreenTransmissionSettings(options.Codec, options.Width, options.Height, options.Fps,
                options.Bitrate, options.Encoder, options.Preset, options.RateControl, options.Quality, options.KeyFrameIntervalMs, options.BufferMs) : _browserScreenSettings;
            if (native && ScreenEncoder.Profile is { } profile) current = current with { Width = profile.Width, Height = profile.Height, Fps = profile.Fps, Encoder = profile.Encoder };
            if (!native && IsLocalScreenSharing)
                current = current with { Width = LocalScreenWidth, Height = LocalScreenHeight, Fps = LocalScreenFps };
            var codecs = native ? encoders.Select(e => e.Codec).Distinct().ToArray() :
                DateTime.UtcNow - _browserScreenReportedAt < TimeSpan.FromSeconds(30) ? _browserScreenCodecs : [];
            bool accessible = !IsLocalScreenSharing || ActiveCalls.TryGetValue(peer.Id, out var session) && CanReceiveLocalScreen(session);
            return new(1, _screenSettingsRevision, native ? "native" : "browser", IsLocalScreenSharing && accessible,
                HasScreenPermission(peer, "screen-share-request"), accessible && HasScreenPermission(peer, "screen-settings"), codecs,
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
        try { return await StartNativeScreenCoreAsync(options, remoteOwner, ct).ConfigureAwait(false); }
        finally { _screenChangeGate.Release(); }
    }

    private async Task<NativeScreenProfile> StartNativeScreenCoreAsync(ScreenEncoderOptions options, PeerInfo? remoteOwner, CancellationToken ct)
    {
        await StopSystemAudioAsync().ConfigureAwait(false);
        options.Validate();
        if (ActiveCalls.IsEmpty || options.TargetPeerId.HasValue && !ActiveCalls.ContainsKey(options.TargetPeerId.Value))
            throw new InvalidOperationException("The target call has ended.");
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
