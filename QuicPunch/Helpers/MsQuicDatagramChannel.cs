using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Quic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace QuicPunch.Helpers;

/// <summary>
/// Unmanaged helper and channel for RFC 9221 Unreliable QUIC Datagrams on top of Microsoft MsQuic (.NET 11).
/// Enables negotiating, sending, and receiving raw unreliable QUIC datagram frames through native MsQuic
/// API table hooks and configuration tuning without waiting for official System.Net.Quic datagram APIs.
/// </summary>
public sealed class MsQuicDatagramChannel : IQuicDatagramChannel
{
    private const uint QuicParamConfigSettings = 0x03000000;
    private const uint QuicParamConnDatagramReceiveEnabled = 0x0500000D;
    private const uint QuicParamConnDatagramSendEnabled = 0x0500000E;
    private const uint QuicParamConnDatagramMaxLength = 0x0500000F;
    private const uint QuicParamConnSettings = 0x05000004;

    private const int EventDatagramReceived = 11;
    private const int EventDatagramStateChanged = 10;
    private const int EventDatagramSendStateChanged = 12;
    private const int EventPeerAddressChanged = 14;

    private const int StatusSuccess = 0;
    private const int StatusPendingLinux = -2; // QUIC_STATUS_PENDING on POSIX/Linux (0xFFFFFFFE)
    private const int StatusPendingWindows = 0x00000103; // STATUS_PENDING on Windows NTSTATUS (259)
    private const int StatusPendingWin32 = 0x703E5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSuccessOrPending(int status) =>
        status == StatusSuccess ||
        status == StatusPendingLinux ||
        status == StatusPendingWindows ||
        status == StatusPendingWin32 ||
        status == unchecked((int)0xFFFFFFFE) ||
        status == unchecked((int)0x800703E5);

    [StructLayout(LayoutKind.Sequential)]
    private struct QuicBuffer
    {
        public uint Length;
        public IntPtr Buffer;
    }

    private unsafe delegate int SetParamDelegate(IntPtr handle, uint param, uint bufferLength, IntPtr buffer);
    private unsafe delegate int GetParamDelegate(IntPtr handle, uint param, IntPtr bufferLength, IntPtr buffer);
    private unsafe delegate int DatagramSendDelegate(IntPtr handle, IntPtr buffers, uint bufferCount, uint flags, IntPtr clientSendContext);
    private unsafe delegate IntPtr GetContextDelegate(IntPtr handle);
    private unsafe delegate void SetCallbackDelegate(IntPtr handle, IntPtr handler, IntPtr context);

    private static FieldInfo? HandleField;
    private static FieldInfo? ConfigurationField;
    private static MethodInfo? NativeCallbackMethod;
    private static IntPtr OriginalNativeCallbackPtr;

    private static SetParamDelegate? SetParam;
    private static GetParamDelegate? GetParam;
    private static DatagramSendDelegate? DatagramSend;
    private static GetContextDelegate? GetContext;
    private static SetCallbackDelegate? SetCallback;

    private static readonly ConcurrentDictionary<IntPtr, MsQuicDatagramChannel> ActiveChannels = new();
    private static readonly object ConfigPatchLock = new();
    private static bool s_isConfigPatched;
    public static bool IsConfigurationCachePatched => s_isConfigPatched;

    private static bool s_initialized;

    public static bool IsSupported
    {
        get
        {
            EnsureInitialized();
            return HandleField != null &&
                   OriginalNativeCallbackPtr != IntPtr.Zero &&
                   SetParam != null &&
                   GetParam != null &&
                   DatagramSend != null &&
                   GetContext != null &&
                   SetCallback != null;
        }
    }

