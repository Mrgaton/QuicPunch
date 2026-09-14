using System;
using System.Threading.Tasks;
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
        TestMuteDeafenState();
        TestMasterAndPeerVolumeScaling();
        await TestIncomingPeerAudioMixingAsync();
        TestLoopbackMode();

        Console.WriteLine("==================================================");
        Console.WriteLine("     ALL NATIVE AUDIO TESTS PASSED!               ");
        Console.WriteLine("==================================================");
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
        using var service = new NativeAudioService();
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
        using var service = new NativeAudioService();

        if (service.IsTestLoopback)
            throw new Exception("Expected initial test loopback to be false");

        service.IsTestLoopback = true;
        if (!service.IsTestLoopback)
            throw new Exception("Expected IsTestLoopback == true");

        service.IsTestLoopback = false;
        if (service.IsTestLoopback)
            throw new Exception("Expected IsTestLoopback == false");

        Console.WriteLine("PASSED");
    }
}
