using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace QuicPunch.Helpers;

internal static class MsQuicPqcHandshake
{
    internal const uint X25519MlKem768 = 4588;

    private const uint QuicParamTlsHandshakeInfo = 0x06000000;

    private unsafe delegate int GetParamDelegate(
        IntPtr handle,
        uint param,
        uint* bufferLength,
        IntPtr buffer);

    private static readonly FieldInfo? HandleField;
    private static readonly GetParamDelegate? GetParam;

    static MsQuicPqcHandshake()
    {
        try
        {
            Type connectionType = typeof(System.Net.Quic.QuicConnection);
            HandleField = connectionType.GetField(
                "_handle",
                BindingFlags.NonPublic | BindingFlags.Instance);

            Type? apiType = connectionType.Assembly.GetType("System.Net.Quic.MsQuicApi");
            object? api = apiType?
                .GetProperty("Api", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?
                .GetValue(null);
            FieldInfo? apiTableField = apiType?.GetField(
                "<ApiTable>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (api == null || apiTableField == null)
                return;

            unsafe
            {
                IntPtr apiTable = (IntPtr)Pointer.Unbox(apiTableField.GetValue(api)!);
                IntPtr getParam = Marshal.ReadIntPtr(apiTable, 4 * IntPtr.Size);
                GetParam = Marshal.GetDelegateForFunctionPointer<GetParamDelegate>(getParam);
            }
        }
        catch
        {
            GetParam = null;
        }
    }

    internal static unsafe bool TryGetNegotiatedGroup(
        System.Net.Quic.QuicConnection connection,
        out uint group)
    {
        group = 0;
        if (GetParam == null || HandleField == null)
            return false;

        try
        {
            if (HandleField.GetValue(connection) is not SafeHandle handle || handle.IsInvalid)
                return false;

            using var lease = new NativeHandleLease(handle);
            QuicHandshakeInfo info = default;
            uint size = (uint)sizeof(QuicHandshakeInfo);
            int status = GetParam(
                lease.Handle,
                QuicParamTlsHandshakeInfo,
                &size,
                (IntPtr)(&info));

            if (status != 0 || size < sizeof(QuicHandshakeInfo))
                return false;

            group = info.TlsGroup;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void LogNegotiatedGroup(
        System.Net.Quic.QuicConnection connection,
        string role)
    {
        if (!MsQuicNativeLoader.IsCustomLibraryLoaded)
            return;

        if (!TryGetNegotiatedGroup(connection, out uint group))
        {
            QuicPunchLog.Info($"[PQC] {role} could not query the negotiated TLS group.");
            return;
        }

        string name = group switch
        {
            X25519MlKem768 => "X25519MLKEM768",
            29 => "X25519",
            23 => "secp256r1",
            24 => "secp384r1",
            _ => $"unknown-{group}"
        };

        QuicPunchLog.Info($"[PQC] {role} negotiated TLS group {name} ({group}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QuicHandshakeInfo
    {
        internal uint TlsProtocolVersion;
        internal uint CipherAlgorithm;
        internal int CipherStrength;
        internal uint Hash;
        internal int HashStrength;
        internal uint KeyExchangeAlgorithm;
        internal int KeyExchangeStrength;
        internal uint CipherSuite;
        internal uint TlsGroup;
    }
}