    public static void EnsureInitialized()
    {
        if (s_initialized) return;

        lock (ConfigPatchLock)
        {
            if (s_initialized) return;

            try
            {
                if (!global::System.Net.Quic.QuicConnection.IsSupported)
                {
                    s_initialized = true;
                    return;
                }

                var qcType = typeof(global::System.Net.Quic.QuicConnection);
                var asm = qcType.Assembly;

                HandleField = qcType.GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance);
                ConfigurationField = qcType.GetField("_configuration", BindingFlags.NonPublic | BindingFlags.Instance);
                NativeCallbackMethod = qcType.GetMethod("NativeCallback", BindingFlags.NonPublic | BindingFlags.Static);
                if (NativeCallbackMethod != null)
                {
                    OriginalNativeCallbackPtr = NativeCallbackMethod.MethodHandle.GetFunctionPointer();
                }

                var apiType = asm.GetType("System.Net.Quic.MsQuicApi");
                if (apiType != null)
                {
                    var apiProp = apiType.GetProperty("Api", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    var api = apiProp?.GetValue(null);
                    var apiTableField = apiType.GetField("<ApiTable>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

                    if (api != null && apiTableField != null)
                    {
                        unsafe
                        {
                            IntPtr apiTablePtr = (IntPtr)Pointer.Unbox(apiTableField.GetValue(api)!);

                            IntPtr getContextPtr = Marshal.ReadIntPtr(apiTablePtr, 1 * IntPtr.Size);
                            IntPtr setCallbackPtr = Marshal.ReadIntPtr(apiTablePtr, 2 * IntPtr.Size);
                            IntPtr setParamPtr = Marshal.ReadIntPtr(apiTablePtr, 3 * IntPtr.Size);
                            IntPtr getParamPtr = Marshal.ReadIntPtr(apiTablePtr, 4 * IntPtr.Size);
                            IntPtr datagramSendPtr = Marshal.ReadIntPtr(apiTablePtr, 28 * IntPtr.Size);

                            GetContext = Marshal.GetDelegateForFunctionPointer<GetContextDelegate>(getContextPtr);
                            SetCallback = Marshal.GetDelegateForFunctionPointer<SetCallbackDelegate>(setCallbackPtr);
                            SetParam = Marshal.GetDelegateForFunctionPointer<SetParamDelegate>(setParamPtr);
                            GetParam = Marshal.GetDelegateForFunctionPointer<GetParamDelegate>(getParamPtr);
                            DatagramSend = Marshal.GetDelegateForFunctionPointer<DatagramSendDelegate>(datagramSendPtr);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[MsQuicDatagram] Static init exception: {ex.Message}");
            }

            s_initialized = true;
        }
    }

    /// <summary>
    /// Injects RFC 9221 DatagramReceiveEnabled into all MsQuicConfiguration caches in System.Net.Quic.
    /// Call this before establishing connections to ensure max_datagram_frame_size is negotiated during handshake.
    /// </summary>
    public static bool EnableDatagramsOnConfigurationCache(List<System.Net.Security.SslApplicationProtocol>? customProtocols = null)
    {
        EnsureInitialized();
        if (SetParam == null) return false;

        lock (ConfigPatchLock)
        {
            try
            {
                var asm = typeof(global::System.Net.Quic.QuicConnection).Assembly;
                var configType = asm.GetType("System.Net.Quic.MsQuicConfiguration");
                var cacheField = configType?.GetField("s_configurationCache", BindingFlags.NonPublic | BindingFlags.Static);
                var cacheObj = cacheField?.GetValue(null);
                if (cacheObj == null) return false;

                byte[] cfgSettings = new byte[144];
                // Bit 27: DatagramReceiveEnabled
                // Bit 26: MigrationEnabled
                // Bit 23: MaximumMtu
                // Bit 22: MinimumMtu
                ulong isSetFlags = (1UL << 27) | (1UL << 26) | (1UL << 23) | (1UL << 22);
                BinaryPrimitives.WriteUInt64LittleEndian(cfgSettings.AsSpan(0, 8), isSetFlags);

                // Offset 102: MinimumMtu = 1280
                BinaryPrimitives.WriteUInt16LittleEndian(cfgSettings.AsSpan(102, 2), 1280);
                // Offset 104: MaximumMtu = 1500 (standard Ethernet MTU)
                BinaryPrimitives.WriteUInt16LittleEndian(cfgSettings.AsSpan(104, 2), 1500);

                // Offset 106: _bitfield: bit 2 = MigrationEnabled, bit 3 = DatagramReceiveEnabled
                cfgSettings[106] = (byte)((1 << 3) | (1 << 2));

                if (!s_warmedUpForSupportedProtocols || customProtocols != null)
                {
                    s_warmedUpForSupportedProtocols = true;
                    WarmUpCache(customProtocols);
                }

                int patchedCount = PatchAllConfigurations(cacheObj, cfgSettings);
                s_isConfigPatched = patchedCount > 0;
                return s_isConfigPatched;
            }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[MsQuicDatagram] Config patch error: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Ensures that MsQuic's cached configuration handle for the given client connection options is created
    /// and patched with DatagramReceiveEnabled before the connection is initiated.
    /// </summary>
    public static bool EnsureClientConfigurationPatched(global::System.Net.Quic.QuicClientConnectionOptions options)
    {
        EnsureInitialized();
        if (SetParam == null || options == null) return false;

        try
        {
            var asm = typeof(global::System.Net.Quic.QuicConnection).Assembly;
            var configType = asm.GetType("System.Net.Quic.MsQuicConfiguration");
            MethodInfo? createMethod = null;
            if (configType != null)
            {
                foreach (var m in configType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name == "Create")
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.Name == "QuicClientConnectionOptions")
                        {
                            createMethod = m;
                            break;
                        }
                    }
                }
            }

            if (createMethod != null)
            {
                var handle = createMethod.Invoke(null, new object[] { options }) as SafeHandle;
                if (handle != null && !handle.IsInvalid)
                {
                    return PatchConfiguration(handle);
                }
            }
        }
        catch (Exception ex)
        {
            QuicPunchLog.Error("[EnsureClientConfig] Exception", ex);
        }
        return false;
    }

    /// <summary>
    /// Ensures that MsQuic's cached configuration handle for the given server connection options is created
    /// and patched with DatagramReceiveEnabled before listener accepts connections.
    /// </summary>
    public static bool EnsureServerConfigurationPatched(global::System.Net.Quic.QuicServerConnectionOptions options, string? targetHost = null)
    {
        EnsureInitialized();
        if (SetParam == null || options == null) return false;

        try
        {
            var asm = typeof(global::System.Net.Quic.QuicConnection).Assembly;
            var configType = asm.GetType("System.Net.Quic.MsQuicConfiguration");
            MethodInfo? createMethod = null;
            if (configType != null)
            {
                foreach (var m in configType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name == "Create")
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType.Name == "QuicServerConnectionOptions")
                        {
                            createMethod = m;
                            break;
                        }
                    }
                }
            }

            if (createMethod != null)
            {
                var handle = createMethod.Invoke(null, new object[] { options, targetHost ?? string.Empty }) as SafeHandle;
                if (handle != null && !handle.IsInvalid)
                {
                    return PatchConfiguration(handle);
                }
            }
        }
        catch (Exception ex)
        {
            QuicPunchLog.Error("[EnsureServerConfig] Exception", ex);
        }
        return false;
    }

