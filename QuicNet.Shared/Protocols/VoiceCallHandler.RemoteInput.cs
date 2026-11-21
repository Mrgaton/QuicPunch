using System.Text.Json;
using QuicNet.Serialization;
using QuicNet.Video;

namespace QuicNet.Protocols;

internal sealed record RemoteInputPacket(string action, Guid id, Guid lease = default, RemoteDesktopInput[]? inputs = null, string? reason = null,
    RemoteInputMonitor[]? monitors = null, bool clipboard = false, string? text = null, bool? keyboardAvailable = null);
internal sealed record RemoteInputView(Guid id, Guid peerId, string peerName, bool incoming, bool active, bool administrator,
    bool fullKeyboard, RemoteInputMonitor[]? monitors, bool clipboardAllowed, bool clipboardEnabled, bool keyboardAvailable);
internal sealed record RemoteInputStatus(long revision, RemoteInputView? session, string? message = null, bool nativeClipboard = false);
internal sealed record RemoteInputClipboard(Guid id, string text);

internal sealed partial class VoiceCallHandler
{
    internal const byte RemoteInputVersion = 5;
    private const byte FirstRemoteInputVersion = 3;
    private const byte RemoteInputExtensionsVersion = 4;
    internal const int RemoteClipboardLimit = 8192;
    private const int MaxRemoteInputPacket = 64 * 1024;
    private static readonly TimeSpan RemoteInputRequestDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RemoteInputLeaseDuration = TimeSpan.FromSeconds(25);
    private readonly object _remoteInputGate = new();
    private readonly SemaphoreSlim _remoteInputCaptureRequest = new(1, 1);
    private RemoteInputSession? _remoteInput;
    private long _remoteInputRevision;
    private long _nextIncomingRequest;
    internal event Action<RemoteInputStatus>? RemoteInputChanged;
    internal event Action<RemoteInputClipboard>? RemoteClipboardReceived;
    private (IPlatformRemoteInput Device, long Generation)? _platformRemoteInput;
    internal IRemoteSessionClipboard? RemoteSessionClipboard { get; set; }

    private sealed class RemoteInputSession(VoiceCallSession call, Guid id, bool incoming, bool administrator, long generation = 0,
        RemoteInputArea? area = null, IRemoteInputDevice? host = null)
    {
        internal readonly VoiceCallSession Call = call;
        internal readonly Guid Id = id;
        internal readonly bool Incoming = incoming;
        internal readonly bool Administrator = administrator;
        internal readonly long Generation = generation;
        internal readonly RemoteInputArea? Area = area;
        internal readonly IRemoteInputDevice? Host = host;
        internal bool SupportsExtensions => Call.RemoteScreenControlVersion >= RemoteInputExtensionsVersion;
        internal bool KeyboardAvailable = host?.KeyboardAvailable ?? true;
        internal RemoteInputMonitor[]? Monitors;
        internal bool ClipboardAllowed;
        internal bool ClipboardEnabled;
        internal long LastClipboardReceived;
        internal string? LastLocalClipboard;
        internal bool ClipboardBaseline;
        internal long NextClipboardPoll;
        internal bool ClipboardWriteInProgress;
        internal Guid Lease;
        internal long Deadline = Environment.TickCount64 + (long)RemoteInputRequestDuration.TotalMilliseconds;
        internal long RateWindow;
        internal int EventsInWindow;
        internal RemoteInputView View() => new(Id, Call.Peer.Id, Call.Peer.Name ?? "Contacto", Incoming, Lease != Guid.Empty, Administrator,
            SupportsExtensions, Monitors, ClipboardAllowed, ClipboardEnabled, KeyboardAvailable);
    }

    internal RemoteInputStatus GetRemoteInputStatus()
    {
        lock (_remoteInputGate) return new(_remoteInputRevision, _remoteInput?.View(), nativeClipboard: RemoteSessionClipboard != null);
    }

    private void PublishRemoteInput(string? message = null)
    {
        _remoteInputRevision++;
        RemoteInputChanged?.Invoke(new(_remoteInputRevision, _remoteInput?.View(), message, RemoteSessionClipboard != null));
    }

