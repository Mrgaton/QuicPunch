using QuicNet.Video;
using QuicNet.Protocols;
using QuicNet.WebUi;
using QuicNet.WebUi.Modules;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;

namespace QuicPunchTests.Tests;

public static class ScreenAudioTests
{
    public static async Task RunAsync()
    {
        if (WindowsSystemAudioCapture.IsSupported)
        {
            // Verify real COM activation, process exclusion and PCM initialization.
            // Probe never starts the audio client or reads any system samples.
            await WindowsSystemAudioCapture.ProbeAsync();
            await WindowsSystemAudioCapture.ProbeAsync();
            Console.WriteLine("PASS Windows process-loopback exclusion: activation, 48 kHz PCM initialization, clean release and reopen; no audio captured.");
        }
        else Console.WriteLine("SKIP native process loopback: platform does not support it.");
        if (VoiceCallHandler.IsLocalScreenSharing) throw new Exception("Unexpected real sharing session in isolated test.");
        await VerifyAudioSessionGuardsAsync();
    }

    private static async Task VerifyAudioSessionGuardsAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuicNet-ScreenAudio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var node = new QuicPunch.QuicPunch(appDataPath: directory, listeningPort: 0) { PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
            using var handler = new VoiceCallHandler(node);
            handler.SystemAudioShareId = "current";
            await handler.StopSystemAudioAsync("previous");
            if (handler.SystemAudioShareId != "current") throw new Exception("A stale stop invalidated the current share's audio grant.");
            await handler.StopSystemAudioAsync("current");
            if (handler.SystemAudioShareId != null) throw new Exception("Stopping did not invalidate the audio grant.");

            using var cancellation = new CancellationTokenSource();
            await using var hub = new WebUiWebSocketHub(cancellation);
            var module = new VoiceApiModule(handler, hub);
            using var listener = new HttpListener();
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            const string path = "/api/voice/screen-share/system-audio/start";
            var accepting = listener.GetContextAsync();
            var sending = http.PostAsJsonAsync($"http://127.0.0.1:{port}" + path, new { audioSessionId = Guid.NewGuid() });
            var context = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                if (!await module.HandleRequestAsync(path, context.Request, context.Response)) throw new Exception("System audio route was not handled.");
            }
            finally { context.Response.Close(); }
            using var response = await sending;
            if (response.StatusCode != HttpStatusCode.Conflict) throw new Exception("Audio capture was allowed without an active share and matching session grant.");
            Console.WriteLine("PASS system audio HTTP guard: no capture without an active sharing session; stale stop isolation and grant revocation.");
        }
        finally
        {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(directory).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test directory escaped the temporary folder.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
