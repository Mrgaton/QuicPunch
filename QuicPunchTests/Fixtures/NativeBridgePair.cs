using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicPunchTests.Fixtures;

internal sealed class NativeBridgePair : IAsyncDisposable
{
    public QuicConnection Client { get; }
    public QuicConnection Server { get; }
    private NativeBridgePair(QuicConnection client, QuicConnection server) { Client = client; Server = server; }
    public static async Task<NativeBridgePair> CreateAsync(CancellationToken token)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = new CertificateRequest("CN=native-bridge-test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return await CreateAsync(identity, identity, token);
    }
    public static async Task<NativeBridgePair> CreateAsync(X509Certificate2 serverIdentity, X509Certificate2 clientIdentity, CancellationToken token)
    {
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        ushort port = (ushort)((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        var server = MsQuicBridgeTransport.Create(serverIdentity, Sha3Crypto.Hash256(clientIdentity.GetPublicKey()));
        var client = MsQuicBridgeTransport.Create(clientIdentity, Sha3Crypto.Hash256(serverIdentity.GetPublicKey()), new IPEndPoint(IPAddress.Loopback, port));
        try
        {
            server.Listen(port);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port), 0);
            await Task.WhenAll(server.WaitConnectedAsync(token), client.WaitConnectedAsync(token));
            return new NativeBridgePair(QuicConnection.FromTransport(client), QuicConnection.FromTransport(server));
        }
        catch { await client.DisposeAsync(); await server.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync() { await Client.DisposeAsync(); await Server.DisposeAsync(); }
}
