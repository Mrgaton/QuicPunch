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
    private readonly object _captureLock = new();
    private readonly short[] _captureFrame = new short[OpusVoiceCodec.DefaultFrameSize];
    private int _capturedSamples;
    private long _captureTimestamp, _lastCaptureAt;
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
        set
        {
            lock (_captureLock)
            {
                if (_isMuted == value) return;
                _isMuted = value;
                _capturedSamples = 0; _lastCaptureAt = 0;
            }
        }
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
            lock (_captureLock) { _capturedSamples = 0; _lastCaptureAt = 0; }
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
            ? context.Statistics.BufferedMs
            : 0;

    public AudioBufferStatistics? GetPeerAudioStatistics(Guid peerId) =>
        _peerAudioContexts.TryGetValue(peerId, out var context) ? context.Statistics : null;

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

            QuicPunchLog.Info($"[NATIVE AUDIO] Native audio service started on {_backend.Name} (10ms frames, adaptive playout)");
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
        // Driver callbacks can be partial or batched. Packetize by sample count,
        // never truncate a callback to the first 10ms or encode an incomplete frame.
        lock (_captureLock)
        {
            long now = Environment.TickCount64;
            if (_lastCaptureAt == 0 || now - _lastCaptureAt > 100)
            { _capturedSamples = 0; _captureTimestamp = now; }
            _lastCaptureAt = now;
            int offset = 0;
            while (offset < pcmSamples.Length)
            {
                int count = Math.Min(pcmSamples.Length - offset, _captureFrame.Length - _capturedSamples);
                pcmSamples.AsSpan(offset, count).CopyTo(_captureFrame.AsSpan(_capturedSamples));
                offset += count; _capturedSamples += count;
                if (_capturedSamples == _captureFrame.Length)
                {
                    EncodeMicrophoneFrame(_captureFrame, unchecked((ushort)_captureTimestamp));
                    _captureTimestamp += OpusVoiceCodec.DefaultFrameDurationMs;
                    _capturedSamples = 0;
                }
            }
        }
    }

    private void EncodeMicrophoneFrame(short[] pcmSamples, ushort timestamp)
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
            byte[] opusPacket = _opusCodec.EncodeFramed(outputSamples, seq, timestamp, OpusVoiceCodec.DefaultFrameSize);
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
            ctx.Enqueue(audioData);
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
        const int frameSize = OpusVoiceCodec.DefaultFrameSize; // 480 samples = 10ms
        int[] accumulator = new int[frameSize];
        short[] mixedOutput = new short[frameSize];

        using var windowsClock = WindowsAudioClock.TryCreate(ct);
        using var timer = windowsClock == null ? new PeriodicTimer(TimeSpan.FromMilliseconds(OpusVoiceCodec.DefaultFrameDurationMs)) : null;
        QuicPunchLog.Info(windowsClock != null ? "[NATIVE AUDIO] Windows high-resolution mixer clock; 10ms frames, adaptive playout."
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

            Array.Clear(accumulator);
            bool hasPeers = false;
            foreach (var pair in _peerAudioContexts)
            {
                hasPeers = true;
                // Drain muted/deafened peers too, so unmuting never replays old speech.
                short[] samples = pair.Value.ReadFrame();
                double sum = 0;
                foreach (short sample in samples) sum += (double)sample * sample;
                OnPeerAudioLevel?.Invoke(pair.Key, Math.Clamp((float)(Math.Sqrt(sum / samples.Length) / 32768 * 3.5), 0, 1));
                if (_isDeafened || GetPeerMuted(pair.Key)) continue;
                float volume = GetPeerVolume(pair.Key) * _masterVolume;
                for (int i = 0; i < frameSize; i++) accumulator[i] += (int)(samples[i] * volume);
            }
            // During a call write a continuous stream, including startup, PLC and
            // silence. Stopping writes on an empty network queue starves the device.
            if (hasPeers && !_isTestLoopback)
            {
                for (int i = 0; i < frameSize; i++) mixedOutput[i] = (short)Math.Clamp(accumulator[i], short.MinValue, short.MaxValue);
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
