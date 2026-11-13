using System.Diagnostics;
using System.Text.Json;
using QuicNet.Video;

namespace QuicPunchTests.Tests;

internal static class NativeVideoTests
{
    private sealed class Host : INativeVideoHost
    {
        internal int Surfaces;
        public Task<nint> SurfaceAsync(string peer,bool probe=false){Interlocked.Increment(ref Surfaces);return Task.FromResult((nint)0);}
        public Task LayoutAsync(NativeVideoLayout layout)=>Task.CompletedTask;
        public Task ReleaseAsync(string peer)=>Task.CompletedTask;
        public string? HardwareComponent(int codec,int width,int height)=>"D3D11VA";
    }
    internal static async Task RunAsync()
    {
        using var input=typeof(NativeVideoRuntime).Assembly.GetManifestResourceStream("QuicNet.NativeVideo.Probes.json")!;
        var fixtures=JsonSerializer.Deserialize(input,NativeVideoJsonContext.Default.NativeVideoFixtureArray)!;
        foreach(var fixture in fixtures)
        {
            var result=NativeVideoRuntime.Probe(fixture,false,0,null);
            Check(result.Success,fixture.codec+": "+result.Error);
        }
        using var runtime=new NativeVideoRuntime();
        var host=new Host();runtime.Attach(host);
        var capabilities=await runtime.EnsureCapabilitiesAsync();
        Check(capabilities.available&&capabilities.decoders.All(d=>d.software==true),"Missing CPU decoder evidence");
        var avc=fixtures.Single(f=>f.codecId==1);
        int probes=host.Surfaces;
        await Task.WhenAll(Enumerable.Range(0,16).Select(_=>runtime.EnsureCapabilitiesAsync()));
        Check(host.Surfaces==probes,"Concurrent call joins repeated codec detection");
        for(int i=0;i<100;i++)
        {
            var reply=runtime.CheckProfileAsync(avc.codec,1280,720,avc.description);
            Check(reply.IsCompletedSuccessfully && reply.Result.Supported,"Cached codec request did not complete immediately");
            Check(reply.Result.Reason.Contains("caché"),"Cached baseline was presented as exact profile validation");
        }
        Check(host.Surfaces==probes,"A peer request allocated a decoder surface");
        await runtime.DetectAsync();
        Check(host.Surfaces==probes+fixtures.Length,"Explicit refresh did not rerun detection exactly once");
        byte[][] packets=avc.packets.Select(Convert.FromBase64String).ToArray();
        string peer=Guid.NewGuid().ToString();
        runtime.Configure(new(80,16,"software"));
        await runtime.LayoutAsync(new(peer,0,0,640,360,true));
        var clock=Stopwatch.StartNew();
        foreach(byte[] packet in packets)runtime.Enqueue(Guid.Parse(peer),1,packet);
        await WaitAsync(()=>runtime.States.FirstOrDefault()?.decoded>=8,()=>JsonSerializer.Serialize(runtime.States,NativeVideoJsonContext.Default.NativeVideoStateArray));
        Check(clock.ElapsedMilliseconds>=180,"Buffered playback ignored packet timestamps");
        Check(runtime.States.Single().mode=="software","Forced software policy was ignored");
        await runtime.LayoutAsync(new(peer,0,0,640,360,false));
        await Task.Delay(50);
        var hiddenState=runtime.States.Single();
        foreach(byte[] packet in packets)runtime.Enqueue(Guid.Parse(peer),1,packet);
        await Task.Delay(50);
        Check(runtime.States.Single().decoded==hiddenState.decoded,"Hidden video consumed packets");
        await runtime.LayoutAsync(new(peer,0,0,640,360,true));
        runtime.Configure(new(0,16,"software"));
        foreach(byte[] packet in packets)runtime.Enqueue(Guid.Parse(peer),1,packet);
        await WaitAsync(()=>runtime.States.Single().decoded==8&&runtime.States.Single().mode=="software");
        await runtime.StopAsync(peer);Check(runtime.States.Length==0,"Decoder survived stream stop");
        foreach(byte[] malformed in new byte[][] { [],[2,255,255,1],[2,0,2,123,125,1] })
        {
            bool rejected=false;try{NativeVideoRuntime.Parse(malformed);}catch(Exception ex) when(ex is InvalidDataException or KeyNotFoundException){rejected=true;}
            Check(rejected,"Malformed packet accepted");
        }
        bool invalid=false;try{runtime.Configure(new(251,4,"auto"));}catch(ArgumentException){invalid=true;}Check(invalid,"Unbounded playback delay accepted");
        Console.WriteLine("PASS native FFmpeg codecs, hardware evidence, buffered timestamps, forced software, hidden/stop lifecycle and malformed input.");
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static async Task WaitAsync(Func<bool> condition,Func<string>? detail=null)
    { var timer=Stopwatch.StartNew();while(!condition()){if(timer.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("Native decoder did not advance: "+detail?.Invoke());await Task.Delay(10);} }
}
