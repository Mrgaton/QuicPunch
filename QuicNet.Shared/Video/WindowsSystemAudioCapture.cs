using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading.Channels;

namespace QuicNet.Video;

// WASAPI process loopback excludes the host and its WebView children. Ordinary
// endpoint loopback also records the call's playback and sends it back as echo.
// https://learn.microsoft.com/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-process_loopback_mode
internal sealed partial class WindowsSystemAudioCapture : IAsyncDisposable
{
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> _packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _sender;
    private readonly bool _probeOnly;
    private int _disposed;
    public event Action<Exception>? Failed;

    private WindowsSystemAudioCapture(Func<byte[], Task> output, bool probeOnly = false)
    {
        _probeOnly = probeOnly;
        _sender = SendAsync(output);
        new Thread(Capture) { IsBackground = true, Name = "QuicNet screen audio without call playback" }.Start();
    }

    // Opens and initializes the exclusion interface without starting capture or
    // reading any system samples. Useful for deployment/runtime verification.
    internal static async Task ProbeAsync()
    {
        if (!IsSupported) throw new PlatformNotSupportedException();
        var capture = new WindowsSystemAudioCapture(_ => Task.CompletedTask, probeOnly: true);
        try { await capture._started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        finally { await capture.DisposeAsync().ConfigureAwait(false); }
    }

    public static async Task<WindowsSystemAudioCapture> StartAsync(Func<byte[], Task> output)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("La exclusión del audio de la llamada requiere Windows 10 build 20348 o posterior.");
        var capture = new WindowsSystemAudioCapture(output);
        try { await capture._started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); return capture; }
        catch { _ = capture.DisposeAsync().AsTask(); throw; }
    }

    private async Task SendAsync(Func<byte[], Task> output)
    {
        try
        {
            await foreach (var packet in _packets.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await output(packet).WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { _stop.Cancel(); Failed?.Invoke(error); }
    }

    private unsafe void Capture()
    {
        nint client = 0, reader = 0, operation = 0, callbackPointer = 0;
        bool comInitialized = false, running = false;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0)); // Dedicated MTA thread.
            comInitialized = true;
            var activation = new ActivationParams { Type = 1, ProcessId = (uint)Environment.ProcessId, Mode = 1 }; // EXCLUDE_TARGET_PROCESS_TREE
            var variant = new PropVariant { Type = 65, Blob = new Blob { Size = (uint)sizeof(ActivationParams), Data = (nint)(&activation) } }; // VT_BLOB
            var callback = new ActivationCompletion();
            callbackPointer = (nint)ComInterfaceMarshaller<IActivationCompletion>.ConvertToUnmanaged(callback);
            Guid clientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", &clientId, &variant, callbackPointer, out operation));
            nint activated = callback.Result.Task.GetAwaiter().GetResult();
            try { Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtable(activated)[0])(activated, &clientId, &client)); }
            finally { Release(activated); }
            _stop.Token.ThrowIfCancellationRequested();

            // The wire format is 48 kHz, mono, signed 16-bit PCM. Windows performs
            // channel mixing/resampling, so microphone and screen formats agree.
            var format = new WaveFormat { Tag = 1, Channels = 1, SampleRate = 48000, AverageBytes = 96000, BlockAlign = 2, Bits = 16 };
            const uint flags = 0x00020000 | 0x00040000 | 0x80000000; // LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormat*, nint, int>)Vtable(client)[3])(client, 0, flags, 0, 0, &format, 0));
            Guid captureId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtable(client)[14])(client, &captureId, &reader));
            using var samplesReady = new AutoResetEvent(false);
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Vtable(client)[13])(client, samplesReady.SafeWaitHandle.DangerousGetHandle()));
            if (_probeOnly) { _started.TrySetResult(); return; }
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Vtable(client)[10])(client));
            running = true;
            _started.TrySetResult();
            WaitHandle[] waits = [samplesReady, _stop.Token.WaitHandle];
            byte[] packet = new byte[1920]; // 20 ms, bounded by the transport's existing packet format.
            int used = 0;
            while (!_stop.IsCancellationRequested && WaitHandle.WaitAny(waits, 100) != 1)
            {
                uint available = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtable(reader)[5])(reader, &available));
                while (available > 0 && !_stop.IsCancellationRequested)
                {
                    nint data = 0; uint frames = 0, bufferFlags = 0;
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, nint, nint, int>)Vtable(reader)[3])(reader, &data, &frames, &bufferFlags, 0, 0));
                    try
                    {
                        int bytes = checked((int)frames * 2), offset = 0;
                        while (offset < bytes)
                        {
                            int count = Math.Min(packet.Length - used, bytes - offset);
                            if ((bufferFlags & 2) != 0) packet.AsSpan(used, count).Clear(); // AUDCLNT_BUFFERFLAGS_SILENT
                            else new ReadOnlySpan<byte>((byte*)data + offset, count).CopyTo(packet.AsSpan(used));
                            used += count; offset += count;
                            if (used == packet.Length) { _packets.Writer.TryWrite(packet); packet = new byte[1920]; used = 0; }
                        }
                    }
                    finally { Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, int>)Vtable(reader)[4])(reader, frames)); }
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtable(reader)[5])(reader, &available));
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _started.TrySetCanceled(); }
        catch (Exception error) { _started.TrySetException(error); Failed?.Invoke(error); }
        finally
        {
            if (running) ((delegate* unmanaged[Stdcall]<nint, int>)Vtable(client)[11])(client);
            Release(reader); Release(client); Release(operation);
            if (callbackPointer != 0) ComInterfaceMarshaller<IActivationCompletion>.Free((void*)callbackPointer);
            if (comInitialized) CoUninitialize();
            _packets.Writer.TryComplete(); _finished.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        await _finished.Task.ConfigureAwait(false);
        await _sender.ConfigureAwait(false);
        _stop.Dispose();
    }

    private static unsafe nint* Vtable(nint instance) => *(nint**)instance;
    private static unsafe void Release(nint instance) { if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(instance)[2])(instance); }

    [StructLayout(LayoutKind.Sequential)] private struct ActivationParams { public int Type; public uint ProcessId; public int Mode; }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint Size; public nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct PropVariant { public ushort Type, Reserved1, Reserved2, Reserved3; public Blob Blob; }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct WaveFormat { public ushort Tag, Channels; public uint SampleRate, AverageBytes; public ushort BlockAlign, Bits, ExtraSize; }

    [GeneratedComInterface, Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    internal partial interface IActivationCompletion { [PreserveSig] int ActivateCompleted(nint operation); }
    [GeneratedComInterface, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    internal partial interface IAgileObject { }
    [GeneratedComClass]
    private sealed partial class ActivationCompletion : IActivationCompletion, IAgileObject
    {
        public TaskCompletionSource<nint> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public unsafe int ActivateCompleted(nint operation)
        {
            nint activated = 0;
            try
            {
                int activationResult = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)Vtable(operation)[3])(operation, &activationResult, &activated));
                Marshal.ThrowExceptionForHR(activationResult);
                Result.TrySetResult(activated); activated = 0;
            }
            catch (Exception error) { Result.TrySetException(error); }
            finally { Release(activated); }
            return 0;
        }
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int ActivateAudioInterfaceAsync(string path, Guid* iid, PropVariant* activation, nint completion, out nint operation);
}