    private bool IsCurrentInputCall(VoiceCallSession call) => ActiveCalls.TryGetValue(call.Peer.Id, out var current) &&
        ReferenceEquals(current, call) && _qcc.IsTrustedPeer(call.Peer);

    private bool IsAdministrator(VoiceCallSession call) => call.Peer.TryGetCertificateHash(out var hash) &&
        _qcc.PeerStore?.IsAdministrator(hash) == true;

    internal void SetPlatformRemoteInput(IPlatformRemoteInput? device, long generation)
    {
        lock (_remoteInputGate) _platformRemoteInput = device == null ? null : (device, generation);
    }

    private (long Generation, RemoteInputArea Area, string Source, Func<IRemoteInputDevice?> Create)? GetRemoteInputCapture(VoiceCallSession call)
    {
        if (!IsLocalScreenSharing || LocalVideoKind != "screen" || !CanReceiveLocalScreen(call)) return null;
        if (_platformRemoteInput is { } platform && platform.Device.RemoteInputArea is { } platformArea)
            return (platform.Generation, platformArea, "platform", platform.Device.CreateRemoteInputDevice);
        if (ScreenEncoder.Profile is not { } profile || NativeScreenSettings is not { } options) return null;
        if (WindowsRemoteInput.CaptureArea(options.Source) is { } area)
            return (profile.Generation, area, options.Source, () => new WindowsRemoteInput());
        if (X11RemoteInput.CaptureArea(options.Source) is { } x11Area)
            return (profile.Generation, x11Area, options.Source, () => new X11RemoteInput());
        return null;
    }

    private bool RemoteInputAvailable(RemoteInputSession session)
    {
        if (!IsCurrentInputCall(session.Call) || Environment.TickCount64 >= session.Deadline) return false;
        if (!session.Incoming) return session.Call.IsRemoteScreenSharing && session.Call.RemoteVideoKind == "screen";
        if (session.Administrator && !IsAdministrator(session.Call)) return false;
        var capture = GetRemoteInputCapture(session.Call);
        return capture.HasValue && capture.Value.Generation == session.Generation && capture.Value.Area == session.Area;
    }

    internal async Task RequestRemoteInputWithCaptureAsync(Guid peerId)
    {
        if (!await _remoteInputCaptureRequest.WaitAsync(0).ConfigureAwait(false))
            throw new ArgumentException("Ya se está preparando una solicitud de control remoto.");
        try { await PrepareRemoteInputAsync(peerId).ConfigureAwait(false); }
        finally { _remoteInputCaptureRequest.Release(); }
    }

    private async Task PrepareRemoteInputAsync(Guid peerId)
    {
        if (!ActiveCalls.TryGetValue(peerId, out var call) || !IsCurrentInputCall(call))
            throw new ArgumentException("Inicia una llamada con el contacto para solicitar el control remoto.");
        if (!(call.RemoteScreenControlVersion >= FirstRemoteInputVersion))
            throw new ArgumentException("El contacto necesita una versión compatible con el control remoto.");
        lock (_remoteInputGate)
        {
            if (_remoteInput != null) throw new ArgumentException("Ya hay una solicitud o sesión de control remoto.");
        }
        if (!call.IsRemoteScreenSharing || call.RemoteVideoKind != "screen")
        {
            if (call.RemoteScreenControlVersion < RemoteInputVersion)
                throw new ArgumentException("Pide al contacto que comparta su pantalla o que actualice QuicNet.");
            var reply = await RequestScreenControlAsync(peerId, "start-control", ct: call.CancellationToken).ConfigureAwait(false);
            if (!reply.Success) throw new ArgumentException(reply.Error ?? "El contacto no pudo iniciar la captura.");
        }
        await RequestRemoteInputAsync(peerId, call).ConfigureAwait(false);
    }

