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
        internal SurfaceView? View;
        internal FrameLayout? Container;
        internal Surface? Surface;
        internal SurfaceTexture? Texture;
        internal Callback? Callback;
        internal TaskCompletionSource<nint> Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Callback(string id,Slot slot) : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder){slot.Surface=holder.Surface;slot.Ready.TrySetResult(slot.Surface?.Handle??0);}
        public void SurfaceChanged(ISurfaceHolder holder,Format format,int width,int height) { }
        public void SurfaceDestroyed(ISurfaceHolder holder){NativeVideoRuntime.Instance.StopDecoder(id);_ = NativeVideoRuntime.Instance.StopAsync(id);}
    }
    private readonly Dictionary<string,Slot> _slots=[];
    private Task<T> UI<T>(Func<T> action)
    {
        var done=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(()=>{try{done.TrySetResult(action());}catch(Exception ex){done.TrySetException(ex);}});return done.Task;
    }
    public async Task<nint> SurfaceAsync(string peerId,bool probe=false)
    {
        var slot=await UI(()=>
        {
            if(_slots.TryGetValue(peerId,out var found))return found;
            var item=new Slot();_slots[peerId]=item;
            if(probe){item.Texture=new SurfaceTexture(false);item.Texture.SetDefaultBufferSize(320,180);item.Surface=new Surface(item.Texture);item.Ready.TrySetResult(item.Surface.Handle);}
            else {
                item.View=new SurfaceView(activity);item.View.SetZOrderOnTop(true);
                item.Callback=new Callback(peerId,item);item.View.Holder!.AddCallback(item.Callback);
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
        if(_slots.TryGetValue(layout.peerId,out var slot)&&slot.View!=null){
            // CSS pixels are mapped using WebView devicePixelRatio; root already excludes insets.
            double scale=layout.scale;
            int cx=(int)Math.Round(Math.Clamp(layout.clipX,0,layout.width)*scale),cy=(int)Math.Round(Math.Clamp(layout.clipY,0,layout.height)*scale);
            int cw=(int)Math.Round((layout.clipWidth<0?layout.width:Math.Clamp(layout.clipWidth,0,layout.width))*scale);
            int ch=(int)Math.Round((layout.clipHeight<0?layout.height:Math.Clamp(layout.clipHeight,0,layout.height))*scale);
            slot.Container!.LayoutParameters=new FrameLayout.LayoutParams(Math.Max(1,cw),Math.Max(1,ch)){LeftMargin=(int)Math.Round(layout.x*scale)+cx,TopMargin=(int)Math.Round(layout.y*scale)+cy};
            slot.View.LayoutParameters=new FrameLayout.LayoutParams(Math.Max(1,(int)Math.Round(layout.width*scale)),Math.Max(1,(int)Math.Round(layout.height*scale))){LeftMargin=-cx,TopMargin=-cy};
            slot.Container.TranslationX=layout.visible?0:-100000;
        }return true;
    });
    public async Task ReleaseAsync(string peerId)=>await UI(()=>
    {
        if(_slots.Remove(peerId,out var slot)){
            if(slot.View!=null){if(slot.Callback!=null)slot.View.Holder?.RemoveCallback(slot.Callback);root.RemoveView(slot.Container);slot.View.Dispose();slot.Container?.Dispose();slot.Callback?.Dispose();}
            else {slot.Surface?.Release();slot.Surface?.Dispose();slot.Texture?.Release();slot.Texture?.Dispose();}
        }return true;
    });
    public string? HardwareComponent(int codecId,int width,int height) => FindComponent(codecId,width,height,false);
    internal static string? FindComponent(int codecId,int width,int height,bool encoder)
    {
        string mime=codecId switch{1=>"video/avc",3=>"video/x-vnd.on2.vp9",4=>"video/av01",5=>"video/hevc",_=>""};
        using var list=new MediaCodecList(MediaCodecListKind.AllCodecs);
        foreach(var codec in list.GetCodecInfos() ?? [])
        {
            if(codec.IsEncoder!=encoder||!codec.IsHardwareAccelerated||!(codec.GetSupportedTypes() ?? []).Contains(mime))continue;
            try{var caps=codec.GetCapabilitiesForType(mime);if(caps?.VideoCapabilities?.IsSizeSupported(width,height)==true)return codec.Name;}catch(Java.Lang.Exception){}
        }
        return null;
    }
}
