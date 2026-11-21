using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuicNet.Serialization;

namespace QuicNet.Video;

internal sealed record NativePlaybackOptions(int bufferMs = 0, int maxQueueFrames = 4, string decoder = "auto")
{
    internal void Validate() { if (bufferMs is < 0 or > 250 || maxQueueFrames is < 2 or > 32 || decoder is not ("auto" or "hardware" or "software")) throw new ArgumentException("Opciones de recepción inválidas."); }
}
internal sealed record NativeVideoLayout(string peerId, double x, double y, double width, double height, bool visible, double scale = 1, double clipX = 0, double clipY = 0, double clipWidth = -1, double clipHeight = -1, double zoom = 1)
{
    internal NativeVideoViewport Viewport()
    {
        double left = Math.Clamp(clipX, 0, width);
        double top = Math.Clamp(clipY, 0, height);
        double right = clipWidth < 0 ? width : Math.Clamp(left + clipWidth, left, width);
        double bottom = clipHeight < 0 ? height : Math.Clamp(top + clipHeight, top, height);
        int pixelX = (int)Math.Round((x + left) * scale);
        int pixelY = (int)Math.Round((y + top) * scale);
        return new(pixelX, pixelY, (int)Math.Round((x + right) * scale) - pixelX,
            (int)Math.Round((y + bottom) * scale) - pixelY,
            left / width, top / height, right / width, bottom / height);
    }
}
internal readonly record struct NativeVideoViewport(int X, int Y, int Width, int Height, double Left, double Top, double Right, double Bottom);
internal sealed record NativeDecoderCapability(int codecId, string codec, bool? hardware, bool? software, string status, string reason, int testWidth, int testHeight, string? component = null);
internal sealed record NativeVideoCapabilities(bool available, string engine, uint decoderMask, uint hardwareMask, NativeDecoderCapability[] decoders);
internal sealed record NativeVideoState(string peerId, string mode, int width, int height, long decoded, long presented,
    long dropped, double decodeMs, string? error, byte codecId = 0, double fps = 0, long bitrate = 0,
    ScreenReceiveStatistics? timing = null);
internal sealed record NativeVideoFixture(int codecId, string codec, int width, int height, string? description, string[] packets);

internal interface INativeVideoHost
{
    Task<nint> SurfaceAsync(string peerId, bool probe = false);
    Task LayoutAsync(NativeVideoLayout layout);
    Task ReleaseAsync(string peerId);
    string? HardwareComponent(int codecId, int width, int height);
    bool SupportsLowLatency(string component, int codecId) => false;
}

