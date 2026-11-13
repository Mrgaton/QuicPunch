using Photino.NET;
using System.Runtime.InteropServices;
using QuicNet.Video;

namespace QuicNet;

internal sealed class WindowsVideoHost(PhotinoWindow window) : INativeVideoHost
{
    private readonly Dictionary<string,nint> _surfaces = new();
    private Task<T> Invoke<T>(Func<T> action)
    {
        var done=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Invoke(()=>{try{done.SetResult(action());}catch(Exception ex){done.SetException(ex);}});
        return done.Task;
    }
    public Task<nint> SurfaceAsync(string peerId,bool probe=false) => Invoke(()=>
    {
        if(probe)return (nint)0; // D3D11 probes do not need a visible swap chain.
        if(_surfaces.TryGetValue(peerId,out var existing))return existing;
        nint surface=NativeMedia.qpv_surface_create(window.WindowHandle);
        if(surface==0)throw new InvalidOperationException("No se pudo crear la superficie nativa de vídeo.");
        _surfaces[peerId]=surface;return surface;
    });
    public async Task LayoutAsync(NativeVideoLayout layout) => await Invoke(()=>
    {
        if(_surfaces.TryGetValue(layout.peerId,out var surface))
        {
            NativeMedia.qpv_surface_layout(surface,(int)Math.Round(layout.x*layout.scale),(int)Math.Round(layout.y*layout.scale),
                Math.Max(1,(int)Math.Round(layout.width*layout.scale)),Math.Max(1,(int)Math.Round(layout.height*layout.scale)),layout.visible&&!window.Minimized?1:0);
            int x=(int)Math.Round(Math.Clamp(layout.clipX,0,layout.width)*layout.scale),y=(int)Math.Round(Math.Clamp(layout.clipY,0,layout.height)*layout.scale);
            int w=(int)Math.Round((layout.clipWidth<0?layout.width:Math.Clamp(layout.clipWidth,0,layout.width))*layout.scale);
            int h=(int)Math.Round((layout.clipHeight<0?layout.height:Math.Clamp(layout.clipHeight,0,layout.height))*layout.scale);
            nint region=CreateRectRgn(x,y,x+w,y+h);
            if(SetWindowRgn(surface,region,true)==0)DeleteObject(region);
        }
        return true;
    });
    public async Task ReleaseAsync(string peerId) => await Invoke(()=>{if(_surfaces.Remove(peerId,out var surface))NativeMedia.qpv_surface_destroy(surface);return true;});
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window,nint region,bool redraw);
    public string? HardwareComponent(int codecId,int width,int height) => "D3D11VA";
}
