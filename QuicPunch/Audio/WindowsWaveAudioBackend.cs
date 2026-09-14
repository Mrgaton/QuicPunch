using System.Runtime.InteropServices;
using QuicPunch.Helpers;

namespace QuicPunch.Audio;

internal sealed class WindowsWaveAudioBackend : INativeAudioBackend
{
    private const string Winmm = "winmm.dll";

    private const int MMSYSERR_NOERROR = 0;
    private const int WAVE_MAPPER = -1;
    private const int CALLBACK_FUNCTION = 0x00030000;
    private const int WIM_DATA = 0x3C0;
    private const int WOM_DONE = 0x3BD;
    private const int WHDR_DONE = 0x00000001;

    private const int SampleRate = OpusVoiceCodec.DefaultSampleRate; // 48000
    private const int Channels = OpusVoiceCodec.DefaultChannels;     // 1 (mono)
    private const int FrameSize = OpusVoiceCodec.DefaultFrameSize;   // 2880 samples (60 ms)
    private const int FrameBytes = FrameSize * sizeof(short);        // 5760 bytes
    private const int BufferCount = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WaveInCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WaveOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);
    private delegate void WaveOutProc(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [DllImport(Winmm, EntryPoint = "waveInGetNumDevs")]
    private static extern int WaveInGetNumDevs();

    [DllImport(Winmm, EntryPoint = "waveInGetDevCaps", CharSet = CharSet.Auto)]
    private static extern int WaveInGetDevCaps(IntPtr uDeviceID, out WaveInCaps pwic, uint cbwic);

    [DllImport(Winmm, EntryPoint = "waveOutGetNumDevs")]
    private static extern int WaveOutGetNumDevs();

    [DllImport(Winmm, EntryPoint = "waveOutGetDevCaps", CharSet = CharSet.Auto)]
    private static extern int WaveOutGetDevCaps(IntPtr uDeviceID, out WaveOutCaps pwoc, uint cbwoc);

    [DllImport(Winmm, EntryPoint = "waveInOpen")]
    private static extern int WaveInOpen(out IntPtr phwi, int uDeviceID, ref WaveFormatEx pwfx, WaveInProc dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport(Winmm, EntryPoint = "waveInPrepareHeader")]
    private static extern int WaveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport(Winmm, EntryPoint = "waveInUnprepareHeader")]
    private static extern int WaveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport(Winmm, EntryPoint = "waveInAddBuffer")]
    private static extern int WaveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport(Winmm, EntryPoint = "waveInStart")]
    private static extern int WaveInStart(IntPtr hwi);

    [DllImport(Winmm, EntryPoint = "waveInReset")]
    private static extern int WaveInReset(IntPtr hwi);

    [DllImport(Winmm, EntryPoint = "waveInClose")]
    private static extern int WaveInClose(IntPtr hwi);

    [DllImport(Winmm, EntryPoint = "waveOutOpen")]
    private static extern int WaveOutOpen(out IntPtr phwo, int uDeviceID, ref WaveFormatEx pwfx, WaveOutProc dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport(Winmm, EntryPoint = "waveOutPrepareHeader")]
    private static extern int WaveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport(Winmm, EntryPoint = "waveOutUnprepareHeader")]
    private static extern int WaveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport(Winmm, EntryPoint = "waveOutWrite")]
    private static extern int WaveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport(Winmm, EntryPoint = "waveOutReset")]
    private static extern int WaveOutReset(IntPtr hwo);

    [DllImport(Winmm, EntryPoint = "waveOutClose")]
    private static extern int WaveOutClose(IntPtr hwo);

    public string Name => "Windows Multimedia (waveIn/waveOut)";
    public bool IsSupported => OperatingSystem.IsWindows();

    private IntPtr _waveInHandle = IntPtr.Zero;
    private WaveInProc? _waveInProc;
    private readonly IntPtr[] _inHeaders = new IntPtr[BufferCount];
    private readonly IntPtr[] _inBuffers = new IntPtr[BufferCount];
    private Action<short[]>? _onSamplesCaptured;
    private System.Threading.Channels.Channel<short[]>? _captureChannel;
    private CancellationTokenSource? _captureCts;
    private Task? _captureWorkerTask;

    private IntPtr _waveOutHandle = IntPtr.Zero;
    private WaveOutProc? _waveOutProc;
    private const int PlaybackBufferCount = 8;
    private const int MaxPlaybackFrameBytes = 5760 * 2; // Headroom for up to 60ms stereo / 120ms mono frames
    private readonly IntPtr[] _outHeaders = new IntPtr[PlaybackBufferCount];
    private readonly IntPtr[] _outBuffers = new IntPtr[PlaybackBufferCount];
    private int _nextOutIndex;
    private readonly object _outLock = new();
    private int _disposed;

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        var list = new List<AudioDeviceInfo>
        {
            new("default", "Default Microphone (System Default)", true, true)
        };

        if (!IsSupported) return list;

        try
        {
            int numDevs = WaveInGetNumDevs();
            for (int i = 0; i < numDevs; i++)
            {
                if (WaveInGetDevCaps((IntPtr)i, out var caps, (uint)Marshal.SizeOf<WaveInCaps>()) == MMSYSERR_NOERROR)
                {
                    list.Add(new AudioDeviceInfo(i.ToString(), caps.szPname, false, true));
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
            new("default", "Default Speakers (System Default)", true, false)
        };

        if (!IsSupported) return list;

        try
        {
            int numDevs = WaveOutGetNumDevs();
            for (int i = 0; i < numDevs; i++)
            {
                if (WaveOutGetDevCaps((IntPtr)i, out var caps, (uint)Marshal.SizeOf<WaveOutCaps>()) == MMSYSERR_NOERROR)
                {
                    list.Add(new AudioDeviceInfo(i.ToString(), caps.szPname, false, false));
                }
            }
        }
        catch { }

        return list;
    }

    public bool StartCapture(Action<short[]> onPcmSamples, string? deviceId = null)
    {
        if (!IsSupported) return false;
        StopCapture();

        _onSamplesCaptured = onPcmSamples;
        int devIndex = WAVE_MAPPER;
        if (!string.IsNullOrWhiteSpace(deviceId) && int.TryParse(deviceId, out int parsed))
            devIndex = parsed;

        var wfx = new WaveFormatEx
        {
            wFormatTag = 1, // PCM
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = SampleRate * Channels * sizeof(short),
            nBlockAlign = Channels * sizeof(short),
            wBitsPerSample = 16,
            cbSize = 0
        };

        _waveInProc = OnWaveInMessage;
        int res = WaveInOpen(out _waveInHandle, devIndex, ref wfx, _waveInProc, IntPtr.Zero, CALLBACK_FUNCTION);
        if (res != MMSYSERR_NOERROR)
        {
            QuicPunchLog.Info($"[AUDIO-WIN] WaveInOpen failed (error {res})");
            return false;
        }

        for (int i = 0; i < BufferCount; i++)
        {
            _inBuffers[i] = Marshal.AllocHGlobal(FrameBytes);
            var hdr = new WaveHdr
            {
                lpData = _inBuffers[i],
                dwBufferLength = FrameBytes
            };
            _inHeaders[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());
            Marshal.StructureToPtr(hdr, _inHeaders[i], false);
            WaveInPrepareHeader(_waveInHandle, _inHeaders[i], (uint)Marshal.SizeOf<WaveHdr>());
            WaveInAddBuffer(_waveInHandle, _inHeaders[i], (uint)Marshal.SizeOf<WaveHdr>());
        }

        _captureCts = new CancellationTokenSource();
        _captureChannel = System.Threading.Channels.Channel.CreateBounded<short[]>(new System.Threading.Channels.BoundedChannelOptions(16)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        CancellationToken ct = _captureCts.Token;
        var reader = _captureChannel.Reader;
        _captureWorkerTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        while (reader.TryRead(out var samples))
                        {
                            _onSamplesCaptured?.Invoke(samples);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, ct);

        WaveInStart(_waveInHandle);
        QuicPunchLog.Info("[AUDIO-WIN] WaveIn capture started.");
        return true;
    }

    private void OnWaveInMessage(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        if (uMsg != WIM_DATA || _waveInHandle == IntPtr.Zero || dwParam1 == IntPtr.Zero) return;

        try
        {
            var hdr = Marshal.PtrToStructure<WaveHdr>(dwParam1);
            if (hdr.dwBytesRecorded > 0)
            {
                short[] samples = new short[hdr.dwBytesRecorded / sizeof(short)];
                Marshal.Copy(hdr.lpData, samples, 0, samples.Length);
                _captureChannel?.Writer.TryWrite(samples);
            }

            if (_waveInHandle != IntPtr.Zero)
            {
                WaveInAddBuffer(_waveInHandle, dwParam1, (uint)Marshal.SizeOf<WaveHdr>());
            }
        }
        catch { }
    }

    public void StopCapture()
    {
        try { _captureCts?.Cancel(); } catch { }
        try { _captureWorkerTask?.Wait(250); } catch { }
        try { _captureCts?.Dispose(); } catch { }
        _captureCts = null;
        _captureWorkerTask = null;
        _captureChannel = null;

        if (_waveInHandle != IntPtr.Zero)
        {
            IntPtr h = _waveInHandle;
            _waveInHandle = IntPtr.Zero;
            WaveInReset(h);
            for (int i = 0; i < BufferCount; i++)
            {
                if (_inHeaders[i] != IntPtr.Zero)
                {
                    WaveInUnprepareHeader(h, _inHeaders[i], (uint)Marshal.SizeOf<WaveHdr>());
                    Marshal.FreeHGlobal(_inHeaders[i]);
                    _inHeaders[i] = IntPtr.Zero;
                }
                if (_inBuffers[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_inBuffers[i]);
                    _inBuffers[i] = IntPtr.Zero;
                }
            }
            WaveInClose(h);
        }
        _onSamplesCaptured = null;
    }

    public bool StartPlayback(string? deviceId = null)
    {
        if (!IsSupported) return false;
        lock (_outLock)
        {
            StopPlayback();

            int devIndex = WAVE_MAPPER;
            if (!string.IsNullOrWhiteSpace(deviceId) && int.TryParse(deviceId, out int parsed))
                devIndex = parsed;

            var wfx = new WaveFormatEx
            {
                wFormatTag = 1, // PCM
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                nAvgBytesPerSec = SampleRate * Channels * sizeof(short),
                nBlockAlign = Channels * sizeof(short),
                wBitsPerSample = 16,
                cbSize = 0
            };

            _waveOutProc = OnWaveOutMessage;
            int res = WaveOutOpen(out _waveOutHandle, devIndex, ref wfx, _waveOutProc, IntPtr.Zero, CALLBACK_FUNCTION);
            if (res != MMSYSERR_NOERROR)
            {
                QuicPunchLog.Info($"[AUDIO-WIN] WaveOutOpen failed (error {res})");
                return false;
            }

            // Pre-allocate playback ring buffers
            for (int i = 0; i < PlaybackBufferCount; i++)
            {
                _outBuffers[i] = Marshal.AllocHGlobal(MaxPlaybackFrameBytes);
                var hdr = new WaveHdr
                {
                    lpData = _outBuffers[i],
                    dwBufferLength = (uint)MaxPlaybackFrameBytes,
                    dwFlags = WHDR_DONE // Initially marked as done/ready to be written
                };
                _outHeaders[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());
                Marshal.StructureToPtr(hdr, _outHeaders[i], false);
                WaveOutPrepareHeader(_waveOutHandle, _outHeaders[i], (uint)Marshal.SizeOf<WaveHdr>());
            }
            _nextOutIndex = 0;

            QuicPunchLog.Info("[AUDIO-WIN] WaveOut playback stream opened with pre-allocated ring buffer.");
            return true;
        }
    }

    private void OnWaveOutMessage(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        // winmm callback: dwParam1 is pointer to WaveHdr that finished playing.
        // Operating system sets WHDR_DONE flag in dwFlags.
    }

    public void PlaySamples(short[] pcmSamples)
    {
        if (!IsSupported || pcmSamples == null || pcmSamples.Length == 0) return;
        lock (_outLock)
        {
            if (_waveOutHandle == IntPtr.Zero)
            {
                if (!StartPlayback()) return;
            }

            int byteCount = pcmSamples.Length * sizeof(short);
            if (byteCount > MaxPlaybackFrameBytes) return;

            // Find next available buffer in ring that has completed playback (WHDR_DONE)
            int chosenIndex = -1;
            for (int i = 0; i < PlaybackBufferCount; i++)
            {
                int idx = (_nextOutIndex + i) % PlaybackBufferCount;
                if (_outHeaders[idx] != IntPtr.Zero)
                {
                    var currentHdr = Marshal.PtrToStructure<WaveHdr>(_outHeaders[idx]);
                    if ((currentHdr.dwFlags & WHDR_DONE) != 0)
                    {
                        chosenIndex = idx;
                        break;
                    }
                }
            }

            // If hardware has a momentary backlog, use next slot to prevent drift
            if (chosenIndex == -1)
            {
                chosenIndex = _nextOutIndex;
            }
            _nextOutIndex = (chosenIndex + 1) % PlaybackBufferCount;

            IntPtr pHdr = _outHeaders[chosenIndex];
            IntPtr pData = _outBuffers[chosenIndex];
            if (pHdr == IntPtr.Zero || pData == IntPtr.Zero) return;

            try
            {
                WaveOutUnprepareHeader(_waveOutHandle, pHdr, (uint)Marshal.SizeOf<WaveHdr>());
                Marshal.Copy(pcmSamples, 0, pData, pcmSamples.Length);

                var hdr = new WaveHdr
                {
                    lpData = pData,
                    dwBufferLength = (uint)byteCount,
                    dwFlags = 0
                };
                Marshal.StructureToPtr(hdr, pHdr, false);

                WaveOutPrepareHeader(_waveOutHandle, pHdr, (uint)Marshal.SizeOf<WaveHdr>());
                WaveOutWrite(_waveOutHandle, pHdr, (uint)Marshal.SizeOf<WaveHdr>());
            }
            catch { }
        }
    }

    public void StopPlayback()
    {
        lock (_outLock)
        {
            if (_waveOutHandle != IntPtr.Zero)
            {
                IntPtr h = _waveOutHandle;
                _waveOutHandle = IntPtr.Zero;
                WaveOutReset(h);
                for (int i = 0; i < PlaybackBufferCount; i++)
                {
                    if (_outHeaders[i] != IntPtr.Zero)
                    {
                        WaveOutUnprepareHeader(h, _outHeaders[i], (uint)Marshal.SizeOf<WaveHdr>());
                        Marshal.FreeHGlobal(_outHeaders[i]);
                        _outHeaders[i] = IntPtr.Zero;
                    }
                    if (_outBuffers[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_outBuffers[i]);
                        _outBuffers[i] = IntPtr.Zero;
                    }
                }
                WaveOutClose(h);
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
