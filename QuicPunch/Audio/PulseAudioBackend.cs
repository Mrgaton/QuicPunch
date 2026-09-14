using System.Diagnostics;
using System.Runtime.InteropServices;
using QuicPunch.Helpers;

namespace QuicPunch.Audio;

internal sealed class PulseAudioBackend : INativeAudioBackend
{
    private const string LibPulseSimple = "libpulse-simple.so.0";

    private const int PA_STREAM_PLAYBACK = 1;
    private const int PA_STREAM_RECORD = 2;
    private const int PA_SAMPLE_S16LE = 3;

    private const int SampleRate = OpusVoiceCodec.DefaultSampleRate; // 48000
    private const int Channels = OpusVoiceCodec.DefaultChannels;     // 1 (mono)
    private const int FrameSize = OpusVoiceCodec.DefaultFrameSize;   // 2880 samples (60 ms)
    private const int FrameBytes = FrameSize * sizeof(short);        // 5760 bytes

    [StructLayout(LayoutKind.Sequential)]
    private struct PaSampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaBufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    [DllImport(LibPulseSimple, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pa_simple_new")]
    private static extern IntPtr PaSimpleNew(
        string? server,
        string name,
        int dir,
        string? dev,
        string streamName,
        ref PaSampleSpec ss,
        IntPtr map,
        ref PaBufferAttr attr,
        out int error);

    [DllImport(LibPulseSimple, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pa_simple_new")]
    private static extern IntPtr PaSimpleNewNoAttr(
        string? server,
        string name,
        int dir,
        string? dev,
        string streamName,
        ref PaSampleSpec ss,
        IntPtr map,
        IntPtr attr,
        out int error);

    [DllImport(LibPulseSimple, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pa_simple_free")]
    private static extern void PaSimpleFree(IntPtr s);

    [DllImport(LibPulseSimple, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pa_simple_read")]
    private static extern int PaSimpleRead(IntPtr s, byte[] data, UIntPtr bytes, out int error);

    [DllImport(LibPulseSimple, CallingConvention = CallingConvention.Cdecl, EntryPoint = "pa_simple_write")]
    private static extern int PaSimpleWrite(IntPtr s, byte[] data, UIntPtr bytes, out int error);

    public string Name => "PulseAudio / PipeWire";
    public bool IsSupported => OperatingSystem.IsLinux();

    private IntPtr _recordHandle = IntPtr.Zero;
    private IntPtr _playbackHandle = IntPtr.Zero;
    private readonly object _playbackLock = new();

    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private int _disposed;

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        var list = new List<AudioDeviceInfo>
        {
            new("default", "Default Microphone (System Default)", true, true)
        };

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pactl",
                    Arguments = "list short sources",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (proc.Start())
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);
                foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] parts = rawLine.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        string id = parts[1];
                        if (id.EndsWith(".monitor")) continue; // Skip monitor devices (output loops)
                        list.Add(new AudioDeviceInfo(id, id, false, true));
                    }
                }
            }
        }
        catch { }

        return list;
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var list = new List<AudioDeviceInfo>
        {
            new("default", "Default Speakers / Headphones (System Default)", true, false)
        };

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pactl",
                    Arguments = "list short sinks",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (proc.Start())
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);
                foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] parts = rawLine.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        string id = parts[1];
                        list.Add(new AudioDeviceInfo(id, id, false, false));
                    }
                }
            }
        }
        catch { }

        return list;
    }

    public bool StartCapture(Action<short[]> onPcmSamples, string? deviceId = null)
    {
        StopCapture();

        var spec = new PaSampleSpec
        {
            Format = PA_SAMPLE_S16LE,
            Rate = SampleRate,
            Channels = Channels
        };

        var attr = new PaBufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = uint.MaxValue,
            PreBuf = uint.MaxValue,
            MinReq = uint.MaxValue,
            FragSize = FrameBytes // 60 ms frames
        };

        string? dev = string.IsNullOrWhiteSpace(deviceId) || deviceId.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? null
            : deviceId;

        int error;
        _recordHandle = PaSimpleNew(null, "QuicPunch", PA_STREAM_RECORD, dev, "MicrophoneCapture", ref spec, IntPtr.Zero, ref attr, out error);
        if (_recordHandle == IntPtr.Zero)
        {
            QuicPunchLog.Info($"[AUDIO-PULSE] Could not open capture stream (error code {error})");
            return false;
        }

        _captureCts = new CancellationTokenSource();
        CancellationToken ct = _captureCts.Token;
        _captureTask = Task.Run(() => CaptureLoop(onPcmSamples, ct), ct);
        QuicPunchLog.Info("[AUDIO-PULSE] Native microphone capture started.");
        return true;
    }

    private void CaptureLoop(Action<short[]> onPcmSamples, CancellationToken ct)
    {
        byte[] rawBuffer = new byte[FrameBytes];
        short[] pcmBuffer = new short[FrameSize];

        while (!ct.IsCancellationRequested && _recordHandle != IntPtr.Zero)
        {
            int error;
            int ret = PaSimpleRead(_recordHandle, rawBuffer, (UIntPtr)rawBuffer.Length, out error);
            if (ret < 0)
            {
                if (ct.IsCancellationRequested) break;
                Thread.Sleep(10);
                continue;
            }

            Buffer.BlockCopy(rawBuffer, 0, pcmBuffer, 0, rawBuffer.Length);
            try
            {
                onPcmSamples(pcmBuffer);
            }
            catch { }
        }
    }

    public void StopCapture()
    {
        try { _captureCts?.Cancel(); } catch { }
        try { _captureTask?.Wait(500); } catch { }
        _captureCts?.Dispose();
        _captureCts = null;
        _captureTask = null;

        if (_recordHandle != IntPtr.Zero)
        {
            PaSimpleFree(_recordHandle);
            _recordHandle = IntPtr.Zero;
        }
    }

    public bool StartPlayback(string? deviceId = null)
    {
        lock (_playbackLock)
        {
            StopPlayback();

            var spec = new PaSampleSpec
            {
                Format = PA_SAMPLE_S16LE,
                Rate = SampleRate,
                Channels = Channels
            };

            var attr = new PaBufferAttr
            {
                MaxLength = uint.MaxValue,
                TLength = FrameBytes * 2, // Low latency playback buffer
                PreBuf = FrameBytes,
                MinReq = FrameBytes,
                FragSize = uint.MaxValue
            };

            string? dev = string.IsNullOrWhiteSpace(deviceId) || deviceId.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? null
                : deviceId;

            int error;
            _playbackHandle = PaSimpleNew(null, "QuicPunch", PA_STREAM_PLAYBACK, dev, "VoicePlayback", ref spec, IntPtr.Zero, ref attr, out error);
            if (_playbackHandle == IntPtr.Zero)
            {
                QuicPunchLog.Info($"[AUDIO-PULSE] Could not open playback stream (error code {error})");
                return false;
            }

            QuicPunchLog.Info("[AUDIO-PULSE] Native playback stream opened.");
            return true;
        }
    }

    public void PlaySamples(short[] pcmSamples)
    {
        if (pcmSamples == null || pcmSamples.Length == 0) return;
        lock (_playbackLock)
        {
            if (_playbackHandle == IntPtr.Zero)
            {
                if (!StartPlayback()) return;
            }

            byte[] raw = new byte[pcmSamples.Length * sizeof(short)];
            Buffer.BlockCopy(pcmSamples, 0, raw, 0, raw.Length);
            PaSimpleWrite(_playbackHandle, raw, (UIntPtr)raw.Length, out _);
        }
    }

    public void StopPlayback()
    {
        lock (_playbackLock)
        {
            if (_playbackHandle != IntPtr.Zero)
            {
                PaSimpleFree(_playbackHandle);
                _playbackHandle = IntPtr.Zero;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopCapture();
        StopPlayback();
    }
}
