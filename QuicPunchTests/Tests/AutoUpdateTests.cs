using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuicNet.Updates;

namespace QuicPunchTests.Tests;

internal static class AutoUpdateTests
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        await CompressedUpdateTests.RunAsync();
        using var rsa = RSA.Create(2048);
        string publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        byte[] binary = Enumerable.Range(0, 150000).Select(i => (byte)i).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(binary));
        var feed = new Uri(ReleaseIdentity.FeedUrl);
        byte[] Envelope(long build, string? url = null, string? digest = null, string? channel = null)
        {
            var content = new Dictionary<string, object> { ["schema"] = 1, ["version"] = "9.2.1", ["build"] = build,
                ["artifacts"] = new Dictionary<string, object> { ["win-x64"] = new { url = url ?? "releases/9.2.1-99/QuicNet.exe", sha256 = digest ?? hash, size = binary.Length } } };
            if (channel != null) content["channel"] = channel;
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(content);
            return JsonSerializer.SerializeToUtf8Bytes(new { payload = Convert.ToBase64String(payload),
                signature = Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) });
        }
        var artifact = UpdateManifest.Parse(Envelope(ReleaseIdentity.Build + 1), feed, "win-x64", publicKey);
        Require(artifact.Size == binary.Length && artifact.Url.Scheme == "https", "Valid signed manifest rejected");
        Require(artifact.Channel == "stable", "Legacy manifests must stay on stable");
        var betaFeed = new Uri(ReleaseIdentity.GetFeedUrl("beta"));
        Require(UpdateManifest.Parse(Envelope(99, channel: "beta"), betaFeed, "win-x64", publicKey, "beta").Channel == "beta", "Beta manifest rejected");
        Reject(() => UpdateManifest.Parse(Envelope(99, channel: "beta"), feed, "win-x64", publicKey), "Stable accepted a beta manifest");
        Reject(() => UpdateManifest.Parse(Envelope(99), betaFeed, "win-x64", publicKey, "beta"), "Beta accepted a legacy stable manifest");
        Reject(() => UpdateManifest.Parse(Envelope(99, channel: "nightly"), feed, "win-x64", publicKey), "Unknown channel accepted");
        using (var wrong = RSA.Create(2048))
            Reject(() => UpdateManifest.Parse(Envelope(99), feed, "win-x64", wrong.ExportSubjectPublicKeyInfoPem()), "Wrong signing key accepted");
        foreach (string url in new[] { "http://cdn.gato.ovh/QuicNet/evil.exe", "https://evil.test/QuicNet/evil.exe",
            "../evil.exe", "/outside/evil.exe", "releases/update.exe?redirect=evil", "releases/evil.apk", "releases/%2e%2e%2f%2e%2e%2fevil.exe" })
            Reject(() => UpdateManifest.Parse(Envelope(99, url), feed, "win-x64", publicKey), "Unsafe URL accepted: " + url);
        Reject(() => UpdateManifest.Parse(Envelope(99, digest: "abc"), feed, "win-x64", publicKey), "Invalid SHA accepted");
        var envelopeObject = JsonDocument.Parse(Envelope(99)).RootElement;
        byte[] tampered = JsonSerializer.SerializeToUtf8Bytes(new { payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")),
            signature = envelopeObject.GetProperty("signature").GetString() });
        Reject(() => UpdateManifest.Parse(tampered, feed, "win-x64", publicKey), "Modified payload accepted");
        string directory = Path.Combine(Path.GetTempPath(), "QuicNet-updates-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "package.exe");
            await File.WriteAllBytesAsync(file, binary);
            await UpdateManifest.VerifyFileAsync(file, artifact);
            binary[8] ^= 1;
            await File.WriteAllBytesAsync(file, binary);
            try { await UpdateManifest.VerifyFileAsync(file, artifact); throw new Exception("Corrupt file accepted"); }
            catch (InvalidDataException) { }
            binary[8] ^= 1;
            var handler = new FakeHandler(Envelope(ReleaseIdentity.Build + 1), binary);
            int installs = 0;
            using (var updater = new AutoUpdateService(directory, "win-x64", async (path, release, automatic) =>
            {
                await UpdateManifest.VerifyFileAsync(path, release);
                Require(!File.Exists(path + ".partial"), "Partial download exposed to installer");
                installs++; return true;
            }, handler, publicKey))
            {
                await updater.CheckAsync();
                Require(updater.Status.Stage == "available" && installs == 0, "Manual check installed automatically");
                await updater.InstallAsync();
                Require(updater.Status.Stage == "installing" && installs == 1 && handler.Downloads == 1, "Verified update did not install");
                try { await updater.SetChannelAsync("beta"); throw new Exception("Channel changed during installation"); }
                catch (InvalidOperationException) { }
                Require(updater.Status.Channel == "stable", "Installing channel was modified");
                updater.SetAutomatic(false);
            }
            using (var updater = new AutoUpdateService(directory, "win-x64", (_, _, _) => Task.FromResult(false), new FakeHandler(Envelope(99), binary), publicKey))
                Require(!updater.Status.Automatic, "Automatic preference was not persisted");
            var downgrade = new FakeHandler(Envelope(ReleaseIdentity.Build), binary);
            using (var updater = new AutoUpdateService(Path.Combine(directory, "old"), "win-x64",
                (_, _, _) => throw new Exception("Downgrade installer invoked"), downgrade, publicKey))
            {
                await updater.CheckAsync(true);
                Require(updater.Status.Stage == "current" && downgrade.Downloads == 0, "Downgrade was downloaded");
            }
            string failedDirectory = Path.Combine(directory, "failed");
            Directory.CreateDirectory(Path.Combine(failedDirectory, "updates"));
            File.WriteAllText(Path.Combine(failedDirectory, "updates", "release-" + (ReleaseIdentity.Build + 1) + ".exe.failed"), "fixture");
            var failedHandler = new FakeHandler(Envelope(ReleaseIdentity.Build + 1), binary);
            using (var updater = new AutoUpdateService(failedDirectory, "win-x64", (_, _, _) => Task.FromResult(true), failedHandler, publicKey))
            {
                await updater.CheckAsync(true);
                Require(updater.Status.Stage == "error" && failedHandler.Downloads == 0, "Failed release was installed automatically again");
                await updater.InstallAsync();
                Require(updater.Status.Stage == "installing", "An explicit retry of a failed release was blocked");
            }
            byte[] corrupted = (byte[])binary.Clone(); corrupted[99] ^= 1;
            using (var updater = new AutoUpdateService(Path.Combine(directory, "corrupt"), "win-x64",
                (_, _, _) => throw new Exception("Corrupt installer invoked"), new FakeHandler(Envelope(ReleaseIdentity.Build + 2), corrupted), publicKey))
            {
                await updater.CheckAsync(true);
                Require(updater.Status.Stage == "error" && updater.ReadyPath == null, "Corrupt update was staged");
                Require(!Directory.EnumerateFiles(Path.Combine(directory, "corrupt"), "*.partial", SearchOption.AllDirectories).Any(), "Partial file leaked");
            }
            using (var updater = new AutoUpdateService(Path.Combine(directory, "offline"), "win-x64",
                (_, _, _) => throw new Exception("Offline installer invoked"), new FakeHandler([], binary) { Offline = true }, publicKey))
            {
                await updater.CheckAsync(true);
                Require(updater.Status.Stage == "error", "Offline failure not shown");
            }
            // A valid signature does not make a redirect or oversized response acceptable.
            using (var updater = new AutoUpdateService(Path.Combine(directory, "redirect"), "win-x64",
                (_, _, _) => throw new Exception("Redirect installer invoked"), new FakeHandler(Envelope(99), binary) { Redirect = true }, publicKey))
            {
                await updater.CheckAsync(true);
                Require(updater.Status.Stage == "error", "Redirect accepted");
            }
            string channelDirectory = Path.Combine(directory, "channels");
            var channels = new FakeHandler(Envelope(ReleaseIdentity.Build + 1, channel: "stable"), binary)
            { BetaManifest = Envelope(ReleaseIdentity.Build + 2, channel: "beta") };
            using (var updater = new AutoUpdateService(channelDirectory, "win-x64", (_, _, _) => Task.FromResult(false), channels, publicKey))
            {
                Require(updater.Status.Channel == ReleaseIdentity.Channel, "Build channel default ignored");
                await updater.SetChannelAsync("stable");
                await updater.CheckAsync();
                Require(channels.LastFeed == "/QuicNet/latest.json", "Stable used the wrong feed");
                await updater.InstallAsync();
                Require(updater.ReadyPath != null, "Stable fixture was not staged");
                await updater.SetChannelAsync("beta");
                Require(updater.ReadyPath == null && updater.Status.LatestBuild == null, "Switch left a stable package ready to install");
                await updater.InstallAsync();
                Require(channels.Downloads == 1 && updater.ReadyPath == null, "Installed stale package after channel switch");
                await updater.CheckAsync();
                Require(channels.Feeds.Contains("/QuicNet/latest.json") && channels.Feeds.Contains("/QuicNet/latest-beta.json") && updater.Status.LatestBuild == ReleaseIdentity.Build + 2, "Beta used the wrong feeds");
                await updater.InstallAsync();
                Require(updater.ReadyPath != null && updater.Status.Channel == "beta", "Beta did not download");
                try { await updater.SetChannelAsync("nightly"); throw new Exception("Unknown channel was saved"); }
                catch (ArgumentException) { }
                await updater.SetChannelAsync("beta");
                Require(updater.ReadyPath != null, "Selecting the same channel discarded a ready package");
            }
            using (var updater = new AutoUpdateService(channelDirectory, "win-x64", (_, _, _) => Task.FromResult(false), channels, publicKey))
            {
                Require(updater.Status.Channel == "beta", "Channel preference did not survive restart");
                await updater.SetChannelAsync("stable");
                channels.Manifest = Envelope(ReleaseIdentity.Build, channel: "stable");
                int before = channels.Downloads;
                await updater.CheckAsync(true);
                Require(updater.Status.Stage == "current" && channels.Downloads == before, "Returning to stable caused a downgrade");
                await updater.SetChannelAsync("beta");
                channels.BetaManifest = Envelope(ReleaseIdentity.Build + 3, channel: "stable");
                await updater.CheckAsync(true);
                Require(updater.Status.Stage == "current" && updater.Status.LatestChannel == "stable" && channels.Downloads == before, "Invalid beta manifest was not isolated from the valid stable feed");
            }
            using (var updater = new AutoUpdateService(Path.Combine(directory, "empty-beta"), "win-x64", (_, _, _) => Task.FromResult(false),
                new FakeHandler([], binary) { Missing = true }, publicKey))
            {
                await updater.SetChannelAsync("beta"); await updater.CheckAsync(true);
                Require(updater.Status.Stage == "current" && updater.Status.LatestBuild == null, "Empty beta feed was not handled");
            }
            async Task Audience(string name, string channel, long stableBuild, long betaBuild, string expected, bool automatic = true, bool missingBeta = false, bool stableOffline = false)
            {
                var feeds = new FakeHandler(Envelope(stableBuild, channel: "stable"), binary)
                { BetaManifest = Envelope(betaBuild, channel: "beta"), MissingBeta = missingBeta, StableOffline = stableOffline };
                int installed = 0;
                using var updater = new AutoUpdateService(Path.Combine(directory, name), "win-x64", (_, release, isAutomatic) =>
                {
                    Require(release.Channel == expected && release.Build == (expected == "stable" ? stableBuild : betaBuild) && isAutomatic,
                        "Installed the wrong audience, build or automatic mode: " + name);
                    installed++; return Task.FromResult(true);
                }, feeds, publicKey);
                Require(File.ReadAllText(Path.Combine(directory, name, "updates", "channel.txt")) == ReleaseIdentity.Channel, "Default enrollment was not persisted");
                await updater.SetChannelAsync(channel); updater.SetAutomatic(automatic);
                await updater.CheckAsync(true);
                Require(updater.Status.LatestChannel == expected && updater.Status.Channel == channel, "A stable promotion changed beta enrollment: " + name);
                Require(installed == (automatic ? 1 : 0) && feeds.Downloads == (automatic ? 1 : 0), "Automatic preference ignored: " + name);
                if (channel == "stable") Require(!feeds.Feeds.Contains("/QuicNet/latest-beta.json"), "Stable queried beta");
                else Require(feeds.Feeds.Contains("/QuicNet/latest.json") && feeds.Feeds.Contains("/QuicNet/latest-beta.json"), "Beta did not query both channels");
                Require(File.ReadAllText(Path.Combine(directory, name, "updates", "channel.txt")) == channel, "Installing a release lost channel enrollment");
            }
            await Audience("stable-only", "stable", ReleaseIdentity.Build + 5, ReleaseIdentity.Build + 6, "stable");
            await Audience("beta-newer", "beta", ReleaseIdentity.Build + 5, ReleaseIdentity.Build + 6, "beta");
            await Audience("release-newer", "beta", ReleaseIdentity.Build + 7, ReleaseIdentity.Build + 6, "stable");
            await Audience("release-tie", "beta", ReleaseIdentity.Build + 7, ReleaseIdentity.Build + 7, "stable");
            await Audience("release-no-beta", "beta", ReleaseIdentity.Build + 7, ReleaseIdentity.Build + 8, "stable", missingBeta: true);
            await Audience("beta-no-stable", "beta", ReleaseIdentity.Build + 7, ReleaseIdentity.Build + 8, "beta", stableOffline: true);
            await Audience("beta-manual", "beta", ReleaseIdentity.Build + 9, ReleaseIdentity.Build + 8, "stable", automatic: false);
            var pendingFeeds = new FakeHandler(Envelope(ReleaseIdentity.Build + 5, channel: "stable"), binary)
            { BetaManifest = Envelope(ReleaseIdentity.Build + 6, channel: "beta") };
            using (var updater = new AutoUpdateService(Path.Combine(directory, "pending-promotion"), "win-x64", (_, _, _) => Task.FromResult(false), pendingFeeds, publicKey))
            {
                await updater.SetChannelAsync("beta"); await updater.CheckAsync(true);
                Require(updater.ReadyPath != null && updater.Status.LatestChannel == "beta", "Pending beta was not prepared");
                pendingFeeds.Manifest = Envelope(ReleaseIdentity.Build + 7, channel: "stable");
                await updater.CheckAsync(true);
                Require(updater.Status.LatestChannel == "stable" && updater.Status.LatestBuild == ReleaseIdentity.Build + 7 && pendingFeeds.Downloads == 2,
                    "A pending beta prevented a newer stable release");
                pendingFeeds.Offline = true; string? ready = updater.ReadyPath;
                await updater.CheckAsync();
                Require(updater.ReadyPath == ready && updater.Status.Stage == "ready", "Offline feed discarded a verified release");
            }
            await TestChannelApiAsync(Path.Combine(directory, "api"), publicKey, channels);
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Signed manifests, channel isolation/persistence/switching, no downgrade, SHA/size, atomic downloads and network failure passed.");
    }

    private static async Task TestChannelApiAsync(string directory, string publicKey, FakeHandler handler)
    {
        using var node = new QuicPunch.QuicPunch(appDataPath: directory);
        using var voice = new QuicNet.Protocols.VoiceCallHandler(node);
        using var speed = new QuicNet.Protocols.SpeedTestHandler();
        using var cts = new CancellationTokenSource();
        using var updater = new AutoUpdateService(directory, "win-x64", (_, _, _) => Task.FromResult(false), handler, publicKey);
        var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var server = new QuicNet.WebUi.WebUiServer(node, new QuicNet.Protocols.ChatHandler(), null, voice, speed,
            new QuicNet.Settings.AppPreferencesStore(Path.Combine(directory, "settings.json")), cts, port: port) { Updates = updater };
        server.Start();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
            async Task<HttpResponseMessage> Post(string json) => await client.PostAsync("api/updates/channel", new StringContent(json, Encoding.UTF8, "application/json"));
            using (var denied = await Post("{\"channel\":\"beta\"}")) Require(denied.StatusCode == HttpStatusCode.Forbidden, "Channel API bypassed CSRF");
            client.DefaultRequestHeaders.Add("X-QuicPunch-CSRF", server.CsrfToken);
            foreach (string invalid in new[] { "{}", "[]", "{\"channel\":1}", "{\"channel\":\"nightly\"}" })
                using (var response = await Post(invalid)) Require(response.StatusCode == HttpStatusCode.BadRequest, "Invalid channel API request accepted");
            using (var changed = await Post("{\"channel\":\"beta\"}")) Require(changed.IsSuccessStatusCode, "Channel API switch failed");
            using var status = JsonDocument.Parse(await client.GetStringAsync("api/updates"));
            Require(status.RootElement.GetProperty("Channel").GetString() == "beta", "Channel API status did not persist");
            await TestUpdateSocketAsync(server, updater);
        }
        finally { server.Stop(); cts.Cancel(); }
    }

    private static async Task TestUpdateSocketAsync(QuicNet.WebUi.WebUiServer server, AutoUpdateService updater)
    {
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        var uri = new Uri(server.Url.Replace("http:", "ws:") + "ws?csrf=" + server.CsrfToken);
        await socket.ConnectAsync(uri, CancellationToken.None);
        async Task<JsonElement> Receive(Func<JsonElement, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            byte[] buffer = new byte[8192];
            while (true)
            {
                using var message = new MemoryStream();
                System.Net.WebSockets.WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    Require(result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text, "Unexpected socket frame");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                if (predicate(json.RootElement)) return json.RootElement.Clone();
            }
        }
        async Task<JsonElement> Command(string id, string command, object data)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "request", id, command, data }));
            await socket.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            return await Receive(root => root.GetProperty("type").GetString() == "response" && root.GetProperty("id").GetString() == id);
        }
        await Receive(root => root.GetProperty("type").GetString() == "status_updated");
        var initial = await Receive(root => root.GetProperty("type").GetString() == "update_status");
        Require(initial.GetProperty("data").GetProperty("Channel").GetString() == "beta", "Socket did not replay updater state");
        var general = await Command("status", "status.get", new { });
        Require(general.GetProperty("success").GetBoolean() && general.GetProperty("data").TryGetProperty("peers", out _), "Socket status request failed");
        var invalid = await Command("bad", "updates.channel", new { channel = "nightly" });
        Require(!invalid.GetProperty("success").GetBoolean(), "Socket accepted an invalid channel");
        var unknown = await Command("unknown", "delete-files", new { });
        Require(!unknown.GetProperty("success").GetBoolean(), "Socket accepted an unknown command");
        var changed = await Command("automatic", "updates.automatic", new { enabled = false });
        Require(changed.GetProperty("success").GetBoolean() && !updater.Status.Automatic, "Socket automatic preference failed");
        for (int i = 0; i < 20; i++) updater.SetAutomatic(i % 2 == 0);
        long revision = updater.Status.Revision;
        int frames = 0;
        var pushed = await Receive(root => { frames++; return root.GetProperty("type").GetString() == "update_status" && root.GetProperty("data").GetProperty("Revision").GetInt64() >= revision; });
        Require(!pushed.GetProperty("data").GetProperty("Automatic").GetBoolean() && frames < 10, "Update push lost the latest state or queued every progress change");
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "reconnect", CancellationToken.None);
        using var reconnect = new System.Net.WebSockets.ClientWebSocket();
        await reconnect.ConnectAsync(uri, CancellationToken.None);
        // The complete snapshot is sent automatically; no sync/HTTP request is needed.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var receiveBuffer = new byte[128 * 1024];
        bool replayed = false;
        while (!replayed)
        {
            var received = await reconnect.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), deadline.Token);
            Require(received.EndOfMessage, "Replay unexpectedly exceeded test buffer");
            using var json = JsonDocument.Parse(receiveBuffer.AsMemory(0, received.Count));
            replayed = json.RootElement.GetProperty("type").GetString() == "update_status" &&
                json.RootElement.GetProperty("data").GetProperty("Revision").GetInt64() >= revision;
        }
        await reconnect.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Console.WriteLine("PASS update/status WebSocket: initial snapshot, commands, validation, coalesced live push and reconnect replay");
    }

    private sealed class FakeHandler(byte[] manifest, byte[] binary) : HttpMessageHandler
    {
        public int Downloads;
        public byte[] Manifest = manifest;
        public byte[]? BetaManifest;
        public string? LastFeed;
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Feeds = new();
        public bool Offline, Redirect, Missing, MissingBeta, StableOffline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Offline) throw new HttpRequestException("Offline test");
            if (Redirect) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect));
            if (Missing) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            bool beta = request.RequestUri!.AbsolutePath.EndsWith("/latest-beta.json");
            bool isManifest = beta || request.RequestUri.AbsolutePath.EndsWith("/latest.json");
            if (isManifest) { LastFeed = request.RequestUri.AbsolutePath; Feeds.Add(LastFeed); }
            if (isManifest && !beta && StableOffline) throw new HttpRequestException("Stable feed offline test");
            if (beta && MissingBeta) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (isManifest) { Require(request.RequestUri.Query.StartsWith("?check="), "Manifest requests must avoid immutable CDN cache"); Require(request.Headers.CacheControl == null, "Cache-Control triggers a CDN challenge"); }
            if (!isManifest) Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(isManifest ? (beta ? BetaManifest ?? Manifest : Manifest) : binary) });
        }
    }
}
