using System;
using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;

namespace QuicPunch.Helpers;

/// <summary>
/// Managed RFC 6716 Opus audio codec encoder and decoder based on Concentus.
/// Provides high-efficiency voice compression (48 kHz VoIP, ~80-90% bandwidth reduction over raw PCM),
/// in-band Forward Error Correction (FEC), and Packet Loss Concealment (PLC).
/// </summary>
public sealed class OpusVoiceCodec : IDisposable
{
    public const byte CodecOpus = 0x01;
    public const byte CodecPcm = 0x00;
    public const byte CodecOpusFramed = 0x02;

    public const int FramedHeaderSize = 5; // [Codec (1B)][SeqNum (2B)][Timestamp (2B)]

    public const int DefaultSampleRate = 48000;
    public const int DefaultChannels = 1;
    public const int DefaultFrameDurationMs = 20; // 20 ms frames (Discord / WebRTC standard for ultra-low latency)
    public const int DefaultFrameSize = (DefaultSampleRate * DefaultFrameDurationMs) / 1000; // 960 samples

    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;
    private readonly object _encodeLock = new();
    private readonly object _decodeLock = new();
    private readonly byte[] _encodeBuffer = new byte[4000]; // Max Opus packet size
    private int _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public int Bitrate
    {
        get => _encoder.Bitrate;
        set => _encoder.Bitrate = Math.Clamp(value, 8000, 128000);
    }

    public OpusVoiceCodec(int sampleRate = DefaultSampleRate, int channels = DefaultChannels, int bitrate = 64000)
    {
        SampleRate = sampleRate;
        Channels = channels;

        _encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = bitrate;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _encoder.Complexity = 10;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;

        _decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
    }

    /// <summary>
    /// Checks if a voice payload is flagged as an Opus-encoded frame (legacy 0x01 or framed 0x02).
    /// </summary>
    public static bool IsOpusPacket(ReadOnlySpan<byte> data)
    {
        return data.Length > 1 && (data[0] == CodecOpus || data[0] == CodecOpusFramed);
    }

    /// <summary>
    /// Checks if a voice payload is flagged as a sequenced, framed Opus packet (0x02).
    /// </summary>
    public static bool IsFramedOpusPacket(ReadOnlySpan<byte> data)
    {
        return data.Length >= FramedHeaderSize && data[0] == CodecOpusFramed;
    }

    /// <summary>
    /// Checks if a voice payload is flagged as uncompressed raw PCM.
    /// </summary>
    public static bool IsPcmPacket(ReadOnlySpan<byte> data)
    {
        return data.Length > 1 && data[0] == CodecPcm;
    }

