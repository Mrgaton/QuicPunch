using System.Diagnostics;
using Concentus;
using Concentus.Structs;
using QuicPunch.Helpers;

namespace QuicPunch.Audio;

public sealed record AudioBufferStatistics(int TargetMs, double BufferedMs, long ConcealedPackets,
    long FecAttempts, long LatePackets, long DuplicatePackets, long OverflowPackets, long InvalidPackets,
    int HistorySamples, int DelayEpisodes, double PlaybackRate);

/// <summary>
/// Orders encoded packets before decoding. ReadFrame is the only decoder consumer and
/// returns exactly 10 ms, including bounded concealment when a packet misses its turn.
/// </summary>
internal sealed class PeerAudioContext : IDisposable
{
    internal const int MaximumBufferedMs = 160;
    private const int SamplesPerMs = OpusVoiceCodec.DefaultSampleRate / 1000;
    private const int OutputSamples = OpusVoiceCodec.DefaultFrameSize;
    private readonly object _sync = new();
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(48000, 1);
    private readonly Func<double> _clock;
    private readonly AdaptiveAudioDelay _delay = new();
    private readonly SortedDictionary<long, Packet> _packets = new();
    private readonly List<short> _pcm = new(6240);
    private sealed record Packet(byte[] Payload, int Samples, bool Pcm);
    private long? _newestSequence;
    private long _nextSequence, _legacySequence;
    private ushort _newestTimestamp;
    private double _senderTime, _lastArrival, _firstArrival, _lastRead;
    private bool _started, _dormant, _disposed, _transition = true, _lastWasPcm;
    private int _packetSamples = OutputSamples, _missingSamples;
    private double _position, _rate = 1;
    private short _lastOutput;
    private long _concealed, _fec, _late, _duplicates, _overflow, _invalid;

    public PeerAudioContext(Func<double>? clock = null) =>
        _clock = clock ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    public AudioBufferStatistics Statistics
    {
        get { lock (_sync) return new(_delay.TargetMs, BufferedSamples / (double)SamplesPerMs,
            _concealed, _fec, _late, _duplicates, _overflow, _invalid,
            _delay.HistoryCount, _delay.EpisodeCount, _rate); }
    }
    private int BufferedSamples => _packets.Values.Sum(p => p.Samples) + _pcm.Count - (int)_position;

