using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using QuicNet.Updates;

namespace QuicPunchTests.Tests;

internal static class CompressedUpdateTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private sealed class Downloads(byte[] manifest, byte[] compressed) : HttpMessageHandler
    {
        internal int PackedRequests, RawRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            bool feed=request.RequestUri!.AbsolutePath.EndsWith(".json");
            if(!feed) { if(request.RequestUri.AbsolutePath.EndsWith(".zst")) PackedRequests++; else RawRequests++; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(feed?manifest:compressed)});
        }
    }
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"QuicNet-zstd-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string input=Path.Combine(root,"input"),packed=Path.Combine(root,"input.zst"),output=Path.Combine(root,"output");
            byte[] bytes=Enumerable.Range(0,150000).Select(i=>(byte)(i%173)).ToArray();
            await File.WriteAllBytesAsync(input,bytes);
            await UpdateCompression.CompressAsync(input,packed);
            await UpdateCompression.DecompressAsync(packed,output,bytes.Length);
            Check((await File.ReadAllBytesAsync(output)).AsSpan().SequenceEqual(bytes),"High-window Zstandard round-trip failed");
            try{await UpdateCompression.DecompressAsync(packed,output,bytes.Length-1);throw new Exception("Decompression exceeded signed size");}catch(InvalidDataException){}
            try{await UpdateCompression.DecompressAsync(packed,output,bytes.Length+1);throw new Exception("Truncated output accepted");}catch(InvalidDataException){}
            byte[] compressed=await File.ReadAllBytesAsync(packed);
            using var rsa=RSA.Create(2048);string key=rsa.ExportSubjectPublicKeyInfoPem();
            string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            byte[] Manifest(string platform,string? compressedHash=null,string? rawHash=null,int window=27,string? compressedUrl=null)
            {
                string url="releases/test/app"+UpdateManifest.PlatformExtension(platform);
                var payload=JsonSerializer.SerializeToUtf8Bytes(new {schema=1,channel="stable",version="9.9.9",build=ReleaseIdentity.Build+100,
                    artifacts=new Dictionary<string,object>{[platform]=new{url,sha256=rawHash??Hash(bytes),size=bytes.Length,compressed=new{algorithm="zstd",url=compressedUrl??url+".zst",sha256=compressedHash??Hash(compressed),size=compressed.Length,windowLog=window}}}});
                return JsonSerializer.SerializeToUtf8Bytes(new{payload=Convert.ToBase64String(payload),signature=Convert.ToBase64String(rsa.SignData(payload,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))});
            }
            foreach(string platform in new[]{"win-x64","android-arm64","linux-x64"})
            {
                int installs=0;
                var handler=new Downloads(Manifest(platform),compressed);
                using var updater=new AutoUpdateService(Path.Combine(root,platform),platform,async(path,artifact,automatic)=>
                {await UpdateManifest.VerifyFileAsync(path,artifact);Check((await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(bytes),"Installer received compressed data");installs++;return true;},handler,key);
                await updater.CheckAsync();await updater.InstallAsync();
                Check(installs==1&&handler.PackedRequests==1&&handler.RawRequests==0,"Updater did not choose verified Zstandard: "+updater.Status.Message);
                Check(!Directory.GetFiles(Path.Combine(root,platform,"updates"),"*.partial*").Any(),"Temporary compressed file leaked");
            }
            foreach(var manifest in new[]{Manifest("win-x64",compressedHash:new string('0',64)),Manifest("win-x64",rawHash:new string('0',64))})
            {
                int installs=0;
                using var updater=new AutoUpdateService(Path.Combine(root,Guid.NewGuid().ToString("N")),"win-x64",(_,_,_)=>{installs++;return Task.FromResult(true);},new Downloads(manifest,compressed),key);
                await updater.CheckAsync();await updater.InstallAsync();Check(installs==0&&updater.Status.Stage=="error"&&updater.ReadyPath==null,"Corrupt compressed/raw data reached installer");
            }
            foreach(var manifest in new[]{Manifest("win-x64",window:28),Manifest("win-x64",compressedUrl:"https://evil.test/app.exe.zst")})
                try{UpdateManifest.Parse(manifest,new Uri(ReleaseIdentity.FeedUrl),"win-x64",key);throw new Exception("Unsafe compressed metadata accepted");}catch(InvalidDataException){}
        }
        finally{Directory.Delete(root,true);}
    }
}