    private static int PatchAllConfigurations(object cacheObj, byte[] cfgSettings)
    {
        int count = 0;
        var curType = cacheObj.GetType();
        while (curType != null && curType != typeof(object))
        {
            foreach (var f in curType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                var val = f.GetValue(cacheObj);
                if (val is System.Collections.IDictionary dict)
                {
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        if (entry.Value is SafeHandle sh && !sh.IsInvalid)
                        {
                            IntPtr hConfig = sh.DangerousGetHandle();
                            unsafe
                            {
                                fixed (byte* p = cfgSettings)
                                {
                                    if (SetParam!(hConfig, QuicParamConfigSettings, 144, (IntPtr)p) == StatusSuccess)
                                    {
                                        count++;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            curType = curType.BaseType;
        }
        return count;
    }

    private static bool s_warmedUpForSupportedProtocols;

    private static void WarmUpCache(List<System.Net.Security.SslApplicationProtocol>? customProtocols = null)
    {
        try
        {
            using var ecdh = System.Security.Cryptography.ECDiffieHellman.Create();
            using var pfxCert = CertManager.GenerateIdentityCertificate("warmup", ecdh);

            var alpn = customProtocols ?? QuicPunchConnection.SupportedProtocols;
            var listenerTask = System.Net.Quic.QuicListener.ListenAsync(new System.Net.Quic.QuicListenerOptions
            {
                ListenEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0),
                ApplicationProtocols = alpn,
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new System.Net.Quic.QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    ServerAuthenticationOptions = new System.Net.Security.SslServerAuthenticationOptions
                    {
                        ServerCertificate = pfxCert,
                        ApplicationProtocols = alpn,
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = delegate { return true; }
                    }
                })
            }).AsTask();

            var listener = listenerTask.GetAwaiter().GetResult();
            try
            {
                var serverTask = listener.AcceptConnectionAsync().AsTask();
                var clientConnTask = System.Net.Quic.QuicConnection.ConnectAsync(new System.Net.Quic.QuicClientConnectionOptions
                {
                    RemoteEndPoint = listener.LocalEndPoint,
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    ClientAuthenticationOptions = new System.Net.Security.SslClientAuthenticationOptions
                    {
                        ApplicationProtocols = alpn,
                        ClientCertificates = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection(pfxCert),
                        RemoteCertificateValidationCallback = delegate { return true; }
                    }
                }).AsTask();

                Task.WaitAll([serverTask, clientConnTask], 2000);
                try { clientConnTask.Result.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                try { serverTask.Result.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            }
            finally
            {
                try { listener.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Patches an individual configuration safe handle to enable datagram reception, migration, and MTU 1500.
    /// </summary>
    public static bool PatchConfiguration(SafeHandle configHandle)
    {
        EnsureInitialized();
        if (SetParam == null || configHandle.IsInvalid) return false;
        try
        {
            byte[] cfgSettings = new byte[144];
            ulong isSetFlags = (1UL << 27) | (1UL << 26) | (1UL << 23) | (1UL << 22);
            BinaryPrimitives.WriteUInt64LittleEndian(cfgSettings.AsSpan(0, 8), isSetFlags);
            BinaryPrimitives.WriteUInt16LittleEndian(cfgSettings.AsSpan(102, 2), 1280);
            BinaryPrimitives.WriteUInt16LittleEndian(cfgSettings.AsSpan(104, 2), 1500);
            cfgSettings[106] = (byte)((1 << 3) | (1 << 2));

            unsafe
            {
                fixed (byte* p = cfgSettings)
                {
                    int res = SetParam(configHandle.DangerousGetHandle(), QuicParamConfigSettings, 144, (IntPtr)p);
                    return res == StatusSuccess;
                }
            }
        }
        catch
        {
            return false;
        }
    }

    public IntPtr ConnectionHandle { get; }
    public IntPtr OriginalContext { get; }
    public bool IsSendEnabled { get; private set; }
    public bool IsReceiveEnabled { get; private set; }
    public ushort MaxSendDatagramLength { get; private set; } = 1420;

    public Channel<byte[]> IncomingDatagrams { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false
    });

    public event Action<byte[]>? OnDatagramReceived;
    public event Action<System.Net.IPEndPoint>? OnPeerAddressChanged;

    private int _disposed;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private MsQuicDatagramChannel(IntPtr hConn, IntPtr origContext)
    {
        ConnectionHandle = hConn;
        OriginalContext = origContext;
        RefreshStatus();
    }

    /// <summary>
    /// Attaches an unreliable datagram channel to an established QuicConnection.
    /// Intercepts native MsQuic DATAGRAM_RECEIVED callbacks and forwards all other connection events.
    /// </summary>
    public static MsQuicDatagramChannel Attach(global::System.Net.Quic.QuicConnection connection)
    {
        EnsureInitialized();
        if (!IsSupported)
            throw new NotSupportedException("MsQuic native datagram tuning is not supported on this platform.");

        if (HandleField?.GetValue(connection) is not SafeHandle safeHandle || safeHandle.IsInvalid)
            throw new ArgumentException("Invalid QuicConnection native handle.", nameof(connection));

        IntPtr hConn = safeHandle.DangerousGetHandle();
        if (ActiveChannels.TryGetValue(hConn, out var existing) && existing != null && !existing.IsDisposed)
        {
            return existing;
        }

        IntPtr origContext = IntPtr.Zero;
        var contextField = safeHandle.GetType().GetField("_context", BindingFlags.NonPublic | BindingFlags.Instance);
        if (contextField?.GetValue(safeHandle) is GCHandle gcHandle && gcHandle.IsAllocated)
        {
            origContext = GCHandle.ToIntPtr(gcHandle);
        }
        else if (GetContext != null)
        {
            origContext = GetContext(hConn);
        }

        var channel = new MsQuicDatagramChannel(hConn, origContext);
        ActiveChannels[hConn] = channel;

        unsafe
        {
            delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> hookPtr = &NativeHookedCallback;
            SetCallback!(hConn, (IntPtr)hookPtr, origContext);
        }

        channel.RefreshStatus();
        return channel;
    }

    /// <summary>
    /// Refreshes the negotiated datagram send and receive capabilities.
    /// </summary>
    public void RefreshStatus()
    {
        if (GetParam == null) return;
        unsafe
        {
            byte recv = 0;
            uint len = 1;
            if (GetParam(ConnectionHandle, QuicParamConnDatagramReceiveEnabled, (IntPtr)(&len), (IntPtr)(&recv)) == 0)
            {
                IsReceiveEnabled = recv != 0;
            }

            byte send = 0;
            len = 1;
            if (GetParam(ConnectionHandle, QuicParamConnDatagramSendEnabled, (IntPtr)(&len), (IntPtr)(&send)) == 0)
            {
                IsSendEnabled = send != 0;
            }

            ushort maxLen = 0;
            uint lenMax = sizeof(ushort);
            if (GetParam(ConnectionHandle, QuicParamConnDatagramMaxLength, (IntPtr)(&lenMax), (IntPtr)(&maxLen)) == 0 && maxLen > 0)
            {
                MaxSendDatagramLength = maxLen;
            }
        }
    }

    private struct NativeSendPair
    {
        public IntPtr QuicBufferPtr;
        public IntPtr DataBufferPtr;
    }

    private static readonly ConcurrentDictionary<long, NativeSendPair> PendingSendBuffers = new();
    private static long s_nextSendId = 1;

    /// <summary>
    /// Transmits an unreliable QUIC datagram over the wire.
    /// Does not block, retransmit, or cause head-of-line blocking.
    /// Memory is safely tracked on the native heap and freed when MsQuic signals QUIC_DATAGRAM_SEND_SENT.
    /// </summary>
    public bool Send(ReadOnlySpan<byte> datagram)
    {
        if (_disposed != 0 || DatagramSend == null) return false;

        if (!IsSendEnabled)
        {
            RefreshStatus();
            if (!IsSendEnabled)
            {
                return false;
            }
        }

        unsafe
        {
            long id = Interlocked.Increment(ref s_nextSendId);

            IntPtr dataPtr = Marshal.AllocHGlobal(datagram.Length);
            datagram.CopyTo(new Span<byte>((void*)dataPtr, datagram.Length));

            // Allocate native memory for the QuicBuffer struct so it survives stack unwinding
            IntPtr qbPtr = Marshal.AllocHGlobal(sizeof(QuicBuffer));
            QuicBuffer* pQb = (QuicBuffer*)qbPtr;
            pQb->Length = (uint)datagram.Length;
            pQb->Buffer = dataPtr;

            PendingSendBuffers[id] = new NativeSendPair
            {
                QuicBufferPtr = qbPtr,
                DataBufferPtr = dataPtr
            };

            int res = DatagramSend(ConnectionHandle, qbPtr, 1, 0, (IntPtr)id);
            if (!IsSuccessOrPending(res))
            {
                QuicPunchLog.Info($"[MsQuicDatagram] Send returned status 0x{res:X}");
                if (PendingSendBuffers.TryRemove(id, out var pair))
                {
                    Marshal.FreeHGlobal(pair.DataBufferPtr);
                    Marshal.FreeHGlobal(pair.QuicBufferPtr);
                }
                return false;
            }

            return true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int NativeHookedCallback(IntPtr connection, IntPtr context, IntPtr connectionEventPtr)
    {
        try
        {
            int eventType = Marshal.ReadInt32(connectionEventPtr, 0);

            // QUIC_CONNECTION_EVENT_TYPE:
            // 10: DATAGRAM_STATE_CHANGED
            // 11: DATAGRAM_RECEIVED
            // 12: DATAGRAM_SEND_STATE_CHANGED
            if (eventType == EventDatagramReceived)
            {
                if (ActiveChannels.TryGetValue(connection, out var channel) && channel != null)
                {
                    IntPtr quicBufferPtr = Marshal.ReadIntPtr(connectionEventPtr, 8);
                    if (quicBufferPtr != IntPtr.Zero)
                    {
                        uint len = (uint)Marshal.ReadInt32(quicBufferPtr, 0);
                        IntPtr bufPtr = Marshal.ReadIntPtr(quicBufferPtr, IntPtr.Size);
                        if (bufPtr != IntPtr.Zero && len > 0)
                        {
                            byte[] data = new byte[len];
                            Marshal.Copy(bufPtr, data, 0, (int)len);
                            channel.IncomingDatagrams.Writer.TryWrite(data);
                            channel.OnDatagramReceived?.Invoke(data);
                        }
                    }
                }
                return StatusSuccess;
            }

            if (eventType == EventDatagramStateChanged)
            {
                if (ActiveChannels.TryGetValue(connection, out var channel) && channel != null)
                {
                    channel.RefreshStatus();
                }
                return StatusSuccess;
            }

            if (eventType == EventDatagramSendStateChanged)
            {
                long id = Marshal.ReadInt64(connectionEventPtr, 8);
                if (PendingSendBuffers.TryRemove(id, out var pair))
                {
                    Marshal.FreeHGlobal(pair.DataBufferPtr);
                    Marshal.FreeHGlobal(pair.QuicBufferPtr);
                }
                return StatusSuccess;
            }

            if (eventType == EventPeerAddressChanged)
            {
                if (ActiveChannels.TryGetValue(connection, out var channel) && channel != null)
                {
                    IntPtr addrPtr = Marshal.ReadIntPtr(connectionEventPtr, 8);
                    if (addrPtr != IntPtr.Zero)
                    {
                        var ep = MsQuicTuner.ParseQuicAddr(new ReadOnlySpan<byte>((void*)addrPtr, 128));
                        if (ep != null)
                        {
                            channel.OnPeerAddressChanged?.Invoke(ep);
                        }
                    }
                }
                // Forward peer address changes to System.Net.Quic so its internal remote endpoint updates
                if (OriginalNativeCallbackPtr != IntPtr.Zero)
                {
                    var orig = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)OriginalNativeCallbackPtr;
                    return orig(connection, context, connectionEventPtr);
                }
                return StatusSuccess;
            }

            if (OriginalNativeCallbackPtr != IntPtr.Zero)
            {
                var orig = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)OriginalNativeCallbackPtr;
                return orig(connection, context, connectionEventPtr);
            }

            return StatusSuccess;
        }
        catch (Exception ex)
        {
            QuicPunchLog.Info($"[MsQuicDatagram] Callback error: {ex.Message}");
            return StatusSuccess;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        ActiveChannels.TryRemove(ConnectionHandle, out _);

        if (SetCallback != null && OriginalNativeCallbackPtr != IntPtr.Zero)
        {
            try
            {
                SetCallback(ConnectionHandle, OriginalNativeCallbackPtr, OriginalContext);
            }
            catch { }
        }

        IncomingDatagrams.Writer.TryComplete();
    }
}

/// <summary>
/// Abstraction for RFC 9221 Unreliable QUIC Datagram channels (both native and virtual multiplexed).
/// </summary>
public interface IQuicDatagramChannel : IDisposable
{
    bool IsSendEnabled { get; }
    bool IsReceiveEnabled { get; }
    ushort MaxSendDatagramLength { get; }
    bool Send(ReadOnlySpan<byte> datagram);
    System.Threading.Channels.Channel<byte[]> IncomingDatagrams { get; }
    event Action<byte[]>? OnDatagramReceived;
    event Action<System.Net.IPEndPoint>? OnPeerAddressChanged;
}
