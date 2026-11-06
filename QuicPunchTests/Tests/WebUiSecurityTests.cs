using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Helpers;
using QuicNet.Protocols;
using QuicNet.Settings;
using QuicNet.WebUi;
using QuicNet.Vpn;
using QuicNet.Watch.Protocols;
using QuicNet.Watch.Domain;
using QuicNet.Watch.Services;
using QuicNet.Watch.Web;

namespace QuicPunchTests.Tests;
    public static class WebUiSecurityTests
    {
        public static async Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   WEBUI & WEBSOCKET SECURITY INTEGRATION TESTS   ");
            Console.WriteLine("==================================================");

            await TestWebUiOriginAndCsrfAsync();
            await TestChatNostrPausedAsync();
            await TestChatOutboxAsync();
            TestChatHistoryPersistenceAndExpiry();
            TestAutomaticPeerPermissions();
            TestAdministratorProfiles();
            await TestChatRequiresNoSessionConsentAsync();
            await TestWatchRoomIsolationAsync();
            TestTorPreExistingExecutableDetection();
            await TestPeerInfoSessionLockConcurrencyAsync();

            Console.WriteLine("==================================================");
            Console.WriteLine("   ALL WEBUI & SECURITY CHECKS PASSED!            ");
            Console.WriteLine("==================================================");
        }

        private static async Task TestChatOutboxAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "qp_chat_outbox_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var node = new QuicPunch.QuicPunch(appDataPath: directory);
                using var peer = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
                typeof(QuicPunch.QuicPunch).GetProperty(nameof(node.PeerStore))!.SetValue(node, new PeerStore(Path.Combine(directory, "peers.db"), watch: false));
                node.PeerStore!.AddOrUpdate([IPAddress.Parse("192.0.2.10")], [5000], peer.CertHash, save: true);
                node.TrustPeer(peer.CertHash);
                var chat = new ChatHandler(); node.RegisterProtocol(chat);
                using var voice = new VoiceCallHandler(node); using var speed = new SpeedTestHandler();
                var preferences = new AppPreferencesStore(Path.Combine(directory, "ui-settings.json"));
                preferences.Update(value => value.Notifications.Enabled = false);
                using var cts = new CancellationTokenSource();
                var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                var server = new WebUiServer(node, chat, null, voice, speed, preferences, cts, port: port); server.Start();
                try
                {
                    using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
                    async Task<HttpResponseMessage> Post(string path, object value) => await client.PostAsync(path,
                        new StringContent(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"));
                    string id = Guid.NewGuid().ToString();
                    var queued = new { peerId = peer.Id.ToString(), msgId = id, message = "Pendiente sin conexión" };
                    using (var denied = await Post("api/chat/outbox", queued))
                        if (denied.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Outbox bypassed CSRF.");
                    client.DefaultRequestHeaders.Add("X-QuicPunch-CSRF", server.CsrfToken);
                    for (int i = 0; i < 2; i++)
                        using (var response = await Post("api/chat/outbox", queued))
                            if (!response.IsSuccessStatusCode) throw new Exception("Outbox rejected a trusted saved contact: " + await response.Content.ReadAsStringAsync());
                    var history = new ChatHistoryStore(Path.Combine(node.NodeAppDataPath, "chat-history.db"));
                    if (history.LoadPending().Count != 1 || history.LoadPending()[0].Message != queued.message)
                        throw new Exception("Pending message was lost or duplicated on disk.");
                    using (var denied = await Post("api/chat/outbox", new { peerId = Guid.NewGuid(), msgId = Guid.NewGuid(), message = "Unknown" }))
                        if (denied.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Outbox accepted an untrusted recipient.");
                    using var wire = new MemoryStream(); using var session = new ChatHandler.ChatSession(peer, wire);
                    var module = (QuicNet.WebUi.Modules.ChatApiModule)typeof(WebUiServer).GetField("_chatModule", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
                    var flush = module.GetType().GetMethod("FlushOutboxAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var process = typeof(ChatHandler).GetMethod("ProcessFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    async Task Frame(object value) => await (Task)process.Invoke(chat, [session, peer, Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(value)), cts.Token])!;
                    // Pending content must never escape through history synchronization.
                    await Frame(new { type = "chat_sync_req" });
                    if (Encoding.UTF8.GetString(wire.ToArray()).Contains(queued.message)) throw new Exception("History sync sent a pending message.");
                    wire.SetLength(0); wire.Position = 0;
                    ChatHandler.ActiveChats[peer.Id] = session;
                    try
                    {
                        for (int i = 0; i < 30 && history.GetMessage(queued.peerId, id)?.DeliveryState != "sent"; i++)
                        { await (Task)flush.Invoke(module, null)!; await Task.Delay(10); }
                        if (history.GetMessage(queued.peerId, id)?.DeliveryState != "sent" || !Encoding.UTF8.GetString(wire.ToArray()).Contains(id))
                            throw new Exception("Reconnection did not send the stored message with its stable ID.");
                        long bytes = wire.Length; await (Task)flush.Invoke(module, null)!;
                        if (wire.Length != bytes) throw new Exception("A sent message was resent.");
                        await Frame(new { type = "chat_ack", msgId = id });
                        if (history.GetMessage(queued.peerId, id)?.IsConfirmed != true) throw new Exception("Delivery receipt was not persisted.");
                        using (var reaction = await Post("api/chat/interactions", new { queued.peerId, msgId = id, kind = "reaction", key = "👍", active = true })) reaction.EnsureSuccessStatusCode();
                        using (var pin = await Post("api/chat/interactions", new { queued.peerId, msgId = id, kind = "pin", key = "", active = true })) pin.EnsureSuccessStatusCode();
                        if (history.GetPinnedMessages(queued.peerId).Length != 1 || history.GetActions(queued.peerId).Count(action => action.kind == "reaction" && action.active) != 1)
                            throw new Exception("Local reactions or pins were not persisted.");
                        long remoteRevision = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
                        await Frame(new { type = "chat_actions", updates = new[] { new { msgId = id, kind = "reaction", key = "👍", active = true, revision = remoteRevision } } });
                        await Frame(new { type = "chat_actions", updates = new[] { new { msgId = id, kind = "reaction", key = "👍", active = false, revision = remoteRevision - 1 } } });
                        if (history.GetActions(queued.peerId).Count(action => action.kind == "reaction" && action.active) != 2)
                            throw new Exception("Remote reactions were duplicated, spoofed or overwritten by stale state.");
                        await Frame(new { type = "chat_actions", updates = new[] { new { msgId = id, kind = "pin", key = "", active = false, revision = remoteRevision } } });
                        if (history.GetPinnedMessages(queued.peerId).Length != 0) throw new Exception("Remote unpin was not synchronized.");
                        using (var pin = await Post("api/chat/interactions", new { queued.peerId, msgId = id, kind = "pin", key = "", active = true })) pin.EnsureSuccessStatusCode();
                        var restoredActions = new ChatHistoryStore(Path.Combine(node.NodeAppDataPath, "chat-history.db"));
                        if (restoredActions.GetPinnedMessages(queued.peerId).Length != 1 || restoredActions.GetActions(queued.peerId).Count(action => action.kind == "reaction" && action.active) != 2)
                            throw new Exception("Pins or reactions were lost after reopening history.");
                        using (var invalidReaction = await Post("api/chat/interactions", new { queued.peerId, msgId = id, kind = "reaction", key = "<script>", active = true }))
                            if (invalidReaction.StatusCode != HttpStatusCode.BadRequest) throw new Exception("Invalid reaction accepted.");
                        await Frame(new { type = "chat_sync_req" });
                        if (!Encoding.UTF8.GetString(wire.ToArray()).Contains("chat_actions")) throw new Exception("Message actions were absent from reconnect synchronization.");
                        using (var tooLate = await Post("api/chat/message/delete", new { queued.peerId, msgId = id, pendingOnly = true }))
                            if (tooLate.StatusCode != HttpStatusCode.Conflict) throw new Exception("A delivered message was reported as cancelled.");
                    }
                    finally { ChatHandler.ActiveChats.TryRemove(peer.Id, out _); }
                    string cancelId = Guid.NewGuid().ToString();
                    using (var response = await Post("api/chat/outbox", new { queued.peerId, msgId = cancelId, message = "Cancelar" })) response.EnsureSuccessStatusCode();
                    using (var removed = await Post("api/chat/message/delete", new { queued.peerId, msgId = cancelId, pendingOnly = true })) removed.EnsureSuccessStatusCode();
                    if (history.LoadPending().Count != 0 || !history.IsHidden(queued.peerId, cancelId)) throw new Exception("Cancellation retained an outgoing message.");
                    await Frame(new { type = "chat_msg", msgId = "incoming", content = "Mensaje recibido", sender = "Test" });
                    using (var removed = await Post("api/chat/message/delete", new { queued.peerId, msgId = "incoming" })) removed.EnsureSuccessStatusCode();
                    await Frame(new { type = "chat_msg", msgId = "incoming", content = "Mensaje recibido", sender = "Test" });
                    if (history.GetMessage(queued.peerId, "incoming") != null || !history.IsHidden(queued.peerId, "incoming"))
                        throw new Exception("A deleted incoming message reappeared through retransmission.");
                    history.Save(new WebUiServer.ChatMessage("other-peer", id, "Test", "Otra conversación", DateTime.Now, false, true));
                    using (var removed = await Post("api/chat/message/delete", new { queued.peerId, msgId = id })) removed.EnsureSuccessStatusCode();
                    if (history.GetMessage("other-peer", id) == null) throw new Exception("Message deletion crossed conversation boundaries.");
                }
                finally { server.Stop(); cts.Cancel(); }
            }
            finally { try { Directory.Delete(directory, true); } catch { } }
        }

        private static async Task TestChatNostrPausedAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "qp_chat_direct_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: directory) { PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
                var chat = new ChatHandler();
                qp.RegisterProtocol(chat);
                if (chat.NostrMessagingEnabled || chat.MailboxChannel != null)
                    throw new Exception("Chat must not attach a Nostr mailbox by default.");
                // Even an explicit old-style attachment must not restore the paused fallback.
                chat.OnMailboxAttached(qp.GetMailboxChannel(chat.ProtocolId));
                if (chat.MailboxChannel != null) throw new Exception("Disabled chat accepted a mailbox channel.");
                using var peer = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
                var offline = await chat.SendMessageAsync(peer.Id, "Test", "Must remain unsent");
                if (offline.Success || offline.IsOffline) throw new Exception("Disconnected chat fell back to Nostr.");
                int incoming = 0;
                chat.OnMessageReceived += (_, _, _, _) => incoming++;
                await chat.OnOfflineMessageReceivedAsync(peer, Encoding.UTF8.GetBytes("Paused"), DateTimeOffset.UtcNow, "offline-test");
                if (incoming != 0) throw new Exception("Paused Nostr messages reached the chat.");
                using var stream = new MemoryStream();
                var session = new ChatHandler.ChatSession(peer, stream);
                ChatHandler.ActiveChats[peer.Id] = session;
                try
                {
                    var direct = await chat.SendMessageAsync(peer.Id, "Test", "Direct message");
                    if (!direct.Success || direct.IsOffline || stream.Length == 0)
                        throw new Exception("Pausing Nostr broke direct chat.");
                    stream.Dispose();
                    var failed = await chat.SendMessageAsync(peer.Id, "Test", "Broken connection");
                    if (failed.Success || failed.IsOffline) throw new Exception("Failed direct send fell back to Nostr.");
                }
                finally { ChatHandler.ActiveChats.TryRemove(peer.Id, out _); session.Dispose(); }
                if (!new ChatHandler { NostrMessagingEnabled = true }.SupportsMailbox)
                    throw new Exception("Future explicit mailbox opt-in was removed.");
            }
            finally { try { Directory.Delete(directory, true); } catch { } }
        }

        private static async Task TestLocalProfileAsync(HttpClient client, int port, string csrf, AppPreferencesStore preferences, string tempDir, QuicPunch.QuicPunch node)
        {
            preferences.Update(p => { p.WanEnabled = false; p.TorVirtualPort = 8443; p.TorTransportMode = "AutoCascade"; });
            string endpoint = $"http://127.0.0.1:{port}/api/profile";
            var id = node.CurrentPeer.Id;
            string cert = Convert.ToHexString(node.CertManager.PeerCertificate.RawData);
            string torName = node.TorCurrentPeer.Name!;
            LocalUserProfile.Apply(node, preferences.Snapshot());
            if (node.CurrentPeer.Name != LocalUserProfile.DeviceName) throw new Exception("Default profile does not use the device name.");
            using (var denied = await client.PostAsync(endpoint, new StringContent("{\"displayName\":\"Denied\"}", Encoding.UTF8, "application/json")))
                if (denied.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Profile bypassed CSRF.");
            async Task Post(string body, HttpStatusCode expected)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Add("X-QuicPunch-CSRF", csrf);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                if (response.StatusCode != expected) throw new Exception("Unexpected profile response: " + await response.Content.ReadAsStringAsync());
            }
            await Post("{\"displayName\":\"  Álex 🐈  \"}", HttpStatusCode.OK);
            const string expectedName = "Álex 🐈";
            var reloaded = new AppPreferencesStore(Path.Combine(tempDir, "preferences.json")).Snapshot();
            if (reloaded.DisplayName != expectedName || node.CurrentPeer.Name != expectedName) throw new Exception("Profile was not applied and persisted.");
            using (var response = await client.GetAsync(endpoint))
            {
                using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.GetProperty("displayName").GetString() != expectedName || json.RootElement.GetProperty("deviceName").GetString() != LocalUserProfile.DeviceName)
                    throw new Exception("Profile GET returned an incorrect name.");
            }
            foreach (string invalid in new[] { "[]", "{}", "{\"displayName\":false}", "{\"displayName\":\"bad\\nname\"}", "{\"displayName\":\"" + new string('x', 65) + "\"}" })
                await Post(invalid, HttpStatusCode.BadRequest);
            if (node.CurrentPeer.Name != expectedName || preferences.Snapshot().DisplayName != expectedName) throw new Exception("Invalid profile changed the name.");
            await Post("{\"displayName\":\"\"}", HttpStatusCode.OK);
            reloaded = new AppPreferencesStore(Path.Combine(tempDir, "preferences.json")).Snapshot();
            LocalUserProfile.Apply(node, reloaded);
            if (reloaded.DisplayName != "" || node.CurrentPeer.Name != LocalUserProfile.DeviceName) throw new Exception("Restoring the device name did not persist.");
            if (node.CurrentPeer.Id != id || Convert.ToHexString(node.CertManager.PeerCertificate.RawData) != cert || node.TorCurrentPeer.Name != torName)
                throw new Exception("Changing the profile changed the identity or exposed a name through Tor.");
            if (reloaded.WanEnabled || reloaded.TorVirtualPort != 8443) throw new Exception("Profile overwrote network preferences.");
            using var unsupported = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, endpoint) { Headers = { { "X-QuicPunch-CSRF", csrf } } });
            if (unsupported.StatusCode != HttpStatusCode.MethodNotAllowed) throw new Exception("Profile accepted an unsupported method.");
            Console.WriteLine("Profile persistence, validation, identity and Tor privacy passed.");
        }

        private static async Task TestUiStructureAsync(HttpClient client, int port, string csrf, AppPreferencesStore preferences, string tempDir)
        {
            using (var removed = await client.GetAsync($"http://127.0.0.1:{port}/api/ui/preferences"))
                if (removed.StatusCode != HttpStatusCode.NotFound) throw new Exception("Removed appearance API is still available.");
            foreach (string route in new[] { "/", "/chat.html", "/call.html", "/vpn.html", "/speedtest.html", "/watch.html", "/settings.html" })
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}{route}");
                string html = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode || !html.Contains("id=\"view-settings\"") || !html.Contains("lang=\"es\"") || html.Contains("user-scalable=no") || html.Contains("id=\"themeSelect\"") || !html.Contains("name=\"color-scheme\" content=\"dark\""))
                    throw new Exception("Redesigned SPA route or accessible viewport missing: " + route);
            }
            foreach (string asset in new[] { "/components.css", "/design.css", "/experience.js", "/screen-audio-capture.js", "/screen-audio-playback.js", "/screen-video-decoder.js", "/voice.js", "/watch/app.css", "/watch/watch-design.css" })
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}{asset}");
                string content = await response.Content.ReadAsStringAsync();
                string expectedType = asset.EndsWith(".css") ? "text/css" : "javascript";
                if (!response.IsSuccessStatusCode || content.Length < 20 || !(response.Content.Headers.ContentType?.MediaType?.Contains(expectedType) ?? false))
                    throw new Exception("Redesigned embedded asset unavailable: " + asset);
            }
        }

        private static async Task TestChatRequiresNoSessionConsentAsync()
        {
            Console.Write("[TEST] Trusted chat needs no petition; voice still asks and untrusted chat is rejected... ");
            string directory = Path.Combine(Path.GetTempPath(), "qp_chat_consent_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: directory) { PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
                var peer = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
                Guid chat = new ChatHandler().ProtocolId;
                Guid voice = Guid.Parse("00000000-0000-0000-0000-000000000003");
                int petitions = 0;
                qp.Manager.HandshakeRequested += (_, _) =>
                {
                    petitions++;
                    return Task.FromResult(new HandshakeDecision(false, null, null));
                };
                if (qp.HasAutomaticProtocolPermission(peer, chat) ||
                    await qp.AuthorizeIncomingSessionAsync(peer, chat, QuicPunch.QuicPunch.TransportType.Wan, CancellationToken.None))
                    throw new Exception("Untrusted chat was accepted.");
                qp.TrustPeer(peer.CertHash);
                foreach (var transport in new[] { QuicPunch.QuicPunch.TransportType.Wan, QuicPunch.QuicPunch.TransportType.Tor })
                    if (!qp.HasAutomaticProtocolPermission(peer, chat) ||
                        !await qp.AuthorizeIncomingSessionAsync(peer, chat, transport, CancellationToken.None))
                        throw new Exception("Trusted chat required an optional chat permission.");
                if (petitions != 0) throw new Exception("Chat raised a session petition.");
                if (qp.HasAutomaticProtocolPermission(peer, voice) ||
                    await qp.AuthorizeIncomingSessionAsync(peer, voice, QuicPunch.QuicPunch.TransportType.Wan, CancellationToken.None) || petitions != 1)
                    throw new Exception("Chat bypass also granted voice consent.");
                Console.WriteLine("PASSED");
            }
            finally { try { Directory.Delete(directory, recursive: true); } catch { } }
        }

        private static void TestAdministratorProfiles()
        {
            Console.Write("[TEST] Administrator profiles persist, remain directional, and downgrade safely... ");
            string directory = Path.Combine(Path.GetTempPath(), "qp_admin_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "peers.db");
            var first = RandomNumberGenerator.GetBytes(32);
            var second = RandomNumberGenerator.GetBytes(32);
            try
            {
                using (var store = new PeerStore(path, watch: false))
                {
                    store.AddOrUpdate([System.Net.IPAddress.Loopback], [4333], first, save: true);
                    store.AddOrUpdate([System.Net.IPAddress.Loopback], [4334], second, save: true);
                    if (store.IsAdministrator(first) || store.HasAutomaticPermission(first, "screen-settings"))
                        throw new Exception("New contacts inherited administrative rights.");
                    if (!store.SetProfile(first, PeerProfile.Administrator)) throw new Exception("Profile was not saved.");
                    store.AddOrUpdate([System.Net.IPAddress.Loopback], [4335], first, save: true);
                }
                using (var store = new PeerStore(path, watch: false))
                {
                    if (!store.IsAdministrator(first) || !store.HasAutomaticPermission(first, "screen-settings") ||
                        !store.HasAutomaticPermission(first, "voice") || store.IsAdministrator(second) ||
                        store.HasAutomaticPermission(second, "screen-settings") || store.HasAutomaticPermission(first, "unknown"))
                        throw new Exception("Administrative authority was lost or leaked.");
                    if (!store.IsAdministrator(first) || store.IsAdministrator(second))
                        throw new Exception("Administrator permissions became reciprocal.");
                    store.SetProfile(first, PeerProfile.Friend);
                    if (store.IsAdministrator(first)) throw new Exception("Downgrade retained application authority.");
                }
                using (var store = new PeerStore(path, watch: false))
                    if (store.IsAdministrator(first) || store.HasAutomaticPermission(first, "voice"))
                        throw new Exception("Downgraded administrator retained implicit permissions.");
                // Old files cannot inject an administrator profile during migration.
                var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                legacy["Version"] = 2;
                legacy["SavedPeers"]![0]!["Profile"] = 1;
                File.WriteAllText(path, legacy.ToJsonString());
                using (var store = new PeerStore(path, watch: false))
                    if (store.IsAdministrator(first) || store.IsAdministrator(second))
                        throw new Exception("Legacy contact was upgraded to administrator.");
                Console.WriteLine("PASSED");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }

        private static void TestAutomaticPeerPermissions()
        {
            Console.Write("[TEST] Saved peer permissions persist, stay scoped, and can be revoked... ");
            if (QuicPunch.QuicPunch.AutomaticPermissionForProtocol(Guid.Parse("00000000-0000-0000-0000-000000000002")) != "vpn" ||
                QuicPunch.QuicPunch.AutomaticPermissionForProtocol(Guid.Parse("00000000-0000-0000-0000-000000000007")) != "watch" ||
                QuicPunch.QuicPunch.AutomaticPermissionForProtocol(Guid.Parse("00000000-0000-0000-0000-000000000008")) != "watch" ||
                QuicPunch.QuicPunch.AutomaticPermissionForProtocol(Guid.Empty) != "")
                throw new Exception("Application protocol permission mapping is incorrect.");
            string directory = Path.Combine(Path.GetTempPath(), "qp_test_permissions_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "peers.db");
                byte[] first = RandomNumberGenerator.GetBytes(32);
                byte[] second = RandomNumberGenerator.GetBytes(32);
                using (var store = new PeerStore(path, watch: false))
                {
                    store.AddOrUpdate(new[] { System.Net.IPAddress.Loopback }, new ushort[] { 4333 }, first, save: true);
                    store.AddOrUpdate(new[] { System.Net.IPAddress.Loopback }, new ushort[] { 4334 }, second, save: true);
                    if (!store.SetAutomaticPermission(first, "vpn", true) ||
                        !store.SetAutomaticPermission(first, "watch", true) ||
                        !store.SetAutomaticPermission(first, "screen-share-request", true) ||
                        store.SetAutomaticPermission(first, "unknown", true))
                        throw new Exception("Permission validation or grant failed.");
                }
                using (var store = new PeerStore(path, watch: false))
                {
                    if (!store.HasAutomaticPermission(first, "vpn") ||
                        !store.HasAutomaticPermission(first, "watch") ||
                        !store.HasAutomaticPermission(first, "screen-share-request") ||
                        store.HasAutomaticPermission(first, "voice") ||
                        store.HasAutomaticPermission(second, "vpn"))
                        throw new Exception("Permissions were lost or leaked across peers and application types.");
                    store.AddOrUpdate(new[] { System.Net.IPAddress.Loopback }, new ushort[] { 4335 }, first, save: true);
                    if (!store.HasAutomaticPermission(first, "vpn"))
                        throw new Exception("Updating a peer removed its permissions.");
                    store.SetAutomaticPermission(first, "vpn", false);
                    store.Remove(first);
                }
                using (var store = new PeerStore(path, watch: false))
                    if (store.HasAutomaticPermission(first, "vpn") || store.HasAutomaticPermission(first, "screen-share-request"))
                        throw new Exception("Revoked or deleted permissions survived restart.");
                Console.WriteLine("PASSED");
            }
            finally { try { Directory.Delete(directory, recursive: true); } catch { } }
        }

        private static void TestChatHistoryPersistenceAndExpiry()
        {
            Console.Write("[TEST] Chat history, timer settings, and expiry persist on disk... ");
            string directory = Path.Combine(Path.GetTempPath(), "qp_test_chat_" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(directory, "chat-history.db");
                var first = new ChatHistoryStore(path);
                first.SetDuration("peer-a", 60);
                var lasting = new WebUiServer.ChatMessage("peer-a", "lasting", "Me", "saved text", DateTime.Now, true, true);
                var expiring = lasting with { MsgId = "expiring", ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1) };
                first.Save(lasting);
                first.Save(expiring);
                var reopened = new ChatHistoryStore(path);
                if (reopened.GetDurations().GetValueOrDefault("peer-a") != 60 ||
                    reopened.LoadRecent(10).Count != 1 || reopened.LoadRecent(10)[0].Message != "saved text")
                    throw new Exception("Chat history or timer setting was not restored.");
                reopened.Save(lasting with { MsgId = "older", Timestamp = lasting.Timestamp.AddSeconds(-1) });
                reopened.Save(lasting with { MsgId = "same-time", Message = "same timestamp" });
                var page1 = reopened.LoadPage("peer-a", 1, null);
                var page2 = reopened.LoadPage("peer-a", 1, page1.NextBefore);
                var page3 = reopened.LoadPage("peer-a", 1, page2.NextBefore);
                if (page1.Messages.Count != 1 || page2.Messages.Count != 1 || page3.Messages.Count != 1 ||
                    page3.NextBefore != null ||
                    new[] { page1.Messages[0].MsgId, page2.Messages[0].MsgId, page3.Messages[0].MsgId }.Distinct().Count() != 3 ||
                    reopened.LoadPage("another-peer", 10, null).Messages.Count != 0)
                    throw new Exception("Chat cursor pagination lost, duplicated, or leaked messages.");
                try { reopened.LoadPage("peer-a", 1, "invalid-cursor"); throw new Exception("Invalid cursor was accepted."); }
                catch (InvalidDataException) { }
                reopened.DeleteExpired(DateTime.UtcNow);
                if (!reopened.IsHidden("peer-a", "expiring"))
                    throw new Exception("An expired message could return through history sync.");
                reopened.DeletePeer("peer-a");
                if (reopened.LoadRecent(10).Count != 0 || !reopened.IsHidden("peer-a", "lasting"))
                    throw new Exception("Clearing a conversation did not persist.");
                var activity = new ChatHistoryStore(Path.Combine(directory, "activity.db"));
                activity.Save(lasting);
                activity.Save(lasting with { PeerId = "peer-b", IsMe = false, Timestamp = lasting.Timestamp.AddSeconds(1) });
                if (activity.GetConversationActivity()["peer-b"] <= activity.GetConversationActivity()["peer-a"])
                    throw new Exception("Incoming messages did not update conversation activity.");
                activity.Save(lasting with { MsgId = "outgoing", Timestamp = lasting.Timestamp.AddSeconds(2) });
                activity.Save(lasting with { MsgId = "expired-newest", Timestamp = lasting.Timestamp.AddSeconds(3), ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1) });
                var restoredActivity = new ChatHistoryStore(Path.Combine(directory, "activity.db")).GetConversationActivity();
                long expectedActivity = new DateTimeOffset(lasting.Timestamp.AddSeconds(2)).ToUnixTimeMilliseconds();
                if (activity.GetConversationActivity()["peer-a"] != expectedActivity || restoredActivity["peer-a"] != expectedActivity)
                    throw new Exception("Outgoing activity was stale, lost on restart, or included expired messages.");
                activity.DeletePeer("peer-a");
                if (activity.GetConversationActivity().ContainsKey("peer-a"))
                    throw new Exception("Clearing a conversation retained its activity.");
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }

        private static async Task TestWebUiOriginAndCsrfAsync()
        {
            Console.Write("[TEST] WebUi Origin & CSRF enforcement for WebSockets & API... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_webui_sec_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            using var cts = new CancellationTokenSource();
            using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0) { PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
            var chatHandler = new ChatHandler();
            using var lanHandler = new VirtualLanHandler();
            using var vpnStore = new VpnConfigurationStore(Path.Combine(tempDir, "vpn.json"), new());
            await using var vpnController = new VpnController(qp, lanHandler, vpnStore, cts.Token);
            var voiceHandler = new VoiceCallHandler(qp);
            var speedTestHandler = new SpeedTestHandler();
            var preferences = new AppPreferencesStore(Path.Combine(tempDir, "preferences.json"));
            using var watchControl = new WatchControlHandler();
            using var watchMedia = new MediaTransferHandler();
            using var watchLibrary = new MediaLibrary(watchMedia, Path.Combine(tempDir, "watch-cache"));
            var watchRooms = new RoomCoordinator(watchControl, watchLibrary, Guid.Empty);
            var watchConnector = new PeerConnector(qp, watchControl, watchMedia, cts.Token);
            await using var watchWeb = new WatchWebServer(qp, watchControl, watchMedia, watchLibrary, watchRooms, watchConnector, cts);

            var server = new WebUiServer(
                qp,
                chatHandler,
                lanHandler,
                voiceHandler,
                speedTestHandler,
                preferences,
                cts,
                watchWeb,
                port: 5800);

            try
            {
                server.Start();
                int port = server.Port;
                string csrf = server.CsrfToken;

                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var client = new HttpClient(handler);
                  await TestUiStructureAsync(client, port, csrf, preferences, tempDir);
                await TestLocalProfileAsync(client, port, csrf, preferences, tempDir, qp);
                // Cancel a queued outgoing attempt before a logical QUIC session exists.
                var peerModule = typeof(WebUiServer).GetField("_peerModule", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
                var cancellations = (System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource>)peerModule.GetType()
                    .GetField("_connectionCancellations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(peerModule)!;
                Guid queuedVoicePeer = Guid.NewGuid(), queuedVoiceAttempt = Guid.NewGuid();
                using (var pendingCancellation = new CancellationTokenSource())
                {
                    cancellations[queuedVoiceAttempt] = pendingCancellation;
                    WebUiServer.InFlightConnections[(queuedVoicePeer, voiceHandler.ProtocolId)] = queuedVoiceAttempt;
                    try
                    {
                        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/voice-hangup");
                        cancel.Headers.Add("X-QuicPunch-CSRF", csrf);
                        cancel.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { peerId = queuedVoicePeer }), Encoding.UTF8, "application/json");
                        using var cancelled = await client.SendAsync(cancel);
                        if (!cancelled.IsSuccessStatusCode || !pendingCancellation.IsCancellationRequested)
                            throw new Exception("Hanging up did not cancel the queued voice connection.");
                    }
                    finally { cancellations.TryRemove(queuedVoiceAttempt, out _); WebUiServer.InFlightConnections.TryRemove((queuedVoicePeer, voiceHandler.ProtocolId), out _); }
                }
                await ProfileAvatarTests.RunAsync(client, port, csrf, tempDir);
                foreach (string recipient in new[] { "global", Guid.NewGuid().ToString() })
                {
                    using var chatRequest = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/chat-send");
                    chatRequest.Headers.Add("X-QuicPunch-CSRF", csrf);
                    chatRequest.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { peerId = recipient, message = "Must not be queued" }), Encoding.UTF8, "application/json");
                    using var chatResponse = await client.SendAsync(chatRequest);
                    if (chatResponse.StatusCode != HttpStatusCode.Conflict) throw new Exception("Disconnected or public chat was accepted without a direct session.");
                }
                using (var chatStatus = await client.GetAsync($"http://127.0.0.1:{port}/api/status"))
                {
                    using var statusJson = System.Text.Json.JsonDocument.Parse(await chatStatus.Content.ReadAsStringAsync());
                    if (statusJson.RootElement.GetProperty("chatTransport").GetProperty("nostrEnabled").GetBoolean())
                        throw new Exception("UI advertised the paused Nostr chat as available.");
                }
                using (var response = await client.GetAsync($"http://127.0.0.1:{port}/api/vpn/config"))
                {
                    using var payload = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (response.StatusCode != HttpStatusCode.OK || !payload.RootElement.TryGetProperty("peerDefaults", out _))
                        throw new Exception("VPN configuration API is missing.");
                    var configuration = payload.RootElement.GetProperty("configuration");
                    if (!configuration.TryGetProperty("peers", out var peers) || peers.ValueKind != System.Text.Json.JsonValueKind.Array ||
                        !configuration.TryGetProperty("revision", out _) || !configuration.TryGetProperty("autoConnect", out _))
                        throw new Exception("VPN API must use the same camelCase contract consumed by the actual peer editor.");
                }
                var vpnConfig = vpnController.Configuration;
                vpnConfig.Mode = "advanced";
                string vpnBody = System.Text.Json.JsonSerializer.Serialize(new { configuration = vpnConfig, expectedRevision = vpnConfig.Revision }, VpnConfigurationStore.JsonOptions);
                using (var denied = await client.PostAsync($"http://127.0.0.1:{port}/api/vpn/config", new StringContent(vpnBody, Encoding.UTF8, "application/json")))
                    if (denied.StatusCode != HttpStatusCode.Forbidden) throw new Exception("VPN policy write bypassed CSRF.");
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/vpn/config");
                    request.Headers.Add("X-QuicPunch-CSRF", csrf);
                    request.Content = new StringContent(vpnBody, Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request);
                    if (response.StatusCode != (attempt == 0 ? HttpStatusCode.OK : HttpStatusCode.BadRequest))
                        throw new Exception("VPN revision update or stale-write rejection failed: " + response.StatusCode + " " + await response.Content.ReadAsStringAsync());
                }
                if (vpnController.Configuration.Mode != "advanced" || lanHandler.IsActive)
                    throw new Exception("VPN API did not preserve desired state while updating policy.");

                using (var pendingPeer = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32)))
                {
                    qp.TrustPeer(pendingPeer.CertHash);
                    qp.AvailablePeers[pendingPeer.Id] = pendingPeer;
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/connect-peer");
                    request.Headers.Add("X-QuicPunch-CSRF", csrf);
                    request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { peerId = pendingPeer.Id, protocol = "lan" }), Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request);
                    if (response.StatusCode != HttpStatusCode.Conflict || vpnController.Configuration.Enabled || lanHandler.IsActive)
                        throw new Exception("LAN connect bypassed remote capability confirmation or activated resources before checking it.");
                    qp.AvailablePeers.TryRemove(pendingPeer.Id, out _);
                }

                // 1. Cross-Origin HTTP request should be rejected (403)
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/status"))
                {
                    req.Headers.Add("Origin", "http://evil-site.com");
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.Forbidden)
                        throw new Exception($"Expected 403 Forbidden for cross-origin request, got {resp.StatusCode}");
                }

                // 2. /api/client-closing with GET should be rejected (405 Method Not Allowed)
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/client-closing"))
                {
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
                        throw new Exception($"Expected 405 Method Not Allowed on GET /api/client-closing, got {resp.StatusCode}");
                }

                // 3. /api/client-closing with POST but without CSRF should be rejected (403 Forbidden)
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/client-closing"))
                {
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.Forbidden)
                        throw new Exception($"Expected 403 Forbidden on POST /api/client-closing without CSRF, got {resp.StatusCode}");
                }

                // 4. /api/client-closing with POST and valid query CSRF should succeed (204 No Content)
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/client-closing?csrf={Uri.EscapeDataString(csrf)}"))
                {
                    req.Headers.Add("Origin", $"http://127.0.0.1:{port}");
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.NoContent)
                        throw new Exception($"Expected 204 NoContent on POST /api/client-closing with CSRF, got {resp.StatusCode}");
                }

                // 5. WebSocket connection without CSRF token should be rejected (403 Forbidden)
                using (var ws = new ClientWebSocket())
                {
                    bool rejected = false;
                    try
                    {
                        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        rejected = true;
                    }
                    if (!rejected && ws.State == WebSocketState.Open)
                        throw new Exception("WebSocket connection without CSRF should have been rejected.");
                }

                // 6. WebSocket connection with valid CSRF should succeed
                using (var ws = new ClientWebSocket())
                {
                    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?csrf={Uri.EscapeDataString(csrf)}"), CancellationToken.None);
                    if (ws.State != WebSocketState.Open)
                        throw new Exception($"Expected WebSocket State Open, got {ws.State}");
                    string marker = "web-diagnostic-" + Guid.NewGuid().ToString("N");
                    byte[] report = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { type = "client_log", data = new {
                        level = "error", context = "Prueba de captura", message = marker + " token=fixture-secret", path = "/call.html",
                        stack = "Error: capture\n at http://127.0.0.1/voice.js?csrf=fixture-secret:100:5" } }));
                    await ws.SendAsync(report, WebSocketMessageType.Text, true, CancellationToken.None);
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        while (!WebUiServer.EventLogs.Any(entry => entry.Contains(marker))) await Task.Delay(10, deadline.Token);
                    string logged = WebUiServer.EventLogs.Single(entry => entry.Contains(marker));
                    if (!logged.Contains("[WEB ERROR]") || !logged.Contains("voice.js") || logged.Contains("fixture-secret"))
                        throw new Exception("Browser diagnostic was not logged with context, stack and redacted credentials");
                    var sink = new WebClientDiagnostics();
                    using var badDiagnostic = System.Text.Json.JsonDocument.Parse("{\"level\":\"execute\",\"message\":\"invalid\"}");
                    if (sink.Accept(badDiagnostic.RootElement)) throw new Exception("Invalid diagnostic level accepted");
                    using var oversized = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { level = "error", message = new string('x', 2049) }));
                    if (sink.Accept(oversized.RootElement)) throw new Exception("Oversized diagnostic accepted");
                    // Correlate the stop event so a delayed acknowledgement cannot
                    // turn off a camera that the browser has already reactivated.
                    string videoStopRequestId = Guid.NewGuid().ToString();
                    using (var stop = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/voice/screen-share/stop"))
                    {
                        stop.Headers.Add("X-QuicPunch-CSRF", csrf);
                        stop.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { requestId = videoStopRequestId }), Encoding.UTF8, "application/json");
                        using var stopped = await client.SendAsync(stop);
                        stopped.EnsureSuccessStatusCode();
                    }
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        var buffer = new byte[8192];
                        while (true)
                        {
                            using var message = new MemoryStream();
                            WebSocketReceiveResult received;
                            do
                            {
                                received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                                if (received.MessageType == WebSocketMessageType.Close) throw new Exception("WebSocket closed before video stop acknowledgement");
                                message.Write(buffer, 0, received.Count);
                            } while (!received.EndOfMessage);
                            using var envelope = System.Text.Json.JsonDocument.Parse(message.ToArray());
                            if (envelope.RootElement.GetProperty("type").GetString() != "screen_share_stopped") continue;
                            var data = envelope.RootElement.GetProperty("data");
                            if (data.GetProperty("peerId").GetString() != "local" || data.GetProperty("requestId").GetString() != videoStopRequestId)
                                throw new Exception("Local video stop event lost its request identity");
                            break;
                        }
                    }
                    // Older clients still send an empty POST body.
                    using (var stop = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/voice/screen-share/stop"))
                    {
                        stop.Headers.Add("X-QuicPunch-CSRF", csrf);
                        using var stopped = await client.SendAsync(stop);
                        stopped.EnsureSuccessStatusCode();
                    }
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }

                // 7. Verify index.html resolves all @include views properly
                using (var flag = await client.GetAsync($"http://127.0.0.1:{port}/flags/es.svg"))
                {
                    flag.EnsureSuccessStatusCode();
                    if (flag.Content.Headers.ContentType?.MediaType != "image/svg+xml" || !(await flag.Content.ReadAsStringAsync()).Contains("<svg"))
                        throw new Exception("Bundled country flag was not served as an SVG image.");
                }
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/"))
                {
                    req.Headers.Add("Origin", $"http://127.0.0.1:{port}");
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                        throw new Exception($"Expected 200 OK for GET /, got {resp.StatusCode}");
                    string html = await resp.Content.ReadAsStringAsync();
                    if (html.Contains("<!-- @include"))
                        throw new Exception("Index HTML still contains unparsed <!-- @include tags.");
                    if (!html.Contains("id=\"view-dashboard\"") ||
                        !html.Contains("id=\"view-chat\"") ||
                        !html.Contains("id=\"view-voice\"") ||
                        !html.Contains("id=\"view-vpn\"") ||
                        !html.Contains("id=\"view-watch\""))
                        throw new Exception("Index HTML is missing one or more SPA view containers.");
                    if (!html.Contains("id=\"pasteClipboardBtn\""))
                        throw new Exception("Index HTML is missing pasteClipboardBtn.");
                    if (!html.Contains("id=\"chatExpirySelect\""))
                        throw new Exception("Chat expiry control is missing.");
                    if (html.Contains("id=\"refreshBtn\"") || html.Contains("id=\"nodeBadge\"") ||
                        html.Contains("protocol-launcher") || html.Contains("id=\"tokenConflictModal\"") ||
                        html.Contains("Llamadas rápidas") || html.Contains("mobile.js") || html.Contains("mobile.html"))
                        throw new Exception("The dashboard still exposes obsolete status, protocol controls, or mobile call remnants.");
                    if (!html.Contains("class=\"subtab-btn active\" data-group=\"peerdir\" data-subtab=\"tab-discovered-peers\"") &&
                        !html.Contains("class=\"subtab-btn active\" data-group=\"peerdir\" data-subtab=\"tab-discovered-contactos\""))
                        throw new Exception("Nostr discovery is not the default peer directory tab.");
                    if (!html.Contains("id=\"untrustedAlert\"") || !html.Contains("id=\"untrustedNavCount\""))
                        throw new Exception("Untrusted peer attention controls are missing.");
                }

                // Verify /api/capabilities on desktop node
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/capabilities"))
                {
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                        throw new Exception("GET /api/capabilities failed on desktop node");
                    string caps = await resp.Content.ReadAsStringAsync();
                    if (!caps.Contains("\"platform\":\"desktop\"") || !caps.Contains("\"vpn\":{\"available\":true"))
                        throw new Exception("Desktop capabilities mismatch: " + caps);
                }

                // Verify obsolete mobile endpoints return 404
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/mobile.html"))
                {
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
                        throw new Exception("mobile.html should return 404 Not Found");
                }
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/mobile.js"))
                {
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
                        throw new Exception("mobile.js should return 404 Not Found");
                }

                // Test Android standalone WebUiServer instance
                var androidServer = new WebUiServer(NodeCapabilities.CreateAndroid(), port: 5880)
                {
                    ProbePingHandler = (h, p, fp) => $"pong-{h}:{p}"
                };
                try
                {
                    androidServer.Start();
                    int aPort = androidServer.Port;
                    using var reqA = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{aPort}/api/capabilities");
                    using var respA = await client.SendAsync(reqA);
                    if (respA.StatusCode != System.Net.HttpStatusCode.OK)
                        throw new Exception("Android WebUiServer /api/capabilities failed");
                    string aCaps = await respA.Content.ReadAsStringAsync();
                    if (!aCaps.Contains("\"platform\":\"android\"") || !aCaps.Contains("\"vpn\":{\"available\":false"))
                        throw new Exception("Android capabilities mismatch: " + aCaps);

                    using var reqPing = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{aPort}/api/probe/ping");
                    reqPing.Headers.Add("X-QuicPunch-CSRF", androidServer.CsrfToken);
                    reqPing.Content = new StringContent("{\"host\":\"127.0.0.1\",\"port\":5000,\"fingerprint\":\"" + new string('a', 64) + "\"}", Encoding.UTF8, "application/json");
                    using var respPing = await client.SendAsync(reqPing);
                    if (respPing.StatusCode != System.Net.HttpStatusCode.OK)
                        throw new Exception("Android WebUiServer probe ping failed: " + respPing.StatusCode);
                    string pingBody = await respPing.Content.ReadAsStringAsync();
                    if (!pingBody.Contains("pong-127.0.0.1:5000"))
                        throw new Exception("Android WebUiServer probe ping unexpected response: " + pingBody);
                }
                finally
                {
                    androidServer.Stop();
                }

                string conversationId = Guid.NewGuid().ToString();
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/chat/disappearing"))
                {
                    req.Headers.Add("X-QuicPunch-CSRF", csrf);
                    req.Content = new StringContent($"{{\"peerId\":\"{conversationId}\",\"seconds\":60}}", Encoding.UTF8, "application/json");
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                        throw new Exception("Could not configure disappearing messages through the web API.");
                }
                using (var resp = await client.GetAsync($"http://127.0.0.1:{port}/api/status"))
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    if (!json.Contains(conversationId) || !json.Contains("disappearingMessages"))
                        throw new Exception("Disappearing message settings were missing from the status snapshot.");
                }

                string watchCsrf;
                using (var resp = await client.GetAsync($"http://127.0.0.1:{port}/watch/"))
                {
                    string html = await resp.Content.ReadAsStringAsync();
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK || !html.Contains("/watch/app.js") || html.Contains("__QUICWATCH_CSRF__"))
                        throw new Exception("QuicWatch is not served from the QuicNet web server.");
                    var tokenMatch = System.Text.RegularExpressions.Regex.Match(html, "name=\"quicwatch-csrf\" content=\"([^\"]+)\"");
                    if (!tokenMatch.Success) throw new Exception("QuicWatch CSRF token is missing.");
                    watchCsrf = tokenMatch.Groups[1].Value;
                }

                using (var resp = await client.GetAsync($"http://127.0.0.1:{port}/watch/app.js"))
                {
                    if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                        throw new Exception("QuicWatch script is not served from QuicNet.");
                }

                using (var resp = await client.PostAsync($"http://127.0.0.1:{port}/watch/api/room/create", new StringContent("{}")))
                {
                    if (resp.StatusCode != System.Net.HttpStatusCode.Forbidden)
                        throw new Exception("QuicWatch accepted a POST without its CSRF token.");
                }
                for (int watchCycle = 0; watchCycle < 2; watchCycle++)
                {
                    foreach (string roomAction in new[] { "create", "leave" })
                    {
                        using var action = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/watch/api/room/{roomAction}");
                        action.Headers.Add("X-QuicWatch-Key", watchCsrf);
                        action.Content = new StringContent("{\"name\":\"Sala de prueba\",\"peerIds\":[]}", Encoding.UTF8, "application/json");
                        using var result = await client.SendAsync(action);
                        if (!result.IsSuccessStatusCode || (watchRooms.Snapshot() != null) != (roomAction == "create"))
                            throw new Exception("Watch create/leave/recreate failed through the HTTP API: " + await result.Content.ReadAsStringAsync());
                    }
                }

                using (var ws = new ClientWebSocket())
                {
                    bool rejected = false;
                    try { await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/watch/ws"), CancellationToken.None); }
                    catch (WebSocketException) { rejected = true; }
                    if (!rejected && ws.State == WebSocketState.Open)
                        throw new Exception("QuicWatch accepted a WebSocket without its CSRF token.");
                }

                using (var ws = new ClientWebSocket())
                {
                    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/watch/ws?csrf={watchCsrf}"), CancellationToken.None);
                    string marker = "watch-diagnostic-" + Guid.NewGuid().ToString("N");
                    byte[] diagnostic = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { type = "client_log", data = new { level = "warn", message = marker, stack = "Error: Watch fixture", path = "/watch/" } }));
                    await ws.SendAsync(diagnostic, WebSocketMessageType.Text, true, CancellationToken.None);
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        while (!WebUiServer.EventLogs.Any(entry => entry.Contains(marker))) await Task.Delay(10, deadline.Token);
                    if (ws.State != WebSocketState.Open)
                        throw new Exception("QuicWatch rejected a WebSocket with a valid CSRF token.");
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }

                byte[] sourceBytes = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
                string mediaPath = Path.Combine(tempDir, "watch-range.mp4");
                File.WriteAllBytes(mediaPath, sourceBytes);
                var manifest = watchLibrary.SelectLocalMedia(mediaPath, Guid.NewGuid());
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/watch/media/{manifest.MediaId}"))
                {
                    req.Headers.TryAddWithoutValidation("Range", "bytes=256-511");
                    using var resp = await client.SendAsync(req);
                    byte[] actual = await resp.Content.ReadAsByteArrayAsync();
                    if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent ||
                        !actual.SequenceEqual(sourceBytes.AsSpan(256, 256).ToArray()))
                        throw new Exception("QuicWatch returned an incorrect media range.");
                }

                using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/watch/media/{manifest.MediaId}"))
                {
                    req.Headers.TryAddWithoutValidation("Range", "bytes=900-100");
                    using var resp = await client.SendAsync(req);
                    if (resp.StatusCode != System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                        throw new Exception("QuicWatch accepted an invalid media range.");
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                server.Stop();
                cts.Cancel();
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        private static async Task TestWatchRoomIsolationAsync()
        {
            Console.Write("[TEST] QuicWatch room ownership and invitation isolation... ");
            string cachePath = Path.Combine(Path.GetTempPath(), "qp_test_watch_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var control = new WatchControlHandler();
                using var transfer = new MediaTransferHandler();
                using var library = new MediaLibrary(transfer, cachePath);
                using var owner = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
                using var outsider = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
                Guid localId = Guid.NewGuid();
                var rooms = new RoomCoordinator(control, library, localId);
                Guid firstId = Guid.NewGuid();

                rooms.AcceptRoomState(owner, new WatchWireMessage("room_invite", firstId, owner.Id, Revision: 1, TargetPeerId: localId));
                if (rooms.Snapshot()?.RoomId != firstId || rooms.Snapshot()?.OwnerPeerId != owner.Id)
                    throw new Exception("A valid invitation did not create the expected room.");

                rooms.AcceptRoomState(outsider, new WatchWireMessage("media", firstId, outsider.Id, Guid.NewGuid(), Revision: 2,
                    MediaSize: 16, ChunkSize: 16));
                if (rooms.Snapshot()?.Media != null || rooms.Snapshot()?.OwnerPeerId != owner.Id)
                    throw new Exception("A non-owner changed the room state.");

                Guid secondId = Guid.NewGuid();
                rooms.AcceptRoomState(owner, new WatchWireMessage("room_invite", secondId, owner.Id, Revision: 1, TargetPeerId: localId));
                if (rooms.Snapshot()?.RoomId != secondId)
                    throw new Exception("A new invitation from the owner mixed two rooms.");

                rooms.AcceptRoomState(owner, new WatchWireMessage("room_invite", Guid.NewGuid(), owner.Id,
                    Revision: 1, TargetPeerId: Guid.NewGuid()));
                if (rooms.Snapshot()?.RoomId != secondId)
                    throw new Exception("An invitation addressed to another peer changed the room.");

                await rooms.HandleMessageAsync(outsider, new WatchWireMessage("room_closed", secondId, outsider.Id));
                if (rooms.Snapshot()?.RoomId != secondId) throw new Exception("An outsider closed the room.");
                await rooms.LeaveAsync(CancellationToken.None);
                if (rooms.Snapshot() != null) throw new Exception("Leaving retained the active room.");
                Guid nextId = Guid.NewGuid();
                rooms.AcceptRoomState(outsider, new WatchWireMessage("room_invite", nextId, outsider.Id, TargetPeerId: localId));
                if (rooms.Snapshot()?.RoomId != nextId) throw new Exception("Could not join another owner after leaving.");
                await rooms.HandleMessageAsync(outsider, new WatchWireMessage("room_closed", nextId, outsider.Id));
                if (rooms.Snapshot() != null) throw new Exception("The owner's close did not end the viewer session.");

                var owned = new RoomCoordinator(control, library, localId);
                var ownedRoom = await owned.CreateRoomAsync("Prueba", [owner.Id], CancellationToken.None);
                var media = new MediaManifest(Guid.NewGuid(), "fixture.mp4", 32, "video/mp4", 16, localId);
                await owned.SetMediaAsync(media, CancellationToken.None);
                if (!owned.CanServeRemoteMedia(owner.Id, media.MediaId) || owned.CanServeRemoteMedia(outsider.Id, media.MediaId))
                    throw new Exception("Media access was not restricted to the current room's participants.");
                await owned.HandleMessageAsync(owner, new WatchWireMessage("room_leave", ownedRoom.RoomId, owner.Id));
                if (ownedRoom.Participants.ContainsKey(owner.Id) || owned.CanServeRemoteMedia(owner.Id, media.MediaId))
                    throw new Exception("Leaving did not revoke participant membership and media access.");
                await owned.LeaveAsync(CancellationToken.None);
                if (owned.Snapshot() != null || owned.CanServeRemoteMedia(owner.Id, media.MediaId)) throw new Exception("Closing retained room authority.");
                await owned.CreateRoomAsync("Otra sala", [], CancellationToken.None);
                await owned.LeaveAsync(CancellationToken.None);

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(cachePath, recursive: true); } catch { }
            }
        }

        private static void TestTorPreExistingExecutableDetection()
        {
            Console.Write("[TEST] Tor pre-existing executable detection... ");
            // Use reflection to invoke private static TryFindPreExistingTorExecutable
            var method = typeof(TorRuntimeManager).GetMethod("TryFindPreExistingTorExecutable", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                throw new Exception("TryFindPreExistingTorExecutable method not found on TorRuntimeManager.");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_tor_detection_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string exeName = OperatingSystem.IsWindows() ? "tor.exe" : "tor";
                string dummyTor = Path.Combine(tempDir, exeName);
                File.WriteAllText(dummyTor, "dummy-tor-binary");

                object?[] args = new object?[] { tempDir, null };
                bool found = (bool)method.Invoke(null, args)!;
                string? detected = (string?)args[1];

                if (!found || string.IsNullOrEmpty(detected) || !File.Exists(detected))
                    throw new Exception("Failed to detect existing Tor executable in target directory.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        private static async Task TestPeerInfoSessionLockConcurrencyAsync()
        {
            Console.Write("[TEST] PeerInfo thread safety and session lock under concurrency... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_peerinfo_lock_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0) { PeerTransport = new() { AutoConnectDiscoveredPeers = false } };

                using var certA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                using var ecdhA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var reqA = new CertificateRequest("CN=PeerA", certA, HashAlgorithmName.SHA256);
                using var x509A = reqA.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

                var peer = new PeerInfo(x509A, ecdhA.ExportSubjectPublicKeyInfo());
                peer.SessionNonce = RandomNumberGenerator.GetBytes(32);
                peer.EphemeralEcdhPublicKey = ecdhA.ExportSubjectPublicKeyInfo();

                // Run concurrent InitSession, RenewLocalEntropy, and Dispose
                var tasks = new Task[10];
                for (int i = 0; i < tasks.Length; i++)
                {
                    int idx = i;
                    tasks[i] = Task.Run(() =>
                    {
                        try
                        {
                            if (idx % 2 == 0)
                                peer.RenewLocalEntropy();
                            else
                                peer.InitSession(qp, QuicPunch.QuicPunch.TransportType.Wan);
                        }
                        catch (ObjectDisposedException) { /* Expected when disposed */ }
                        catch (CryptographicException) { /* Expected if entropy renewed mid-flight */ }
                    });
                }

                await Task.WhenAll(tasks);
                peer.Dispose();

                // Disposing again should be safe and idempotent
                peer.Dispose();

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
