using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using QuicPunch;
using QuicPunch.Discovery.PortMapping;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

/// <summary>Explicit opt-in live WAN probe. Never part of the offline suite.</summary>
internal static class WanNativeProbe
{
    public static async Task RunAsync()
    {
        string directory = Environment.GetEnvironmentVariable("QUICNET_TEST_IDENTITY_DIRECTORY")
            ?? throw new InvalidOperationException("Set QUICNET_TEST_IDENTITY_DIRECTORY to an ephemeral identity directory outside the repository.");
        using var own = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directory, "server.pfx"), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        using var peer = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directory, "client.der"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        using var mapper = new PortMappingCoordinator();
        var mapping = await mapper.TryMapPortAsync(44777, 44777, ProtocolType.Udp, TimeSpan.FromMinutes(3), ct);
        if (!mapping.Success) throw new IOException("WAN mapping unavailable: " + mapping.ErrorMessage);
        try
        {
            await using var native = MsQuicBridgeTransport.Create(own, Sha3Crypto.Hash256(peer.GetPublicKey()));
            native.Listen(44777);
            string ready = $"WAN READY {mapping.ExternalIp}:{mapping.ExternalPort} via {mapping.ProtocolName}";
            Console.WriteLine(ready);
            string? readyFile = Environment.GetEnvironmentVariable("QUICNET_TEST_READY_FILE");
            if (readyFile != null) await File.WriteAllTextAsync(readyFile, ready, ct);
            await native.WaitConnectedAsync(ct);
            await using var connection = QuicConnection.FromTransport(native);
            await using var stream = await connection.AcceptInboundStreamAsync(ct);
            var expected = Enumerable.Range(0, 1000).Select(i => (byte)(i * 17 + 3)).ToArray();
            var data = new byte[1000]; await stream.ReadExactlyAsync(data, ct);
            if (!data.SequenceEqual(expected)) throw new IOException("WAN stream payload mismatch.");
            await stream.WriteAsync(data, ct); stream.CompleteWrites(); await stream.WritesClosed.WaitAsync(ct);
            int echoed = 0;
            var channel = connection.DatagramChannel ?? throw new IOException("No native datagrams.");
            using var datagramDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            datagramDeadline.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                while (echoed < 20)
                {
                    var packet = await channel.IncomingDatagrams.Reader.ReadAsync(datagramDeadline.Token);
                    if (packet.Length == 1000 && channel.Send(packet)) echoed++;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (!connection.TryGetTelemetry(out var t) || t.NegotiatedTlsGroup != 4588 || echoed < 15)
                throw new IOException($"WAN validation failed: datagrams echoed {echoed}/20.");
            Console.WriteLine($"WAN PASS remote={connection.RemoteEndPoint}; TLS={t.NegotiatedTlsGroupName}; CC={t.CongestionAlgorithm}; RTT={t.RttMs:F1}ms; MTU={t.PathMtu}; stream=1000; FIN=ack; datagrams={echoed}/20; TURN=none");
            await Task.Delay(600, ct);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            if (!await mapper.TryUnmapPortAsync(mapping.ExternalPort, ProtocolType.Udp, cleanup.Token))
                throw new IOException("Could not remove WAN test mapping; its requested lifetime is three minutes.");
        }
    }
}
