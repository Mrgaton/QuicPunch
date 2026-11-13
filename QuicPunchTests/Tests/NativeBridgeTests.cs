using QuicPunch;
using QuicPunch.Helpers;
using QuicPunchTests.Fixtures;

namespace QuicPunchTests.Tests;

internal static class NativeBridgeTests
{
    public static async Task RunAsync()
    {
        LoopbackOnlyNetwork.Configure();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        Check(MsQuicTuner.EnsureOptimalConfiguration(minMtu: 1280, keepAliveIntervalMs: 5000), "pre-handshake policy");
        Check(!MsQuicTuner.EnsureOptimalConfiguration(enableZeroRtt: true), "replay-sensitive 0-RTT rejected");
        try
        {
            await using var pair = await NativeBridgePair.CreateAsync(ct);
            foreach (var c in new[] { pair.Client, pair.Server })
            {
                Check(c.NativeConnection == null, "transport owns handles independently of System.Net.Quic");
                Check(c.TryGetTelemetry(out var t) && t.NegotiatedTlsGroup == 4588 && t.CongestionAlgorithm == QuicCongestionAlgorithm.Bbr, "mutual pinned TLS with hybrid PQC and BBR");
                Check(c.RemoteCertificate != null && c.NegotiatedApplicationProtocol.ToString() == "h3", "authenticated certificate and ALPN");
                Check(c.TrySetPathMtuDiscovery(1280, 1500), "native configuration is applied before handshake");
                Check(!c.TrySetPathMtuDiscovery(1300, 1500), "immutable MTU bounds rejected");
                Check(!c.TrySetCongestionControl(QuicCongestionAlgorithm.Cubic), "active controller cannot be silently changed");
                Check(c.ApplyOptimalTuning(), "runtime-safe settings");
                Check(c.TrySetStreamSchedulingScheme(QuicStreamSchedulingScheme.RoundRobin) && c.TryGetStreamSchedulingScheme(out var scheduling) && scheduling == QuicStreamSchedulingScheme.RoundRobin, "public scheduling API");
                Check(c.TryGetNetworkStatistics(out _) && c.TryGetIdealProcessor(out _), "native diagnostics");
            }
            await using var outgoing = await pair.Client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            outgoing.Priority16 = 0xff00;
            Check(outgoing.Priority16 == 0xff00, "native stream priority");
            await outgoing.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            outgoing.CompleteWrites();
            await using var incoming = await pair.Server.AcceptInboundStreamAsync(ct);
            byte[] data = new byte[3];
            await incoming.ReadExactlyAsync(data, ct);
            Check(data.SequenceEqual(new byte[] { 1, 2, 3 }) && await incoming.ReadAsync(new byte[1], ct) == 0, "stream data and FIN");
            await outgoing.WritesClosed.WaitAsync(ct);
            var tx = pair.Client.DatagramChannel!; var rx = pair.Server.DatagramChannel!;
            Check(tx.IsSendEnabled && rx.IsReceiveEnabled, "RFC9221 negotiated");
            Check(!tx.Send([]) && !tx.Send(new byte[tx.MaxSendDatagramLength + 1]), "datagram size bounds");
            byte[] payload = new byte[Math.Min(1000, (int)tx.MaxSendDatagramLength)]; Random.Shared.NextBytes(payload);
            Check(tx.Send(payload), "datagram submitted");
            Check((await rx.IncomingDatagrams.Reader.ReadAsync(ct)).SequenceEqual(payload), "datagram received intact");
            Check(rx.Send(payload), "reverse datagram submitted");
            Check((await tx.IncomingDatagrams.Reader.ReadAsync(ct)).SequenceEqual(payload), "reverse datagram received intact");
            using var cancel = new CancellationTokenSource(40);
            try { await pair.Client.AcceptInboundStreamAsync(cancel.Token); throw new Exception("Accept ignored cancellation"); }
            catch (OperationCanceledException) { }
            Check(MsQuicTuner.TryGetLibraryVersion(out var version, out _) && version.Length > 0, "public library version");
            Check(pair.Client.TrySetDscp(QuicDscpPriority.Default), "native DSCP via public API");
            using var callbackEntered = new SemaphoreSlim(0);
            using var callbackRelease = new ManualResetEventSlim();
            rx.OnDatagramReceived += _ => { callbackEntered.Release(); callbackRelease.Wait(); };
            Check(tx.Send(payload), "callback isolation probe submitted");
            await callbackEntered.WaitAsync(ct);
            try
            {
                await pair.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
                Check(true, "blocked application callback cannot prevent native teardown");
            }
            finally { callbackRelease.Set(); }
        }
        finally { MsQuicTuner.EnsureOptimalConfiguration(minMtu: 1248); }
        await QuicNet.ReleaseSmokeTest.VerifyQuicConfigurationAsync();
    }
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception(description);
        Console.WriteLine("PASS: " + description);
    }
}
