using System;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using QuicNet.Protocols;
using QuicNet.WebUi;
using QuicNet.WebUi.Modules;
using QuicPunch;
using QuicPunch.Audio;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

public static class NativeAudioServiceTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("     NATIVE AUDIO SERVICE VERIFICATION TESTS      ");
        Console.WriteLine("==================================================");

        TestBackendCreation();
        TestCaptureFailureAndRetry();
        TestMuteDeafenState();
        TestMasterAndPeerVolumeScaling();
        TestOrderedAudioPackets();
        await TestWindowsMixerClockAsync();
        await TestIncomingPeerAudioMixingAsync();
        TestLoopbackMode();
        await TestDuplexAudioAsync();
        await TestWindowsPlaybackRingAsync();
        await TestPlaybackRecoversFromStallAsync();
        await TestMicrophoneBitrateConfigurationAsync();

        Console.WriteLine("==================================================");
        Console.WriteLine("     ALL NATIVE AUDIO TESTS PASSED!               ");
        Console.WriteLine("==================================================");
    }

    private static void TestCaptureFailureAndRetry()
    {
        using var backend = new CaptureTestBackend { FailCapture = true };
        using var service = new NativeAudioService(backend);
        service.Start();
        if (service.IsCaptureRunning || service.CaptureError == null || !service.IsPlaybackRunning)
            throw new Exception("Capture failure must be visible without disabling playback.");
        backend.FailCapture = false;
        service.SetInputDevice("retry");
        if (!service.IsCaptureRunning || service.CaptureError != null)
            throw new Exception("Retry did not clear the microphone error.");
        service.Stop();
        if (service.IsCaptureRunning || service.IsPlaybackRunning)
            throw new Exception("Stop left audio devices reported as active.");
        Console.WriteLine("PASSED microphone failure reporting and retry");
    }

    private static void TestOrderedAudioPackets()
    {
        using var codec = new OpusVoiceCodec();
        using var context = new PeerAudioContext();
        using var reference = new PeerAudioContext();
        short[] tone = Enumerable.Range(0, 960).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 6000)).ToArray();
        var packets = new List<byte[]>();
        foreach (ushort sequence in new ushort[] { 65534, 65535, 0, 1, 3 }) packets.Add(codec.EncodeFramed(tone, sequence, 0));
        var actual = new List<short[]>(); var expected = new List<short[]>();
        foreach (var packet in packets) reference.ProcessIncomingPacket(packet, expected.Add);
        context.ProcessIncomingPacket(packets[0], actual.Add);
        context.ProcessIncomingPacket(packets[1], actual.Add);
        context.ProcessIncomingPacket(packets[1], actual.Add); // Duplicate.
        context.ProcessIncomingPacket(packets[0], actual.Add); // Late delivery.
        foreach (var packet in packets.Skip(2)) context.ProcessIncomingPacket(packet, actual.Add);
        if (actual.Count != expected.Count || actual.Where((samples, i) => !samples.SequenceEqual(expected[i])).Any() ||
            context.DroppedFramesCount != 1 || context.IgnoredPacketsCount != 2)
            throw new Exception("Late packets altered decoder continuity, loss accounting or sequence wraparound.");
        typeof(PeerAudioContext).GetField("_lastPacketAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(context, Environment.TickCount64 - 3000);
        int before = actual.Count;
        context.ProcessIncomingPacket(codec.EncodeFramed(tone, 60000, 0), actual.Add);
        if (actual.Count != before + 1 || context.DroppedFramesCount != 1)
            throw new Exception("A long interruption blocked resumed speech or synthesized a stale backlog.");
        Console.WriteLine("PASSED Opus duplicate/late rejection, wraparound and contiguous decoder state without extra buffering");
    }

    private static async Task TestWindowsMixerClockAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        static double[] Measure(bool precise)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var clock = precise ? WindowsAudioClock.TryCreate(cancel.Token) : null;
            if (precise && clock == null) throw new Exception("Windows high-resolution clock unavailable on the test host.");
            using var standard = precise ? null : new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            var intervals = new List<double>();
            long previous = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < 40; i++)
            {
                if (!(clock?.WaitForNextTick() ?? standard!.WaitForNextTickAsync(cancel.Token).AsTask().GetAwaiter().GetResult()))
                    throw new Exception("Mixer clock stopped unexpectedly.");
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                intervals.Add((now - previous) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); previous = now;
            }
            if (clock != null)
            {
                Thread.Sleep(85); clock.WaitForNextTick();
                if (clock.MissedDeadlines < 2) throw new Exception("A stalled clock did not skip expired deadlines.");
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                cancel.Cancel();
                if (clock.WaitForNextTick() || elapsed.ElapsedMilliseconds > 250)
                    throw new Exception("Native mixer wait did not observe cancellation.");
            }
            return intervals.Skip(1).ToArray(); // Exclude JIT/first-tick startup.
        }
        var baseline = await Task.Factory.StartNew(() => Measure(false), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var precise = await Task.Factory.StartNew(() => Measure(true), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        static string Describe(double[] values) => $"mean={values.Average():F2}ms, min={values.Min():F2}ms, max={values.Max():F2}ms, p95 error={values.Select(v => Math.Abs(v-20)).Order().ElementAt((int)(values.Length*.95)):F2}ms";
        Console.WriteLine("Mixer cadence, standard: " + Describe(baseline));
        Console.WriteLine("Mixer cadence, Windows precise: " + Describe(precise));
        Console.WriteLine("PASSED Windows deadline clock, stall recovery and cancellation (no added audio queue)");
    }

    private static void TestBackendCreation()
    {
        Console.Write("[TEST 1] NativeAudioService backend initialization... ");
        using var service = new NativeAudioService();
        string backend = service.BackendName;

        if (string.IsNullOrEmpty(backend))
            throw new Exception("Expected non-empty BackendName");

        var inputs = service.GetInputDevices();
        var outputs = service.GetOutputDevices();

        if (inputs == null || outputs == null)
            throw new Exception("Input or output devices returned null");

        Console.WriteLine($"PASSED (Backend: {backend}, Inputs: {inputs.Count}, Outputs: {outputs.Count})");
    }

    private static void TestMuteDeafenState()
    {
        Console.Write("[TEST 2] Local mute and deafen controls... ");
        using var service = new NativeAudioService();

        if (service.IsMuted || service.IsDeafened)
            throw new Exception("Expected initial mute and deafen state to be false");

        service.IsMuted = true;
        if (!service.IsMuted)
            throw new Exception("Expected IsMuted == true after setting true");

        service.IsDeafened = true;
        if (!service.IsDeafened)
            throw new Exception("Expected IsDeafened == true after setting true");

        service.IsMuted = false;
        service.IsDeafened = false;
        if (service.IsMuted || service.IsDeafened)
            throw new Exception("Expected mute and deafen to be reset to false");

        Console.WriteLine("PASSED");
    }

    private static void TestMasterAndPeerVolumeScaling()
    {
        Console.Write("[TEST 3] Master and peer volume adjustment... ");
        using var service = new NativeAudioService();

        service.MasterVolume = 1.5f;
        if (Math.Abs(service.MasterVolume - 1.5f) > 0.001f)
            throw new Exception($"Expected MasterVolume to be 1.5, got {service.MasterVolume}");

        // Test clamping
        service.MasterVolume = 3.0f;
        if (service.MasterVolume > 2.0f)
            throw new Exception($"Expected MasterVolume clamped to 2.0, got {service.MasterVolume}");

        service.MasterVolume = -0.5f;
        if (service.MasterVolume < 0.0f)
            throw new Exception($"Expected MasterVolume clamped to 0.0, got {service.MasterVolume}");

        Guid peerId = Guid.NewGuid();
        service.SetPeerVolume(peerId, 0.8f);
        if (Math.Abs(service.GetPeerVolume(peerId) - 0.8f) > 0.001f)
            throw new Exception($"Expected peer volume to be 0.8, got {service.GetPeerVolume(peerId)}");

        service.SetPeerMuted(peerId, true);
        if (!service.GetPeerMuted(peerId))
            throw new Exception("Expected peer to be muted");

        service.SetPeerMuted(peerId, false);
        if (service.GetPeerMuted(peerId))
            throw new Exception("Expected peer mute to be false");

        Console.WriteLine("PASSED");
    }

    private static async Task TestIncomingPeerAudioMixingAsync()
    {
        Console.Write("[TEST 4] Opus packet decoding and peer jitter queue... ");
        using var backend = new CaptureTestBackend();
        using var service = new NativeAudioService(backend);
        service.Start();

        // Encode a test frame with Opus
        using var codec = new OpusVoiceCodec(48000, 1, 64000);
        short[] sine = new short[2880];
        for (int i = 0; i < sine.Length; i++)
        {
            sine[i] = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 48000.0) * 16000.0);
        }
        byte[] opusPacket = codec.Encode(sine, sine.Length);

        Guid peerId = Guid.NewGuid();
        bool levelFired = false;
        service.OnPeerAudioLevel += (id, level) =>
        {
            if (id == peerId && level > 0.01f)
                levelFired = true;
        };

        // Enqueue audio packet for peer
        service.EnqueueIncomingAudio(peerId, opusPacket);

        // Allow background mixing thread to decode and process frame
        await Task.Delay(150);

        service.RemovePeer(peerId);
        service.Stop();
        Console.WriteLine($"PASSED (Audio level fired: {levelFired})");
    }

    private static void TestLoopbackMode()
    {
        Console.Write("[TEST 5] Mic loopback diagnostic test mode... ");
        using var backend = new CaptureTestBackend();
        using var service = new NativeAudioService(backend);
        service.Start("selected-microphone", "selected-speaker");
        bool metered = false;
        service.OnLocalAudioLevel += level => metered |= level > 0;

        if (service.IsTestLoopback)
            throw new Exception("Expected initial test loopback to be false");

        service.IsTestLoopback = true;
        if (!service.IsTestLoopback)
            throw new Exception("Expected IsTestLoopback == true");

        short[] frame = Enumerable.Repeat((short)3000, OpusVoiceCodec.DefaultFrameSize).ToArray();
        backend.Capture(frame);
        if (!metered || !backend.Played.TryDequeue(out var echoed) || !echoed.SequenceEqual(frame))
            throw new Exception("Microphone test must deliver captured samples to playback, not just update the meter.");
        service.IsTestLoopback = false;
        if (service.IsTestLoopback)
            throw new Exception("Expected IsTestLoopback == false");

        Console.WriteLine("PASSED");
    }

    private static async Task TestDuplexAudioAsync()
    {
        using var firstBackend = new CaptureTestBackend();
        using var secondBackend = new CaptureTestBackend();
        using var first = new NativeAudioService(firstBackend);
        using var second = new NativeAudioService(secondBackend);
        first.Start(); second.Start();
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        first.OnAudioPacketReady += data => second.EnqueueIncomingAudio(firstId, data);
        second.OnAudioPacketReady += data => first.EnqueueIncomingAudio(secondId, data);
        var frame = Enumerable.Range(0, OpusVoiceCodec.DefaultFrameSize)
            .Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 6000)).ToArray();
        for (int i = 0; i < 5; i++) { firstBackend.Capture(frame); secondBackend.Capture(frame); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!firstBackend.Played.Any(p => p.Any(v => v != 0)) || !secondBackend.Played.Any(p => p.Any(v => v != 0)))
            await Task.Delay(10, timeout.Token);
        Console.WriteLine("PASSED bidirectional microphone -> framed Opus -> decode -> mixer -> output");
    }

    public static async Task TestWindowsPlaybackRingAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var backend = new WindowsWaveAudioBackend();
        if (!backend.GetOutputDevices().Any(d => !d.IsDefault))
        {
            Console.WriteLine("SKIP WinMM hardware verification: no output device.");
            return;
        }
        for (int restart = 0; restart < 2; restart++)
        {
            if (!backend.StartPlayback()) throw new Exception(backend.PlaybackError);
            // Silence exercises the real driver without playing a tone or opening the microphone.
            for (int i = 0; i < 3; i++) backend.PlaySamples(new short[960]);
            if (backend.SubmittedPlaybackFrames != 3)
                throw new Exception("WinMM discarded its first frames before ever queueing audio.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (backend.CompletedPlaybackFrames < 3) await Task.Delay(10, timeout.Token);
            backend.PlaySamples(new short[480]);
            while (backend.CompletedPlaybackFrames < 4) await Task.Delay(10, timeout.Token);
            if (backend.PlaybackError != null) throw new Exception(backend.PlaybackError);
            backend.StopPlayback();
        }
        if (backend.StartPlayback(int.MaxValue.ToString()) || backend.PlaybackError == null)
            throw new Exception("Invalid output device must surface a playback error.");
        if (!backend.StartPlayback() || backend.PlaybackError != null)
            throw new Exception("Selecting a working output must clear the playback error.");
        Console.WriteLine("PASSED real WinMM initial buffers, completion, reuse, restart and device failure/retry");
    }

    private static async Task TestPlaybackRecoversFromStallAsync()
    {
        Console.Write("[TEST 6] Playback recovers with recent speech after a stalled output... ");
        using var backend = new StalledPlaybackBackend();
        using var service = new NativeAudioService(backend);
        service.Start();
        Guid peerId = Guid.NewGuid();
        static byte[] PcmFrame(short value)
        {
            byte[] packet = new byte[1 + OpusVoiceCodec.DefaultFrameSize * sizeof(short)];
            for (int i = 1; i < packet.Length; i += 2)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(i, 2), value);
            return packet;
        }
        try
        {
            service.EnqueueIncomingAudio(peerId, PcmFrame(1000));
            if (!backend.Entered.Wait(TimeSpan.FromSeconds(2))) throw new Exception("Mixer did not start playback.");
            for (short value = 1001; value <= 1010; value++) service.EnqueueIncomingAudio(peerId, PcmFrame(value));
            if (service.GetPeerBufferedAudioMs(peerId) > 60) throw new Exception("Stalled playback accumulated more than 60ms of speech.");
            backend.Resume.Set();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (backend.Played.Count < 4) await Task.Delay(10, timeout.Token);
            short[] played = backend.Played.ToArray();
            if (played[1] < 1008 || played[3] != 1010)
                throw new Exception("Playback retained stale speech after the output recovered.");
        }
        finally { backend.Resume.Set(); service.Stop(); }
        Console.WriteLine("PASSED");
    }

    private static async Task TestMicrophoneBitrateConfigurationAsync()
    {
        Console.Write("[TEST 7] WebUI bitrate controls captured packets and respects every active call... ");
        string appDataPath = Path.Combine(Path.GetTempPath(), "QuicPunch-Audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDataPath);
        try
        {
            using var node = new QuicPunch.QuicPunch(appDataPath: appDataPath, listeningPort: 0)
            {
                PeerTransport = new() { AutoConnectDiscoveredPeers = false }
            };
            using var backend = new CaptureTestBackend();
            using var handler = new VoiceCallHandler(node, backend);
            using var cancellation = new CancellationTokenSource();
            await using var hub = new WebUiWebSocketHub(cancellation);
            var module = new VoiceApiModule(handler, hub);
            using var referenceCodec = new OpusVoiceCodec();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var listener = new HttpListener();
            using var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            int port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            byte[]? lastPacket = null;
            int packetCount = 0;
            handler.AudioService.OnAudioPacketReady += packet => { lastPacket = packet; packetCount++; };
            handler.AudioService.Start();
            short[] samples = new short[OpusVoiceCodec.DefaultFrameSize];
            var random = new Random(1234);
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)random.Next(-16000, 16001);

            async Task SetBitrateAsync(int bitrate, HttpStatusCode expectedStatus = HttpStatusCode.OK)
            {
                Task<HttpListenerContext> accepting = listener.GetContextAsync();
                Task<HttpResponseMessage> sending = client.PostAsJsonAsync(
                    $"http://127.0.0.1:{port}/api/voice/codec-settings", new { bitrate });
                HttpListenerContext context = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    if (!await module.HandleRequestAsync("/api/voice/codec-settings", context.Request, context.Response))
                        throw new Exception("Voice settings endpoint was not handled.");
                }
                finally { context.Response.Close(); }
                using HttpResponseMessage response = await sending;
                if (response.StatusCode != expectedStatus)
                    throw new Exception($"Unexpected bitrate settings response: {response.StatusCode}.");
                if (expectedStatus == HttpStatusCode.OK)
                {
                    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (body.RootElement.GetProperty("bitrate").GetInt32() != handler.AudioService.Bitrate)
                        throw new Exception("The UI reported a different bitrate from the microphone encoder.");
                    if (body.RootElement.GetProperty("requestedBitrate").GetInt32() != bitrate)
                        throw new Exception("The UI settings response lost the requested bitrate.");
                }
            }

            async Task AssertVoiceStateAsync(int requested, int effective)
            {
                Task<HttpListenerContext> accepting = listener.GetContextAsync();
                Task<HttpResponseMessage> sending = client.GetAsync($"http://127.0.0.1:{port}/api/voice/state");
                HttpListenerContext context = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
                try { await module.HandleRequestAsync("/api/voice/state", context.Request, context.Response); }
                finally { context.Response.Close(); }
                using HttpResponseMessage response = await sending;
                response.EnsureSuccessStatusCode();
                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (body.RootElement.GetProperty("requestedOpusBitrate").GetInt32() != requested ||
                    body.RootElement.GetProperty("opusBitrate").GetInt32() != effective)
                    throw new Exception("Voice state conflated the requested bitrate with its adaptive limit.");
            }

            void AssertCapturedPacket(int expectedBitrate)
            {
                lastPacket = null;
                backend.Capture(samples);
                if (lastPacket == null || !OpusVoiceCodec.TryUnpackFramed(lastPacket, out ushort sequence, out ushort timestamp, out _))
                    throw new Exception("The microphone did not produce a sequenced Opus packet.");
                if (sequence != packetCount || handler.AudioService.Bitrate != expectedBitrate)
                    throw new Exception("Microphone sequence or effective bitrate diverged.");
                referenceCodec.Bitrate = expectedBitrate;
                byte[] expected = referenceCodec.EncodeFramed(samples, sequence, timestamp);
                if (!lastPacket.AsSpan().SequenceEqual(expected))
                    throw new Exception("The captured packet was not encoded at the configured bitrate.");
            }

            async Task ToggleMicrophoneTestAsync(bool enabled)
            {
                Task<HttpListenerContext> accepting = listener.GetContextAsync();
                Task<HttpResponseMessage> sending = client.PostAsJsonAsync($"http://127.0.0.1:{port}/api/voice/test-mic", new { enabled });
                var context = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
                try { await module.HandleRequestAsync("/api/voice/test-mic", context.Request, context.Response); }
                finally { context.Response.Close(); }
                using var response = await sending;
                response.EnsureSuccessStatusCode();
            }

            try
            {
                await AssertVoiceStateAsync(96000, 96000);
                AssertCapturedPacket(96000);
                handler.AudioService.Start("chosen-input", "chosen-output");
                handler.AudioService.Stop();
                await ToggleMicrophoneTestAsync(true);
                if (backend.InputId != "chosen-input" || backend.OutputId != "chosen-output")
                    throw new Exception("Test-mic replaced the selected audio devices with system defaults.");
                int starts = backend.InputStarts + backend.OutputStarts;
                await ToggleMicrophoneTestAsync(true);
                if (backend.InputStarts + backend.OutputStarts != starts)
                    throw new Exception("Test-mic unnecessarily restarted an active audio path.");
                await ToggleMicrophoneTestAsync(false);
                handler.AudioService.Start(handler.AudioService.CurrentInputDeviceId, handler.AudioService.CurrentOutputDeviceId);
                await SetBitrateAsync(8000);
                AssertCapturedPacket(8000);
                await SetBitrateAsync(128000);
                AssertCapturedPacket(128000);
                foreach (int bitrate in new[] { 160000, 192000, 256000, 300000 })
                {
                    await SetBitrateAsync(bitrate);
                    AssertCapturedPacket(bitrate);
                    await AssertVoiceStateAsync(bitrate, bitrate);
                }
                await SetBitrateAsync(300001, HttpStatusCode.BadRequest);
                AssertCapturedPacket(300000);
                await SetBitrateAsync(64000);
                AssertCapturedPacket(64000);

                using var firstPeer = new PeerInfo();
                using var secondPeer = new PeerInfo();
                using var firstSession = new VoiceCallHandler.VoiceCallSession(handler, firstPeer, new MemoryStream(),
                    QuicPunch.QuicConnection.CreateDummy(new MemoryStream(), QuicConnectionRole.Client));
                using var secondSession = new VoiceCallHandler.VoiceCallSession(handler, secondPeer, new MemoryStream(),
                    QuicPunch.QuicConnection.CreateDummy(new MemoryStream(), QuicConnectionRole.Client));
                handler.UpdateAudioBitrate(firstSession, 0.1, 150);
                AssertCapturedPacket(56000);
                handler.UpdateAudioBitrate(secondSession, 0, 20);
                AssertCapturedPacket(56000); // A healthy peer cannot undo another peer's limit.
                await SetBitrateAsync(128000);
                await AssertVoiceStateAsync(128000, 56000);
                AssertCapturedPacket(56000);
                await SetBitrateAsync(64000);
                handler.UpdateAudioBitrate(secondSession, 0.1, 150);
                handler.UpdateAudioBitrate(secondSession, 0.1, 150);
                AssertCapturedPacket(48000);
                secondSession.Dispose();
                handler.UpdateAudioBitrate(secondSession, 0.2, 200); // Late telemetry is ignored.
                AssertCapturedPacket(56000);
                handler.UpdateAudioBitrate(firstSession, 0, 20);
                AssertCapturedPacket(64000);
                await SetBitrateAsync(8000);
                handler.UpdateAudioBitrate(firstSession, 0.1, 150);
                AssertCapturedPacket(8000); // Congestion must never raise a low user setting.
                firstSession.Dispose();
                await SetBitrateAsync(64000);
                AssertCapturedPacket(64000);

                handler.AudioService.IsMuted = true;
                int beforeMute = packetCount;
                backend.Capture(samples);
                if (packetCount != beforeMute) throw new Exception("Muted microphone transmitted audio.");
                handler.AudioService.IsMuted = false;
                handler.AudioService.IsDeafened = true;
                AssertCapturedPacket(64000);

                Task settings = Task.Run(() =>
                {
                    for (int i = 0; i < 50; i++) handler.RequestedBitrate = i % 2 == 0 ? 8000 : 128000;
                });
                int beforeConcurrent = packetCount;
                for (int i = 0; i < 50; i++) backend.Capture(samples);
                await settings;
                if (packetCount != beforeConcurrent + 50)
                    throw new Exception("Concurrent bitrate changes interrupted microphone encoding.");
            }
            finally
            {
                cancellation.Cancel();
                handler.AudioService.Stop();
            }
        }
        finally { Directory.Delete(appDataPath, recursive: true); }
        Console.WriteLine("PASSED");
    }

    private sealed class CaptureTestBackend : INativeAudioBackend
    {
        private Action<short[]>? _onSamples;
        public string? InputId, OutputId;
        public int InputStarts, OutputStarts;
        public readonly System.Collections.Concurrent.ConcurrentQueue<short[]> Played = new();
        public string Name => "Test microphone";
        public bool FailCapture { get; set; }
        public string? CaptureError => FailCapture ? "Permission denied" : null;
        public bool IsSupported => true;
        public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => [];
        public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => [];
        public bool StartCapture(Action<short[]> onPcmSamples, string? deviceId = null) { InputStarts++; InputId = deviceId; _onSamples = FailCapture ? null : onPcmSamples; return !FailCapture; }
        public void Capture(short[] samples) => _onSamples?.Invoke(samples);
        public void StopCapture() => _onSamples = null;
        public bool StartPlayback(string? deviceId = null) { OutputStarts++; OutputId = deviceId; return true; }
        public void PlaySamples(short[] samples) { Played.Enqueue(samples.ToArray()); }
        public void StopPlayback() { }
        public void Dispose() { }
    }

    private sealed class StalledPlaybackBackend : INativeAudioBackend
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<short> Played = new();
        public readonly System.Threading.ManualResetEventSlim Entered = new();
        public readonly System.Threading.ManualResetEventSlim Resume = new();
        public string Name => "Test output";
        public bool IsSupported => true;
        public System.Collections.Generic.IReadOnlyList<AudioDeviceInfo> GetInputDevices() => [];
        public System.Collections.Generic.IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => [];
        public bool StartCapture(Action<short[]> onPcmSamples, string? deviceId = null) => true;
        public void StopCapture() { }
        public bool StartPlayback(string? deviceId = null) => true;
        public void PlaySamples(short[] samples)
        {
            Played.Enqueue(samples[0]);
            if (Played.Count == 1) { Entered.Set(); Resume.Wait(TimeSpan.FromSeconds(3)); }
        }
        public void StopPlayback() { }
        public void Dispose() { Resume.Set(); }
    }
}
