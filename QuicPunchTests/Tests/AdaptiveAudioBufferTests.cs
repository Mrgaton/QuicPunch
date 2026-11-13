using QuicPunch.Audio;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

public static class AdaptiveAudioBufferTests
{
    private sealed record Simulation(AudioBufferStatistics Final, int PeakTarget, long LateStageLoss,
        long RecoveryLoss, int MaximumBoundaryJump, int SilentActiveFrames);
    private static readonly byte[] TonePayload = MakeTone();

    public static void Run()
    {
        Assert(OpusVoiceCodec.DefaultFrameSize == 480 && OpusVoiceCodec.DefaultFrameDurationMs == 10, "Microphone packets must be 10ms");
        TestReorderingAndWrap();
        TestLegacyDurationsAndBounds();
        TestLossAndRecovery();
        TestHistoryExpiry();
        TestChangingSignal();
        var stable = Simulate(12_000, _ => 0);
        Assert(stable.Final.ConcealedPackets == 0 && stable.Final.TargetMs == 10, "Stable network must reach 10ms without concealment");
        Assert(stable.Final.BufferedMs < 35, "Actual stable latency must follow the target");
        var jitter = Simulate(12_000, t => (t / 10 * 17) % 9);
        Assert(jitter.Final.ConcealedPackets == 0, "Bounded 0-8ms jitter must not cause losses");
        var isolated = Simulate(12_000, t => t >= 4000 && t < 4080 ? 4080 - t : 0);
        Assert(isolated.PeakTarget <= 20 && isolated.Final.TargetMs == 10, "One 80ms burst must not inflate persistent latency");
        var repeated = Simulate(40_000, t => t >= 2000 && t < 18_000 && t % 2000 < 40 ? 40 - t % 2000 : 0);
        Assert(repeated.PeakTarget >= 40, "Repeated bursts must increase reserve");
        Assert(repeated.LateStageLoss == 0, "Learned recurring bursts still cause concealment");
        Assert(repeated.Final.TargetMs == 10 && repeated.Final.BufferedMs < 35, "Stable recovery must reduce target AND real queued audio");
        Assert(repeated.RecoveryLoss == 0, "Reducing latency must not empty the stream");
        var routeChange = Simulate(20_000, t => t >= 4000 ? 40 : 0);
        Assert(routeChange.Final.TargetMs == 10 && routeChange.Final.BufferedMs < 35, "Stable route latency was mistaken for permanent jitter");
        var loss = Simulate(12_000, _ => 0, t => t > 1000 && t % 970 == 0);
        Assert(loss.Final.ConcealedPackets > 0 && loss.Final.FecAttempts > 0, "Loss trace did not exercise PLC/FEC");
        var drift = Simulate(180_000, _ => 0, clockDrift: 1.0002);
        Assert(drift.Final.ConcealedPackets == 0 && drift.Final.BufferedMs < 35 && drift.Final.HistorySamples <= 6001,
            "Clock drift accumulated latency or exhausted playback");
        var fastClock = Simulate(180_000, _ => 0, clockDrift: 0.9998);
        Assert(fastClock.Final.ConcealedPackets == 0 && fastClock.Final.OverflowPackets == 0 && fastClock.Final.BufferedMs < 35,
            "Fast sender clock accumulated latency or dropped audio");
        foreach (var result in new[] { stable, jitter, isolated, repeated, routeChange, loss, drift, fastClock })
        {
            Assert(result.MaximumBoundaryJump < 2500, $"Abrupt waveform join: {result.MaximumBoundaryJump}");
            Assert(result.SilentActiveFrames == 0, "Active tone unexpectedly contains a silent 10ms block");
        }
        Console.WriteLine($"PASSED adaptive audio: stable={stable.Final.TargetMs}ms/{stable.Final.BufferedMs:F1}ms queued; jitter PLC={jitter.Final.ConcealedPackets}; isolated peak={isolated.PeakTarget}ms; recurring peak={repeated.PeakTarget}ms -> {repeated.Final.TargetMs}ms/{repeated.Final.BufferedMs:F1}ms queued; learned-burst PLC={repeated.LateStageLoss}; largest boundary jump={new[] { stable, jitter, isolated, repeated }.Max(r => r.MaximumBoundaryJump)}");
    }

