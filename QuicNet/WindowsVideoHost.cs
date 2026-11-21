using Photino.NET;
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
            if (!layout.visible || window.Minimized)
                NativeMedia.qpv_surface_layout(surface, 0, 0, 1, 1, 0);
            else
            {
                var area = layout.Viewport();
                NativeMedia.qpv_desktop_surface_crop(surface, area.X, area.Y, area.Width, area.Height, 1,
                    area.Left, area.Top, area.Right, area.Bottom);
            }
        }
        return true;
    });
    public async Task ReleaseAsync(string peerId) => await Invoke(()=>{if(_surfaces.Remove(peerId,out var surface))NativeMedia.qpv_surface_destroy(surface);return true;});
    public string? HardwareComponent(int codecId,int width,int height) => "D3D11VA";
}
