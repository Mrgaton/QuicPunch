using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuicPunch.Helpers;
using CoreQuicPunchConnection = global::QuicPunch.QuicPunchConnection;

namespace QuicPunchTests.Tests;

public static class PqcHandshakeTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("  MSQUIC OPENSSL POST-QUANTUM HANDSHAKE TEST");
        Console.WriteLine("==================================================");

        RuntimeHelpers.RunClassConstructor(typeof(CoreQuicPunchConnection).TypeHandle);

        if (!MsQuicNativeLoader.IsCustomLibraryLoaded || MsQuicNativeLoader.LoadedPath == null)
            throw new InvalidOperationException("The application-local MsQuic library was not loaded.");

        string expectedPath = Path.GetFullPath(MsQuicNativeLoader.LoadedPath);
        bool moduleFound = Process.GetCurrentProcess().Modules
            .Cast<ProcessModule>()
            .Any(module => string.Equals(
                module.FileName,
                expectedPath,
                StringComparison.OrdinalIgnoreCase));

        if (!moduleFound)
            throw new InvalidOperationException($"The loaded module list does not contain '{expectedPath}'.");

        Console.WriteLine($"[PASS] Loaded custom MsQuic: {expectedPath}");

        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificateRequest = new CertificateRequest(
            "CN=localhost",
            signingKey,
            HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(san.Build());
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            critical: true));
        using X509Certificate2 certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        var serverOptions = new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = CoreQuicPunchConnection.SupportedProtocols,
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = CoreQuicPunchConnection.SupportedProtocols,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions)
        });

        ValueTask<QuicConnection> serverTask = listener.AcceptConnectionAsync();
        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = listener.LocalEndPoint,
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = CoreQuicPunchConnection.DefaultDisguiseHost,
                ApplicationProtocols = CoreQuicPunchConnection.SupportedProtocols,
                ClientCertificates = new X509Certificate2Collection(certificate),
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        await using QuicConnection client = await QuicConnection.ConnectAsync(clientOptions);
        await using QuicConnection server = await serverTask;

        string[] loadedMsQuicModules = Process.GetCurrentProcess().Modules
            .Cast<ProcessModule>()
            .Where(module => string.Equals(
                Path.GetFileName(module.FileName),
                "msquic.dll",
                StringComparison.OrdinalIgnoreCase))
            .Select(module => module.FileName)
            .ToArray();
        Console.WriteLine($"[DIAGNOSTIC] Loaded MsQuic modules: {string.Join(" | ", loadedMsQuicModules)}");
        if (loadedMsQuicModules.Length != 1 || !string.Equals(
            loadedMsQuicModules[0],
            expectedPath,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "System.Net.Quic loaded an MsQuic module other than the application-local build.");
        }

        AssertHybridGroup(client, "client");

        Console.WriteLine("==================================================");
        Console.WriteLine("  [PASSED] X25519MLKEM768 NEGOTIATED BY BOTH PEERS");
        Console.WriteLine("==================================================");
    }

    private static void AssertHybridGroup(QuicConnection connection, string role)
    {
        if (!MsQuicPqcHandshake.TryGetNegotiatedGroup(connection, out uint group))
            throw new InvalidOperationException($"Could not query the {role} TLS group from MsQuic.");

        Console.WriteLine($"[{role.ToUpperInvariant()}] Negotiated TLS group: {group}");
        if (group != MsQuicPqcHandshake.X25519MlKem768)
        {
            throw new InvalidOperationException(
                $"The {role} negotiated TLS group {group}; expected X25519MLKEM768 " +
                $"({MsQuicPqcHandshake.X25519MlKem768}).");
        }
    }
}