    /// <summary>
    /// Extracts sequence number, timestamp, and payload from a voice packet if framed.
    /// </summary>
    public static bool TryUnpackFramed(ReadOnlySpan<byte> packet, out ushort sequenceNumber, out ushort timestamp, out ReadOnlySpan<byte> opusPayload)
    {
        if (packet.Length >= FramedHeaderSize && packet[0] == CodecOpusFramed)
        {
            sequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(1, 2));
            timestamp = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(3, 2));
            opusPayload = packet.Slice(FramedHeaderSize);
            return true;
        }

        if (packet.Length > 1 && packet[0] == CodecOpus)
        {
            sequenceNumber = 0;
            timestamp = 0;
            opusPayload = packet.Slice(1);
            return true;
        }

        sequenceNumber = 0;
        timestamp = 0;
        opusPayload = default;
        return false;
    }

    /// <summary>
    /// Encodes raw 16-bit signed PCM samples into an Opus packet prefixed with 0x01 (CodecOpus).
    /// </summary>
    public byte[] Encode(short[] pcmSamples, int frameSize = DefaultFrameSize)
    {
        return Encode(pcmSamples.AsSpan(), frameSize);
    }

    /// <summary>
    /// Encodes a span of 16-bit signed PCM samples into an Opus packet prefixed with 0x01 (CodecOpus).
    /// </summary>
    public byte[] Encode(ReadOnlySpan<short> pcmSamples, int frameSize = DefaultFrameSize)
    {
        lock (_encodeLock)
        {
            Span<byte> outPayload = _encodeBuffer.AsSpan(1);
            int encodedLen = _encoder.Encode(pcmSamples, frameSize, outPayload, outPayload.Length);
            _encodeBuffer[0] = CodecOpus;

            byte[] packet = new byte[encodedLen + 1];
            Buffer.BlockCopy(_encodeBuffer, 0, packet, 0, encodedLen + 1);
            return packet;
        }
    }

    /// <summary>
    /// Encodes a span of 16-bit signed PCM samples into a sequenced, framed Opus packet prefixed with 0x02.
    /// </summary>
    public byte[] EncodeFramed(ReadOnlySpan<short> pcmSamples, ushort sequenceNumber, ushort timestamp, int frameSize = DefaultFrameSize)
    {
        lock (_encodeLock)
        {
            Span<byte> outPayload = _encodeBuffer.AsSpan(FramedHeaderSize);
            int encodedLen = _encoder.Encode(pcmSamples, frameSize, outPayload, outPayload.Length);
            _encodeBuffer[0] = CodecOpusFramed;
            BinaryPrimitives.WriteUInt16LittleEndian(_encodeBuffer.AsSpan(1, 2), sequenceNumber);
            BinaryPrimitives.WriteUInt16LittleEndian(_encodeBuffer.AsSpan(3, 2), timestamp);

            byte[] packet = new byte[encodedLen + FramedHeaderSize];
            Buffer.BlockCopy(_encodeBuffer, 0, packet, 0, encodedLen + FramedHeaderSize);
            return packet;
        }
    }

    /// <summary>
    /// Encodes a raw byte buffer of 16-bit Little Endian PCM samples.
    /// </summary>
    public byte[] Encode(ReadOnlySpan<byte> pcm16Bytes, int frameSize = DefaultFrameSize)
    {
        int sampleCount = pcm16Bytes.Length / 2;
        short[] samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16Bytes.Slice(i * 2, 2));
        }

        return Encode(samples.AsSpan(), frameSize);
    }

    /// <summary>
    /// Decodes an incoming audio packet. If the packet starts with CodecOpusFramed (0x02) or CodecOpus (0x01),
    /// it decompresses using Opus. If it starts with CodecPcm (0x00), it returns the raw PCM samples.
    /// </summary>
    public short[] Decode(ReadOnlySpan<byte> packet, int frameSize = DefaultFrameSize, bool decodeFec = false)
    {
        if (packet.Length <= 1)
            return Array.Empty<short>();

        byte codec = packet[0];
        ReadOnlySpan<byte> payload;

        if (codec == CodecOpusFramed)
        {
            if (packet.Length < FramedHeaderSize)
                return Array.Empty<short>();
            payload = packet.Slice(FramedHeaderSize);
        }
        else if (codec == CodecOpus)
        {
            payload = packet.Slice(1);
        }
        else // Fallback to raw PCM
        {
            payload = packet.Slice(1);
            int sampleCount = payload.Length / 2;
            short[] samples = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(i * 2, 2));
            }
            return samples;
        }

        lock (_decodeLock)
        {
            int bufferSize = Math.Max(frameSize, 2880);
            short[] pcmOut = new short[bufferSize];
            int decodedSamples = _decoder.Decode(payload, pcmOut.AsSpan(), bufferSize, decodeFec);
            if (decodedSamples < bufferSize)
            {
                Array.Resize(ref pcmOut, decodedSamples);
            }
            return pcmOut;
        }
    }

    /// <summary>
    /// Performs Packet Loss Concealment (PLC) for a dropped or missing frame.
    /// </summary>
    public short[] DecodeLoss(int frameSize = DefaultFrameSize)
    {
        lock (_decodeLock)
        {
            short[] pcmOut = new short[frameSize];
            int decodedSamples = _decoder.Decode(ReadOnlySpan<byte>.Empty, pcmOut.AsSpan(), frameSize, false);
            if (decodedSamples < frameSize)
            {
                Array.Resize(ref pcmOut, decodedSamples);
            }
            return pcmOut;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _encoder.ResetState();
        _decoder.ResetState();
    }
}
