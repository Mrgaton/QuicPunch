using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using QuicPunch;

namespace QuicPunchTests.Tests;

internal static class StunValidationTests
{
    public static async Task RunAsync()
    {
        var failures = new List<string>();
        void Check(bool result, string message)
        {
            Console.WriteLine($"{(result ? "PASS" : "FAIL")}: {message}");
            if (!result) failures.Add(message);
        }
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var client = new SimpleStunClient(udp, [endpoint]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        async Task<byte[]> Response()
        {
            await client.SendRequest(deadline.Token);
            var request = await server.ReceiveAsync(deadline.Token);
            byte[] response = new byte[32];
            request.Buffer.CopyTo(response, 0);
            BinaryPrimitives.WriteUInt16BigEndian(response, 0x0101);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 12);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20), 0x0020);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22), 8);
            response[25] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(26), (ushort)(45000 ^ 0x2112));
            var address = IPAddress.Parse("93.184.216.34").GetAddressBytes();
            for (int i = 0; i < 4; i++) response[28 + i] = (byte)(address[i] ^ response[4 + i]);
            return response;
        }

        var valid = await Response();
        Check(!client.TryProcessIncoming(valid, new IPEndPoint(IPAddress.Loopback, endpoint.Port == 65535 ? 65534 : endpoint.Port + 1)),
            "matching transaction from a different source port is rejected");
        Check(client.TryProcessIncoming(valid, endpoint), "wrong source cannot consume the legitimate transaction");
        Check(!client.TryProcessIncoming(valid, endpoint), "a valid transaction can only be consumed once");

        valid = await Response();
        var malformed = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(malformed.AsSpan(2), 4);
        Check(!client.TryProcessIncoming(malformed, endpoint), "attribute cannot extend past the declared STUN message");
        Check(client.TryProcessIncoming(valid, endpoint), "malformed packet cannot consume a pending transaction");

        valid = await Response();
        malformed = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(malformed.AsSpan(2), 16);
        Check(!client.TryProcessIncoming(malformed, endpoint), "truncated declared message is rejected");
        Check(client.TryProcessIncoming(valid, endpoint), "valid response survives a truncated packet");

        valid = await Response();
        malformed = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(malformed.AsSpan(26), 0x2112);
        Check(!client.TryProcessIncoming(malformed, endpoint), "mapped UDP port zero is rejected");
        Check(client.TryProcessIncoming(valid, endpoint), "invalid mapping cannot consume a pending transaction");

        client.Timeout = TimeSpan.FromMilliseconds(1);
        valid = await Response();
        await Task.Delay(30, deadline.Token);
        Check(!client.TryProcessIncoming(valid, endpoint), "expired transaction is rejected without waiting for another send");

        // Exercise the candidate-gathering path too: it used to bypass transaction validation.
        using var gatherSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var gathering = SimpleStunClient.GatherCandidatesAsync(gatherSocket, 12345, [endpoint],
            TimeSpan.FromMilliseconds(700), deadline.Token);
        var gatherRequest = await server.ReceiveAsync(deadline.Token);
        var forged = (byte[])valid.Clone();
        gatherRequest.Buffer.AsSpan(8, 12).CopyTo(forged.AsSpan(8, 12));
        forged[8] ^= 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(forged.AsSpan(26), (ushort)(46000 ^ 0x2112));
        await server.SendAsync(forged, gatherRequest.RemoteEndPoint, deadline.Token);
        var legitimate = (byte[])forged.Clone();
        legitimate[8] ^= 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(legitimate.AsSpan(26), (ushort)(45000 ^ 0x2112));
        await server.SendAsync(legitimate, gatherRequest.RemoteEndPoint, deadline.Token);
        var gathered = await gathering;
        var reflexive = gathered.Where(c => c.Type == CandidateType.ServerReflexive).ToArray();
        Check(reflexive.Length == 1 && reflexive[0].EndPoint.Port == 45000,
            "candidate gathering accepts only a correlated, validated STUN response");
        if (failures.Count != 0) throw new Exception(string.Join("; ", failures));
    }
}
