using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QuicPunch.AndroidProbe.Shared;

internal static class QuicProbe
{
    internal static readonly SslApplicationProtocol Alpn = new("qp-android-probe-1");
    private static IntPtr _nativeHandle;

    internal static string LoadNative(string? androidNativeDirectory = null)
    {
        if (_nativeHandle != IntPtr.Zero)
            return "MsQuic already loaded";

        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(AppContext.BaseDirectory, "msquic.dll");
            AppContext.SetSwitch("System.Net.Quic.AppLocalMsQuic", true);
        }
        else if (OperatingSystem.IsAndroid())
        {
            string candidate = Path.Combine(androidNativeDirectory ?? "", "libmsquic.so");
            path = File.Exists(candidate) ? candidate : "libmsquic.so";
        }
        else
        {
            throw new PlatformNotSupportedException("This probe targets Windows and Android ARM64.");
        }

        _nativeHandle = NativeLibrary.Load(path);
        global::QuicPunch.Helpers.MsQuicNativeLoader.LoadFromPath(path);
        return $"Loaded {path}; QUIC supported: {QuicConnection.IsSupported}";
    }

    internal static async Task<string> PingAsync(string host, int port, string expectedCertificateSha256, CancellationToken cancellationToken)
    {
        if (!QuicConnection.IsSupported)
            throw new NotSupportedException("System.Net.Quic reports that QUIC is unavailable.");

        byte[] expected = Convert.FromHexString(expectedCertificateSha256.Replace(":", "").Replace(" ", ""));
        if (expected.Length != 32)
            throw new ArgumentException("The certificate SHA-256 fingerprint must contain 64 hex digits.");

        IPAddress address = (await Dns.GetHostAddressesAsync(host, cancellationToken))
            .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException("No IPv4 address found for the desktop peer.");

        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(address, port),
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            HandshakeTimeout = TimeSpan.FromSeconds(12),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "quicpunch-android-probe",
                ApplicationProtocols = [Alpn],
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    byte[] actual = SHA256.HashData(certificate.GetRawCertData());
                    return CryptographicOperations.FixedTimeEquals(actual, expected);
                }
            }
        };

        await using QuicConnection connection = await QuicConnection.ConnectAsync(options, cancellationToken);
        string group = GetTlsGroup(connection);
        await using QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
        await stream.WriteAsync("PING"u8.ToArray(), cancellationToken);
        stream.CompleteWrites();
        byte[] pong = new byte[4];
        await stream.ReadExactlyAsync(pong, cancellationToken);
        if (Encoding.ASCII.GetString(pong) != "PONG")
            throw new InvalidDataException("The desktop peer did not return PONG.");
        return $"PONG received from {address}:{port}; TLS group: {group}";
    }

    internal static unsafe string GetTlsGroup(QuicConnection connection)
    {
        try
        {
            Type type = typeof(QuicConnection);
            object? handle = type.GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(connection);
            Type? apiType = type.Assembly.GetType("System.Net.Quic.MsQuicApi");
            object? api = apiType?.GetProperty("Api", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            object? table = apiType?.GetField("<ApiTable>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(api);
            if (handle is not SafeHandle safeHandle || table is not Pointer pointer)
                return "unavailable";

            IntPtr getParamPointer = Marshal.ReadIntPtr((IntPtr)Pointer.Unbox(pointer), 4 * IntPtr.Size);
            GetParamDelegate getParam = Marshal.GetDelegateForFunctionPointer<GetParamDelegate>(getParamPointer);
            unsafe
            {
                HandshakeInfo info = default;
                uint length = (uint)sizeof(HandshakeInfo);
                int status = getParam(safeHandle.DangerousGetHandle(), 0x06000000, &length, (IntPtr)(&info));
                if (status != 0 || length < sizeof(HandshakeInfo))
                    return $"unavailable (status {status})";
                return info.TlsGroup switch
                {
                    4588 => "X25519MLKEM768 (PQC)",
                    29 => "X25519 (classical fallback)",
                    var other => $"unknown ({other})"
                };
            }
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name})";
        }
    }

    private unsafe delegate int GetParamDelegate(IntPtr handle, uint parameter, uint* length, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandshakeInfo
    {
        public uint TlsProtocolVersion;
        public uint CipherAlgorithm;
        public int CipherStrength;
        public uint Hash;
        public int HashStrength;
        public uint KeyExchangeAlgorithm;
        public int KeyExchangeStrength;
        public uint CipherSuite;
        public uint TlsGroup;
    }
}
