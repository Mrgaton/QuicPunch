using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

public static class QuicDatagramTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("    RFC 9221 UNRELIABLE QUIC DATAGRAM TESTS       ");
        Console.WriteLine("==================================================");

        if (!MsQuicDatagramChannel.IsSupported)
        {
            throw new Exception("MsQuicDatagramChannel is not supported on this platform.");
        }
        Console.WriteLine("[TEST 1] Native MsQuic API Table & Reflection Hooks: DETECTED & SUPPORTED");

        // Generate self-signed certificate for local QUIC listener
        using var rsa = RSA.Create(2048);
        var certReq = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var tempCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        using var cert = new X509Certificate2(tempCert.Export(X509ContentType.Pfx));

        var alpn = new List<SslApplicationProtocol> { new("qdatagram") };

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = alpn,
            ConnectionOptionsCallback = (connection, sslHello, ct) =>
            {
                return ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = cert,
                        ApplicationProtocols = alpn
                    }
                });
            }
        };

        await using var listener = await QuicListener.ListenAsync(listenerOptions);
        var listenEp = listener.LocalEndPoint;
        Console.WriteLine($"[SERVER] Listening on QUIC {listenEp}");

        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = listenEp,
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = alpn,
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
            }
        };

        // 1. Establish a warm-up connection so System.Net.Quic builds and caches the MsQuicConfiguration handles
        var warmServerTask = listener.AcceptConnectionAsync();
        var warmClient = await QuicConnection.ConnectAsync(clientOptions);
        var warmServer = await warmServerTask;
        await warmClient.DisposeAsync();
        await warmServer.DisposeAsync();

        // 2. Enable RFC 9221 Datagrams on the cached configurations
        bool patched = MsQuicDatagramChannel.EnableDatagramsOnConfigurationCache();
        if (!patched)
            throw new Exception("Failed to patch MsQuicConfiguration cache with DatagramReceiveEnabled.");
        Console.WriteLine("[TEST 2] Configuration Cache Injected with DatagramReceiveEnabled: SUCCESS");

        // 3. Establish active connection with datagram support negotiated
        var serverTask = listener.AcceptConnectionAsync();
        var clientConn = await QuicConnection.ConnectAsync(clientOptions);
        var serverConn = await serverTask;

        using var serverChannel = MsQuicDatagramChannel.Attach(serverConn);
        using var clientChannel = MsQuicDatagramChannel.Attach(clientConn);

        Console.WriteLine($"[TEST 3] Client Datagram Negotiated: Send={clientChannel.IsSendEnabled}, Recv={clientChannel.IsReceiveEnabled}");
        Console.WriteLine($"[TEST 3] Server Datagram Negotiated: Send={serverChannel.IsSendEnabled}, Recv={serverChannel.IsReceiveEnabled}");

        var serverReceived = new List<string>();
        var clientReceived = new List<string>();

        serverChannel.OnDatagramReceived += data =>
        {
            lock (serverReceived) serverReceived.Add(Encoding.UTF8.GetString(data));
        };

        clientChannel.OnDatagramReceived += data =>
        {
            lock (clientReceived) clientReceived.Add(Encoding.UTF8.GetString(data));
        };

        const int packetCount = 20;
        Console.Write($"\n[TEST 4] Transmitting {packetCount} Client -> Server datagrams... ");
        for (int i = 0; i < packetCount; i++)
        {
            byte[] msg = Encoding.UTF8.GetBytes($"ClientMsg-{i:D3}");
            if (!clientChannel.Send(msg))
                throw new Exception($"Failed to transmit client datagram {i}");
        }

        Console.Write($"Transmitting {packetCount} Server -> Client datagrams... ");
        for (int i = 0; i < packetCount; i++)
        {
            byte[] msg = Encoding.UTF8.GetBytes($"ServerMsg-{i:D3}");
            if (!serverChannel.Send(msg))
                throw new Exception($"Failed to transmit server datagram {i}");
        }

        for (int w = 0; w < 30; w++)
        {
            await Task.Delay(100);
            lock (serverReceived)
            {
                lock (clientReceived)
                {
                    if (serverReceived.Count == packetCount && clientReceived.Count == packetCount)
                        break;
                }
            }
        }

        Console.WriteLine($"\n[VERIFY] Server received: {serverReceived.Count}/{packetCount}, Client received: {clientReceived.Count}/{packetCount}");

        if (serverReceived.Count != packetCount)
        {
            Console.WriteLine($"[FAIL] Server received {serverReceived.Count}/{packetCount} datagrams!");
            return;
        }

        if (clientReceived.Count != packetCount)
        {
            Console.WriteLine($"[FAIL] Client received {clientReceived.Count}/{packetCount} datagrams!");
            return;
        }

        Console.WriteLine("PASSED");
        Console.WriteLine($"[RESULT] Server received {serverReceived.Count} datagrams, Client received {clientReceived.Count} datagrams. Zero Loss!");

        await clientConn.DisposeAsync();
        await serverConn.DisposeAsync();

        Console.WriteLine("==================================================");
        Console.WriteLine("    ALL RFC 9221 QUIC DATAGRAM TESTS PASSED!      ");
        Console.WriteLine("==================================================");
    }
}
