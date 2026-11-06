using System.Collections.Concurrent;
using QuicPunch.Helpers;

namespace QuicPunch.Audio;

public sealed class NativeAudioService : IDisposable
{
    private readonly INativeAudioBackend _backend;
    private readonly OpusVoiceCodec _opusCodec;
    private readonly ConcurrentDictionary<Guid, PeerAudioContext> _peerAudioContexts = new();
    private readonly ConcurrentDictionary<Guid, float> _peerVolumes = new();
    private readonly ConcurrentDictionary<Guid, bool> _peerMutes = new();

    private readonly object _stateLock = new();
    private CancellationTokenSource? _mixerCts;
    private Task? _mixerTask;

    private ushort _outgoingSeqNum;
    private volatile bool _isMuted;
    private volatile bool _isDeafened;
    private volatile float _masterVolume = 1.0f;
    private volatile bool _isTestLoopback;
    private string? _currentInputDeviceId;
    private string? _currentOutputDeviceId;
    private int _disposed;

    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    public bool IsDeafened
    {
        get => _isDeafened;
        set => _isDeafened = value;
    }

    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0.0f, 2.0f);
    }

    public bool IsTestLoopback
    {
        get => _isTestLoopback;
        set => _isTestLoopback = value;
    }

    public string? CurrentInputDeviceId => _currentInputDeviceId;
    public string? CurrentOutputDeviceId => _currentOutputDeviceId;
    public string BackendName => _backend.Name;
    public bool IsCaptureRunning { get; private set; }
    public bool IsPlaybackRunning { get; private set; }
    private string? _captureError, _playbackError;
    public string? CaptureError => _backend.CaptureError ?? _captureError;
    public string? PlaybackError => _backend.PlaybackError ?? _playbackError;
    public event Action? OnDeviceStateChanged;

    private void StartInput(string? deviceId)
    {
        try
        {
            IsCaptureRunning = _backend.StartCapture(OnMicrophoneCaptured, deviceId);
            _captureError = IsCaptureRunning ? null : _backend.CaptureError ?? "No se pudo abrir el micrófono. Comprueba los permisos y el dispositivo.";
        }
        catch (Exception ex) { IsCaptureRunning = false; _captureError = ex.Message; }
    }
    private void StartOutput(string? deviceId)
    {
        try
        {
            IsPlaybackRunning = _backend.StartPlayback(deviceId);
            _playbackError = IsPlaybackRunning ? null : _backend.PlaybackError ?? "No se pudo abrir la salida de audio.";
        }
        catch (Exception ex) { IsPlaybackRunning = false; _playbackError = ex.Message; }
    }

    /// <summary>The bitrate of the encoder that produces microphone packets.</summary>
    public int Bitrate
    {
        get => _opusCodec.Bitrate;
        set => _opusCodec.Bitrate = value;
    }

    public event Action<byte[]>? OnAudioPacketReady;
    public event Action<Guid, float>? OnPeerAudioLevel;
    public event Action<float>? OnLocalAudioLevel;

    public NativeAudioService(INativeAudioBackend? backend = null)
    {
        _opusCodec = new OpusVoiceCodec();

        if (backend != null)
        {
            _backend = backend;
            return;
        }

        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            _backend = new PulseAudioBackend();
        }
        else if (OperatingSystem.IsWindows())
        {
            _backend = new WindowsWaveAudioBackend();
        }
        else
        {
            _backend = new NullAudioBackend();
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => _backend.GetInputDevices();
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => _backend.GetOutputDevices();

    public void SetPeerVolume(Guid peerId, float volume)
    {
        _peerVolumes[peerId] = Math.Clamp(volume, 0.0f, 2.0f);
    }

    public float GetPeerVolume(Guid peerId)
    {
        return _peerVolumes.TryGetValue(peerId, out float v) ? v : 1.0f;
    }

    public double GetPeerBufferedAudioMs(Guid peerId) =>
        _peerAudioContexts.TryGetValue(peerId, out var context)
            ? context.JitterQueue.Sum(frame => frame.Length) * 1000.0 / OpusVoiceCodec.DefaultSampleRate
            : 0;

    public void SetPeerMuted(Guid peerId, bool muted)
    {
        _peerMutes[peerId] = muted;
    }

    public bool GetPeerMuted(Guid peerId)
    {
        return _peerMutes.TryGetValue(peerId, out bool m) && m;
    }

    public void Start(string? inputDeviceId = null, string? outputDeviceId = null)
    {
        lock (_stateLock)
        {
            Stop();

            _currentInputDeviceId = inputDeviceId;
            _currentOutputDeviceId = outputDeviceId;

            StartOutput(outputDeviceId);
            StartInput(inputDeviceId);
            OnDeviceStateChanged?.Invoke();

            _mixerCts = new CancellationTokenSource();
            CancellationToken ct = _mixerCts.Token;
            _mixerTask = OperatingSystem.IsWindows()
                ? Task.Factory.StartNew(() => MixerLoop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                : Task.Run(() => MixerLoop(ct), ct);

            QuicPunchLog.Info($"[NATIVE AUDIO] Native audio service started on {_backend.Name} (20ms frames)");
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            try { _mixerCts?.Cancel(); } catch { }
            try { _mixerTask?.Wait(500); } catch { }
            _mixerCts?.Dispose();
            _mixerCts = null;
            _mixerTask = null;

            _backend.StopCapture();
            _backend.StopPlayback();
            IsCaptureRunning = IsPlaybackRunning = false;
            foreach (var ctx in _peerAudioContexts.Values) ctx.Dispose();
            _peerAudioContexts.Clear();
        }
    }

    public void SetInputDevice(string deviceId)
    {
        lock (_stateLock)
        {
            _currentInputDeviceId = deviceId;
            StartInput(deviceId);
            OnDeviceStateChanged?.Invoke();
        }
    }

    public void SetOutputDevice(string deviceId)
    {
        lock (_stateLock)
        {
            _currentOutputDeviceId = deviceId;
            StartOutput(deviceId);
            OnDeviceStateChanged?.Invoke();
        }
    }

    private void OnMicrophoneCaptured(short[] pcmSamples)
    {
        if (pcmSamples == null || pcmSamples.Length == 0) return;

        // Calculate RMS audio level for VAD / meter
        double sum = 0;
        for (int i = 0; i < pcmSamples.Length; i++)
        {
            sum += pcmSamples[i] * pcmSamples[i];
        }
        double rms = Math.Sqrt(sum / pcmSamples.Length) / 32768.0;
        float level = Math.Clamp((float)(rms * 3.5), 0.0f, 1.0f);
        OnLocalAudioLevel?.Invoke(level);

        if (_isTestLoopback)
        {
            // Local loopback for microphone testing
            _backend.PlaySamples(pcmSamples);
        }

        if (_isMuted)
        {
            // Microphone is muted: do not transmit voice packets
            return;
        }

        short[] outputSamples = pcmSamples;

        try
        {
            ushort seq = unchecked(++_outgoingSeqNum);
            ushort ts = unchecked((ushort)(Environment.TickCount64 & 0xFFFF));
            byte[] opusPacket = _opusCodec.EncodeFramed(outputSamples, seq, ts, OpusVoiceCodec.DefaultFrameSize);
            OnAudioPacketReady?.Invoke(opusPacket);
        }
        catch (Exception ex)
        {
            QuicPunchLog.Info($"[NATIVE AUDIO] Opus encode error: {ex.Message}");
        }
    }

    public void EnqueueIncomingAudio(Guid peerId, byte[] audioData)
    {
        if (audioData == null || audioData.Length == 0) return;

        try
        {
            var ctx = _peerAudioContexts.GetOrAdd(peerId, _ => new PeerAudioContext());
            ctx.ProcessIncomingPacket(audioData, pcmSamples =>
            {
                if (pcmSamples == null || pcmSamples.Length == 0) return;

                // Calculate RMS for VAD per peer
                double sum = 0;
                for (int i = 0; i < pcmSamples.Length; i++) sum += pcmSamples[i] * pcmSamples[i];
                double rms = Math.Sqrt(sum / pcmSamples.Length) / 32768.0;
                float level = Math.Clamp((float)(rms * 3.5), 0.0f, 1.0f);
                OnPeerAudioLevel?.Invoke(peerId, level);

                ctx.JitterQueue.Enqueue(pcmSamples);

                // Keep up to 60ms for short bursts; a stalled mixer should catch
                // up to live audio instead of retaining an extra 120ms of speech.
                while (ctx.JitterQueue.Count > 3) ctx.JitterQueue.TryDequeue(out _);
            });
        }
        catch (Exception ex)
        {
            QuicPunchLog.Info($"[NATIVE AUDIO] Decode error for peer {peerId}: {ex.Message}");
        }
    }

    public void RemovePeer(Guid peerId)
    {
        if (_peerAudioContexts.TryRemove(peerId, out var ctx))
        {
            ctx.Dispose();
        }
        _peerVolumes.TryRemove(peerId, out _);
        _peerMutes.TryRemove(peerId, out _);
    }

    private void MixerLoop(CancellationToken ct)
    {
        const int frameSize = OpusVoiceCodec.DefaultFrameSize; // 960 samples = 20ms
        int[] accumulator = new int[frameSize];
        short[] mixedOutput = new short[frameSize];

        using var windowsClock = WindowsAudioClock.TryCreate(ct);
        using var timer = windowsClock == null ? new PeriodicTimer(TimeSpan.FromMilliseconds(OpusVoiceCodec.DefaultFrameDurationMs)) : null;
        QuicPunchLog.Info(windowsClock != null ? "[NATIVE AUDIO] Windows high-resolution mixer clock; 20ms frames, no extra prebuffer."
            : "[NATIVE AUDIO] Standard mixer clock.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!(windowsClock?.WaitForNextTick() ?? timer!.WaitForNextTickAsync(ct).AsTask().GetAwaiter().GetResult()))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Array.Clear(accumulator, 0, accumulator.Length);
            bool hasAudio = false;

            if (!_isDeafened)
            {
                float master = _masterVolume;

                foreach (var pair in _peerAudioContexts)
                {
                    Guid peerId = pair.Key;
                    var ctx = pair.Value;
                    if (GetPeerMuted(peerId))
                    {
                        ctx.JitterQueue.TryDequeue(out _);
                        continue;
                    }

                    if (ctx.JitterQueue.TryDequeue(out short[]? samples) && samples != null)
                    {
                        hasAudio = true;
                        float peerVol = GetPeerVolume(peerId) * master;
                        int count = Math.Min(samples.Length, frameSize);
                        for (int i = 0; i < count; i++)
                        {
                            accumulator[i] += (int)(samples[i] * peerVol);
                        }
                    }
                }
            }
            else
            {
                // Discard frames if deafened
                foreach (var ctx in _peerAudioContexts.Values)
                {
                    ctx.JitterQueue.TryDequeue(out _);
                }
            }

            if (hasAudio && !_isDeafened)
            {
                for (int i = 0; i < frameSize; i++)
                {
                    mixedOutput[i] = (short)Math.Clamp(accumulator[i], short.MinValue, short.MaxValue);
                }
                _backend.PlaySamples(mixedOutput);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _backend.Dispose();
        _opusCodec.Dispose();
    }
}

/// <summary>
/// Dedicated Opus decoding context and jitter buffer per peer.
/// Maintains internal codec LPC state, gap detection, and Packet Loss Concealment (PLC).
/// </summary>
internal sealed class PeerAudioContext : IDisposable
{
    private readonly Concentus.IOpusDecoder _decoder;
    private readonly object _lock = new();
    private ushort? _lastSeq;
    private long _lastPacketAt;
    private int _disposed;

    public ConcurrentQueue<short[]> JitterQueue { get; } = new();
    public int DroppedFramesCount { get; private set; }
    internal int IgnoredPacketsCount { get; private set; }

    public PeerAudioContext(int sampleRate = OpusVoiceCodec.DefaultSampleRate, int channels = OpusVoiceCodec.DefaultChannels)
    {
        _decoder = Concentus.OpusCodecFactory.CreateDecoder(sampleRate, channels);
    }

    public short[] DecodeLoss(int frameSize = OpusVoiceCodec.DefaultFrameSize)
    {
        lock (_lock)
        {
            short[] pcmOut = new short[frameSize];
            int decoded = _decoder.Decode(ReadOnlySpan<byte>.Empty, pcmOut.AsSpan(), frameSize, false);
            if (decoded < frameSize)
            {
                Array.Resize(ref pcmOut, decoded);
            }
            return pcmOut;
        }
    }

    public short[] DecodePacket(ReadOnlySpan<byte> opusPayload, int frameSize = OpusVoiceCodec.DefaultFrameSize, bool decodeFec = false)
    {
        lock (_lock)
        {
            int bufferSize = Math.Max(frameSize, 2880);
            short[] pcmOut = new short[bufferSize];
            int decoded = _decoder.Decode(opusPayload, pcmOut.AsSpan(), bufferSize, decodeFec);
            if (decoded < bufferSize)
            {
                Array.Resize(ref pcmOut, decoded);
            }
            return pcmOut;
        }
    }

    public void ProcessIncomingPacket(ReadOnlySpan<byte> audioData, Action<short[]> onDecoded)
    {
        if (audioData.Length <= 1) return;

        int frameSize = OpusVoiceCodec.DefaultFrameSize;

        if (OpusVoiceCodec.IsFramedOpusPacket(audioData))
        {
            if (!OpusVoiceCodec.TryUnpackFramed(audioData, out ushort seq, out _, out var payload))
                return;

            lock (_lock)
            {
                long now = Environment.TickCount64;
                // After a long interruption the sequence may have restarted or
                // crossed half the 16-bit range. Resume immediately, without PLC backlog.
                if (_lastSeq.HasValue && now - _lastPacketAt > 2000)
                {
                    _lastSeq = null;
                    _decoder.ResetState();
                }
                if (_lastSeq.HasValue)
                {
                    int distance = unchecked((ushort)(seq - _lastSeq.Value));
                    // Datagram duplication/reordering must not rewind the decoder's
                    // predictive state or create false losses on the next packet.
                    if (distance == 0 || distance >= 0x8000) { IgnoredPacketsCount++; return; }
                    int gap = distance - 1;

                    // If gap is reasonable (1 to 5 lost frames), run PLC (Packet Loss Concealment)
                    if (gap > 0 && gap <= 5)
                    {
                        for (int i = 0; i < gap; i++)
                        {
                            DroppedFramesCount++;
                            short[] plcSamples = DecodeLoss(frameSize);
                            onDecoded(plcSamples);
                        }
                    }
                }
                // Sequence checks and decoder state changes form one operation,
                // even if datagram callbacks arrive on different threads.
                short[] decoded = DecodePacket(payload, frameSize);
                _lastSeq = seq; _lastPacketAt = now;
                onDecoded(decoded);
            }
        }
        else if (OpusVoiceCodec.IsOpusPacket(audioData))
        {
            // Legacy 0x01 packet
            ReadOnlySpan<byte> payload = audioData.Slice(1);
            short[] decoded = DecodePacket(payload, frameSize);
            onDecoded(decoded);
        }
        else if (OpusVoiceCodec.IsPcmPacket(audioData))
        {
            // Raw PCM fallback
            int sampleCount = (audioData.Length - 1) / sizeof(short);
            short[] samples = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(audioData.Slice(1 + i * 2, 2));
            }
            onDecoded(samples);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_lock)
        {
            _decoder.ResetState();
        }
    }
}

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault, bool IsInput);

public interface INativeAudioBackend : IDisposable
{
    string Name { get; }
    bool IsSupported { get; }
    string? CaptureError => null;
    string? PlaybackError => null;

    IReadOnlyList<AudioDeviceInfo> GetInputDevices();
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    bool StartCapture(Action<short[]> onPcmSamples, string? deviceId = null);
    void StopCapture();

    bool StartPlayback(string? deviceId = null);
    void PlaySamples(short[] pcmSamples);
    void StopPlayback();
}

internal sealed class NullAudioBackend : INativeAudioBackend
{
    public string Name => "Null Audio (Dummy/Disabled)";
    public bool IsSupported => true;

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() =>
        new[] { new AudioDeviceInfo("none", "No Audio Input Available", true, true) };

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() =>
        new[] { new AudioDeviceInfo("none", "No Audio Output Available", true, false) };

    public bool StartCapture(Action<short[]> onPcmSamples, string? deviceId = null) => false;
    public void StopCapture() { }

    public bool StartPlayback(string? deviceId = null) => false;
    public void PlaySamples(short[] pcmSamples) { }
    public void StopPlayback() { }

    public void Dispose() { }
}
