using System;
using System.Collections.Generic;
using System.Buffers.Binary;
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
    private static async Task TestTorQueueExpiryAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = System.Threading.Channels.Channel.CreateUnbounded<byte>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var channel = new QuicPunch.TorEmulatedDatagramChannel(1200, async (data, ct) =>
        {
            if (data.Span[0] == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            delivered.Writer.TryWrite(data.Span[0]);
        });
        if (channel.Send([]) || channel.Send(new byte[1201])) throw new Exception("Invalid Tor datagram admitted.");
        channel.Send(new byte[] { 1 });
        await entered.Task.WaitAsync(deadline.Token);
        for (int i = 0; i < 1000; i++) channel.Send(new byte[] { 2 });
        await Task.Delay(300, deadline.Token);
        release.SetResult();
        if (await delivered.Reader.ReadAsync(deadline.Token) != 1) throw new Exception("Missing in-flight Tor unit.");
        channel.Send(new byte[] { 3 });
        if (await delivered.Reader.ReadAsync(deadline.Token) != 3) throw new Exception("Tor emitted stale queued media after a stall.");
        Console.WriteLine("PASS: Tor queue saturation retains fresh data and expires queued media after a stall.");
    }

    public static async Task RunAsync()
    {
        await TestTorQueueExpiryAsync();
        await NativeBridgeTests.RunAsync();
    }
}