    private static Simulation Simulate(int duration, Func<int, int> extraDelay, Func<int, bool>? drop = null, double clockDrift = 1,
        IReadOnlyList<byte[]>? encoded = null)
    {
        double now = 0;
        using var context = new PeerAudioContext(() => now);
        // Keep the sender running beyond the observation window, including a fast
        // sender clock. Draining a finite fixture would otherwise look like loss.
        var arrivals = Enumerable.Range(0, encoded?.Count ?? duration / 10 + 100).Where(i => drop?.Invoke(i * 10) != true)
            .Select(i => (Sent: i * 10, At: i * 10 * clockDrift + 10 + extraDelay(i * 10), Seq: (ushort)i))
            .OrderBy(p => p.At).ThenBy(p => p.Seq).ToArray();
        int index = 0, peak = 0, jump = 0, silent = 0;
        long at12 = 0, at18 = 0, at25 = 0;
        short previous = 0;
        for (int tick = 0; tick < duration; tick += 10)
        {
            while (index < arrivals.Length && arrivals[index].At <= tick)
            {
                var p = arrivals[index++]; now = p.At;
                context.Enqueue(encoded == null ? Packet(p.Seq, (ushort)p.Sent) : encoded[p.Sent / 10]);
            }
            now = tick;
            short[] output = context.ReadFrame();
            Assert(output.Length == 480, "Playback must return exactly 10ms on every tick");
            var stats = context.Statistics;
            peak = Math.Max(peak, stats.TargetMs);
            if (tick >= 500)
            {
                jump = Math.Max(jump, Math.Abs(output[0] - previous));
                for (int i = 1; i < output.Length; i++) jump = Math.Max(jump, Math.Abs(output[i] - output[i - 1]));
                if (output.All(v => v == 0)) silent++;
            }
            previous = output[^1];
            if (tick == 12_000) at12 = stats.ConcealedPackets;
            if (tick == 18_000) at18 = stats.ConcealedPackets;
            if (tick == 25_000) at25 = stats.ConcealedPackets;
        }
        var final = context.Statistics;
        Console.WriteLine($"  trace {duration}ms: target={final.TargetMs}, peak={peak}, queued={final.BufferedMs:F2}, PLC={final.ConcealedPackets}, late={final.LatePackets}, jump={jump}, silent={silent}, learnedPLC={at18-at12}");
        return new(final, peak, at18 - at12, final.ConcealedPackets - at25, jump, silent);
    }

    private static void TestReorderingAndWrap()
    {
        double now = 0;
        using var ordered = new PeerAudioContext(() => now);
        using var reordered = new PeerAudioContext(() => now);
        var packets = Enumerable.Range(0, 8).Select(i => Packet(unchecked((ushort)(65534 + i)), (ushort)(65520 + i * 10))).ToArray();
        for (int i = 0; i < packets.Length; i++) ordered.Enqueue(packets[i]);
        foreach (int i in new[] { 0, 2, 1, 3, 5, 4, 6, 7 }) reordered.Enqueue(packets[i]);
        reordered.Enqueue(packets[2]);
        for (int i = 0; i < 6; i++)
        {
            now = 20 + i * 10;
            Assert(ordered.ReadFrame().SequenceEqual(reordered.ReadFrame()), "Reordering changed decoded PCM");
        }
        Assert(reordered.Statistics.ConcealedPackets == 0 && reordered.Statistics.DuplicatePackets == 1, "Reordered packets should be recovered before playout");
        reordered.Enqueue(packets[0]);
        Assert(reordered.Statistics.LatePackets == 1, "Expired packet must not rewind decoder");
        Console.WriteLine("PASSED exact PCM after reordering, duplicates, sequence/timestamp wrap and late rejection");
    }

    private static void TestChangingSignal()
    {
        using var codec = new OpusVoiceCodec();
        var packets = new List<byte[]>();
        double phase = 0;
        for (int frame = 0; frame < 800; frame++)
        {
            var samples = new short[480];
            for (int i = 0; i < samples.Length; i++)
            {
                double time = (frame * 480 + i) / 48000.0;
                double frequency = 160 + 60 * Math.Sin(time * 2.1);
                phase += 2 * Math.PI * frequency / 48000;
                double envelope = 0.25 + 0.75 * Math.Pow(Math.Sin(time * 6.7), 2);
                samples[i] = (short)(envelope * (5000 * Math.Sin(phase) + 1800 * Math.Sin(phase * 3) + 700 * Math.Sin(phase * 7)));
            }
            packets.Add(codec.EncodeFramed(samples, (ushort)frame, (ushort)(frame * 10)));
        }
        var clean = Simulate(8000, _ => 0, encoded: packets);
        var impaired = Simulate(8000, t => t >= 3000 && t < 3040 ? 3040 - t : t % 170 == 0 ? 15 : 0,
            t => t > 1000 && t % 1970 == 0, encoded: packets);
        Assert(impaired.MaximumBoundaryJump < Math.Max(1500, clean.MaximumBoundaryJump * 3), "Changing voice-like signal acquired sharp transients");
        Assert(impaired.SilentActiveFrames == 0, "Changing signal lost output during jitter/loss");
        Console.WriteLine($"PASSED varying pitch/envelope/harmonics with jitter, reordering and loss: max sample delta clean={clean.MaximumBoundaryJump}, impaired={impaired.MaximumBoundaryJump}");
    }

