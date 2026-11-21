using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using QuicNet.Notifications;

namespace QuicPunchTests.Tests;

internal static class OnboardingTests
{
    private sealed class Platform : IAppPlatform
    {
        public bool StartupSupported => true;
        public string Description => "Onboarding test";
        public bool Startup { get; private set; }
        public bool RejectStartup { get; set; }
        public void SetStartWithSystem(bool enabled)
        {
            if (RejectStartup) throw new IOException("Startup registration failed");
            Startup = enabled;
        }
        public Task NotifyAsync(AppNotice notice, bool desktop, CancellationToken ct) => Task.CompletedTask;
        public Task PlayRingtoneAsync(int volume, CancellationToken ct) => Task.CompletedTask;
    }

    internal static async Task RunAsync()
    {
        string directory = Directory.CreateTempSubdirectory("quicnet-onboarding-").FullName;
        try
        {
            string fresh = Path.Combine(directory, "fresh");
            await VerifyFirstRunAsync(fresh);
            await VerifyReopenAsync(fresh);

            string legacy = Path.Combine(directory, "legacy");
            Directory.CreateDirectory(legacy);
            string legacySettings = Path.Combine(legacy, "ui-settings.json");
            const string legacyJson = """{"DisplayName":"Perfil anterior","WanEnabled":false,"TorEnabled":true,"StartWithSystem":false,"RunInBackground":true}""";
            await File.WriteAllTextAsync(legacySettings, legacyJson);
            var previous = AppPreferencesStore.OpenForApp(legacy).Snapshot();
            Check(previous.OnboardingCompleted && previous.TorEnabled && !previous.WanEnabled &&
                !previous.StartWithSystem && previous.RunInBackground && previous.DisplayName == "Perfil anterior",
                "Existing profile and preferences changed");
            Check(await File.ReadAllTextAsync(legacySettings) == legacyJson, "Legacy file rewritten on open");

            string identityOnly = Path.Combine(directory, "identity-only");
            await using var node = new QuicPunch.QuicPunch(CancellationToken.None, true, appDataPath: identityOnly);
            Check(AppPreferencesStore.OpenForApp(identityOnly).Snapshot().OnboardingCompleted,
                "An existing identity without a settings file was treated as a fresh install");
            Console.WriteLine("PASS first run, persistence, transport selection and legacy installation detection");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task VerifyFirstRunAsync(string directory)
    {
        var initial = AppPreferencesStore.OpenForApp(directory);
        Check(!initial.Snapshot().OnboardingCompleted && !initial.Snapshot().WanEnabled, "Fresh install enabled WAN");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var node = new QuicPunch.QuicPunch(lifetime.Token, true, appDataPath: directory)
        {
            WanEnabled = false,
            EnableUpnp = false,
            DefaultTorRuntimeOptions = new QuicPunch.TorRuntimeOptions { ExecutablePath = Path.Combine(directory, "unavailable-tor") }
        };
        var settings = AppPreferencesStore.OpenForApp(directory);
        Check(!settings.Snapshot().OnboardingCompleted, "Reopening with generated identities skipped the unfinished onboarding");
        string? torName = node.TorCurrentPeer.Name;
        var platform = new Platform();
        int starts = 0;
        using var voice = new VoiceCallHandler(node);
        using var speed = new SpeedTestHandler();
        var server = new WebUiServer(node, new ChatHandler(), null, voice, speed, settings, lifetime,
            port: AvailablePort(), startAfterOnboarding: async selected =>
            {
                starts++;
                var saved = AppPreferencesStore.OpenForApp(directory).Snapshot();
                Check(saved.OnboardingCompleted && saved.TorEnabled && !saved.WanEnabled && saved.DisplayName == "Ada",
                    "The chosen mode and profile must be durable before the node starts");
                if (starts == 1) throw new IOException("Temporary startup failure");
                LocalUserProfile.Apply(node, selected);
                node.WanEnabled = selected.WanEnabled;
                await node.StartAsync(lifetime.Token);
            }) { AppPlatform = platform };
        server.Start();
        try
        {
            using var http = Client(server);
            using var firstPage = await http.GetAsync("settings.html?section=connection");
            Check(firstPage.StatusCode == HttpStatusCode.Redirect &&
                firstPage.Headers.Location?.OriginalString == "/onboarding.html", "First run did not open onboarding");
            string page = await http.GetStringAsync("onboarding.html");
            Check(page.Contains("¿Cómo quieres conectar?") && page.Contains("X-QuicPunch-CSRF"), "Onboarding or CSRF bootstrap missing");
            using var onboard = JsonDocument.Parse(await http.GetStringAsync("api/onboarding"));
            Check(onboard.RootElement.GetProperty("required").GetBoolean() &&
                onboard.RootElement.GetProperty("mode").ValueKind == JsonValueKind.Null, "Initial mode was preselected");
            using var noCsrf = await http.PostAsJsonAsync("api/onboarding", new { mode = "wan" });
            Check(noCsrf.StatusCode == HttpStatusCode.Forbidden, "Onboarding accepted a write without CSRF");
            http.DefaultRequestHeaders.Add("X-QuicPunch-CSRF", server.CsrfToken);
            using var earlyMode = await http.PostAsJsonAsync("api/connection-mode", new { mode = "wan" });
            Check(earlyMode.StatusCode == HttpStatusCode.Conflict, "A peer route bypassed the first-run selection");
            using var incomplete = await http.PostAsJsonAsync("api/onboarding", new { displayName = "Ada" });
            Check(incomplete.StatusCode == HttpStatusCode.BadRequest && !node.IsStarted && starts == 0,
                "Incomplete choices started the node");
            var choices = new { mode = "tor", displayName = "  Ada  ", startWithSystem = true, runInBackground = false };
            platform.RejectStartup = true;
            using var failed = await http.PostAsJsonAsync("api/onboarding", choices);
            Check(!failed.IsSuccessStatusCode && !settings.Snapshot().OnboardingCompleted && !node.IsStarted,
                "Failed platform registration completed onboarding");
            platform.RejectStartup = false;
            using var startupFailed = await http.PostAsJsonAsync("api/onboarding", choices);
            Check(!startupFailed.IsSuccessStatusCode && settings.Snapshot().OnboardingCompleted && !node.IsStarted,
                "A failed node startup lost its saved selection");
            using var complete = await http.PostAsJsonAsync("api/onboarding", new { mode = "wan" });
            complete.EnsureSuccessStatusCode();
            Check(node.IsStarted && !node.IsWanStarted && !node.WanEnabled, "Tor selection started WAN");
            Check(platform.Startup && !settings.Snapshot().RunInBackground, "Startup and background choices were not independent");
            Check(node.CurrentPeer.Name == "Ada" && node.TorCurrentPeer.Name == torName, "Profile did not retain the separate Tor identity");
            using var retry = await http.PostAsJsonAsync("api/onboarding", new { mode = "wan" });
            retry.EnsureSuccessStatusCode();
            Check(starts == 2 && !settings.Snapshot().WanEnabled, "Retry changed an already completed onboarding");
            using var status = JsonDocument.Parse(await http.GetStringAsync("api/profile"));
            Check(status.RootElement.GetProperty("customName").GetString() == "Ada", "Profile API lost the display name");
            while (node.TorLastError == null) await Task.Delay(25, lifetime.Token);
            Check(!node.IsWanStarted && settings.Snapshot().TorEnabled, "Unavailable Tor caused direct fallback");
            using var switchMode = await http.PostAsJsonAsync("api/connection-mode", new { mode = "wan" }, lifetime.Token);
            Check(switchMode.StatusCode == HttpStatusCode.Accepted, "WAN switch was not accepted");
            using var receipt = JsonDocument.Parse(await switchMode.Content.ReadAsStringAsync(lifetime.Token));
            long transitionId = receipt.RootElement.GetProperty("id").GetInt64();
            string? phase;
            do
            {
                using var snapshot = JsonDocument.Parse(await http.GetStringAsync("api/status", lifetime.Token));
                var transition = snapshot.RootElement.GetProperty("connectionMode");
                Check(transition.GetProperty("id").GetInt64() == transitionId, "The status reported a different transition");
                phase = transition.GetProperty("phase").GetString();
                Check(phase is "applying" or "canceling-tor" or "ready", $"WAN switch did not complete: {transition}");
                if (phase != "ready") await Task.Delay(25, lifetime.Token);
            }
            while (phase != "ready");
            Check(node.IsWanStarted && !settings.Snapshot().TorEnabled, "Changing to WAN after onboarding failed");
        }
        finally { await lifetime.CancelAsync(); server.Stop(); }
    }

    private static async Task VerifyReopenAsync(string directory)
    {
        var preferences = AppPreferencesStore.OpenForApp(directory);
        var saved = preferences.Snapshot();
        Check(saved.OnboardingCompleted && saved.WanEnabled && saved.DisplayName == "Ada" &&
            saved.StartWithSystem && !saved.RunInBackground, "Selections were not loaded after restarting");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var node = new QuicPunch.QuicPunch(lifetime.Token, true, appDataPath: directory);
        using var voice = new VoiceCallHandler(node);
        using var speed = new SpeedTestHandler();
        var server = new WebUiServer(node, new ChatHandler(), null, voice, speed, preferences, lifetime,
            port: AvailablePort(), startAfterOnboarding: _ => throw new InvalidOperationException("Onboarding ran on restart"));
        server.Start();
        try
        {
            using var http = Client(server);
            Check((await http.GetStringAsync("")).Contains("spaContainer"), "Reopen did not load the usual UI");
            using var wizard = await http.GetAsync("onboarding.html");
            Check(wizard.StatusCode == HttpStatusCode.Redirect && wizard.Headers.Location?.OriginalString == "/",
                "Completed installation reopened the wizard directly");
        }
        finally { await lifetime.CancelAsync(); server.Stop(); }
    }

    private static HttpClient Client(WebUiServer server) => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(server.Url),
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
