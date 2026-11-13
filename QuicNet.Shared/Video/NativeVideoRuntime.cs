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
internal sealed record NativeVideoLayout(string peerId, double x, double y, double width, double height, bool visible, double scale = 1, double clipX = 0, double clipY = 0, double clipWidth = -1, double clipHeight = -1);
internal sealed record NativeDecoderCapability(int codecId, string codec, bool? hardware, bool? software, string status, string reason, int testWidth, int testHeight, string? component = null);
internal sealed record NativeVideoCapabilities(bool available, string engine, uint decoderMask, uint hardwareMask, NativeDecoderCapability[] decoders);
internal sealed record NativeVideoState(string peerId, string mode, int width, int height, long decoded, long presented, long dropped, double decodeMs, string? error);
internal sealed record NativeVideoFixture(int codecId, string codec, int width, int height, string? description, string[] packets);

internal interface INativeVideoHost
{
    Task<nint> SurfaceAsync(string peerId, bool probe = false);
    Task LayoutAsync(NativeVideoLayout layout);
    Task ReleaseAsync(string peerId);
    string? HardwareComponent(int codecId, int width, int height);
}

internal static unsafe class NativeMedia
{
    private const string Library = "nettt_media";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint qpv_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint qpv_error();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint qpv_open(int codec, int hardware, int width, int height, byte[]? extra, int length, nint surface, [MarshalAs(UnmanagedType.LPUTF8Str)] string? component);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_close(nint decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int qpv_decode(nint decoder, byte* data, int size, long pts, int key, int render);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int qpv_stats(nint decoder, out Statistics stats);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint qpv_surface_create(nint parent);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_surface_layout(nint surface, int x, int y, int width, int height, int visible);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_surface_destroy(nint surface);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_android_init(nint env);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint qpv_encoder_open(int codec, int width, int height, int fps, int bitrate, int gop, nint surface, [MarshalAs(UnmanagedType.LPUTF8Str)] string? component, int vbr, int bufferMs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int qpv_encoder_read(nint encoder, byte[] output, int capacity, out long pts, out int key);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void qpv_encoder_close(nint encoder);
    internal static string Error => Marshal.PtrToStringUTF8(qpv_error()) ?? "Error del motor FFmpeg";
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
    internal NativePlaybackOptions Playback { get; private set; } = new();
    internal void Configure(NativePlaybackOptions value) { value.Validate(); Playback = value; foreach(var receiver in _receivers.Values)receiver.Wake(); }
    internal void Detach() { Dispose(); _host = null; }
    internal void StopDecoder(string peer) { if(_receivers.TryRemove(peer,out var receiver))receiver.Dispose(); }
    internal NativeVideoCapabilities Capabilities { get; private set; } = new(false, "FFmpeg nativo", 0, 0, []);
    internal event Action<NativeVideoCapabilities>? CapabilitiesChanged;
    internal event Action<NativeVideoState>? StateChanged;
    internal INativeVideoHost? Host => _host;
    internal void Attach(INativeVideoHost host)
    {
        lock(_capabilityGate) { _host=host; _capabilityTask=null; }
        _ = EnsureCapabilitiesAsync();
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
        if (!double.IsFinite(layout.x + layout.y + layout.width + layout.height + layout.scale + layout.clipX + layout.clipY + layout.clipWidth + layout.clipHeight) || layout.width < 0 || layout.height < 0 || layout.width * layout.scale > 16384 || layout.height * layout.scale > 16384 || Math.Abs(layout.x)>32768 || Math.Abs(layout.y)>32768 || layout.scale is < .25 or > 8) throw new ArgumentException("Geometría inválida.");
        if (!_receivers.ContainsKey(layout.peerId) && layout.visible)
        {
            nint surface = await _host.SurfaceAsync(layout.peerId).ConfigureAwait(false);
            var receiver = new Receiver(this, layout.peerId, surface);
            if (!_receivers.TryAdd(layout.peerId,receiver)) receiver.Dispose();
        }
        await _host.LayoutAsync(layout).ConfigureAwait(false);
        if (_receivers.TryGetValue(layout.peerId, out var stream)) { stream.Visible = layout.visible; stream.Wake(); }
        } finally { _lifecycle.Release(); }
    }
    internal void Enqueue(Guid peer, byte codec, byte[] packet)
    { if (_receivers.TryGetValue(peer == Guid.Empty ? "local" : peer.ToString(), out var receiver)) receiver.Enqueue(codec,packet); }
    internal async Task StopAsync(string peer)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try {
            if (_receivers.TryRemove(peer,out var receiver)) await Task.Run(receiver.Dispose).ConfigureAwait(false);
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
    public void Dispose() { foreach(var receiver in _receivers.Values)receiver.Dispose();_receivers.Clear(); }

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

    private sealed class Receiver : IDisposable
    {
        private readonly NativeVideoRuntime _owner;
        private readonly string _peer;
        private readonly nint _surface;
        private readonly object _gate=new();
        private readonly Queue<(byte Codec, byte[] Data, long Time)> _packets=new();
        private readonly AutoResetEvent _ready=new(false);
        private readonly Thread _worker;
        private bool _stopped, _needsKey=true;
        private int _bytes;
        private long _dropped;
        internal volatile bool Visible;
        internal NativeVideoState State {get;private set;}
        internal Receiver(NativeVideoRuntime owner,string peer,nint surface)
        {
            _owner=owner;_peer=peer;_surface=surface;State=new(peer,"pending",0,0,0,0,0,0,null);
            _worker=new Thread(Run){IsBackground=true,Name="NetTT decode "+peer};_worker.Start();
        }
        internal void Wake() { lock(_gate) { if(!_stopped)_ready.Set(); } }
        internal void Enqueue(byte codec,byte[] bytes)
        {
            lock(_gate)
            {
                if(_stopped)return;
                if(!Visible){_needsKey=true;return;}
                bool key=bytes.Length>0&&(bytes[0]&1)!=0;
                if(_packets.Count>=_owner.Playback.maxQueueFrames||_bytes+bytes.Length>16*1024*1024){_dropped+=_packets.Count;_packets.Clear();_bytes=0;_needsKey=true;}
                if(bytes.Length>2*1024*1024||(_needsKey&&!key)){++_dropped;return;}
                if(key)_needsKey=false;
                _packets.Enqueue((codec,bytes,Stopwatch.GetTimestamp()));_bytes+=bytes.Length;_ready.Set();
            }
        }
        private void Run()
        {
            nint decoder=0;long generation=-1,lastReport=0;string signature="";bool hardware=false,hardwareFailed=false,awaitKey=true;long lastPacket=0;string decoderPreference="";long clockAnchor=0,ptsAnchor=0,clockGeneration=-1;
            try
            {
                while(true)
                {
                    var playback=_owner.Playback;
                    if (!Visible || decoderPreference != playback.decoder)
                    {
                        if(decoder!=0){NativeMedia.qpv_close(decoder);decoder=0;}awaitKey=true;
                        decoderPreference=playback.decoder;
                        if(!Visible)lock(_gate){State=State with{mode="paused",error=null};_dropped+=_packets.Count;_packets.Clear();_bytes=0;_needsKey=true;}
                    }
                    (byte Codec,byte[] Data,long Time) item=default;
                    lock(_gate){if(_stopped)break;if(_packets.Count>0){item=_packets.Dequeue();_bytes-=item.Data.Length;}}
                    if(item.Data==null)
                    {
                        if(decoder!=0 && OperatingSystem.IsAndroid() && Stopwatch.GetElapsedTime(lastPacket).TotalMilliseconds<100)NativeMedia.Decode(decoder,[],0,false,Visible);
                        _ready.WaitOne(OperatingSystem.IsAndroid()&&decoder!=0&&Stopwatch.GetElapsedTime(lastPacket).TotalMilliseconds<100?8:Timeout.Infinite);continue;
                    }
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
                            if(remaining < -100){++_dropped;awaitKey=true;continue;}
                        }
                        else if(Stopwatch.GetElapsedTime(item.Time).TotalMilliseconds>100){++_dropped;awaitKey=true;continue;}
                        string next=p.Codec+":"+p.Width+":"+p.Height+":"+p.Generation+":"+Convert.ToBase64String(p.Extra??[]);
                        if(next!=signature||decoder==0)
                        {
                            if(!p.Key)continue;
                            if(decoder!=0)NativeMedia.qpv_close(decoder);decoder=0;
                            string? component=_owner._host?.HardwareComponent(item.Codec,p.Width,p.Height);
                            if(p.Generation!=generation)hardwareFailed=false;
                            hardware=playback.decoder=="hardware"||(playback.decoder!="software"&&!hardwareFailed&&(_owner.Capabilities.hardwareMask&(1u<<item.Codec))!=0);
                            decoder=NativeMedia.qpv_open(item.Codec,hardware?1:0,p.Width,p.Height,p.Extra,p.Extra?.Length??0,_surface,component);
                            if(decoder==0 && hardware && playback.decoder=="auto"){hardware=false;decoder=NativeMedia.qpv_open(item.Codec,0,p.Width,p.Height,p.Extra,p.Extra?.Length??0,_surface,null);}
                            if(decoder==0)throw new InvalidOperationException(NativeMedia.Error);
                            generation=p.Generation;signature=next;awaitKey=false;
                        }
                        if(awaitKey&&!p.Key)continue;if(p.Key)awaitKey=false;
                        int result=NativeMedia.Decode(decoder,item.Data.AsSpan(p.Offset),p.Timestamp,p.Key,Visible);
                        if(result<0){string error=NativeMedia.Error;if(hardware)hardwareFailed=true;NativeMedia.qpv_close(decoder);decoder=0;awaitKey=true;throw new InvalidOperationException(error);}
                        NativeMedia.qpv_stats(decoder,out var stats);
                        State=new(_peer,stats.HardwareFrames>0?"hardware":stats.Decoded>0?"software":"pending",stats.Width,stats.Height,stats.Decoded,stats.Presented,stats.Dropped+_dropped,stats.DecodeUs/1000.0,null);
                    }
                    catch(Exception ex){State=State with{error=ex.Message};}
                    if(Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds>=1000){lastReport=Stopwatch.GetTimestamp();_owner.StateChanged?.Invoke(State);}
                }
            }
            finally {if(decoder!=0)NativeMedia.qpv_close(decoder);}
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