    private static void TestLegacyDurationsAndBounds()
    {
        double now = 0;
        using var context = new PeerAudioContext(() => now);
        // Older 60ms PCM payloads must be split across ticks, not truncated to 10ms.
        byte[] pcm = new byte[1 + 2880 * 2];
        for (int i = 0; i < 2880; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(1 + i * 2), 6000);
        context.Enqueue(pcm);
        int nonzero = 0;
        for (int i = 0; i < 6; i++) { now = 20 + i * 10; nonzero += context.ReadFrame().Count(v => v != 0); }
        Assert(nonzero > 2800, "Legacy 60ms PCM was truncated");
        using var codec = new OpusVoiceCodec();
        foreach (int duration in new[] { 10, 20, 40, 60 })
        {
            using var legacy = new PeerAudioContext(() => now);
            legacy.Enqueue(codec.Encode(new short[duration * 48], duration * 48));
            Assert(legacy.Statistics.BufferedMs == duration, "Opus duration detection failed");
        }
        for (int i = 0; i < 100; i++) context.Enqueue(Packet((ushort)i, (ushort)(i * 10)));
        Assert(context.Statistics.BufferedMs <= PeerAudioContext.MaximumBufferedMs, "Unbounded burst queue");
        context.Enqueue([2, 0, 0]); context.Enqueue([0, 1, 2, 3]);
        Assert(context.Statistics.InvalidPackets >= 2, "Malformed packets were accepted");
        Console.WriteLine("PASSED legacy 20/40/60ms, bounded bursts and malformed input");
    }

    private static void TestLossAndRecovery()
    {
        double now = 0;
        using var context = new PeerAudioContext(() => now);
        context.Enqueue(Packet(0, 0)); context.Enqueue(Packet(2, 20)); context.Enqueue(Packet(3, 30));
        for (int i = 0; i < 8; i++) { now = 20 + i * 10; Assert(context.ReadFrame().Length == 480, "Missing output during loss"); }
        Assert(context.Statistics.FecAttempts >= 1, "Available next packet was not used for FEC");
        for (int i = 0; i < 30; i++) { now += 10; context.ReadFrame(); }
        Assert(context.ReadFrame().All(v => v == 0), "PLC must stop after a long interruption");
        now += 2500;
        context.Enqueue(Packet(60000, 1000)); context.Enqueue(Packet(60001, 1010));
        now += 30;
        Assert(context.ReadFrame().Any(v => v != 0), "Talk spurt restart did not recover");
        Console.WriteLine("PASSED deadline PLC/FEC, bounded concealment and restart after silence");
    }

    private static void TestHistoryExpiry()
    {
        var delay = new AdaptiveAudioDelay();
        for (int t = 0; t < 10_000; t += 10) delay.Observe(t, t % 2000 == 0 && t > 0 ? 50 : 0);
        Assert(delay.TargetMs >= 50 && delay.EpisodeCount >= 3, "Minute history did not learn recurrence");
        for (int t = 10_000; t < 30_000; t += 10) delay.Observe(t, 0);
        Assert(delay.TargetMs == 10 && delay.HistoryCount > 0, "Stable network did not lower target while retaining history");
        delay.Evaluate(90_000);
        Assert(delay.HistoryCount == 0 && delay.EpisodeCount == 0, "History exceeded one minute");
        Console.WriteLine("PASSED recurrence history, stable decay and 60-second expiry");
    }

    private static byte[] MakeTone()
    {
        using var codec = new OpusVoiceCodec();
        byte[] packet = [];
        var samples = Enumerable.Range(0, 480).Select(i => (short)(6000 * Math.Sin(i * 2 * Math.PI * 500 / 48000))).ToArray();
        for (int i = 0; i < 30; i++) packet = codec.EncodeFramed(samples, 0, 0);
        return packet[5..];
    }
    private static byte[] Packet(ushort sequence, ushort timestamp)
    {
        var packet = new byte[5 + TonePayload.Length]; packet[0] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), sequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(3), timestamp);
        TonePayload.CopyTo(packet, 5); return packet;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