internal static unsafe class NativeMedia
{
    private const string Library = "nettt_media";
    static NativeMedia()
    {
        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
            NativeLibrary.SetDllImportResolver(typeof(NativeMedia).Assembly, (name, _, _) =>
            {
                if (name != Library) return 0;
                var msquic = QuicPunch.Helpers.MsQuicNativeLoader.LoadedPath;
                if (msquic == null || Path.GetDirectoryName(msquic) is not { Length: > 0 } directory) return 0;
                var media = Path.Combine(directory, "libnettt_media.so");
                return File.Exists(media) ? NativeLibrary.Load(media) : 0;
            });
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint qpv_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint qpv_error();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint qpv_open(int codec, int hardware, int width, int height, byte[]? extra, int length, nint surface, [MarshalAs(UnmanagedType.LPUTF8Str)] string? component);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_close(nint decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int qpv_decode(nint decoder, byte* data, int size, long pts, int key, int render);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int qpv_stats(nint decoder, out Statistics stats);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int qpv_present_stats(nint decoder, out long presentUs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int qpv_present_pending(nint decoder, int render);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint qpv_surface_create(nint parent);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_surface_layout(nint surface, int x, int y, int width, int height, int visible);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_surface_destroy(nint surface);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_x11_surface_layout(nint surface,
        int x, int y, int width, int height, int visible, int clipX, int clipY, int clipWidth, int clipHeight);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_desktop_surface_crop(nint surface,
        int x, int y, int width, int height, int visible, double left, double top, double right, double bottom);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_android_init(nint env);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint qpv_encoder_open(int codec, int width, int height, int fps, int bitrate, int gop, nint surface, [MarshalAs(UnmanagedType.LPUTF8Str)] string? component, int vbr, int bufferMs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int qpv_encoder_read(nint encoder, byte[] output, int capacity, out long pts, out int key);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_encoder_close(nint encoder);
    internal static string Error => Marshal.PtrToStringUTF8(qpv_error()) ?? "Error del motor FFmpeg";
    private static bool _legacyPresentation;
    internal static bool PresentationState(nint decoder, out long presentUs)
    {
        if (!Volatile.Read(ref _legacyPresentation))
        {
            try { return qpv_present_stats(decoder, out presentUs) != 0; }
            catch (EntryPointNotFoundException) { Volatile.Write(ref _legacyPresentation, true); }
        }
        presentUs = -1;
        return false;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Statistics
    {
        public int Width, Height, Hardware, Reserved;
        public long Decoded, Presented, Dropped, HardwareFrames, DecodeUs;
    }
    internal static int Decode(nint decoder, ReadOnlySpan<byte> bytes, long pts, bool key, bool render)
    { fixed (byte* data = bytes) return qpv_decode(decoder, data, bytes.Length, pts, key ? 1 : 0, render ? 1 : 0); }
}

// All C/FFmpeg operations for one stream run on one worker, including destruction.
// In particular Android EGL contexts never migrate with async continuations.
internal sealed class NativeVideoRuntime : IDisposable
{
    internal static NativeVideoRuntime Instance { get; } = new();
    private readonly ConcurrentDictionary<string, Receiver> _receivers = new();
    private readonly SemaphoreSlim _probeGate = new(1);
    private readonly object _capabilityGate = new();
    private Task<NativeVideoCapabilities>? _capabilityTask;
    private INativeVideoHost? _host;
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly object _streamGate = new();
    private readonly Dictionary<string, StartupFrames> _startupFrames = new();
    internal NativePlaybackOptions Playback { get; private set; } = new();
    internal void Configure(NativePlaybackOptions value) { value.Validate(); Playback = value; foreach(var receiver in _receivers.Values)receiver.Wake(); }
    internal void Detach() { Dispose(); _host = null; }
    internal async Task DetachAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { Detach(); }
        finally { _lifecycle.Release(); }
    }
    internal void StopDecoder(string peer) { if(_receivers.TryRemove(peer,out var receiver))receiver.Dispose(); }
    internal NativeVideoCapabilities Capabilities { get; private set; } = new(false, "FFmpeg nativo", 0, 0, []);
    internal event Action<NativeVideoCapabilities>? CapabilitiesChanged;
    internal event Action<NativeVideoState>? StateChanged;
    internal event Action<Guid>? KeyFrameNeeded;
    internal INativeVideoHost? Host => _host;
    internal void Attach(INativeVideoHost host, bool warmCapabilities = true)
    {
        lock(_capabilityGate) { _host=host; _capabilityTask=null; }
        if (warmCapabilities) _ = EnsureCapabilitiesAsync();
    }

    // Warm once per native host. Joining calls and concurrent readers reuse the same task/result.
    internal Task<NativeVideoCapabilities> EnsureCapabilitiesAsync() => GetCapabilitiesAsync(false);
    internal Task<NativeVideoCapabilities> DetectAsync() => GetCapabilitiesAsync(true);
    private Task<NativeVideoCapabilities> GetCapabilitiesAsync(bool refresh)
    {
        lock(_capabilityGate)
        {
            if(_capabilityTask!=null && (!refresh || !_capabilityTask.IsCompleted))return _capabilityTask;
            return _capabilityTask=DetectCoreAsync();
        }
    }

    private async Task<NativeVideoCapabilities> DetectCoreAsync()
    {
        await _probeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var host=_host;
            if (host == null) return Capabilities;
            string version = Marshal.PtrToStringUTF8(NativeMedia.qpv_version()) ?? "";
            using var source = typeof(NativeVideoRuntime).Assembly.GetManifestResourceStream("QuicNet.NativeVideo.Probes.json")
                ?? throw new InvalidDataException("Faltan las muestras de decodificación nativa.");
            var fixtures = JsonSerializer.Deserialize(source, NativeVideoJsonContext.Default.NativeVideoFixtureArray) ?? [];
            var results = new List<NativeDecoderCapability>(); uint mask = 0, hardwareMask = 0;
            foreach (var fixture in fixtures.GroupBy(f => f.codecId).Select(g => g.First()))
            {
                string probeId = "probe-" + fixture.codecId;
                nint surface = await host.SurfaceAsync(probeId, true).ConfigureAwait(false);
                string? component = host.HardwareComponent(fixture.codecId, fixture.width, fixture.height);
                try
                {
                    var hw = await Task.Run(() => Probe(fixture, true, surface, component)).ConfigureAwait(false);
                    var sw = await Task.Run(() => Probe(fixture, false, surface, null)).ConfigureAwait(false);
                    if (hw.Success || sw.Success) mask |= 1u << fixture.codecId;
                    if (hw.Success) hardwareMask |= 1u << fixture.codecId;
                    results.Add(new(fixture.codecId, fixture.codec, hw.Success, sw.Success, hw.Success ? "hardware" : sw.Success ? "software" : "unsupported",
                        hw.Success ? "Fotogramas decodificados por hardware en la muestra indicada; el perfil final se comprueba al iniciar." : sw.Success ? "Decodificación FFmpeg por software comprobada; no garantiza el FPS del perfil final." : hw.Error + " · " + sw.Error,
                        fixture.width, fixture.height, component));
                }
                finally { await host.ReleaseAsync(probeId).ConfigureAwait(false); }
            }
            if(!ReferenceEquals(host,_host))return Capabilities;
            Capabilities = new(true, "FFmpeg " + version, mask, hardwareMask, results.ToArray());
            CapabilitiesChanged?.Invoke(Capabilities);
        }
        catch (Exception ex)
        {
            Capabilities = new(false, "FFmpeg nativo", 0, 0, [new(0,"runtime",null,null,"unknown",ex.Message,0,0)]);
            CapabilitiesChanged?.Invoke(Capabilities);
        }
        finally { _probeGate.Release(); }
        return Capabilities;
    }

    internal static (bool Success, string Error) Probe(NativeVideoFixture fixture, bool hardware, nint surface, string? component)
    {
        byte[]? extra = fixture.description == null ? null : Convert.FromBase64String(fixture.description);
        nint handle = NativeMedia.qpv_open(fixture.codecId, hardware ? 1 : 0, fixture.width, fixture.height, extra, extra?.Length ?? 0, surface, component);
        if (handle == 0) return (false, NativeMedia.Error);
        try
        {
            foreach (string encoded in fixture.packets)
            {
                byte[] packet = Convert.FromBase64String(encoded); var parsed = Parse(packet);
                if (NativeMedia.Decode(handle, packet.AsSpan(parsed.Offset), parsed.Timestamp, parsed.Key, false) < 0) return (false, NativeMedia.Error);
            }
            for (int i = 0; i < 20; i++)
            {
                NativeMedia.qpv_stats(handle, out var stats);
                if (stats.Decoded > 0) return (!hardware || stats.HardwareFrames > 0, hardware && stats.HardwareFrames == 0 ? "No produjo frames de hardware." : "");
                Thread.Sleep(5);
                if (NativeMedia.Decode(handle, [], 0, false, false) < 0) return (false, NativeMedia.Error);
            }
            return (false, "El decoder no produjo fotogramas dentro del plazo de prueba.");
        }
        finally { NativeMedia.qpv_close(handle); }
    }

    internal async Task LayoutAsync(NativeVideoLayout layout)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try {
        if (_host == null) return;
        if (layout.peerId != "local" && !Guid.TryParse(layout.peerId, out _)) throw new ArgumentException("Pista inválida.");
        if (!double.IsFinite(layout.x + layout.y + layout.width + layout.height + layout.scale +
                layout.clipX + layout.clipY + layout.clipWidth + layout.clipHeight + layout.zoom) ||
            layout.width < 0 || layout.height < 0 || layout.width > 1e12 || layout.height > 1e12 ||
            Math.Abs(layout.x) > 1e12 || Math.Abs(layout.y) > 1e12 || layout.scale is < .25 or > 8 || layout.zoom < 1)
            throw new ArgumentException("Geometría inválida.");
        if (layout.visible)
        {
            double clipWidth = layout.clipWidth < 0 ? layout.width : layout.clipWidth;
            double clipHeight = layout.clipHeight < 0 ? layout.height : layout.clipHeight;
            if (layout.width < 1 || layout.height < 1 || clipWidth * layout.scale > 16384 || clipHeight * layout.scale > 16384 ||
                Math.Abs(layout.x + Math.Clamp(layout.clipX, 0, layout.width)) * layout.scale > 32768 ||
                Math.Abs(layout.y + Math.Clamp(layout.clipY, 0, layout.height)) * layout.scale > 32768)
                throw new ArgumentException("Área visible de vídeo inválida.");
        }
        if (!_receivers.ContainsKey(layout.peerId) && layout.visible)
        {
            nint surface = await _host.SurfaceAsync(layout.peerId).ConfigureAwait(false);
            await _host.LayoutAsync(layout).ConfigureAwait(false);
            var receiver = new Receiver(this, layout.peerId, surface) { Visible = true };
            lock (_streamGate)
            {
                if (_receivers.TryAdd(layout.peerId, receiver))
                {
                    if (_startupFrames.Remove(layout.peerId, out var pending) &&
                        Stopwatch.GetElapsedTime(pending.Time) < TimeSpan.FromSeconds(2))
                        receiver.Prime(pending);
                }
                else receiver.Dispose();
            }
        }
        else
        {
            await _host.LayoutAsync(layout).ConfigureAwait(false);
            if (_receivers.TryGetValue(layout.peerId, out var stream)) stream.SetVisible(layout.visible);
        }
        } finally { _lifecycle.Release(); }
    }
    internal void Enqueue(Guid peer, byte codec, byte[] packet)
    {
        string id = peer == Guid.Empty ? "local" : peer.ToString();
        lock (_streamGate)
        {
            if (_receivers.TryGetValue(id, out var receiver)) receiver.Enqueue(codec, packet);
            else if (_host != null && packet.Length is > 0 and <= 2 * 1024 * 1024)
            {
                if ((packet[0] & 1) == 0)
                {
                    if (_startupFrames.TryGetValue(id, out var pending)) pending.Append(packet);
                    return;
                }
                if (_startupFrames.Count >= 16)
                    foreach (var expired in _startupFrames.Where(p => Stopwatch.GetElapsedTime(p.Value.Time) >= TimeSpan.FromSeconds(2)).Select(p => p.Key).ToArray())
                        _startupFrames.Remove(expired);
                if (_startupFrames.Count < 16 || _startupFrames.ContainsKey(id))
                    _startupFrames[id] = new(codec, packet);
            }
        }
    }
    internal async Task StopAsync(string peer)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try {
            Receiver? receiver;
            lock (_streamGate)
            {
                _startupFrames.Remove(peer);
                _receivers.TryRemove(peer, out receiver);
            }
            if (receiver != null) await Task.Run(receiver.Dispose).ConfigureAwait(false);
            if (_host != null) await _host.ReleaseAsync(peer).ConfigureAwait(false);
        } finally { _lifecycle.Release(); }
    }
    internal NativeVideoState[] States => _receivers.Values.Select(r => r.State).ToArray();
    // Requests only read the warmed codec inventory. Exact profiles are validated by playback,
    // never by allocating a decoder/surface on another participant's request.
    internal Task<(bool Supported,string Mode,string Reason)> CheckProfileAsync(string codec,int width,int height,string? description)
    {
        int id=codec?.StartsWith("avc",StringComparison.Ordinal)==true?1:codec?.StartsWith("hvc",StringComparison.Ordinal)==true||codec?.StartsWith("hev",StringComparison.Ordinal)==true?5:codec?.StartsWith("vp09",StringComparison.Ordinal)==true?3:codec?.StartsWith("av01",StringComparison.Ordinal)==true?4:0;
        (bool,string,string) result;
        if(id==0||width<2||height<2||width>7680||height>4320||description?.Length>87384)
            result=(false,"invalid","Perfil fuera de los límites del motor.");
        else
        {
            var capability=Capabilities.decoders.FirstOrDefault(c=>c.codecId==id);
            bool supported=capability?.hardware==true||capability?.software==true;
            result=(supported,capability?.hardware==true?"native-hardware":capability?.software==true?"native-software":capability==null?"pending":"unsupported",
                supported?"Códec disponible según la prueba local en caché. El perfil y su rendimiento se confirman al recibir los primeros fotogramas.":capability?.reason??"La detección local todavía no ha terminado.");
        }
        return Task.FromResult(result);
    }
    public void Dispose()
    {
        Receiver[] receivers;
        lock (_streamGate)
        {
            receivers = _receivers.Values.ToArray();
            _receivers.Clear();
            _startupFrames.Clear();
        }
        foreach (var receiver in receivers) receiver.Dispose();
    }

    internal sealed record Packet(int Offset, bool Key, string Codec, byte[]? Extra, int Width, int Height, long Generation, long Timestamp);
    internal static Packet Parse(byte[] bytes)
    {
        if(bytes.Length<4||bytes.Length>2*1024*1024||(bytes[0]&2)==0)throw new InvalidDataException("Falta configuración multimedia.");
        int length=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(1));
        if(length<2||3+length>=bytes.Length)throw new InvalidDataException("Paquete multimedia truncado.");
        using var json=JsonDocument.Parse(bytes.AsMemory(3,length));var m=json.RootElement;
        int w=m.GetProperty("w").GetInt32(),h=m.GetProperty("h").GetInt32();
        if(w<2||w>7680||h<2||h>4320)throw new InvalidDataException("Dimensiones inválidas.");
        string c=m.GetProperty("c").GetString() ?? "";
        string? d=m.TryGetProperty("d",out var desc)&&desc.ValueKind==JsonValueKind.String?desc.GetString():null;
        byte[]? extra=string.IsNullOrEmpty(d)?null:Convert.FromBase64String(d);
        if(extra?.Length>65536)throw new InvalidDataException("Configuración demasiado grande.");
        return new(3+length,(bytes[0]&1)!=0,c,extra,w,h,m.GetProperty("g").GetInt64(),m.GetProperty("t").GetInt64());
    }

