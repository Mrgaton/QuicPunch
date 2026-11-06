using Android.Media;
using QuicPunch.Audio;
using QuicPunch.Helpers;
using AudioDeviceInfo = QuicPunch.Audio.AudioDeviceInfo;

namespace QuicPunch.AndroidProbe;

internal sealed class AndroidAudioBackend : INativeAudioBackend
{
    private const int SampleRate = OpusVoiceCodec.DefaultSampleRate;
    private const int FrameSamples = OpusVoiceCodec.DefaultFrameSize;
    private readonly object _sync = new();
    private AudioRecord? _recorder;
    private AudioTrack? _player;
    private CancellationTokenSource? _captureStop;
    private Task? _captureTask;

    public string Name => "Android AudioRecord/AudioTrack";
    public bool IsSupported => true;
    public string? CaptureError { get; private set; }
    public string? PlaybackError { get; private set; }

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() =>
        [new("android-default-input", "Micrófono Android", true, true)];

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() =>
        [new("android-default-output", "Salida Android", true, false)];

    public bool StartCapture(Action<short[]> onPcmSamples, string? deviceId = null)
    {
        StopCapture();
        CaptureError = null;
        if (Android.App.Application.Context.CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Android.Content.PM.Permission.Granted)
        {
            CaptureError = "El permiso de micrófono está desactivado. Permítelo desde la llamada o en los ajustes de Android.";
            return false;
        }
        AudioRecord? recorder = null;
        try
        {
            int minimum = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
            if (minimum <= 0) throw new InvalidOperationException($"Android rechazó el formato de audio ({minimum}).");
            recorder = new AudioRecord(AudioSource.VoiceCommunication, SampleRate, ChannelIn.Mono,
                Encoding.Pcm16bit, Math.Max(minimum, FrameSamples * sizeof(short) * 2));
            if (recorder.State != State.Initialized) throw new InvalidOperationException("Android no pudo inicializar el micrófono.");
            recorder.StartRecording();
            if (recorder.RecordingState != RecordState.Recording)
                throw new InvalidOperationException("Android no pudo iniciar la grabación. Comprueba el permiso y si otra aplicación ocupa el micrófono.");
            var stop = new CancellationTokenSource();
            lock (_sync)
            {
                _recorder = recorder;
                _captureStop = stop;
                // Capture a stable reference: the cleanup variable is cleared below.
                var activeRecorder = recorder;
                _captureTask = Task.Run(() =>
                {
                    try { CaptureLoop(activeRecorder, onPcmSamples, stop.Token); }
                    catch (Exception ex) when (!stop.IsCancellationRequested)
                    {
                        CaptureError = "El micrófono se ha detenido: " + ex.Message;
                        Android.Util.Log.Warn("QuicPunch", CaptureError);
                    }
                    catch (Exception) when (stop.IsCancellationRequested) { }
                });
            }
            recorder = null;
            return true;
        }
        catch (Exception ex)
        {
            if (recorder != null) { try { recorder.Release(); recorder.Dispose(); } catch { } }
            CaptureError = "No se pudo abrir el micrófono: " + ex.Message;
            Android.Util.Log.Warn("QuicPunch", CaptureError);
            StopCapture();
            return false;
        }
    }

    private static void CaptureLoop(AudioRecord recorder, Action<short[]> onPcmSamples, CancellationToken stop)
    {
        var frame = new short[FrameSamples];
        while (!stop.IsCancellationRequested)
        {
            int filled = 0;
            while (filled < frame.Length && !stop.IsCancellationRequested)
            {
                int count = recorder.Read(frame, filled, frame.Length - filled);
                if (count <= 0) { if (stop.IsCancellationRequested) return; throw new IOException($"AudioRecord.Read devolvió {count}."); }
                filled += count;
            }
            if (filled == frame.Length) onPcmSamples((short[])frame.Clone());
        }
    }

    public void StopCapture()
    {
        AudioRecord? recorder;
        CancellationTokenSource? stop;
        Task? task;
        lock (_sync)
        {
            recorder = _recorder;
            stop = _captureStop;
            task = _captureTask;
            _recorder = null;
            _captureStop = null;
            _captureTask = null;
        }
        if (recorder == null) return;
        stop?.Cancel();
        try { recorder.Stop(); } catch { }
        try { task?.Wait(500); } catch { }
        recorder.Release();
        recorder.Dispose();
        stop?.Dispose();
    }

    public bool StartPlayback(string? deviceId = null)
    {
        StopPlayback();
        PlaybackError = null;
        AudioTrack? player = null;
        try
        {
            int minimum = AudioTrack.GetMinBufferSize(SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
            if (minimum <= 0) return false;
            using var attributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.VoiceCommunication)!.SetContentType(AudioContentType.Speech)!.Build()!;
            using var format = new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(SampleRate)!.SetChannelMask(ChannelOut.Mono)!.Build()!;
            using var builder = new AudioTrack.Builder();
            player = builder.SetAudioAttributes(attributes)!.SetAudioFormat(format)!
                .SetBufferSizeInBytes(Math.Max(minimum, FrameSamples * sizeof(short) * 4))!
                .SetTransferMode(AudioTrackMode.Stream)!.SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)!.Build();
            if (player.State != AudioTrackState.Initialized) { player.Release(); player.Dispose(); return false; }
            // GetMinBufferSize describes allocation capacity; the active playback
            // buffer can be smaller. Keep capacity for the driver but request 40ms.
            player.SetBufferSizeInFrames(FrameSamples * 2);
            player.Play();
            Android.Util.Log.Info("QuicPunch", $"[ANDROID AUDIO] Playback buffer: {player.BufferSizeInFrames * 1000.0 / SampleRate:F1}ms; mode: {player.PerformanceMode}");
            lock (_sync) _player = player;
            player = null;
            return true;
        }
        catch (Exception ex)
        {
            if (player != null) { try { player.Release(); player.Dispose(); } catch { } }
            PlaybackError = "No se pudo abrir la salida de audio: " + ex.Message;
            Android.Util.Log.Warn("QuicPunch", PlaybackError);
            StopPlayback();
            return false;
        }
    }

    public void PlaySamples(short[] pcmSamples)
    {
        lock (_sync)
        {
            if (_player == null || pcmSamples.Length == 0) return;
            try { _player.Write(pcmSamples, 0, pcmSamples.Length); }
            catch (Exception ex) { Android.Util.Log.Warn("QuicPunch", $"Audio playback failed: {ex.Message}"); }
        }
    }

    public void StopPlayback()
    {
        lock (_sync)
        {
            if (_player == null) return;
            try { _player.Stop(); } catch { }
            _player.Release();
            _player.Dispose();
            _player = null;
        }
    }

    public void Dispose()
    {
        StopCapture();
        StopPlayback();
    }
}
