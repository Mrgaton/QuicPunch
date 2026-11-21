using Android.Graphics;
using Android.Media;
using Android.Views;
using Android.Widget;
using QuicNet.Video;

namespace QuicPunch.AndroidProbe;

internal sealed class AndroidVideoHost(MainActivity activity, FrameLayout root) : INativeVideoHost
{
    private sealed class Slot
    {
        internal TextureView? View;
        internal FrameLayout? Container;
        internal Surface? Surface;
        internal SurfaceTexture? Texture;
        internal Callback? Callback;
        internal TaskCompletionSource<nint> Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Callback(string id, Slot slot) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            slot.Surface = new Surface(surface);
            slot.Ready.TrySetResult(slot.Surface.Handle);
        }
        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            NativeVideoRuntime.Instance.StopDecoder(id);
            slot.Surface?.Release();
            slot.Surface?.Dispose();
            slot.Surface = null;
            _ = NativeVideoRuntime.Instance.StopAsync(id);
            return true;
        }
    }
    private readonly Dictionary<string,Slot> _slots=[];
    private bool _nativeInitialized;
    private Task<T> UI<T>(Func<T> action)
    {
        var done=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(()=>{try{done.TrySetResult(action());}catch(Exception ex){done.TrySetException(ex);}});return done.Task;
    }
    public async Task<nint> SurfaceAsync(string peerId,bool probe=false)
    {
        var slot=await UI(()=>
        {
            if (!_nativeInitialized)
            {
                NativeMedia.qpv_android_init(Android.Runtime.JNIEnv.Handle);
                _nativeInitialized = true;
            }
            if(_slots.TryGetValue(peerId,out var found))return found;
            var item=new Slot();_slots[peerId]=item;
            if(probe){item.Texture=new SurfaceTexture(false);item.Texture.SetDefaultBufferSize(320,180);item.Surface=new Surface(item.Texture);item.Ready.TrySetResult(item.Surface.Handle);}
            else {
                // A TextureView obeys the container's clip when the zoomed video extends
                // past the viewport or the controls, and leaves input to the WebView.
                item.View = new TextureView(activity) { PivotX = 0, PivotY = 0, Clickable = false, Focusable = false };
                item.Callback = new Callback(peerId, item);
                item.View.SurfaceTextureListener = item.Callback;
                item.Container=new FrameLayout(activity);item.Container.SetClipChildren(true);item.Container.SetClipToPadding(true);
                item.Container.AddView(item.View,new FrameLayout.LayoutParams(1,1));
                root.AddView(item.Container,new FrameLayout.LayoutParams(1,1));
            }
            return item;
        });
        return await slot.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    public async Task LayoutAsync(NativeVideoLayout layout)=>await UI(()=>
    {
        if (!_slots.TryGetValue(layout.peerId, out var slot) || slot.View is not { } view)
            return true;
        var container = slot.Container!;
        // Hiding keeps the decoder surface attached and avoids a buffer resize.
        container.TranslationX = layout.visible ? 0 : -100000;
        if (!layout.visible) return true;

        // Both the WebView and the native video use the same inset-adjusted root.
        double scale = layout.scale;
        int left = (int)Math.Round((layout.x + Math.Clamp(layout.clipX, 0, layout.width)) * scale);
        int top = (int)Math.Round((layout.y + Math.Clamp(layout.clipY, 0, layout.height)) * scale);
        int cw = Math.Max(1, (int)Math.Round((layout.clipWidth < 0 ? layout.width : Math.Clamp(layout.clipWidth, 0, layout.width)) * scale));
        int ch = Math.Max(1, (int)Math.Round((layout.clipHeight < 0 ? layout.height : Math.Clamp(layout.clipHeight, 0, layout.height)) * scale));
        if (container.LayoutParameters is not FrameLayout.LayoutParams bounds ||
            bounds.Width != cw || bounds.Height != ch || bounds.LeftMargin != left || bounds.TopMargin != top)
            container.LayoutParameters = new FrameLayout.LayoutParams(cw, ch) { LeftMargin = left, TopMargin = top };

        int width = Math.Max(1, (int)Math.Round(layout.width * scale / layout.zoom));
        int height = Math.Max(1, (int)Math.Round(layout.height * scale / layout.zoom));
        if (view.LayoutParameters!.Width != width || view.LayoutParameters.Height != height)
            view.LayoutParameters = new FrameLayout.LayoutParams(width, height);
        view.TranslationX = (float)(layout.x * scale - left);
        view.TranslationY = (float)(layout.y * scale - top);
        view.ScaleX = (float)(layout.width * scale / width);
        view.ScaleY = (float)(layout.height * scale / height);
        return true;
    });
    public async Task ReleaseAsync(string peerId)=>await UI(()=>
    {
        if(_slots.Remove(peerId,out var slot)){
            if (slot.View is { } view)
            {
                view.SurfaceTextureListener = null;
                root.RemoveView(slot.Container);
                view.Dispose();
                slot.Container?.Dispose();
                slot.Callback?.Dispose();
            }
            slot.Surface?.Release();
            slot.Surface?.Dispose();
            slot.Texture?.Release();
            slot.Texture?.Dispose();
        }return true;
    });
    public string? HardwareComponent(int codecId,int width,int height) => FindComponent(codecId,width,height,false);
    public bool SupportsLowLatency(string component, int codecId)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return false;
        using var list = new MediaCodecList(MediaCodecListKind.AllCodecs);
        foreach (var codec in list.GetCodecInfos() ?? [])
        {
            if (codec.IsEncoder || codec.Name != component) continue;
            try { return codec.GetCapabilitiesForType(Mime(codecId))?.IsFeatureSupported("low-latency") == true; }
            catch (Java.Lang.Exception) { return false; }
        }
        return false;
    }
    private static string Mime(int codecId) => codecId switch
    {
        1 => "video/avc", 3 => "video/x-vnd.on2.vp9", 4 => "video/av01", 5 => "video/hevc", _ => ""
    };

    internal static string? FindComponent(int codecId,int width,int height,bool encoder)
    {
        string mime = Mime(codecId);
        using var list=new MediaCodecList(MediaCodecListKind.AllCodecs);
        string? fallback = null;
        foreach(var codec in list.GetCodecInfos() ?? [])
        {
            if(codec.IsEncoder!=encoder||!codec.IsHardwareAccelerated||!(codec.GetSupportedTypes() ?? []).Contains(mime))continue;
            try
            {
                var caps = codec.GetCapabilitiesForType(mime);
                if (caps?.VideoCapabilities?.IsSizeSupported(width, height) != true) continue;
                if (encoder || !OperatingSystem.IsAndroidVersionAtLeast(30) || caps.IsFeatureSupported("low-latency")) return codec.Name;
                fallback ??= codec.Name;
            }
            catch (Java.Lang.Exception) { }
        }
        return fallback;
    }

    internal sealed record ScreenEncoderProfile(string Component, int Width, int Height, int Fps, int Bitrate);

    internal static ScreenEncoderProfile? FindScreenEncoder(int codecId, int width, int height, int fps, int bitrate)
    {
        string mime = Mime(codecId);
        using var list = new MediaCodecList(MediaCodecListKind.RegularCodecs);
        var candidates = new List<ScreenEncoderProfile>();
        foreach (var codec in list.GetCodecInfos() ?? [])
        {
            if (!codec.IsEncoder || !codec.IsHardwareAccelerated || !(codec.GetSupportedTypes() ?? []).Contains(mime)) continue;
            try
            {
                var video = codec.GetCapabilitiesForType(mime)?.VideoCapabilities;
                if (video == null) continue;
                foreach (double scale in new[] { 1.0, 0.75, 0.5, 0.375 })
                {
                    int w = (int)(width * scale) / video.WidthAlignment * video.WidthAlignment;
                    int h = (int)(height * scale) / video.HeightAlignment * video.HeightAlignment;
                    if (w < 128 || h < 128 || !video.IsSizeSupported(w, h)) continue;
                    int rate = new[] { fps, 60, 30, 24, 15, 5 }.FirstOrDefault(f =>
                        f <= fps && video.AreSizeAndRateSupported(w, h, f));
                    if (rate == 0) continue;
                    int min = video.BitrateRange?.Lower is Java.Lang.Integer lo ? Math.Max(500_000, lo.IntValue()) : 500_000;
                    int max = video.BitrateRange?.Upper is Java.Lang.Integer hi ? Math.Min(100_000_000, hi.IntValue()) : 100_000_000;
                    if (min <= max) candidates.Add(new(codec.Name!, w, h, rate, Math.Clamp(bitrate, min, max)));
                    if (rate == fps) break;
                }
            }
            catch (Java.Lang.Exception) { }
        }
        return candidates.OrderByDescending(c => c.Fps).ThenByDescending(c => c.Width * c.Height).FirstOrDefault();
    }
}