    private sealed class StartupFrames(byte codec, byte[] keyFrame)
    {
        internal byte Codec { get; } = codec;
        internal long Time { get; } = Stopwatch.GetTimestamp();
        internal List<byte[]> Packets { get; } = [keyFrame];
        internal bool HasGap { get; private set; }
        private int _bytes = keyFrame.Length;

        internal void Append(byte[] packet)
        {
            if (HasGap) return;
            if (Packets.Count >= 4 || _bytes + packet.Length > 2 * 1024 * 1024)
            {
                HasGap = true;
                return;
            }
            Packets.Add(packet);
            _bytes += packet.Length;
        }
    }

    private sealed class Receiver : IDisposable
    {
        private readonly NativeVideoRuntime _owner;
        private readonly string _peer;
        private readonly nint _surface;
        private readonly object _gate=new();
        private readonly Queue<(byte Codec, byte[] Data, long Time)> _packets=new();
        private readonly AutoResetEvent _ready=new(false);
        private readonly Thread _worker;
        private bool _stopped, _needsKey=true, _resetDecoder;
        private int _bytes;
        private long _dropped;
        private long _overflow, _expired, _awaitingKey, _lastKeyRequest;
        private readonly RecentVideoTimings _queueTiming = new(), _decodeTiming = new(), _presentationTiming = new();
        private long _sampleTime, _sampleBytes, _lastIngress;
        private int _sampleFrames;
        private double _fps;
        private long _bitrate;
        internal volatile bool Visible;
        internal NativeVideoState State {get;private set;}
        internal Receiver(NativeVideoRuntime owner,string peer,nint surface)
        {
            _owner=owner;_peer=peer;_surface=surface;State=new(peer,"pending",0,0,0,0,0,0,null);
            _worker=new Thread(Run){IsBackground=true,Name="NetTT decode "+peer};_worker.Start();
        }
        internal void Wake() { lock(_gate) { if(!_stopped)_ready.Set(); } }
        internal void SetVisible(bool visible)
        {
            lock (_gate)
            {
                if (_stopped || Visible == visible) return;
                Visible = visible;
                if (!visible)
                {
                    _packets.Clear();
                    _bytes = 0;
                    _needsKey = _resetDecoder = true;
                }
                _ready.Set();
            }
            if (visible) RequestKeyFrame();
        }
        internal void Prime(StartupFrames pending)
        {
            lock (_gate)
            {
                int count = Math.Min(pending.Packets.Count, _owner.Playback.maxQueueFrames - 1);
                long now = Stopwatch.GetTimestamp();
                _lastIngress = now;
                for (int i = 0; i < count; i++)
                {
                    var packet = pending.Packets[i];
                    _packets.Enqueue((pending.Codec, packet, now));
                    _bytes += packet.Length;
                }
                _needsKey = pending.HasGap || count < pending.Packets.Count;
                _ready.Set();
            }
        }
        internal void Enqueue(byte codec,byte[] bytes)
        {
            lock(_gate)
            {
                if(_stopped || !Visible)return;
                long now = Stopwatch.GetTimestamp();
                if (_sampleTime == 0) _sampleTime = now;
                _sampleFrames++;
                _sampleBytes += bytes.Length;
                double elapsed = Stopwatch.GetElapsedTime(_sampleTime, now).TotalSeconds;
                if (elapsed >= 1)
                {
                    _fps = _sampleFrames / elapsed;
                    _bitrate = (long)(_sampleBytes * 8 / elapsed);
                    _sampleTime = now;
                    _sampleFrames = 0;
                    _sampleBytes = 0;
                }
                bool key=bytes.Length>0&&(bytes[0]&1)!=0;
                if(bytes.Length>2*1024*1024){Interlocked.Increment(ref _dropped);return;}
                if(_packets.Count>=_owner.Playback.maxQueueFrames||_bytes+bytes.Length>16*1024*1024)
                {
                    Interlocked.Add(ref _dropped, _packets.Count);
                    _overflow += _packets.Count;
                    _packets.Clear();_bytes=0;_needsKey=true;
                }
                if(_needsKey&&!key){Interlocked.Increment(ref _dropped);Interlocked.Increment(ref _awaitingKey);RequestKeyFrame();return;}
                if(key)_needsKey=false;
                _packets.Enqueue((codec,bytes,now));_bytes+=bytes.Length;_ready.Set();
            }
        }