    internal async Task RequestRemoteInputAsync(Guid peerId, VoiceCallSession? expectedCall = null)
    {
        RemoteInputSession session;
        lock (_remoteInputGate)
        {
            if (_remoteInput != null) throw new ArgumentException("Ya hay una solicitud o sesión de control remoto.");
            if (!ActiveCalls.TryGetValue(peerId, out var call) || expectedCall != null && call != expectedCall || !IsCurrentInputCall(call) ||
                !call.IsRemoteScreenSharing || call.RemoteVideoKind != "screen")
                throw new ArgumentException("El contacto debe estar compartiendo su pantalla en esta llamada.");
            if (!(call.RemoteScreenControlVersion >= FirstRemoteInputVersion))
                throw new ArgumentException("El contacto necesita una versión compatible con el control remoto.");
            session = _remoteInput = new(call, Guid.NewGuid(), false, false);
            PublishRemoteInput();
        }
        _ = WatchRemoteInputAsync(session);
        await SendRemoteInputAsync(session.Call, new("request", session.Id)).ConfigureAwait(false);
    }

    internal async Task RespondRemoteInputAsync(Guid id, bool allow, bool confirmed, bool clipboard = false)
    {
        RemoteInputSession session;
        RemoteInputPacket reply;
        lock (_remoteInputGate)
        {
            if (_remoteInput is not { Incoming: true } current || current.Id != id || current.Lease != Guid.Empty)
                throw new ArgumentException("Esta solicitud ya no está pendiente.");
            session = current;
            if (!allow)
            {
                ClearRemoteInput(session);
                reply = new("end", id, reason: "declined");
            }
            else
            {
                if (!confirmed)
                    throw new ArgumentException("Confirma que quieres compartir el ratón y el teclado.");
                if (!RemoteInputAvailable(session)) throw new ArgumentException("La solicitud o la captura ya no está disponible.");
                session.ClipboardAllowed = session.SupportsExtensions && clipboard;
                reply = GrantRemoteInput(session);
                PublishRemoteInput();
            }
        }
        await SendRemoteInputAsync(session.Call, reply).ConfigureAwait(false);
    }

    internal void StopRemoteInput(Guid id) => EndRemoteInput(id: id);

    private void EndRemoteInput(VoiceCallSession? call = null, bool? incoming = null, Guid? id = null)
    {
        RemoteInputSession session;
        lock (_remoteInputGate)
        {
            if (_remoteInput is not { } current || call != null && current.Call != call ||
                incoming.HasValue && current.Incoming != incoming || id.HasValue && current.Id != id) return;
            session = current;
            ClearRemoteInput(session);
        }
        _ = SendRemoteInputAsync(session.Call, new("end", session.Id));
    }

    private void ClearRemoteInput(RemoteInputSession session, string? message = null)
    {
        _remoteInput = null;
        session.Host?.Dispose();
        PublishRemoteInput(message);
    }

    internal async Task SetRemoteClipboardAsync(Guid id, bool enabled)
    {
        VoiceCallSession call;
        RemoteInputPacket packet;
        lock (_remoteInputGate)
        {
            if (_remoteInput is not { } session || session.Id != id || session.Lease == Guid.Empty ||
                !session.SupportsExtensions || !RemoteInputAvailable(session))
                throw new ArgumentException("El portapapeles remoto no está disponible.");
            if (session.Incoming)
            {
                session.ClipboardAllowed = enabled;
                if (!enabled) session.ClipboardEnabled = false;
            }
            else session.ClipboardEnabled = enabled && session.ClipboardAllowed;
            if (!session.ClipboardAllowed || !session.ClipboardEnabled) session.ClipboardBaseline = false;
            call = session.Call;
            packet = new("clipboard-setting", id, session.Lease, clipboard: session.Incoming ? session.ClipboardAllowed : session.ClipboardEnabled);
            PublishRemoteInput();
        }
        await SendRemoteInputAsync(call, packet).ConfigureAwait(false);
    }

    internal async Task SendRemoteClipboardAsync(Guid id, string text)
    {
        if (!ValidClipboard(text)) throw new ArgumentException("El portapapeles admite hasta 8192 caracteres de texto.");
        VoiceCallSession call;
        RemoteInputPacket packet;
        lock (_remoteInputGate)
        {
            if (_remoteInput is not { ClipboardAllowed: true, ClipboardEnabled: true } session || session.Id != id ||
                session.Lease == Guid.Empty || !RemoteInputAvailable(session))
                throw new ArgumentException("Ambos participantes deben activar el portapapeles.");
            call = session.Call;
            packet = new("clipboard", id, session.Lease, text: text);
        }
        await SendRemoteInputAsync(call, packet).ConfigureAwait(false);
    }

