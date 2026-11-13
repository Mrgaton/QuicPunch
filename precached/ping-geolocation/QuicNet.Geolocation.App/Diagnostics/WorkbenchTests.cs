using System.Net;
using System.Text.Json;
using QuicNet.Geolocation;
using QuicNet.Geolocation.Workbench;

namespace QuicNet.Geolocation.App.Diagnostics;

internal static class WorkbenchTests
{
    public static async Task Run(Action<bool,string> check)
    {
        // The original export contains constraints, not raw packets. Reconstruct only their geometry;
        // this cannot validate timing quality, VPN state or the real physical position of each router.
        using var fixture=typeof(WorkbenchTests).Assembly.GetManifestResourceStream("QuicNet.Geolocation.App.Diagnostics.Fixtures.valencia-conflict.json")!;
        var old=JsonSerializer.Deserialize(fixture,JsonFiles.GetTypeInfo<LocationUpdate>())!;
        var catalog=new Catalog(old.Region!.Constraints.Select((c,i)=>new Landmark(c.LandmarkId,"192.0.2."+(i+1),c.Center,
            c.Evidence=="known-router-ttl-rtt" ? "RIPE IPmap 2026-10-18" : "RIPE Atlas 2026-10-17")
            {Kind=c.Evidence=="known-router-ttl-rtt" ? "router":"anchor",SiteId=c.SiteId,LocationVerified=true,
             UncertaintyKm=c.Evidence=="known-router-ttl-rtt" ? 50:25}).ToList());
        var observations=catalog.Landmarks.Where(a=>a.Kind=="anchor").Select(a=>new Observation(a.Id,a.Address,DateTimeOffset.UtcNow,
            [new(old.Region.Constraints.Single(c=>c.LandmarkId==a.Id).RttMs,"Success")])).ToList();
        observations[0]=observations[0] with{Route=catalog.Landmarks.Where(a=>a.Kind=="router").Select((a,i)=>
            new RouteHop(i+1,a.Address,old.Region.Constraints.Single(c=>c.LandmarkId==a.Id).RttMs,"TtlExpired")).ToList()};
        var session=new MeasurementSession("regression-export","geometry-only","ipv4",observations);
        var model=new EstimatorOptions{ResolutionKm=50};
        check(new LatencyEstimator(model).Estimate(catalog,session).Status=="inconsistent","original Valencia export reproduces the incompatible intersection");
        var revised=EvidenceReconciler.Estimate(catalog,session,model);
        var valencia=new GeoPoint(39.46,-.33);
        bool Contains(RegionSnapshot r)=>r.Cells.Any(c=>valencia.Latitude>=c.South && valencia.Latitude<=c.North && valencia.Longitude>=c.West && valencia.Longitude<=c.East);
        check(revised.Estimate.Status!="inconsistent" && revised.Estimate.Cells.Any(c=>valencia.Latitude>=c.South && valencia.Latitude<=c.North && valencia.Longitude>=c.West && valencia.Longitude<=c.East),"published router errors cannot invalidate the primary Valencia-compatible region");
        check(revised.Diagnostics is{InferredRouterConflict:true} && revised.Diagnostics.DisputedLocations.Count>0,"router conflict diagnosed and alternative explanations retained");
        Console.WriteLine($"Valencia review: {revised.Diagnostics!.StrictStatus}; disputed={string.Join(',',revised.Diagnostics.DisputedLocations.Select(d=>d.LandmarkId))}; primaryArea={revised.Estimate.CandidateAreaKm2:F0}; conditionalArea={revised.Diagnostics.ConditionalRegion?.AreaKm2:F0}");
        if(revised.Diagnostics?.ConditionalRegion is { } hypothesis) check(Contains(hypothesis),"conditional repaired router region retains Valencia");
        check(revised.Estimate.Constraints.All(c=>c.Evidence!="known-router-ttl-rtt"),"inferred router coordinates do not become verified mandatory bounds");
        var west=new Landmark("west","192.0.2.100",new(0,0),"RIPE IPmap fixture"){Kind="router",LocationVerified=true,UncertaintyKm=0};
        var east=west with{Id="east",Address="192.0.2.101",Position=new(0,90)};
        var baseAnchor=new Landmark("base","192.0.2.102",new(0,0),"fixture"){LocationVerified=true,UncertaintyKm=0};
        var ambiguity=new MeasurementSession("ambiguous","test","ipv4",[new(baseAnchor.Id,baseAnchor.Address,DateTimeOffset.UtcNow,[new(200,"Success")])
        {Route=[new(1,west.Address,1,"TtlExpired"),new(2,east.Address,1,"TtlExpired")]}]);
        var alternatives=EvidenceReconciler.Estimate(new([baseAnchor,west,east]),ambiguity,model);
        check(alternatives.Diagnostics is{InferredRouterConflict:true,ConditionalRegion:null} && alternatives.Diagnostics.DisputedLocations.Count==2,
            "ambiguous router repairs do not arbitrarily choose a city or a conditional point");
        var hardConflict=ambiguity with{Observations=[new(baseAnchor.Id,baseAnchor.Address,DateTimeOffset.UtcNow,[new(1,"Success")]),
            new("far","192.0.2.103",DateTimeOffset.UtcNow,[new(1,"Success")]) ]};
        var hard=EvidenceReconciler.Estimate(new([baseAnchor,baseAnchor with{Id="far",Address="192.0.2.103",Position=new(0,90)},west]),hardConflict,model);
        check(hard.Estimate.Status=="inconsistent" && hard.Estimate.Representative is null && hard.Diagnostics?.InferredRouterConflict==false,
            "contradictory primary anchors remain conflicting instead of being blamed on IPmap");
        string root=Path.Combine(Path.GetTempPath(),"geo-workbench-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);string bom=Path.Combine(root,"bom.json");
            await File.WriteAllTextAsync(bom,JsonSerializer.Serialize(new WorkbenchSettings{Offline=true},JsonFiles.GetTypeInfo<WorkbenchSettings>()),new System.Text.UTF8Encoding(true));
            check((await JsonFiles.ReadAsync<WorkbenchSettings>(bom)).Offline,"legacy UTF-8 BOM JSON files load without the 0xEF error");
            var echo=new Echo(); await using var workbench=new WorkbenchSession(root,transport:echo);
            await workbench.StartAsync(new(){Offline=true,Ip="213.242.114.213"}); await workbench.Completion;
            var offline=workbench.Snapshot();
            check(!offline.Running && offline.Updates.Length==1 && echo.Sends==0 && offline.Error is null,"C# workbench offline analysis completes without transport calls");
            await workbench.LoadAsync(offline.Directory!);
            check(workbench.Snapshot().Updates.Length==1,"C# saved session opens without Python or network");
            await workbench.StartAsync(new(){Rounds=2,BatchSize=6,Samples=1,MaxHops=2,TraceCount=1,Seconds=15});
            await workbench.Completion;
            var live=workbench.Snapshot();
            check(live.Error is null && !live.Running && live.Packets.Length==echo.Sends && live.Packets.All(p=>p.Status!="Sending"),"C# workbench records all native packet completions");
            check(live.Updates.Length>=4 && File.Exists(Path.Combine(live.Directory!,"session.json")),"C# progressive revisions and full session persist");
            int sends=echo.Sends; string original=live.Directory!;
            await workbench.ReanalyseAsync(new(){ResolutionKm=100});
            check(echo.Sends==sends && workbench.Snapshot().Updates.Last().Phase=="offline-replay","C# reanalysis uses the frozen session without sending packets");
            var loaded=await WorkbenchArchive.ReadAsync(original);
            check(loaded.Packets.Length==live.Packets.Length && loaded.Updates.Length==live.Updates.Length,"packet journal and revisions roundtrip");
            try{workbench.ResolveRun("../escape");throw new Exception("Path traversal accepted");}catch(ArgumentException){check(true,"saved session selection stays within its root");}
            await workbench.StartAsync(new(){Rounds=10});await workbench.StopAsync();int stopped=echo.Sends;
            await Task.Delay(150);check(!workbench.Snapshot().Running && echo.Sends==stopped,"C# stop awaits worker completion and prevents later sends");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private sealed class Echo:IEchoTransport
    {
        public int Sends;
        public Task<EchoResult> SendAsync(IPAddress address,int ttl,int timeoutMs,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();Interlocked.Increment(ref Sends);return Task.FromResult(ttl==1 ? new EchoResult("TtlExpired","213.242.114.213",10):new("Success",address.ToString(),200));}
    }
}
