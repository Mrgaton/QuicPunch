using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuicNet.VirtualDisk;
using QuicNet.WebUi;
using QuicNet.Settings;
using QuicNet.Protocols;
using QuicPunch;

namespace QuicPunchTests.Tests;

public static class VirtualDiskTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static DiskReply Ok(DiskReply reply) { Check(reply.Success, reply.Error ?? "Disk operation failed"); return reply; }
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "quicnet-disk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Console.WriteLine("Testing disk host permissions and transactions...");
            await ServiceTests(directory).WaitAsync(TimeSpan.FromSeconds(15));
            VirtualDiskDocumentsTests.Run(directory);
            Console.WriteLine("Testing duplex disk protocol...");
            await WireTests(directory).WaitAsync(TimeSpan.FromSeconds(15));
            Console.WriteLine("Testing local HTTP and WebDAV bridge...");
            await HttpTests(directory).WaitAsync(TimeSpan.FromSeconds(55));
            Console.WriteLine("PASS virtual disk: host ACLs, administrator scope, concurrent snapshots/writes, integrity, revocation, traversal, wire and HTTP/WebDAV");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task ServiceTests(string directory)
    {
        string root = Path.Combine(directory, "shared"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "first.txt"), "original");
        var trusted = new HashSet<string> { "friend", "other", "admin" };
        var admins = new HashSet<string> { "admin" };
        string storage = Path.Combine(directory, "acl");
        using var disk = new VirtualDiskService(storage, trusted.Contains, admins.Contains);
        Check(!disk.Execute("friend", new("explore", Path: root)).Success, "Friend explored host filesystem");
        Check(!disk.Execute("friend", new("grant", Path: root, Recipient: "friend")).Success, "Friend granted itself access");
        Check(Ok(disk.Execute("admin", new("explore"))).Entries!.Length > 0, "Admin cannot list drive roots");
        Check(Ok(disk.Execute("admin", new("explore", Path: root))).Entries!.Any(e => e.Name == "first.txt"), "Admin cannot explore");
        Guid adminShare = Ok(disk.Execute("admin", new("grant", Path: root, Recipient: "other"))).Shares![0].Id;
        Check(Ok(disk.Execute("admin", new("shares"))).Shares!.Single().Recipient == "admin", "Admin impersonated a grant recipient");
        admins.Clear(); Check(!disk.Execute("admin", new("list", ShareId: adminShare)).Success, "Admin grant survived downgrade"); admins.Add("admin");
        Guid readShare = Ok(disk.Execute(null, new("grant", Path: root, Recipient: "friend"))).Shares![0].Id;
        Check(Ok(disk.Execute("friend", new("shares"))).Shares!.All(s => s.Root == null), "Share disclosed host absolute paths");
        Check(!disk.Execute("other", new("list", ShareId: readShare)).Success, "Share leaked to a different certificate");
        foreach (string path in new[] { "../secret", "x/../../secret", "first.txt:stream", "..\\secret", root, "first.txt.", "first.txt ", ".quicnet-upload-test" })
            Check(!disk.Execute("friend", new("open-read", ShareId: readShare, Path: path)).Success, "Unsafe path accepted: " + path);
        Check(!disk.Execute("friend", new("begin-write", ShareId: readShare, Path: "new.txt", Length: 1)).Success, "Read-only share accepted a write");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "/d", "/c", "mklink", "/J", Path.Combine(root, "outside"), directory }) start.ArgumentList.Add(argument);
                using var junction = System.Diagnostics.Process.Start(start)!;
                await junction.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(junction.ExitCode == 0, "Could not create isolated junction fixture: " + await junction.StandardError.ReadToEndAsync());
            }
            else Directory.CreateSymbolicLink(Path.Combine(root, "outside"), directory);
            Check(!disk.Execute("friend", new("list", ShareId: readShare, Path: "outside")).Success, "Symlink escaped share");
            Check(!Ok(disk.Execute("friend", new("list", ShareId: readShare))).Entries!.Any(e => e.Name == "outside"), "Symlink appeared in listing");
            Directory.Delete(Path.Combine(root, "outside"));
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("SKIP symlink creation requires local OS privilege; traversal checks still ran."); }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 1314) { Console.WriteLine("SKIP symlink creation: Windows privilege unavailable."); }
        Guid share = Ok(disk.Execute(null, new("grant", Path: root, Recipient: "friend", Writable: true))).Shares![0].Id;
        var lease = Ok(disk.Execute("friend", new("lock", ShareId: share, Path: "first.txt")));
        Check(disk.Execute("friend", new("begin-write", ShareId: share, Path: "first.txt", Length: 0, Version: lease.Version)).Code == "Locked", "Browser writer bypassed native file lease");
        Guid overlapping = Ok(disk.Execute(null, new("grant", Path: root, Recipient: "other", Writable: true))).Shares![0].Id;
        Check(disk.Execute("other", new("begin-write", ShareId: overlapping, Path: "first.txt", Length: 0, Version: lease.Version)).Code == "Locked", "Another share bypassed a file lease");
        Check(!disk.Execute("other", new("unlock", ShareId: share, Path: "first.txt", LeaseId: lease.LeaseId)).Success, "Another identity released a lease");
        Ok(disk.Execute("friend", new("unlock", ShareId: share, Path: "first.txt", LeaseId: lease.LeaseId)));
        var read1 = Ok(disk.Execute("friend", new("open-read", ShareId: share, Path: "first.txt")));
        var read2 = Ok(disk.Execute("friend", new("open-read", ShareId: share, Path: "first.txt")));
        Check(Ok(disk.Execute("friend", new("read", TransferId: read2.TransferId))).Data!.SequenceEqual(Encoding.UTF8.GetBytes("original")), "Concurrent reader failed");
        Ok(disk.Execute("friend", new("close", TransferId: read2.TransferId)));
        var write1 = Ok(disk.Execute("friend", new("begin-write", ShareId: share, Path: "first.txt", Length: 7, Version: read1.Version)));
        var write2 = Ok(disk.Execute("friend", new("begin-write", ShareId: share, Path: "first.txt", Length: 7, Version: read1.Version)));
        Ok(disk.Execute("friend", new("write", TransferId: write1.TransferId, Data: Encoding.UTF8.GetBytes("changed"))));
        Ok(disk.Execute("friend", new("write", TransferId: write2.TransferId, Data: Encoding.UTF8.GetBytes("another"))));
        Check(File.ReadAllText(Path.Combine(root, "first.txt")) == "original", "Partial write became visible");
        Ok(disk.Execute("friend", new("commit", TransferId: write1.TransferId)));
        Check(disk.Execute("friend", new("commit", TransferId: write2.TransferId)).Code == "Conflict", "Stale writer silently overwrote content");
        Check(Encoding.UTF8.GetString(Ok(disk.Execute("friend", new("read", TransferId: read1.TransferId))).Data!) == "original", "Open reader mixed file versions");
        Ok(disk.Execute("friend", new("close", TransferId: read1.TransferId)));
        Check(!disk.Execute("friend", new("delete", ShareId: share, Path: "first.txt", Version: read1.Version)).Success, "Write implied permission to delete");
        var partial = Ok(disk.Execute("friend", new("begin-write", ShareId: share, Path: "incomplete", Length: 10)));
        Check(!disk.Execute("friend", new("commit", TransferId: partial.TransferId)).Success && !File.Exists(Path.Combine(root, "incomplete")), "Incomplete upload was published");
        var corrupt = Ok(disk.Execute("friend", new("begin-write", ShareId: share, Path: "corrupt", Length: 1)));
        Ok(disk.Execute("friend", new("write", TransferId: corrupt.TransferId, Data: [42])));
        Check(!disk.Execute("friend", new("commit", TransferId: corrupt.TransferId, Sha256: "wrong")).Success, "Integrity mismatch was accepted");
        Check(!disk.Execute("friend", new("begin-write", ShareId: share, Path: "huge", Length: VirtualDiskService.MaxFileBytes + 1)).Success, "Unbounded upload accepted");
        var revoked = Ok(disk.Execute("friend", new("open-read", ShareId: share, Path: "first.txt")));
        Check(!disk.Execute("other", new("read", TransferId: revoked.TransferId)).Success, "Transfer stolen by another identity");
        Ok(disk.Execute(null, new("revoke", ShareId: share)));
        Check(!disk.Execute("friend", new("read", TransferId: revoked.TransferId)).Success, "Open transfer retained revoked permission");
        Guid expired = Ok(disk.Execute(null, new("grant", Path: root, Recipient: "friend", ExpiresUtc: DateTime.UtcNow.AddMilliseconds(150)))).Shares![0].Id;
        await Task.Delay(200);
        Check(!disk.Execute("friend", new("list", ShareId: expired)).Success, "Expired grant remained usable");
        using (var reload = new VirtualDiskService(storage, trusted.Contains, admins.Contains))
            Check(Ok(reload.Execute("friend", new("shares"))).Shares!.Any(s => s.Id == readShare), "Share did not persist");
        trusted.Remove("friend"); Check(!disk.Execute("friend", new("list", ShareId: readShare)).Success, "Untrusted contact kept filesystem access");
        Check(!Directory.EnumerateFiles(root, ".quicnet-upload-*").Any(), "Temporary uploads leaked after failure/revocation");
        Check(Ok(disk.Execute(null, new("audit"))).Audit!.Any(a => !a.Success), "Denied accesses were not recorded");
        string crashed = Path.Combine(root, ".quicnet-upload-" + Guid.NewGuid().ToString("N"));
        string preserve = Path.Combine(root, "must-not-delete.txt");
        File.WriteAllText(crashed, "incomplete"); File.WriteAllText(preserve, "keep");
        string recoveryStore = Path.Combine(directory, "recovery"); Directory.CreateDirectory(recoveryStore);
        File.WriteAllText(Path.Combine(recoveryStore, "virtual-disk-uploads.json"), JsonSerializer.Serialize(new[] { crashed, preserve }));
        using (var recovered = new VirtualDiskService(recoveryStore, _ => false, _ => false))
            Check(!File.Exists(crashed) && File.Exists(preserve), "Crash cleanup deleted an unrelated file or retained a partial upload");
    }

    private static async Task WireTests(string directory)
    {
        using var first = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        using var second = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        string a = Convert.ToBase64String(first.CertHash), b = Convert.ToBase64String(second.CertHash);
        using var serviceA = new VirtualDiskService(Path.Combine(directory, "wire-a"), h => h == b, h => h == b);
        using var serviceB = new VirtualDiskService(Path.Combine(directory, "wire-b"), h => h == a, _ => false);
        using var handlerA = new VirtualDiskHandler(serviceA); using var handlerB = new VirtualDiskHandler(serviceB);
        string pipe = "quicnet-disk-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(server.WaitForConnectionAsync(lifetime.Token), client.ConnectAsync(lifetime.Token));
        Console.WriteLine("Duplex pipe connected.");
        var ta = handlerA.HandleAsync(null!, server, second, lifetime.Token);
        var tb = handlerB.HandleAsync(null!, client, first, lifetime.Token);
        Console.WriteLine("Disk protocol readers started.");
        var replies = await Task.WhenAll(handlerA.RequestAsync(second.Id, new("shares"), lifetime.Token), handlerB.RequestAsync(first.Id, new("explore", Path: directory), lifetime.Token));
        Console.WriteLine("Simultaneous disk replies received.");
        Check(Ok(replies[1]).CanExplore, "Authenticated wire identity lost administrator authority");
        Check(!Ok(replies[0]).CanExplore, "Wire request invented administrator authority");
        var denied = await handlerA.RequestAsync(second.Id, new("grant", Path: directory, Recipient: a));
        Check(!denied.Success, "Remote payload overrode transport ACLs");
        Console.WriteLine("Closing duplex disk session.");
        lifetime.Cancel(); await Task.WhenAll(ta, tb);
    }

    private static async Task HttpTests(string directory)
    {
        string storage = Path.Combine(directory, "http"); string root = Path.Combine(directory, "http-files"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "MixedCase.txt"), "hello");
        using var qcc = new QuicPunch.QuicPunch(appDataPath: storage);
        using var peer = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32)); qcc.TrustPeer(peer.CertHash);
        var chat = new ChatHandler(); using var voice = new VoiceCallHandler(qcc); using var speed = new SpeedTestHandler();
        using var cts = new CancellationTokenSource();
        var preferences = new AppPreferencesStore(Path.Combine(storage, "ui.json")); preferences.Update(p => p.Notifications.Enabled = false);
        var ui = new WebUiServer(qcc, chat, null, voice, speed, preferences, cts, port: 18500); ui.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(ui.Url), Timeout = TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("QUICNET_TEST_NATIVE_DRIVE") == "1" ? 28 : 8) };
            async Task<DiskReply> Post(string endpoint, object body)
            {
                using var response = await http.PostAsJsonAsync("api/virtual-disk/" + endpoint, body);
                response.EnsureSuccessStatusCode(); return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), DiskJsonContext.Default.DiskReply)!;
            }
            using (var denied = await http.PostAsJsonAsync("api/virtual-disk/operation", new { request = new DiskRequest("shares") }))
                Check(denied.StatusCode == HttpStatusCode.Forbidden, "Filesystem API bypassed CSRF");
            http.DefaultRequestHeaders.Add("X-QuicPunch-CSRF", ui.CsrfToken);
            var granted = Ok(await Post("operation", new { request = new DiskRequest("grant", Path: root, Recipient: Convert.ToBase64String(peer.CertHash), Writable: true) }));
            Guid share = granted.Shares![0].Id;
            var mounted = Ok(await Post("mount", new { shareId = share }));
            string dav = mounted.MountUrl!;
            using (var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), dav))
            {
                request.Headers.Add("Depth", "1"); using var listing = await http.SendAsync(request);
                Check((int)listing.StatusCode == 207 && (await listing.Content.ReadAsStringAsync()).Contains("MixedCase.txt"), "Native directory listing lost case or files");
            }
            using var read = await http.GetAsync(dav + "MixedCase.txt");
            Check(await read.Content.ReadAsStringAsync() == "hello", "WebDAV did not read shared file");
            string version = read.Headers.ETag!.Tag;
            if (Environment.GetEnvironmentVariable("QUICNET_TEST_NATIVE_DRIVE") == "1")
            {
                var native = Ok(await Post("mount", new { shareId = share, native = true }));
                try
                {
                    if (native.Code == "Mounted")
                    {
                        Check(File.ReadAllText(Path.Combine(native.NativePath!, "MixedCase.txt")) == "hello", "Native mounted drive lost content");
                        File.WriteAllText(Path.Combine(native.NativePath!, "NativeCreated.txt"), "native content");
                        for (int i = 0; i < 50 && (!File.Exists(Path.Combine(root, "NativeCreated.txt")) || File.ReadAllText(Path.Combine(root, "NativeCreated.txt")) != "native content"); i++) await Task.Delay(100);
                        Check(File.ReadAllText(Path.Combine(root, "NativeCreated.txt")) == "native content", "Native drive creation did not reach the host: " + string.Join("; ", WebUiServer.EventLogs.Where(l => l.Contains("DISK DAV"))));
                        File.WriteAllText(Path.Combine(native.NativePath!, "NativeCreated.txt"), "native updated");
                        for (int i = 0; i < 50 && File.ReadAllText(Path.Combine(root, "NativeCreated.txt")) != "native updated"; i++) await Task.Delay(100);
                        Check(File.ReadAllText(Path.Combine(root, "NativeCreated.txt")) == "native updated", "Native drive could not replace an existing file");
                        Console.WriteLine("PASS Windows native drive mapping, file read, creation and replacement with host-side leases.");
                    }
                    else Console.WriteLine("[SKIP] Windows WebDAV redirector could not mount; local HTTP WebDAV tests still ran.");
                }
                finally
                {
                    Ok(await Post("unmount", new { url = native.MountUrl }));
                    Check(QuicNet.WebUi.Modules.VirtualDiskWebDav.FindWindowsMapping(native.NativePath!) == null, "Temporary Windows drive mapping was not removed");
                }
                dav = Ok(await Post("mount", new { shareId = share })).MountUrl!;
            }
            using (var create = await http.PutAsync(dav + "new.txt", new StringContent("created"))) Check(create.StatusCode == HttpStatusCode.Created, "WebDAV could not create file");
            using (var overwrite = await http.PutAsync(dav + "MixedCase.txt", new StringContent("unsafe"))) Check(overwrite.StatusCode == HttpStatusCode.PreconditionFailed, "Unconditional overwrite accepted");
            using (var request = new HttpRequestMessage(HttpMethod.Put, dav + "MixedCase.txt"))
            {
                request.Headers.TryAddWithoutValidation("If-Match", version); request.Content = new StringContent("updated");
                using var result = await http.SendAsync(request); Check(result.StatusCode == HttpStatusCode.NoContent, "Versioned WebDAV write failed");
            }
            using (var foreign = new HttpRequestMessage(HttpMethod.Get, dav + "MixedCase.txt"))
            { foreign.Headers.Add("Origin", "https://example.invalid"); using var result = await http.SendAsync(foreign); Check(result.StatusCode == HttpStatusCode.Forbidden, "Native bridge bypassed origin check"); }
            var documents = new VirtualDiskDocumentsTests.Documents();
            ui.DiskDocuments = documents;
            Check(Ok(await Post("pick-folder", new { })).NativePath == VirtualDiskDocumentsTests.Documents.Root, "Native picker result lost");
            Ok(await Post("save", new { shareId = share, path = "MixedCase.txt" }));
            Check(SpinWait.SpinUntil(() => documents.LastDownload?.Disposed == true, 1000), "Successful save did not dispose its destination");
            Check(documents.LastDownload is { Completed: true, Disposed: true } && Encoding.UTF8.GetString(documents.LastDownload.Buffer.ToArray()) == "updated", "Native save did not verify and finish download");
            Check(!(await Post("save", new { shareId = Guid.NewGuid(), path = "missing" })).Success, "Missing file was reported as saved");
            Check(SpinWait.SpinUntil(() => documents.LastDownload?.Disposed == true, 1000) && documents.LastDownload is { Completed: false }, "Failed native save kept partial destination");
            documents.CancelDownload = true;
            Check((await Post("save", new { shareId = share, path = "MixedCase.txt" })).Code == "Cancelled", "Cancelled picker started download");
            Ok(await Post("operation", new { request = new DiskRequest("revoke", ShareId: share) }));
            using var revoked = await http.GetAsync(dav + "MixedCase.txt"); Check(revoked.StatusCode == HttpStatusCode.Forbidden, "Mounted share retained revoked access");
            Check((await http.GetStringAsync("virtual-disk.html")).Contains("id=\"view-virtual-disk\""), "Virtual disk route was not served");
        }
        finally { ui.Stop(); cts.Cancel(); }
    }
}
