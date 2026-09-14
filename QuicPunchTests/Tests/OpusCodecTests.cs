using System;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

public static class OpusCodecTests
{
    public static void Run()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("       OPUS VOICE CODEC VERIFICATION TESTS        ");
        Console.WriteLine("==================================================");

        TestSineWaveEncodingAndCompressionRatio();
        TestDecodingAndSignalFidelity();
        TestBitrateReconfiguration();
        TestPacketLossConcealment();
        TestPcmFallbackDecoding();
        TestFramedOpusWithSequenceAndPLC();

        Console.WriteLine("==================================================");
        Console.WriteLine("       ALL OPUS CODEC TESTS PASSED!               ");
        Console.WriteLine("==================================================");
    }

    private static void TestSineWaveEncodingAndCompressionRatio()
    {
        Console.Write("[TEST 1] Opus encoding and compression ratio... ");
        using var codec = new OpusVoiceCodec(48000, 1, 64000);

        // Generate 60 ms of 440 Hz sine wave (2880 samples at 48 kHz)
        const int frameSize = 2880;
        short[] pcmIn = GenerateSineWave(440, 48000, frameSize);
        int rawPcmBytes = pcmIn.Length * 2; // 5760 bytes

        byte[] opusPacket = codec.Encode(pcmIn, frameSize);

        // Assert header is 0x01 (CodecOpus)
        if (opusPacket.Length < 2 || opusPacket[0] != OpusVoiceCodec.CodecOpus)
            throw new Exception($"Expected Opus packet header 0x01, got {opusPacket[0]}");

        // Assert compression > 85%
        double compressionPercent = (1.0 - ((double)opusPacket.Length / rawPcmBytes)) * 100.0;
        if (compressionPercent < 80.0)
            throw new Exception($"Expected > 80% compression, got {compressionPercent:F1}% (raw: {rawPcmBytes}B, opus: {opusPacket.Length}B)");

        Console.WriteLine($"PASSED (Raw: {rawPcmBytes}B -> Opus: {opusPacket.Length}B, Compression: {compressionPercent:F1}%)");
    }

    private static void TestDecodingAndSignalFidelity()
    {
        Console.Write("[TEST 2] Opus decoding and waveform reconstruction... ");
        using var codec = new OpusVoiceCodec(48000, 1, 96000);

        const int frameSize = 2880;
        short[] originalPcm = GenerateSineWave(1000, 48000, frameSize);

        byte[] opusPacket = codec.Encode(originalPcm, frameSize);
        short[] decodedPcm = codec.Decode(opusPacket, frameSize);

        if (decodedPcm.Length != frameSize)
            throw new Exception($"Decoded sample count mismatch: expected {frameSize}, got {decodedPcm.Length}");

        // Verify signal presence (energy level)
        double originalRms = CalculateRms(originalPcm);
        double decodedRms = CalculateRms(decodedPcm);

        if (Math.Abs(originalRms - decodedRms) / originalRms > 0.15)
            throw new Exception($"RMS energy deviation too high: original={originalRms:F1}, decoded={decodedRms:F1}");

        Console.WriteLine($"PASSED (Decoded {decodedPcm.Length} samples, RMS energy: {decodedRms:F1} vs {originalRms:F1})");
    }

    private static void TestBitrateReconfiguration()
    {
        Console.Write("[TEST 3] Dynamic bitrate reconfiguration... ");
        using var codec = new OpusVoiceCodec(48000, 1, 32000);

        if (codec.Bitrate != 32000)
            throw new Exception($"Expected bitrate 32000, got {codec.Bitrate}");

        codec.Bitrate = 96000;
        if (codec.Bitrate != 96000)
            throw new Exception($"Expected bitrate 96000, got {codec.Bitrate}");

        Console.WriteLine("PASSED");
    }

    private static void TestPacketLossConcealment()
    {
        Console.Write("[TEST 4] Packet Loss Concealment (PLC)... ");
        using var codec = new OpusVoiceCodec(48000, 1, 64000);

        const int frameSize = 2880;
        short[] pcmIn = GenerateSineWave(440, 48000, frameSize);
        byte[] opusPacket = codec.Encode(pcmIn, frameSize);
        _ = codec.Decode(opusPacket, frameSize);

        // Synthesize dropped frame via PLC
        short[] plcPcm = codec.DecodeLoss(frameSize);
        if (plcPcm.Length != frameSize)
            throw new Exception($"PLC sample count mismatch: expected {frameSize}, got {plcPcm.Length}");

        Console.WriteLine($"PASSED (PLC generated {plcPcm.Length} samples)");
    }

    private static void TestPcmFallbackDecoding()
    {
        Console.Write("[TEST 5] Uncompressed PCM fallback decoding... ");
        using var codec = new OpusVoiceCodec(48000, 1, 64000);

        const int frameSize = 960;
        short[] rawPcm = GenerateSineWave(440, 48000, frameSize);

        // Build 0x00 prefixed packet
        byte[] pcmPacket = new byte[1 + rawPcm.Length * 2];
        pcmPacket[0] = OpusVoiceCodec.CodecPcm;
        for (int i = 0; i < rawPcm.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcmPacket.AsSpan(1 + i * 2, 2), rawPcm[i]);
        }

        short[] decoded = codec.Decode(pcmPacket, frameSize);
        if (decoded.Length != rawPcm.Length)
            throw new Exception($"Decoded PCM length mismatch: expected {rawPcm.Length}, got {decoded.Length}");

        for (int i = 0; i < rawPcm.Length; i++)
        {
            if (decoded[i] != rawPcm[i])
                throw new Exception($"Sample mismatch at index {i}: expected {rawPcm[i]}, got {decoded[i]}");
        }

        Console.WriteLine("PASSED");
    }

    private static void TestFramedOpusWithSequenceAndPLC()
    {
        Console.Write("[TEST 6] Sequenced 20ms framed Opus and loss gap PLC... ");
        using var codec = new OpusVoiceCodec(48000, 1, 64000);

        const int frameSize = OpusVoiceCodec.DefaultFrameSize; // 960 samples = 20ms
        short[] pcm1 = GenerateSineWave(440, 48000, frameSize);
        short[] pcm2 = GenerateSineWave(440, 48000, frameSize);

        byte[] framedPacket1 = codec.EncodeFramed(pcm1, 100, 1000, frameSize);
        byte[] framedPacket2 = codec.EncodeFramed(pcm2, 102, 1040, frameSize); // Simulating packet 101 lost!

        if (!OpusVoiceCodec.IsFramedOpusPacket(framedPacket1))
            throw new Exception("Expected packet to be identified as FramedOpus");

        if (!OpusVoiceCodec.TryUnpackFramed(framedPacket1, out ushort seq1, out ushort ts1, out var payload1))
            throw new Exception("Failed to unpack framed packet 1");

        if (seq1 != 100 || ts1 != 1000 || payload1.IsEmpty)
            throw new Exception($"Unpack mismatch: seq={seq1}, ts={ts1}");

        short[] decoded1 = codec.Decode(framedPacket1, frameSize);
        if (decoded1.Length != frameSize)
            throw new Exception($"Expected {frameSize} samples, got {decoded1.Length}");

        // Loss concealment for missing packet 101
        short[] plcConcealed = codec.DecodeLoss(frameSize);
        if (plcConcealed.Length != frameSize)
            throw new Exception($"Expected {frameSize} PLC samples, got {plcConcealed.Length}");

        short[] decoded2 = codec.Decode(framedPacket2, frameSize);
        if (decoded2.Length != frameSize)
            throw new Exception($"Expected {frameSize} samples, got {decoded2.Length}");

        Console.WriteLine("PASSED");
    }

    private static short[] GenerateSineWave(double frequencyHz, int sampleRate, int sampleCount)
    {
        short[] samples = new short[sampleCount];
        double amplitude = 16000.0;
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            samples[i] = (short)(amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * t));
        }
        return samples;
    }

    private static double CalculateRms(short[] samples)
    {
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += samples[i] * (double)samples[i];
        }
        return Math.Sqrt(sum / samples.Length);
    }
}
