using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using QuicPunch.AndroidProbe.Shared;

Console.WriteLine(QuicProbe.LoadNative());

if (args.Length == 4 && args[0].Equals("client", StringComparison.OrdinalIgnoreCase))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    Console.WriteLine(await QuicProbe.PingAsync(args[1], int.Parse(args[2]), args[3], timeout.Token));
    return;
}

if (args.Length is not (0 or 2) || args.Length == 2 && !args[0].Equals("server", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Usage: server [port] | client <host> <port> <SHA256 certificate fingerprint>");
    return;
}

int port = args.Length == 2 ? int.Parse(args[1]) : 44555;
using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var request = new CertificateRequest("CN=quicpunch-android-probe", key, HashAlgorithmName.SHA256);
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
Console.WriteLine($"Certificate SHA-256: {Convert.ToHexString(SHA256.HashData(certificate.RawData))}");

var listenerOptions = new QuicListenerOptions
{
    ListenEndPoint = new IPEndPoint(IPAddress.Any, port),
    ApplicationProtocols = [QuicProbe.Alpn],
    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
    {
        DefaultCloseErrorCode = 0,
        DefaultStreamErrorCode = 0,
        ServerAuthenticationOptions = new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ApplicationProtocols = [QuicProbe.Alpn]
        }
    })
};

await using QuicListener listener = await QuicListener.ListenAsync(listenerOptions);
Console.WriteLine($"Listening on UDP {listener.LocalEndPoint}. Enter this PC's LAN IPv4, port and fingerprint in the Android app.");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (!cts.IsCancellationRequested)
{
    try
    {
        await using QuicConnection connection = await listener.AcceptConnectionAsync(cts.Token);
        Console.WriteLine($"Connected: {connection.RemoteEndPoint}; TLS group: {QuicProbe.GetTlsGroup(connection)}");
        await using QuicStream stream = await connection.AcceptInboundStreamAsync(cts.Token);
        byte[] ping = new byte[4];
        await stream.ReadExactlyAsync(ping, cts.Token);
        if (Encoding.ASCII.GetString(ping) != "PING")
            throw new InvalidDataException("Unexpected payload received.");
        await stream.WriteAsync("PONG"u8.ToArray(), cts.Token);
        stream.CompleteWrites();
        Console.WriteLine("PING/PONG OK");
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Peer failed: {ex}");
    }
}