    public void Enqueue(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            if (_disposed) return;
            double now = _clock();
            bool framed = data.Length >= OpusVoiceCodec.FramedHeaderSize && data[0] == OpusVoiceCodec.CodecOpusFramed;
            bool pcm = data.Length > 1 && data[0] == OpusVoiceCodec.CodecPcm;
            int header = framed ? OpusVoiceCodec.FramedHeaderSize : 1;
            if (data.Length <= header || data.Length > 11521 || (!framed && !pcm && data[0] != OpusVoiceCodec.CodecOpus)) { _invalid++; return; }
            var payload = data[header..];
            int samples;
            try { samples = pcm ? payload.Length / 2 : OpusPacketInfo.GetNumSamples(payload, 48000); }
            catch { _invalid++; return; }
            if (samples < 120 || samples > 5760 || (pcm && payload.Length % 2 != 0)) { _invalid++; return; }
            ushort sequence = framed ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[1..]) : (ushort)++_legacySequence;
            ushort timestamp = framed ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[3..]) : (ushort)(long)now;
            // Muted microphones do not transmit. A fresh talk spurt needs a fresh
            // deadline, not seconds of synthetic losses or old decoder prediction.
            if (_newestSequence.HasValue && (now - _lastArrival > 2000 || (_dormant && now - _lastArrival > 120)))
                ResetStream();
            long extended = _newestSequence.HasValue
                ? _newestSequence.Value + (short)(sequence - (ushort)_newestSequence.Value) : sequence;
            if (_packets.ContainsKey(extended)) { _duplicates++; return; }
            if (!_newestSequence.HasValue)
            {
                _nextSequence = extended;
                _firstArrival = now;
                _senderTime = now;
                _newestTimestamp = timestamp;
                _packetSamples = samples;
            }
            double senderTime = _senderTime + (short)(timestamp - _newestTimestamp);
            bool advancing = !_newestSequence.HasValue || extended > _newestSequence.Value;
            // Observe late arrivals too: measuring only successful packets hides jitter.
            if (advancing)
            {
                _delay.Observe(now, now - senderTime);
                _senderTime = senderTime;
                _newestTimestamp = timestamp;
                _newestSequence = extended;
            }
            else if (_newestSequence is long newest && extended < newest) _delay.Observe(now, now - senderTime);
            if (_started && extended < _nextSequence)
            {
                _late++;
                // A route-delay step can put the entire stream behind its old
                // deadline. After 20ms of concealment, resync to a newly advancing
                // packet instead of discarding all subsequent speech indefinitely.
                // Ordinary reordered/duplicate packets never rewind decoder state.
                if (_missingSamples < 20 * SamplesPerMs || _packets.Count != 0 || !advancing) return;
                _nextSequence = extended; _pcm.Clear(); _position = 0;
                _decoder.ResetState(); _transition = true; _missingSamples = 0;
            }
            if (!_started) _nextSequence = Math.Min(_nextSequence, extended);
            _lastArrival = now;
            _packets[extended] = new(payload.ToArray(), samples, pcm);
            if (_started && (extended - _nextSequence) * _packetSamples > MaximumBufferedMs * SamplesPerMs)
            {
                // Bound sequence gaps as well as stored bytes (e.g. sender restart).
                foreach (long key in _packets.Keys.Where(k => k < extended).ToArray()) { _packets.Remove(key); _overflow++; }
                _nextSequence = extended; _pcm.Clear(); _position = 0;
                _decoder.ResetState(); _transition = true; _missingSamples = 0;
            }
            bool trimmed = false;
            while (BufferedSamples > MaximumBufferedMs * SamplesPerMs && _packets.Count > 1)
            {
                _packets.Remove(_packets.First().Key); _overflow++; trimmed = true;
            }
            if (trimmed)
            {
                _nextSequence = _packets.First().Key;
                _pcm.Clear(); _position = 0;
                _decoder.ResetState(); _transition = true;
            }
        }
    }

    public short[] ReadFrame()
    {
        lock (_sync)
        {
            var output = new short[OutputSamples];
            if (_disposed) return output;
            double now = _clock();
            _delay.Evaluate(now);
            // A stopped mixer must not replay speech queued before its suspension.
            if (_started && now - _lastRead > MaximumBufferedMs && _packets.Count > 0)
            {
                while (_packets.Count > 1 && BufferedSamples > (_delay.TargetMs + 20) * SamplesPerMs)
                { _packets.Remove(_packets.First().Key); _overflow++; }
                _nextSequence = _packets.First().Key;
                _pcm.Clear(); _position = 0; _decoder.ResetState(); _transition = true;
            }
            _lastRead = now;
            if (!_started)
            {
                if (_packets.Count == 0 || now - _firstArrival < _delay.TargetMs) return output;
                _started = true; _dormant = false;
            }
            if (_dormant) return output;

            // Change actual occupancy, not just a target number. A continuous linear
            // resampler adjusts by at most 2%, slewed over 100ms; no whole-frame cuts.
            // Half a frame of phase tolerance keeps the low-water mark above a
            // full output tick, including when sender/device clocks drift apart.
            double errorMs = BufferedSamples / (double)SamplesPerMs - (_delay.TargetMs + 15);
            double desiredRate = errorMs > 2 ? 1.02 : errorMs < -2 ? 0.98 : 1;
            _rate += Math.Clamp(desiredRate - _rate, -0.002, 0.002);
            int needed = (int)Math.Floor(_position + (OutputSamples - 1) * _rate) + 1;
            while (_pcm.Count < needed && !_dormant) DecodeNext(now);
            if (_pcm.Count < needed)
            {
                // Long interruption: fade to silence once, then wait for a talk spurt.
                for (int i = 0; i < output.Length; i++) output[i] = (short)(_lastOutput * (1.0 - (i + 1.0) / output.Length));
                _pcm.Clear(); _position = 0; _lastOutput = 0;
                return output;
            }
            for (int i = 0; i < output.Length; i++)
            {
                double p = _position + i * _rate;
                int index = (int)p;
                double fraction = p - index;
                output[i] = (short)Math.Clamp(Math.Round(_pcm[index] * (1 - fraction) +
                    _pcm[Math.Min(index + 1, _pcm.Count - 1)] * fraction), short.MinValue, short.MaxValue);
            }
            double consumed = _position + output.Length * _rate;
            int whole = Math.Min((int)consumed, _pcm.Count);
            _pcm.RemoveRange(0, whole); _position = consumed - whole;
            if (_transition)
            {
                // Stitch resets/recovery to the previous output over 2ms. Normal
                // packets retain their exact PCM; Opus handles its own PLC overlap.
                const int blend = 96;
                double correction = _lastOutput - output[0];
                for (int i = 0; i < blend; i++) output[i] = (short)Math.Clamp(
                    output[i] + correction * (1.0 - (i + 1.0) / blend), short.MinValue, short.MaxValue);
                _transition = false;
            }
            _lastOutput = output[^1];
            return output;
        }
    }

    private void DecodeNext(double now)
    {
        if (_packets.Remove(_nextSequence, out var packet))
        {
            _packetSamples = packet.Samples;
            _lastWasPcm = packet.Pcm;
            var decoded = new short[packet.Samples];
            try
            {
                if (packet.Pcm)
                    for (int i = 0; i < decoded.Length; i++) decoded[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(packet.Payload.AsSpan(i * 2));
                else if (_decoder.Decode(packet.Payload, decoded, decoded.Length, false) != decoded.Length)
                    throw new InvalidDataException("Unexpected Opus duration.");
                if (_missingSamples > 0)
                {
                    // Recovery can begin inside an output frame when time scaling
                    // leaves a PCM remainder. Stitch at that actual sample boundary.
                    short anchor = _pcm.Count > 0 ? _pcm[^1] : _lastOutput;
                    double correction = anchor - decoded[0];
                    int blend = Math.Min(96, decoded.Length);
                    for (int i = 0; i < blend; i++) decoded[i] = (short)Math.Clamp(
                        decoded[i] + correction * (1.0 - (i + 1.0) / blend), short.MinValue, short.MaxValue);
                }
                _missingSamples = 0;
                _pcm.AddRange(decoded);
                _nextSequence++;
                return;
            }
            catch { _invalid++; _transition = true; }
        }
        if (_missingSamples >= 120 * SamplesPerMs && now - _lastArrival >= 120)
        { _dormant = true; _transition = true; return; }
        var replacement = new short[_packetSamples];
        bool fec = !_lastWasPcm && _packets.TryGetValue(_nextSequence + 1, out var next) && !next.Pcm && next.Samples == _packetSamples;
        try
        {
            if (!_lastWasPcm)
            {
                // Request exactly the missing duration, never the decoder's capacity.
                if (fec) { _decoder.Decode(_packets[_nextSequence + 1].Payload, replacement, replacement.Length, true); _fec++; }
                else _decoder.Decode(ReadOnlySpan<byte>.Empty, replacement, replacement.Length, false);
            }
            else
            {
                short last = _pcm.Count > 0 ? _pcm[^1] : _lastOutput;
                for (int i = 0; i < replacement.Length; i++) replacement[i] = (short)(last * (1.0 - (i + 1.0) / replacement.Length));
            }
        }
        catch { _decoder.ResetState(); _transition = true; }
        _pcm.AddRange(replacement);
        _concealed++; _missingSamples += _packetSamples; _nextSequence++;
    }

    private void ResetStream()
    {
        _packets.Clear(); _pcm.Clear(); _position = 0; _rate = 1;
        _newestSequence = null; _started = _dormant = false;
        _missingSamples = 0; _transition = true; _decoder.ResetState();
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; ResetStream(); }
    }
}
