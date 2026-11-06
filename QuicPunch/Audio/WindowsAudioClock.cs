using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QuicPunch.Audio;

// A deadline clock for the dedicated mixer thread, not an additional audio queue.
// https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw
internal sealed class WindowsAudioClock : IDisposable
{
    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    private readonly TimerHandle _timer;
    private readonly WaitHandle[] _waits;
    private readonly long _period;
    private long _deadline;
    private readonly IntPtr _multimediaTask;
    internal long MissedDeadlines { get; private set; }

    private WindowsAudioClock(SafeWaitHandle timer, CancellationToken token)
    {
        _timer = new(timer);
        _waits = [token.WaitHandle, _timer];
        _period = Stopwatch.Frequency * Helpers.OpusVoiceCodec.DefaultFrameDurationMs / 1000;
        _deadline = Stopwatch.GetTimestamp() + _period;
        uint index = 0;
        _multimediaTask = AvSetMmThreadCharacteristics("Audio", ref index);
    }

    public static WindowsAudioClock? TryCreate(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const uint highResolution = 2, modifyAndSynchronize = 0x00100002;
        var handle = CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero, highResolution, modifyAndSynchronize);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        return new(handle, token);
    }

    public bool WaitForNextTick()
    {
        if (_waits[0].WaitOne(0)) return false;
        long now = Stopwatch.GetTimestamp();
        // Do not emit a burst of stale ticks after a stalled device or resumed PC.
        long missed = Math.Max(0, (now - _deadline) / _period);
        MissedDeadlines += missed;
        _deadline += missed * _period;
        long remaining = _deadline - now;
        if (remaining > 0)
        {
            long due = -Math.Max(1, (long)Math.Ceiling(remaining * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
            if (!SetWaitableTimer(_timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (WaitHandle.WaitAny(_waits) == 0) return false;
        }
        _deadline += _period;
        return true;
    }

    public void Dispose()
    {
        // MMCSS registration belongs to this same dedicated thread.
        if (_multimediaTask != IntPtr.Zero) AvRevertMmThreadCharacteristics(_multimediaTask);
        _timer.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, IntPtr name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr callback, IntPtr state, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string name, ref uint index);
    [DllImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr task);
}