        private void RequestKeyFrame()
        {
            long last = Volatile.Read(ref _lastKeyRequest);
            long now = Stopwatch.GetTimestamp();
            if (last != 0 && Stopwatch.GetElapsedTime(last, now).TotalMilliseconds < 400) return;
            Guid peer = Guid.Empty;
            if ((_peer != "local" && !Guid.TryParse(_peer, out peer)) ||
                Interlocked.CompareExchange(ref _lastKeyRequest, now, last) != last) return;
            _owner.KeyFrameNeeded?.Invoke(peer);
        }

        private void Run()
        {
            nint decoder=0;long generation=-1,lastReport=0;string signature="";bool hardware=false,hardwareFailed=false,awaitKey=true;long lastPacket=0;string decoderPreference="";long clockAnchor=0,ptsAnchor=0,clockGeneration=-1;
            bool pendingPresentation = false;
            byte codec = 0;
            long decoded = 0, presented = 0, renderDrops = 0, decodedAtReport = 0, presentedAtReport = 0;
            NativeMedia.Statistics previous = default;
            try
            {
                while(true)
                {
                    var playback=_owner.Playback;
                    if (decoderPreference != playback.decoder)
                    {
                        if(decoder!=0){NativeMedia.qpv_close(decoder);decoder=0;}awaitKey=true;pendingPresentation=false;
                        decoderPreference=playback.decoder;
                    }
                    (byte Codec,byte[] Data,long Time) item=default;
                    double fps;
                    long bitrate;
                    bool reset;
                    lock(_gate){if(_stopped)break;if(_packets.Count>0){item=_packets.Dequeue();_bytes-=item.Data.Length;}
                        reset = _resetDecoder;
                        _resetDecoder = false;
                        bool idle = Stopwatch.GetElapsedTime(_lastIngress).TotalSeconds >= 2;
                        fps = idle ? 0 : _fps; bitrate = idle ? 0 : _bitrate;}
                    if (reset)
                    {
                        if (decoder != 0) NativeMedia.qpv_close(decoder);
                        decoder = 0;
                        awaitKey = true;
                        pendingPresentation = false;
                        clockAnchor = 0;
                    }
                    if(item.Data==null)
                    {
                        bool androidDrain = OperatingSystem.IsAndroid() && decoder != 0 && Stopwatch.GetElapsedTime(lastPacket).TotalMilliseconds < 100;
                        if (decoder != 0 && (androidDrain || pendingPresentation))
                        {
                            int status = androidDrain ? NativeMedia.Decode(decoder, [], 0, false, Visible) : NativeMedia.qpv_present_pending(decoder, Visible ? 1 : 0);
                            if (status < 0) DecoderFailed();
                            else UpdateState(fps, bitrate, decodeCall: false);
                        }
                        else if (decoder != 0 && Visible && (State.fps != fps || decoded != decodedAtReport ||
                            presented != presentedAtReport || State.timing?.presentedFps > 0 || State.timing?.decodedFps > 0))
                            UpdateState(fps, bitrate, decodeCall: false);
                        _ready.WaitOne(pendingPresentation ? 4 : androidDrain ? 8 : decoder != 0 && Visible ? 1000 : Timeout.Infinite);
                        continue;
                    }
                    if (!Visible) continue;
                    lastPacket=Stopwatch.GetTimestamp();
                    try
                    {
                        var p=Parse(item.Data);if(p.Generation<generation)continue;
                        if(playback.bufferMs>0 && _peer!="local")
                        {
                            if(clockGeneration!=p.Generation || clockAnchor==0){clockAnchor=item.Time;ptsAnchor=p.Timestamp;clockGeneration=p.Generation;}
                            double due=(p.Timestamp-(double)ptsAnchor)/1000+playback.bufferMs;
                            double remaining=due-Stopwatch.GetElapsedTime(clockAnchor).TotalMilliseconds;
                            // Treat discontinuities as a new timestamp origin, never sleep on an untrusted clock.
                            if(remaining>playback.bufferMs+250 || remaining < -1000){clockAnchor=item.Time;ptsAnchor=p.Timestamp;due=playback.bufferMs;}
                            while(Visible && (remaining=due-Stopwatch.GetElapsedTime(clockAnchor).TotalMilliseconds)>0)
                            {
                                lock(_gate){if(_stopped)return;}
                                _ready.WaitOne(Math.Clamp((int)Math.Ceiling(remaining),1,250));
                            }
                            if(remaining < -100){Expired();continue;}
                        }
                        else if(Stopwatch.GetElapsedTime(item.Time).TotalMilliseconds>100){Expired();continue;}
                        string next=p.Codec+":"+p.Width+":"+p.Height+":"+p.Generation+":"+Convert.ToBase64String(p.Extra??[]);
                        if(next!=signature||decoder==0)
                        {
                            if(!p.Key){WaitForKey();continue;}
                            if(decoder!=0)NativeMedia.qpv_close(decoder);decoder=0;
                            previous = default;
                            pendingPresentation = false;
                            string? component=_owner._host?.HardwareComponent(item.Codec,p.Width,p.Height);
                            if(p.Generation!=generation)hardwareFailed=false;
                            hardware=playback.decoder=="hardware"||(playback.decoder!="software"&&!hardwareFailed&&(_owner.Capabilities.hardwareMask&(1u<<item.Codec))!=0);
                            bool lowLatency = hardware && component != null && _owner._host?.SupportsLowLatency(component, item.Codec) == true;
                            if (lowLatency) decoder = NativeMedia.qpv_open(item.Codec, 2, p.Width, p.Height, p.Extra, p.Extra?.Length ?? 0, _surface, component);
                            if (decoder == 0) decoder=NativeMedia.qpv_open(item.Codec,hardware?1:0,p.Width,p.Height,p.Extra,p.Extra?.Length??0,_surface,component);
                            if(decoder==0 && hardware && playback.decoder=="auto"){hardware=false;decoder=NativeMedia.qpv_open(item.Codec,0,p.Width,p.Height,p.Extra,p.Extra?.Length??0,_surface,null);}
                            if(decoder==0)throw new InvalidOperationException(NativeMedia.Error);
                            generation=p.Generation;signature=next;awaitKey=false;
                        }
                        if(awaitKey&&!p.Key){WaitForKey();continue;}if(p.Key)awaitKey=false;
                        codec = item.Codec;
                        _queueTiming.Add((long)Stopwatch.GetElapsedTime(item.Time).TotalMicroseconds);
                        int result=NativeMedia.Decode(decoder,item.Data.AsSpan(p.Offset),p.Timestamp,p.Key,Visible);
                        if(result<0){DecoderFailed();continue;}
                        UpdateState(fps, bitrate, decodeCall: true);
                    }
                    catch(Exception ex)
                    {
                        bool changed = State.error != ex.Message;
                        State = State with { error = ex.Message };
                        if (changed) _owner.StateChanged?.Invoke(State);
                    }
                }
            }
            finally {if(decoder!=0)NativeMedia.qpv_close(decoder);}

            void Expired()
            {
                ++_expired;
                Interlocked.Increment(ref _dropped);
                awaitKey = true;
                RequestKeyFrame();
            }

            void WaitForKey()
            {
                Interlocked.Increment(ref _awaitingKey);
                Interlocked.Increment(ref _dropped);
                RequestKeyFrame();
            }

            void DecoderFailed()
            {
                State = State with { error = NativeMedia.Error };
                if (hardware) hardwareFailed = true;
                NativeMedia.qpv_close(decoder);
                decoder = 0;
                awaitKey = true;
                pendingPresentation = false;
                RequestKeyFrame();
                lastReport = Stopwatch.GetTimestamp();
                _owner.StateChanged?.Invoke(State);
            }

            void UpdateState(double ingressFps, long ingressBitrate, bool decodeCall)
            {
                NativeMedia.qpv_stats(decoder, out var stats);
                pendingPresentation = NativeMedia.PresentationState(decoder, out long presentUs);
                if (decodeCall) _decodeTiming.Add(stats.DecodeUs);
                if (presentUs >= 0 && stats.Presented > previous.Presented) _presentationTiming.Add(presentUs);
                decoded += Math.Max(0, stats.Decoded - previous.Decoded);
                presented += Math.Max(0, stats.Presented - previous.Presented);
                renderDrops += Math.Max(0, stats.Dropped - previous.Dropped);
                bool firstPicture = previous.Presented == 0 && stats.Presented > 0;
                previous = stats;
                double elapsed = lastReport == 0 ? 0 : Stopwatch.GetElapsedTime(lastReport).TotalSeconds;
                string mode = stats.HardwareFrames > 0 ? "hardware" : stats.Decoded > 0 ? "software" : "pending";
                long totalDropped = stats.Dropped + Volatile.Read(ref _dropped);
                if (!firstPicture && elapsed < 1 && lastReport != 0)
                {
                    State = new(_peer, mode, stats.Width, stats.Height, stats.Decoded, stats.Presented, totalDropped,
                        stats.DecodeUs / 1000.0, null, codec, ingressFps, ingressBitrate, State.timing);
                    return;
                }
                var timing = new ScreenReceiveStatistics(elapsed > 0 ? (decoded - decodedAtReport) / elapsed : 0,
                    elapsed > 0 ? (presented - presentedAtReport) / elapsed : 0, Volatile.Read(ref _overflow), _expired,
                    Volatile.Read(ref _awaitingKey), renderDrops, _queueTiming.Summary(), _decodeTiming.Summary(), _presentationTiming.Summary());
                (decodedAtReport, presentedAtReport, lastReport) = (decoded, presented, Stopwatch.GetTimestamp());
                State = new(_peer, mode, stats.Width, stats.Height, stats.Decoded, stats.Presented, totalDropped, stats.DecodeUs / 1000.0,
                    null, codec, ingressFps, ingressBitrate, timing);
                _owner.StateChanged?.Invoke(State);
            }
        }
        public void Dispose(){lock(_gate){if(_stopped)return;_stopped=true;_packets.Clear();_bytes=0;}_ready.Set();_worker.Join();_ready.Dispose();}
    }
}

[JsonSerializable(typeof(NativePlaybackOptions))]
[JsonSerializable(typeof(NativeVideoCapabilities))]
[JsonSerializable(typeof(NativeVideoLayout))]
[JsonSerializable(typeof(NativeVideoState[]))]
[JsonSerializable(typeof(NativeVideoFixture[]))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive=true)]
internal partial class NativeVideoJsonContext : JsonSerializerContext { }