    internal async Task SendLocalClipboardAsync(Guid id)
    {
        var clipboard = RemoteSessionClipboard ?? throw new ArgumentException("El portapapeles del sistema no está disponible.");
        lock (_remoteInputGate)
        {
            if (_remoteInput is not { } session || session.Id != id || !ClipboardSessionCurrent(session))
                throw new ArgumentException("Ambos participantes deben activar el portapapeles.");
        }
        string? text;
        try { text = await clipboard.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { throw new ArgumentException("El portapapeles no respondió."); }
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("No hay texto que enviar en el portapapeles.");
        await SendRemoteClipboardAsync(id, text).ConfigureAwait(false);
    }

    private static bool ValidClipboard(string? text) => text is { Length: > 0 and <= RemoteClipboardLimit } && !text.Contains('\0');

    internal async Task ForwardRemoteInputAsync(Guid id, RemoteDesktopInput[]? inputs)
    {
        if (inputs != null && !ValidRemoteInputs(inputs)) throw new ArgumentException("Entrada remota inválida.");
        RemoteInputSession session;
        RemoteInputPacket packet;
        lock (_remoteInputGate)
        {
            if (_remoteInput is not { Incoming: false } current || current.Id != id ||
                current.Lease == Guid.Empty || !RemoteInputAvailable(current))
                throw new ArgumentException("La sesión de control remoto ya no está activa.");
            if (!current.SupportsExtensions && inputs?.Any(input => input.kind is "keydown" or "keyup") == true)
                throw new ArgumentException("El contacto necesita actualizar QuicNet para usar el teclado completo.");
            session = current;
            packet = new(inputs == null ? "heartbeat" : "input", id, session.Lease, inputs);
        }
        await SendRemoteInputAsync(session.Call, packet).ConfigureAwait(false);
    }

    private static bool ValidRemoteInputs(RemoteDesktopInput[]? inputs) =>
        inputs is { Length: > 0 and <= 16 } && inputs.All(WindowsRemoteInput.IsValid);

    private async Task ReceiveRemoteInputAsync(VoiceCallSession call, byte[] payload)
    {
        if (payload.Length > MaxRemoteInputPacket || !(call.RemoteScreenControlVersion >= FirstRemoteInputVersion)) return;
        RemoteInputPacket? packet;
        try { packet = JsonSerializer.Deserialize<RemoteInputPacket>(payload, SharedJson.Options); }
        catch (JsonException) { return; }
        if (packet == null || packet.id == Guid.Empty) return;

        RemoteInputPacket? reply = null;
        RemoteInputSession? watch = null;
        lock (_remoteInputGate)
        {
            if (!IsCurrentInputCall(call)) return;
            if (packet.action == "request")
            {
                if (_remoteInput?.Call == call && _remoteInput.Id == packet.id) return;
                if (_remoteInput != null || Environment.TickCount64 < _nextIncomingRequest)
                    reply = new("end", packet.id, reason: "busy");
                else if (GetRemoteInputCapture(call) is not { } capture || capture.Create() is not { } host)
                    reply = new("end", packet.id, reason: "unsupported");
                else
                {
                    _nextIncomingRequest = Environment.TickCount64 + 5_000;
                    watch = _remoteInput = new(call, packet.id, true, IsAdministrator(call), capture.Generation, capture.Area, host)
                    { Monitors = CaptureMonitors(capture.Source, capture.Area) };
                    if (watch.Administrator)
                    {
                        if (!RemoteInputAvailable(watch))
                        {
                            ClearRemoteInput(watch);
                            watch = null;
                            reply = new("end", packet.id, reason: "unsupported");
                        }
                        else reply = GrantRemoteInput(watch);
                    }
                    if (watch != null) PublishRemoteInput();
                }
            }
            else if (_remoteInput is { } session && session.Call == call && session.Id == packet.id)
            {
                if (packet.action == "end")
                {
                    ClearRemoteInput(session, session.Incoming ? null : packet.reason switch
                    {
                        "unsupported" => "El contacto debe compartir una pantalla compatible con el control remoto.",
                        "declined" => "El contacto ha rechazado la solicitud de control.",
                        "busy" => "El contacto ya tiene una solicitud o sesión de control.",
                        _ => null
                    });
                }
                else if (!RemoteInputAvailable(session))
                {
                    ClearRemoteInput(session);
                    reply = new("end", packet.id);
                }
                else if (packet.action == "allow" && !session.Incoming && session.Lease == Guid.Empty && packet.lease != Guid.Empty)
                {
                    session.Lease = packet.lease;
                    session.KeyboardAvailable = packet.keyboardAvailable != false;
                    if (session.SupportsExtensions)
                    {
                        session.ClipboardAllowed = packet.clipboard;
                        session.Monitors = packet.monitors?.Take(16).Where(ValidMonitor).ToArray();
                    }
                    RenewRemoteInputLease(session);
                    PublishRemoteInput();
                }
                else if (session.Lease != Guid.Empty && session.Lease == packet.lease)
                {
                    if (packet.action == "heartbeat")
                    {
                        RenewRemoteInputLease(session);
                        if (session.Incoming) reply = new("heartbeat", session.Id, session.Lease);
                    }
                    else if (packet.action == "clipboard-setting" && session.SupportsExtensions)
                    {
                        if (session.Incoming) session.ClipboardEnabled = session.ClipboardAllowed && packet.clipboard;
                        else
                        {
                            session.ClipboardAllowed = packet.clipboard;
                            if (!packet.clipboard) session.ClipboardEnabled = false;
                        }
                        if (!session.ClipboardAllowed || !session.ClipboardEnabled) session.ClipboardBaseline = false;
                        PublishRemoteInput();
                    }
                    else if (packet.action == "clipboard" && session.ClipboardAllowed && session.ClipboardEnabled && !session.ClipboardWriteInProgress &&
                        ValidClipboard(packet.text) && Environment.TickCount64 - session.LastClipboardReceived >= 300)
                    {
                        session.LastClipboardReceived = Environment.TickCount64;
                        if (RemoteSessionClipboard is { } clipboard)
                        {
                            session.ClipboardWriteInProgress = true;
                            _ = Task.Run(() => WriteLocalClipboardAsync(session, clipboard, packet.text!));
                        }
                        else RemoteClipboardReceived?.Invoke(new(session.Id, packet.text!));
                    }
                    else if (packet.action == "input" && session.Incoming && ValidRemoteInputs(packet.inputs))
                    {
                        long now = Environment.TickCount64;
                        if (now - session.RateWindow >= 1000) { session.RateWindow = now; session.EventsInWindow = 0; }
                        session.EventsInWindow += packet.inputs!.Length;
                        if (session.EventsInWindow > 120)
                        {
                            ClearRemoteInput(session);
                            reply = new("end", packet.id);
                        }
                        else
                        {
                            foreach (var input in packet.inputs) session.Host!.Apply(session.Area!.Value, input);
                        }
                    }
                }
            }
        }
        if (watch != null) _ = WatchRemoteInputAsync(watch);
        if (reply != null) await SendRemoteInputAsync(call, reply).ConfigureAwait(false);
    }

    private static RemoteInputPacket GrantRemoteInput(RemoteInputSession session)
    {
        session.Lease = Guid.NewGuid();
        RenewRemoteInputLease(session);
        return new("allow", session.Id, session.Lease, monitors: session.SupportsExtensions ? session.Monitors : null,
            clipboard: session.ClipboardAllowed, keyboardAvailable: session.KeyboardAvailable);
    }

    private static void RenewRemoteInputLease(RemoteInputSession session) =>
        session.Deadline = Environment.TickCount64 + (long)RemoteInputLeaseDuration.TotalMilliseconds;

    private async Task WatchRemoteInputAsync(RemoteInputSession session)
    {
        while (true)
        {
            await Task.Delay(session.Incoming ? 200 : 1000).ConfigureAwait(false);
            bool ended;
            lock (_remoteInputGate)
            {
                if (_remoteInput != session) return;
                bool emergencyStop = session.Lease != Guid.Empty && session.Host?.StopRequested == true;
                ended = emergencyStop || !RemoteInputAvailable(session);
                if (ended) ClearRemoteInput(session, session.Lease == Guid.Empty ? "La solicitud de control ha caducado." : null);
            }
            if (ended)
            {
                await SendRemoteInputAsync(session.Call, new("end", session.Id)).ConfigureAwait(false);
                return;
            }
            if (RemoteSessionClipboard is { } clipboard)
                await PollLocalClipboardAsync(session, clipboard).ConfigureAwait(false);
        }
    }

    private bool ClipboardSessionCurrent(RemoteInputSession session) => ReferenceEquals(_remoteInput, session) &&
        session.Lease != Guid.Empty && session.ClipboardAllowed && session.ClipboardEnabled && RemoteInputAvailable(session);

    private async Task WriteLocalClipboardAsync(RemoteInputSession session, IRemoteSessionClipboard clipboard, string text)
    {
        bool Permitted() { lock (_remoteInputGate) return ClipboardSessionCurrent(session); }
        bool written = false;
        try { written = await clipboard.WriteAsync(text, Permitted).ConfigureAwait(false); }
        catch (Exception) { }
        lock (_remoteInputGate)
        {
            session.ClipboardWriteInProgress = false;
            if (!ClipboardSessionCurrent(session)) return;
            if (written) { session.LastLocalClipboard = text; session.ClipboardBaseline = true; }
            else RemoteClipboardReceived?.Invoke(new(session.Id, text));
        }
    }

    private async Task PollLocalClipboardAsync(RemoteInputSession session, IRemoteSessionClipboard clipboard)
    {
        lock (_remoteInputGate)
        {
            long now = Environment.TickCount64;
            if (now < session.NextClipboardPoll || session.ClipboardWriteInProgress || !ClipboardSessionCurrent(session)) return;
            session.NextClipboardPoll = now + 1000;
        }
        string? text;
        try { text = await clipboard.ReadAsync().WaitAsync(TimeSpan.FromMilliseconds(700)).ConfigureAwait(false); }
        catch (Exception) { return; }
        lock (_remoteInputGate)
        {
            if (!ClipboardSessionCurrent(session) || session.ClipboardWriteInProgress) return;
            if (!session.ClipboardBaseline)
            {
                session.LastLocalClipboard = text;
                session.ClipboardBaseline = true;
                return;
            }
            if (!ValidClipboard(text) || text == session.LastLocalClipboard) return;
            session.LastLocalClipboard = text;
        }
        try { await SendRemoteClipboardAsync(session.Id, text!).ConfigureAwait(false); }
        catch (ArgumentException) { }
    }

    private static bool ValidMonitor(RemoteInputMonitor? monitor) => monitor is { name.Length: > 0 and <= 128,
        x: >= 0 and < 1, y: >= 0 and < 1, width: > 0 and <= 1, height: > 0 and <= 1 } &&
        monitor.x + monitor.width <= 1.000001 && monitor.y + monitor.height <= 1.000001;

    private static RemoteInputMonitor[]? CaptureMonitors(string source, RemoteInputArea area)
    {
        if (source != "desktop") return null;
        return ScreenCaptureInventory.Displays().Take(16).Select((display, index) => new RemoteInputMonitor(
            $"Pantalla {index + 1}", (double)(display.X - area.X) / area.Width, (double)(display.Y - area.Y) / area.Height,
            (double)display.Width / area.Width, (double)display.Height / area.Height)).Where(ValidMonitor).ToArray();
    }

    private static async Task SendRemoteInputAsync(VoiceCallSession call, RemoteInputPacket packet)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await call.SendFrameAsync(FrameTypeRemoteInput, JsonSerializer.SerializeToUtf8Bytes(packet, SharedJson.Options),
                timeout.Token).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
    }
}
