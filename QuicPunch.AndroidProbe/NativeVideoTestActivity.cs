#if DEBUG
using Android.App;
using Android.Widget;
using QuicNet.Video;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace QuicPunch.AndroidProbe;

// Explicit synthetic test entry point: no camera, microphone, screen capture or node service.
[Activity(Name = "org.quicnet.NativeVideoTestActivity", Exported = true)]
public sealed class NativeVideoTestActivity : Activity
{
    [DllImport("nettt_test_pattern")] private static extern int qpt_pattern(nint env,nint surface,int frames);
    private CancellationTokenSource _stop = new();
    private Android.Views.SurfaceView? _view;
    private SurfaceCallback? _callback;
    private sealed class SurfaceCallback : Java.Lang.Object, Android.Views.ISurfaceHolderCallback
    {
        internal TaskCompletionSource<nint> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Android.Views.Surface? Surface;
        public void SurfaceCreated(Android.Views.ISurfaceHolder holder){Surface=holder.Surface;Ready.TrySetResult(Surface!.Handle);}
        public void SurfaceChanged(Android.Views.ISurfaceHolder holder,Android.Graphics.Format format,int w,int h) { }
        public void SurfaceDestroyed(Android.Views.ISurfaceHolder holder) { }
    }
    protected override void OnCreate(Android.OS.Bundle? state)
    {
        base.OnCreate(state);
        NativeMedia.qpv_android_init(Android.Runtime.JNIEnv.Handle);
        var surface = _view = new Android.Views.SurfaceView(this);var callback=_callback=new SurfaceCallback();
        surface.Holder!.AddCallback(callback);SetContentView(surface);
        _ = RunAsync(callback.Ready.Task);
    }
    private async Task RunAsync(Task<nint> ready)
    {
        try
        {
            nint surface=await ready;
            using var source=typeof(NativeVideoRuntime).Assembly.GetManifestResourceStream("QuicNet.NativeVideo.Probes.json")!;
            var fixtures=JsonSerializer.Deserialize(source,NativeVideoJsonContext.Default.NativeVideoFixtureArray)!;
            foreach(var fixture in fixtures)
            foreach(bool hardware in new[]{false,true})
            {
                string? component=AndroidVideoHost.FindComponent(fixture.codecId,fixture.width,fixture.height,false);
                if(hardware&&component==null){Log($"SKIP hardware {fixture.codec}: no component");continue;}
                // Dedicated thread keeps EGL context ownership fixed, including release.
                await Task.Factory.StartNew(()=>Decode(fixture,hardware,surface,component),_stop.Token,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            }
            foreach(int id in new[]{1,5,4})await EncodeAsync(id,surface);
            Log("COMPLETE");
        }
        catch(Exception ex){Log("FAIL "+ex);}
    }
    private void Decode(NativeVideoFixture fixture,bool hardware,nint surface,string? component)
    {
        byte[]? extra=fixture.description==null?null:Convert.FromBase64String(fixture.description);
        nint decoder=NativeMedia.qpv_open(fixture.codecId,hardware?1:0,fixture.width,fixture.height,extra,extra?.Length??0,surface,component);
        if(decoder==0)throw new Exception("Open "+fixture.codec+": "+NativeMedia.Error);
        try
        {
            foreach(string encoded in fixture.packets){_stop.Token.ThrowIfCancellationRequested();byte[] packet=Convert.FromBase64String(encoded);var p=NativeVideoRuntime.Parse(packet);
                if(NativeMedia.Decode(decoder,packet.AsSpan(p.Offset),p.Timestamp,p.Key,true)<0)throw new Exception(NativeMedia.Error);Thread.Sleep(40);}
            for(int i=0;i<20;i++){if(NativeMedia.Decode(decoder,[],0,false,true)<0)throw new Exception(NativeMedia.Error);Thread.Sleep(10);}
            NativeMedia.qpv_stats(decoder,out var stats);
            if(stats.Decoded==0||stats.Presented==0||hardware&&stats.HardwareFrames!=stats.Decoded)throw new Exception("No decoded/presented hardware evidence");
            Log($"PASS decode {fixture.codec} {(hardware?"hardware":"software")} decoded={stats.Decoded} presented={stats.Presented} us={stats.DecodeUs}");
        }
        finally{NativeMedia.qpv_close(decoder);}
    }
    private async Task EncodeAsync(int id,nint display)
    {
        string? component=AndroidVideoHost.FindComponent(id,640,360,true);
        if(component==null){Log($"SKIP encode {id}: no component");return;}
        using var surface=Android.Media.MediaCodec.CreatePersistentInputSurface()!;
        nint encoder=NativeMedia.qpv_encoder_open(id,640,360,30,2_000_000,1000,surface.Handle,component,0,50);
        if(encoder==0)throw new Exception("Encoder "+component+": "+NativeMedia.Error);
        Task<int>? producer=null;
        try
        {
            producer=Task.Factory.StartNew(()=>qpt_pattern(Android.Runtime.JNIEnv.Handle,surface.Handle,60),_stop.Token,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            var packets=new List<byte[]>();var packetizer=new NativeCapturePacketizer((byte)id,640,360,1);byte[] buffer=new byte[2*1024*1024];
            var deadline=DateTime.UtcNow.AddSeconds(8);
            while(DateTime.UtcNow<deadline&&!_stop.IsCancellationRequested){int length=NativeMedia.qpv_encoder_read(encoder,buffer,buffer.Length,out long pts,out int key);
                if(length<0)throw new Exception(NativeMedia.Error);
                if(length>0){var packet=packetizer.Packet(buffer.AsSpan(0,length).ToArray(),pts,key!=0);if(packet!=null)packets.Add(packet);}
                if(producer.IsCompleted&&packets.Count>=30)break;
                await Task.Delay(3);
            }
            if(await producer!=0||packets.Count<30)throw new Exception("Insufficient encoded frames "+packets.Count);
            var fixture=new NativeVideoFixture(id,packetizer.Codec,640,360,packetizer.Description,packets.Select(Convert.ToBase64String).ToArray());
            await Task.Factory.StartNew(()=>Decode(fixture,false,display,null),_stop.Token,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            Log($"PASS encode {id} {component} frames={packets.Count} round-trip decoded");
        }
        finally{if(producer!=null)try{await producer;}catch{}NativeMedia.qpv_encoder_close(encoder);}
    }
    private static void Log(string message)=>Android.Util.Log.Info("NetTT-Native-Test",message);
    protected override void OnDestroy(){_stop.Cancel();base.OnDestroy();}
}
#endif
