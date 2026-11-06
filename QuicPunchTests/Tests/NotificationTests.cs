using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QuicNet.Notifications;
using QuicNet.Settings;
using QuicNet.Protocols;
using QuicNet.WebUi;

namespace QuicPunchTests.Tests;

internal static class NotificationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Platform : IAppPlatform
    {
        public bool StartupSupported => true;
        public string Description => "Test host";
        public bool Startup;
        public bool Reject;
        public TimeSpan? Idle;
        public TimeSpan? GetUserIdleTime() => Idle;
        public int RingCount, ActiveRings, MaxRings, RingVolume;
        public bool HoldRing;
        public TaskCompletionSource<AppNotice> Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SetStartWithSystem(bool enabled) { if (Reject) throw new IOException("Test startup failure"); Startup = enabled; }
        public Task NotifyAsync(AppNotice notice, bool desktop, CancellationToken ct) { Received.TrySetResult(notice); return Task.CompletedTask; }
        public async Task PlayRingtoneAsync(int volume, CancellationToken ct)
        {
            RingVolume = volume;
            int active = Interlocked.Increment(ref ActiveRings);
            MaxRings = Math.Max(MaxRings, active); Interlocked.Increment(ref RingCount);
            try { await Task.Delay(HoldRing ? Timeout.Infinite : 60, ct); }
            finally { Interlocked.Decrement(ref ActiveRings); }
        }
    }
    private sealed class AutomaticCall : QuicPunch.QuicPunch.IProtocolHandler
    {
        public Guid ProtocolId => Guid.Parse("00000000-0000-0000-0000-000000000003");
        public string ProtocolName => "Automatic call fixture";
        public bool? IncomingSessionPolicy(QuicPunch.PeerInfo peer) => true;
        public Task HandleAsync(QuicPunch.QuicConnection connection, Stream stream, QuicPunch.PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
        public Task DeniedAsync(QuicPunch.PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
    }
    public static async Task RunAsync()
    {
        var presencePrefs = new AppPreferences();
        var presenceNow = DateTimeOffset.Now;
        var available = QuicNet.Presence.UserPresenceService.Evaluate(presencePrefs, TimeSpan.FromMinutes(4.99), presenceNow, "system");
        Check(!available.isAfk && available.acceptsNotifications, "Active user must accept notifications by default");
        var away = QuicNet.Presence.UserPresenceService.Evaluate(presencePrefs, TimeSpan.FromMinutes(5), presenceNow, "system");
        Check(away.isAfk && away.acceptsNotifications, "AFK must not silence notifications");
        presencePrefs.Notifications.DoNotDisturb = true;
        var quietAway = QuicNet.Presence.UserPresenceService.Evaluate(presencePrefs, TimeSpan.FromMinutes(5), presenceNow, "system");
        Check(quietAway.isAfk && !quietAway.acceptsNotifications, "AFK and DND must remain independent");
        var quietActive = QuicNet.Presence.UserPresenceService.Evaluate(presencePrefs, TimeSpan.Zero, presenceNow, "system");
        Check(!quietActive.isAfk && !quietActive.acceptsNotifications, "Returning to computer must preserve manual DND");
        var prefs = new NotificationPreferences();
        string hash = new('A', 64);
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(2));
        AppNotice? Decide(bool focused = false) => AppNotificationService.Decide(prefs, "messages", "Alice", "Private", "/chat.html", hash, focused, now, "test");
        Check(Decide() is { Popup: true, Sound: true }, "Default delivery failed");
        prefs.DoNotDisturb = true; Check(Decide() == null, "DND did not block notifications");
        prefs.DoNotDisturbUntil = now.AddMinutes(-1); Check(Decide() != null, "Expired DND did not clear");
        prefs.DoNotDisturbUntil = now.AddHours(1); Check(Decide() == null, "Temporary DND ignored");
        prefs.DoNotDisturb = false; prefs.QuietHours = true;
        Check(prefs.IsQuiet(now.AddHours(11)) && prefs.IsQuiet(now.AddHours(-5)) && !prefs.IsQuiet(now), "Overnight quiet hours failed");
        prefs.QuietStart = 9 * 60; prefs.QuietEnd = 17 * 60; Check(Decide() == null, "Daytime quiet hours failed");
        prefs.QuietHours = false; prefs.WhileFocused = false; Check(Decide(true) == null && Decide(false) != null, "Focus suppression failed");
        prefs.Features["messages"] = new() { Sound = false }; Check(Decide() is { Sound: false, Popup: true }, "Feature sound mute failed");
        prefs.Contacts[hash] = new() { Enabled = false }; Check(Decide() == null, "Contact mute ignored");
        prefs.Contacts[hash] = new() { Popup = false }; Check(Decide() is { Popup: false, Sound: false }, "Contact and feature rules did not combine");
        prefs.Contacts.Clear(); prefs.Features.Clear(); prefs.Preview = false;
        Check(Decide() is { Title: "QuicNet" } result && !result.Message.Contains("Private"), "Private preview leaked");
        prefs.Volume = 0; Check(Decide() is { Sound: false }, "Zero volume was not silent");
        Check(NotificationAudio.Wave("soft", 0).AsSpan(44).ToArray().All(b => b == 0), "Silent waveform contains audio");
        Check(NotificationAudio.Ringtone(0).AsSpan(44).ToArray().All(b => b == 0), "Zero-volume ringtone contains audio");
        var ring = NotificationAudio.Ringtone(100);
        Check(BitConverter.ToInt32(ring, 40) == ring.Length - 44 && ring.Length > NotificationAudio.Wave("soft", 100).Length * 3, "Ringtone WAV duration/header invalid");
        double Energy(byte[] wave, double from, double to) => Enumerable.Range((int)(from * 22050), (int)((to - from) * 22050))
            .Average(i => Math.Abs((int)BitConverter.ToInt16(wave, 44 + i * 2)));
        Check(Energy(ring, 0.05, 0.4) > 4000 && Energy(ring, 0.5, 0.6) == 0 && Energy(ring, 0.72, 1.08) > 4000, "Ringtone must contain two clear pulses with a silent gap");
        if (OperatingSystem.IsWindows())
        {
            using var tray = new QuicNet.WindowsNotificationIcon(() => { }, () => { }, _ => { });
            Check(tray.IsReady, "Windows notification icon did not initialize");
        }
        string directory = Path.Combine(Path.GetTempPath(), "qp-notifications-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new AppPreferencesStore(Path.Combine(directory, "ui-settings.json"));
            Check(store.Snapshot().StartWithSystem && store.Snapshot().RunInBackground, "Startup and background defaults must be enabled");
            Check(!store.Snapshot().TorEnabled && !store.Snapshot().TorNostrDiscoveryEnabled, "Tor and its discovery must be off on a fresh installation");
            string legacyPath = Path.Combine(directory, "legacy-settings.json");
            File.WriteAllText(legacyPath, "{}");
            Check(!new AppPreferencesStore(legacyPath).Snapshot().TorEnabled && !new AppPreferencesStore(legacyPath).Snapshot().TorNostrDiscoveryEnabled, "Missing Tor preferences must default to off");
            File.WriteAllText(legacyPath, "{\"TorEnabled\":true,\"TorNostrDiscoveryEnabled\":true}");
            var migratedTor = new AppPreferencesStore(legacyPath).Snapshot();
            Check(migratedTor.TorEnabled && !migratedTor.TorNostrDiscoveryEnabled && !migratedTor.TorDiscoveryRiskAcknowledged,
                "Legacy discovery opt-ins without the privacy warning must migrate to off without disabling Tor itself");
            var torStore = new AppPreferencesStore(Path.Combine(directory, "tor-settings.json"));
            torStore.Update(p => { p.TorEnabled = true; p.TorDiscoveryRiskAcknowledged = true; p.TorNostrDiscoveryEnabled = true; });
            var torReload = new AppPreferencesStore(Path.Combine(directory, "tor-settings.json")).Snapshot();
            Check(torReload.TorEnabled && torReload.TorNostrDiscoveryEnabled && torReload.TorDiscoveryRiskAcknowledged, "An acknowledged Tor discovery opt-in must survive reload");
            torStore.Update(p => p.TorNostrDiscoveryEnabled = false);
            Check(!torStore.Snapshot().TorDiscoveryRiskAcknowledged, "Disabling discovery must require a fresh acknowledgement next time");
            store.Update(p => { p.Notifications.Contacts[hash] = new() { Sound = false }; p.RunInBackground = true; p.StartWithSystem = false; });
            Check(!new AppPreferencesStore(Path.Combine(directory, "ui-settings.json")).Snapshot().StartWithSystem, "Explicit startup opt-out was overwritten by defaults");
            var snapshot = store.Snapshot(); snapshot.Notifications.Contacts[hash].Sound = true;
            Check(!store.Snapshot().Notifications.Contacts[hash].Sound, "Snapshot mutated stored rules");
            try { store.Update(p => p.Notifications.Volume = 999); throw new Exception("Invalid settings accepted"); }
            catch (InvalidDataException) { }
            Check(store.Snapshot().Notifications.Volume == 60, "Failed update changed live settings");
            Check(new AppPreferencesStore(Path.Combine(directory, "ui-settings.json")).Snapshot().RunInBackground, "Settings not persisted");
            await TestRingingAsync(store, hash);
            using var cts = new CancellationTokenSource();
            using var node = new QuicPunch.QuicPunch(appDataPath: directory);
            using var voice = new VoiceCallHandler(node); using var speed = new SpeedTestHandler();
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var server = new WebUiServer(node, new ChatHandler(), null, voice, speed, store, cts, port: port);
            var platform = new Platform(); server.AppPlatform = platform; server.Start();
            try
            {
                using var http = new HttpClient { BaseAddress = new(server.Url) };
                using var get = await http.GetAsync("api/app/preferences");
                using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("settings").GetProperty("runInBackground").GetBoolean(), "GET contract mismatch");
                var config = new QuicNet.WebUi.Modules.AppBehaviorSettings(true, true, new());
                string json = JsonSerializer.Serialize(config, QuicNet.Serialization.SharedJson.WithOptions(new(JsonSerializerDefaults.Web)));
                using var denied = await http.PostAsync("api/app/preferences", new StringContent(json));
                Check(denied.StatusCode == HttpStatusCode.Forbidden && !platform.Startup, "Settings bypassed CSRF");
                http.DefaultRequestHeaders.Add("X-QuicPunch-CSRF", server.CsrfToken);
                foreach (string risk in new[] { "", ",\"acknowledgeTorPrivacyRisk\":false", ",\"acknowledgeTorPrivacyRisk\":\"true\"" })
                {
                    using var deniedDiscovery = await http.PostAsync("api/discovery", new StringContent("{\"enabled\":true,\"type\":\"tor\"" + risk + "}", Encoding.UTF8, "application/json"));
                    Check(deniedDiscovery.StatusCode == HttpStatusCode.Conflict && !node.Discovery.TorNostrDiscoveryEnabled &&
                        !store.Snapshot().TorNostrDiscoveryEnabled, "The Tor discovery API bypassed explicit privacy acknowledgement");
                }
                using (var enableDiscovery = await http.PostAsync("api/discovery", new StringContent("{\"enabled\":true,\"type\":\"tor\",\"acknowledgeTorPrivacyRisk\":true}", Encoding.UTF8, "application/json")))
                    Check(enableDiscovery.IsSuccessStatusCode && node.Discovery.TorNostrDiscoveryEnabled && store.Snapshot().TorDiscoveryRiskAcknowledged,
                        "Acknowledged discovery did not persist consent and the requested state");
                var discoveryReload = new AppPreferencesStore(Path.Combine(directory, "ui-settings.json")).Snapshot();
                Check(discoveryReload.TorNostrDiscoveryEnabled && discoveryReload.TorDiscoveryRiskAcknowledged, "Discovery consent was lost on reload");
                using (var disableDiscovery = await http.PostAsync("api/discovery", new StringContent("{\"enabled\":false,\"type\":\"tor\"}", Encoding.UTF8, "application/json")))
                    Check(disableDiscovery.IsSuccessStatusCode && !node.Discovery.TorNostrDiscoveryEnabled && !store.Snapshot().TorDiscoveryRiskAcknowledged,
                        "Disabling discovery must be immediate and clear acknowledgement");
                using (var unauthenticated = new HttpClient { BaseAddress = new(server.Url) })
                using (var deniedPresence = await unauthenticated.PostAsync("api/profile/presence", new StringContent("{\"doNotDisturb\":true,\"idleAfterMinutes\":2}", Encoding.UTF8, "application/json")))
                    Check(deniedPresence.StatusCode == HttpStatusCode.Forbidden, "Presence write bypassed CSRF");
                platform.Idle = TimeSpan.FromMinutes(3);
                using (var presenceEdit = await http.PostAsync("api/profile/presence", new StringContent("{\"doNotDisturb\":true,\"idleAfterMinutes\":2}", Encoding.UTF8, "application/json")))
                {
                    presenceEdit.EnsureSuccessStatusCode();
                    using var state = JsonDocument.Parse(await presenceEdit.Content.ReadAsStringAsync());
                    Check(state.RootElement.GetProperty("isAfk").GetBoolean() && !state.RootElement.GetProperty("acceptsNotifications").GetBoolean() &&
                        state.RootElement.GetProperty("activitySource").GetString() == "system", "System idle and DND missing from presence response");
                }
                var persistedPresence = new AppPreferencesStore(Path.Combine(directory, "ui-settings.json")).Snapshot();
                Check(persistedPresence.PresenceIdleMinutes == 2 && persistedPresence.Notifications.DoNotDisturb, "Presence settings did not survive reload");
                platform.Idle = TimeSpan.Zero;
                using (var currentPresence = JsonDocument.Parse(await http.GetStringAsync("api/profile/presence")))
                    Check(!currentPresence.RootElement.GetProperty("isAfk").GetBoolean() && !currentPresence.RootElement.GetProperty("acceptsNotifications").GetBoolean(), "Activity must restore AFK without changing DND");
                using (var invalidPresence = await http.PostAsync("api/profile/presence", new StringContent("{\"doNotDisturb\":false,\"idleAfterMinutes\":0}", Encoding.UTF8, "application/json")))
                    Check(!invalidPresence.IsSuccessStatusCode && store.Snapshot().Notifications.DoNotDisturb, "Invalid presence update mutated settings");
                using (var restoredPresence = await http.PostAsync("api/profile/presence", new StringContent("{\"doNotDisturb\":false,\"idleAfterMinutes\":5}", Encoding.UTF8, "application/json")))
                    restoredPresence.EnsureSuccessStatusCode();
                platform.Idle = null;
                using var saved = await http.PostAsync("api/app/preferences", new StringContent(json, Encoding.UTF8, "application/json"));
                Check(saved.IsSuccessStatusCode && platform.Startup && store.Snapshot().StartWithSystem, "Startup integration did not persist");
                platform.Reject = true;
                json = json.Replace("\"startWithSystem\":true", "\"startWithSystem\":false");
                using var rejected = await http.PostAsync("api/app/preferences", new StringContent(json));
                Check(!rejected.IsSuccessStatusCode && store.Snapshot().StartWithSystem, "Rejected host action changed preference");
                using var test = await http.PostAsync("api/notifications/test", null);
                Check(test.IsSuccessStatusCode && (await platform.Received.Task.WaitAsync(TimeSpan.FromSeconds(3))).Category == "system", "Native delivery did not run");
                using (var testResult = JsonDocument.Parse(await test.Content.ReadAsStringAsync()))
                    Check(testResult.RootElement.GetProperty("sent").GetBoolean(), "Successful notification test did not report its result.");
                store.Update(p => p.Notifications.DoNotDisturb = true);
                platform.Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var suppressed = await http.PostAsync("api/notifications/test", null))
                using (var suppressedResult = JsonDocument.Parse(await suppressed.Content.ReadAsStringAsync()))
                    Check(!suppressedResult.RootElement.GetProperty("sent").GetBoolean() && suppressedResult.RootElement.GetProperty("reason").GetString() == "do_not_disturb" &&
                        suppressedResult.RootElement.GetProperty("message").GetString()!.Contains("No molestar"), "DND test silently reported success instead of explaining suppression.");
                using (var preview = await http.GetAsync("api/notifications/ringtone?volume=37"))
                {
                    Check(preview.IsSuccessStatusCode && preview.Content.Headers.ContentType?.MediaType == "audio/wav", "Ringtone preview did not return playable audio.");
                    Check((await preview.Content.ReadAsByteArrayAsync()).SequenceEqual(NotificationAudio.Ringtone(37)), "Preview differs from the real incoming-call ringtone or ignores volume.");
                }
                Check(store.Snapshot().Notifications.DoNotDisturb && !platform.Received.Task.IsCompleted && platform.ActiveRings == 0,
                    "Explicit ringtone preview changed DND, emitted a notification or started a real ringing call.");
                using (var invalid = await http.GetAsync("api/notifications/ringtone?volume=101"))
                    Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid preview volume was accepted.");
                store.Update(p => p.Notifications.DoNotDisturb = false);
                platform.Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using var caller = new QuicPunch.PeerInfo().SetCertificateHash(Convert.FromHexString(hash));
                node.TrustPeer(caller.CertHash); node.RegisterProtocol(new AutomaticCall());
                Check(await node.AuthorizeIncomingSessionAsync(caller, voice.ProtocolId, QuicPunch.QuicPunch.TransportType.Wan, cts.Token), "Automatic fixture was not accepted");
                Check((await platform.Received.Task.WaitAsync(TimeSpan.FromSeconds(3))).Category == "calls", "Automatic calls bypassed notifications");
                node.RegisterProtocol(voice); node.AvailablePeers[caller.Id] = caller;
                var manualRequest = new QuicPunch.Helpers.HandshakeRequest(Guid.NewGuid(), voice.ProtocolId,
                    new IPEndPoint(IPAddress.Loopback, 1), caller.Id, caller.CertHash);
                var pendingCall = node.Manager.WaitForDecisionAsync(manualRequest, TimeSpan.FromSeconds(5), true, cts.Token);
                await UntilAsync(() => Volatile.Read(ref platform.RingCount) > 0, "Manual call did not start native ringing");
                using var decline = await http.PostAsync("api/respond-petition", new StringContent(JsonSerializer.Serialize(new { requestId = manualRequest.Id, accept = false }), Encoding.UTF8, "application/json"));
                Check(decline.IsSuccessStatusCode && !(await pendingCall).Accepted, "Manual call rejection failed");
                await UntilAsync(() => Volatile.Read(ref platform.ActiveRings) == 0, "Rejected call kept ringing");
                int shown = 0;
                using var untrusted = new QuicPunch.PeerInfo().SetCertificateHash(Convert.FromHexString(new string('B', 64)));
                node.AvailablePeers[untrusted.Id] = untrusted;
                string reviewJson = JsonSerializer.Serialize(new { peerId = untrusted.Id, trust = false });
                for (int i = 0; i < 2; i++)
                {
                    using var review = await http.PostAsync("api/peer/trust", new StringContent(reviewJson, Encoding.UTF8, "application/json"));
                    Check(review.IsSuccessStatusCode, "Declining trust failed");
                }
                string reviewedHash = Convert.ToBase64String(untrusted.CertHash);
                Check(!node.IsTrustedPeer(untrusted) && node.AvailablePeers.ContainsKey(untrusted.Id), "Review granted trust or removed the peer");
                using var typingDenied = await http.PostAsync("api/chat/typing", new StringContent(JsonSerializer.Serialize(new { peerId = untrusted.Id, isTyping = true }), Encoding.UTF8, "application/json"));
                Check(typingDenied.StatusCode == HttpStatusCode.Conflict, "Typing was accepted without a trusted chat session");
                Check(store.Snapshot().ReviewedUntrustedPeers.SequenceEqual([reviewedHash]), "Repeated review duplicated acknowledgement");
                Check(new AppPreferencesStore(Path.Combine(directory, "ui-settings.json")).Snapshot().ReviewedUntrustedPeers.Contains(reviewedHash), "Review acknowledgement was not persisted");
                var reviewSnapshot = store.Snapshot(); reviewSnapshot.ReviewedUntrustedPeers[0] = "changed";
                Check(store.Snapshot().ReviewedUntrustedPeers[0] == reviewedHash, "Snapshot mutated review acknowledgement");
                using var reviewedStatus = JsonDocument.Parse(await http.GetStringAsync("api/status"));
                Check(reviewedStatus.RootElement.GetProperty("preferences").GetProperty("ReviewedUntrustedPeers")[0].GetString() == reviewedHash, "Review state missing from status");
                using var notifications = new AppNotificationService(store, _ => shown++);
                notifications.Publish("messages", "Alice", "Hi", certHash: hash, id: "repeat");
                notifications.Publish("messages", "Alice", "Hi", certHash: hash, id: "repeat");
                Check(shown == 1, "Duplicate message alert");
                store.Update(p => p.Notifications.DoNotDisturb = true);
                notifications.Publish("calls", "Alice", "Call"); Check(shown == 1, "DND allowed an in-app alert");
            }
            finally { server.Stop(); cts.Cancel(); }
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
        Console.WriteLine("PASS notification policy, privacy, persisted rules, CSRF, startup rollback and native dispatch");
    }

    private static async Task UntilAsync(Func<bool> condition, string message)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { while (!condition()) await Task.Delay(10, deadline.Token); }
        catch (OperationCanceledException) { throw new Exception(message); }
    }

    private static async Task TestRingingAsync(AppPreferencesStore store, string hash)
    {
        store.Update(p => p.Notifications = new());
        var platform = new Platform();
        using var notifications = new AppNotificationService(store, _ => { }) { Platform = platform };
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notifications.RingIncomingCall(Guid.NewGuid(), hash, first.Task);
        await UntilAsync(() => Volatile.Read(ref platform.RingCount) >= 2, "Call did not ring repeatedly");
        platform.HoldRing = true;
        notifications.RingIncomingCall(Guid.NewGuid(), new string('B', 64), second.Task);
        await UntilAsync(() => Volatile.Read(ref platform.ActiveRings) == 1, "Second pending call did not preserve ringing");
        store.Update(p => p.Notifications.DoNotDisturb = true);
        await UntilAsync(() => Volatile.Read(ref platform.ActiveRings) == 0, "DND did not cancel playing ringtone");
        int count = platform.RingCount; await Task.Delay(550);
        Check(platform.RingCount == count, "Muted ringtone restarted");
        store.Update(p => { p.Notifications.DoNotDisturb = false; p.Notifications.Volume = 24; p.Notifications.Contacts[hash] = new() { Sound = false }; });
        await UntilAsync(() => Volatile.Read(ref platform.ActiveRings) == 1, "Other unmuted contact did not ring");
        Check(platform.RingVolume == 24 && platform.MaxRings == 1, "Ringtone ignored volume or overlapped calls");
        second.SetResult();
        await UntilAsync(() => Volatile.Read(ref platform.ActiveRings) == 0, "Muted remaining contact rang after other call ended");
        store.Update(p => p.Notifications.Contacts.Clear());
        await UntilAsync(() => Volatile.Read(ref platform.ActiveRings) == 1, "Unmuting pending call did not resume ringing");
        notifications.Presence("ringer-test", true);
        store.Update(p => p.Notifications.WhileFocused = false);
        await UntilAsync(() => Volatile.Read(ref platform.ActiveRings) == 0, "Focus suppression did not stop ringtone");
        notifications.Presence("ringer-test", false);
        await UntilAsync(() => Volatile.Read(ref platform.ActiveRings) == 1, "Background call did not ring");
        first.SetCanceled();
        await UntilAsync(() => Volatile.Read(ref platform.ActiveRings) == 0, "Cancelled/expired request kept ringing");
        count = platform.RingCount; await Task.Delay(150);
        Check(platform.RingCount == count, "Completed call restarted ringing");
        store.Update(p => p.Notifications = new());
        Console.WriteLine("PASS recurring native ringtone: cadence, single playback, live DND/contact/focus/volume rules and cancellation");
    }
}
