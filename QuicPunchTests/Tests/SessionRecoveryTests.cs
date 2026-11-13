using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using QuicPunch;
using QuicPunch.Helpers;
using static QuicPunch.QuicPunchStructures;
using QP = QuicPunch.QuicPunch;

namespace QuicPunchTests.Tests;

internal static class SessionRecoveryTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class Protocol(Guid id, bool receive, bool recovery = true) : QP.IProtocolHandler
    {
        public Guid ProtocolId => id;
        public string ProtocolName => "Generic recovery fixture";
        public bool SupportsSessionRecovery => recovery;
        public Channel<(Guid Session, bool Recovered, byte Value)> Entries { get; } = Channel.CreateUnbounded<(Guid, bool, byte)>();
        public Channel<byte> Datagrams { get; } = Channel.CreateUnbounded<byte>();
        public Channel<byte> VpnFrames { get; } = Channel.CreateUnbounded<byte>();
        private int _entries;
        private int _running;
        public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
        {
            Check(Interlocked.Increment(ref _running) == 1, "one logical protocol handler runs at a time");
            try
            {
                byte[] value = new byte[1];
                if (receive)
                {
                    await stream.ReadExactlyAsync(value, ct);
                    await stream.WriteAsync(value, ct);
                    await stream.FlushAsync(ct);
                }
                else
                {
                    value[0] = (byte)Interlocked.Increment(ref _entries);
                    await stream.WriteAsync(value, ct);
                    await stream.FlushAsync(ct);
                    byte expected = value[0];
                    await stream.ReadExactlyAsync(value, ct);
                    Check(value[0] == expected, "new application data arrives intact after connection establishment");
                }
                Entries.Writer.TryWrite((connection.SessionId, connection.IsRecoveredSession, value[0]));
                using var pulseLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task pulse = receive ? Task.CompletedTask : PulseAsync(connection, peer, value[0], pulseLifetime.Token);
                try
                {
                    try { await stream.ReadExactlyAsync(value, ct); }
                    catch (EndOfStreamException) { }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                }
                finally { pulseLifetime.Cancel(); try { await pulse; } catch (OperationCanceledException) { } }
            }
            finally
            {
                // Simulate asynchronous release of application resources after a cut.
                if (receive && ct.IsCancellationRequested) await Task.Delay(150);
                Interlocked.Decrement(ref _running);
            }
        }
        public void OnDatagramReceived(QuicConnection connection, PeerInfo peer, QuicPunch.Datagrams.QuicDatagramMessage message)
        { if (message.Category == 9 && message.Payload.Length == 1) Datagrams.Writer.TryWrite(message.Payload.Span[0]); }
        public void OnFrameReceived(QuicConnection connection, PeerInfo peer, byte category, ReadOnlyMemory<byte> payload, bool isKeyFrame)
        {
            if (category == 9 && payload.Length == 2000 && payload.Span.ToArray().All(b => b == payload.Span[0]))
                VpnFrames.Writer.TryWrite(payload.Span[0]);
        }
        private static async Task PulseAsync(QuicConnection connection, PeerInfo peer, byte value, CancellationToken ct)
        {
            using var vpn = new QuicNet.Protocols.PeerLanSession(0, IPAddress.Loopback, peer, new MemoryStream(), connection)
                { SupportsFragmentedDatagrams = true };
            using var packet = QuicNet.Protocols.PooledPacket.Create(Enumerable.Repeat(value, 2000).ToArray());
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            do { connection.SendCategorizedDatagram(9, new byte[] { value }); vpn.TrySendDatagram(packet); }
            while (await timer.WaitForNextTickAsync(ct));
        }
        public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task<QP> Node(string path, IPAddress? address = null)
    {
        var node = new QP(appDataPath: path) { EnableUpnp = false, StunServerEndpoints = [] };
        node.Discovery.LanDiscoveryPort = 0;
        node.Discovery.NostrRelays = ["ws://127.0.0.1:9"];
        node.Discovery.WanNostrDiscoveryEnabled = false;
        node.PeerTransport = new() { AutoConnectDiscoveredPeers = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100), HeartbeatTimeout = TimeSpan.FromSeconds(2) };
        node.ReputationFilter?.Dispose(); node.ReputationFilter = null;
        await node.StartAsync(); await node.Mailbox.StopAsync();
        node.CurrentPeer.Addresses = [address ?? IPAddress.Loopback];
        node.CurrentPeer.PortArray = [(ushort)node.LocalBoundPort];
        return node;
    }

    public static async Task RunAsync()
    {
        await Registry();
        await SilentTransport();
        await InitialProbeCancellation();
        using (var nat = new QuicPunch.Discovery.NatPinCoordinator())
        {
            nat.AddManualSample(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 40000), 16);
            Check(nat.CurrentMapping.HasMapping, "fixture records an old network's NAT mapping");
            await nat.RefreshForNetworkChangeAsync();
            Check(!nat.CurrentMapping.HasMapping && nat.CurrentMapping.HistorySamplesCount == 0,
                "network changes discard old STUN mappings before collecting new samples");
        }
        string root = Path.Combine(Path.GetTempPath(), "qp-session-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await NativeRecovery(root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    internal class SilentStream : Stream
    {
        public int Writes;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Writes); return ValueTask.CompletedTask; }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Interlocked.Increment(ref Writes);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class DropSessionResponseStream : SilentStream
    {
        private readonly Channel<byte> _replies = Channel.CreateUnbounded<byte>();
        internal bool Requested;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!await _replies.Reader.WaitToReadAsync(ct)) return 0;
            int count = 0;
            while (count < buffer.Length && _replies.Reader.TryRead(out byte value)) buffer.Span[count++] = value;
            return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var message = QuicPunch.Multiplexer.QuicPeerMultiplexer.ControlMessage.Decode(buffer.Span[4..]);
            if (message.Type == QuicPunch.Multiplexer.QuicPeerMultiplexer.ControlMessageType.Ping)
            {
                byte[] reply = new byte[48];
                new QuicPunch.Multiplexer.QuicPeerMultiplexer.ControlMessage
                { Type = QuicPunch.Multiplexer.QuicPeerMultiplexer.ControlMessageType.Pong, SessionGuid = message.SessionGuid }
                    .Encode(reply.AsSpan(4), out int size);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(reply, size);
                foreach (byte value in reply) _replies.Writer.TryWrite(value);
            }
            else if (message.Type == QuicPunch.Multiplexer.QuicPeerMultiplexer.ControlMessageType.SessionRequest)
            { Requested = true; _replies.Writer.TryComplete(); }
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class SilentConnection : IQuicConnectionTransport
    {
        public EndPoint? LocalEndPoint => new IPEndPoint(IPAddress.Loopback, 12345);
        public EndPoint? RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 12346);
        public ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken ct = default) => throw new NotSupportedException();
        public async ValueTask<IQuicStreamTransport> AcceptInboundStreamAsync(CancellationToken ct = default)
        { await Task.Delay(Timeout.Infinite, ct); throw new OperationCanceledException(ct); }
        public ValueTask CloseAsync(long code, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task SilentTransport()
    {
        using var peer = new PeerInfo().SetCertificateHash(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        long seen = peer.LastActivityTimestampMonotonic;
        using var stream = new SilentStream();
        await using var mux = new QuicPunch.Multiplexer.QuicPeerMultiplexer(QuicConnection.FromTransport(new SilentConnection()),
            peer, true, _ => null, _ => true, initialControlStream: stream,
            transportOptions: new() { HeartbeatInterval = TimeSpan.FromMilliseconds(50), HeartbeatTimeout = TimeSpan.FromMilliseconds(200) });
        await mux.StartAsync();
        await mux.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(mux.IsDisposed && stream.Writes > 0, "shared heartbeat closes a silent peer even when stream writes succeed");
        Check(peer.LastActivityTimestampMonotonic == seen, "sending heartbeats does not invent remote liveness");
    }

    private static async Task Registry()
    {
        var registry = new SessionRecoveryRegistry();
        Guid peer = Guid.NewGuid(), protocol = Guid.NewGuid(), session = Guid.NewGuid(), owner = Guid.NewGuid();
        TimeSpan window = TimeSpan.FromSeconds(5);
        int prompts = 0;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> Decide() { Interlocked.Increment(ref prompts); return answer.Task; }
        var firstDecision = registry.GetDecisionAsync(peer, protocol, session, window, Decide);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await firstDecision.WaitAsync(cancelled.Token); } catch (OperationCanceledException) { }
        }
        var retriedDecision = registry.GetDecisionAsync(peer, protocol, session, window, Decide);
        answer.SetResult(true);
        Check(await retriedDecision && prompts == 1, "loss of the requesting transport preserves a single pending consent decision");
        Check(registry.TryStart(peer, protocol, session, owner, false, window), "accepted session creates a scoped continuation grant");
        Check(!registry.TryStart(peer, protocol, session, Guid.NewGuid(), true, window), "an active session cannot be claimed twice");
        registry.Finish(peer, protocol, session, owner, true);
        Check(!registry.TryStart(Guid.NewGuid(), protocol, session, Guid.NewGuid(), true, window) &&
            !registry.TryStart(peer, Guid.NewGuid(), session, Guid.NewGuid(), true, window) &&
            !registry.TryStart(peer, protocol, Guid.NewGuid(), Guid.NewGuid(), true, window), "continuation never grants another peer, protocol or session authority");
        Guid next = Guid.NewGuid();
        Check(!registry.TryStart(peer, protocol, session, next, true, window), "a resumed handler waits for its predecessor's cleanup");
        registry.HandlerFinished(peer, protocol, session, owner);
        Check(registry.TryStart(peer, protocol, session, next, true, window), "transport loss permits the same logical session to resume");
        registry.Finish(peer, protocol, session, owner, false);
        Check(registry.Contains(peer, protocol, window), "late cleanup from an old connection cannot revoke its replacement");
        registry.Finish(peer, protocol, session, next, false);
        Check(registry.WasDenied(peer, protocol, session, window), "closed logical requests cannot replay their cached consent");
        Check(!registry.TryStart(peer, protocol, session, owner, true, window), "normal close destroys continuation authority");
        Check(!registry.TryStart(peer, protocol, session, owner, false, window), "a closed request cannot reopen by claiming to be initial");
        session = Guid.NewGuid();
        registry.TryStart(peer, protocol, session, owner, false, window);
        registry.Finish(peer, protocol, session, owner, true);
        await Task.Delay(40);
        Check(!registry.TryStart(peer, protocol, session, next, true, TimeSpan.FromMilliseconds(20)), "expired sessions cannot resume");
        registry.TryStart(peer, protocol, session, owner, false, window);
        registry.Finish(peer, protocol, session, owner, true);
        registry.Revoke(peer);
        Check(!registry.TryStart(peer, protocol, session, next, true, window), "revocation destroys suspended grants");
        session = Guid.NewGuid();
        registry.TryStart(peer, protocol, session, owner, false, window);
        registry.Clear();
        Check(!registry.Contains(peer, protocol, window), "restart clears all recovery authority");
    }

    private static async Task InitialProbeCancellation()
    {
        string root = Path.Combine(Path.GetTempPath(), "qp-initial-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var node = await Node(root);
            using var peer = new PeerInfo().SetCertificateHash(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            node.TrustPeer(peer.CertHash);
            node.AvailablePeers[peer.Id] = peer;
            var handler = new Protocol(Guid.NewGuid(), false);
            node.RegisterProtocol(handler);
            using var stream = new SilentStream();
            await using var mux = new QuicPunch.Multiplexer.QuicPeerMultiplexer(
                QuicConnection.FromTransport(new SilentConnection()), peer, true, _ => handler, _ => true,
                initialControlStream: stream, transportOptions: node.PeerTransport);
            await mux.StartAsync();
            node.RegisterPeerMultiplexer(peer.Id, mux);
            using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                try
                {
                    await node.InitQuicConnection(handler.ProtocolId, peer, cancellationToken: cancelled.Token)
                        .WaitAsync(TimeSpan.FromSeconds(2));
                    throw new Exception("Cancelled call unexpectedly started.");
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            }
            Check(!node.HasLogicalProtocolSession(peer.Id, handler.ProtocolId) && !mux.IsDisposed,
                "cancelling the initial liveness check ends the call without destroying the shared transport");
            node.PeerTransport = node.PeerTransport with { AttemptTimeout = TimeSpan.FromMilliseconds(150) };
            try
            {
                await node.InitQuicConnection(handler.ProtocolId, peer).WaitAsync(TimeSpan.FromSeconds(2));
                throw new Exception("Silent transport unexpectedly started a call.");
            }
            catch (TimeoutException ex) when (ex.Message.StartsWith("No responsive connection")) { }
            Check(!node.HasLogicalProtocolSession(peer.Id, handler.ProtocolId),
                "initial connection preparation has a bounded deadline and releases its logical session");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static async Task RunIpv6Async()
    {
        string root = Path.Combine(Path.GetTempPath(), "qp-ipv6-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await NativeRecovery(root, IPAddress.IPv6Loopback); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task NativeRecovery(string root, IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        await using var a = await Node(Path.Combine(root, "a"), address);
        await using var b = await Node(Path.Combine(root, "b"), address);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var states = new ConcurrentQueue<SessionStateChange>();
        var receiverStates = new ConcurrentQueue<SessionStateChange>();
        a.OnSessionStateChanged += states.Enqueue;
        b.OnSessionStateChanged += receiverStates.Enqueue;
        int decisions = 0;
        Guid rejectedProtocol = Guid.NewGuid();
        int rejections = 0;
        b.Manager.HandshakeRequested += (request, _) =>
        {
            if (request.ProtocolId == rejectedProtocol)
            {
                Interlocked.Increment(ref rejections);
                return Task.FromResult(new HandshakeDecision(false, null, CancellationToken.None));
            }
            Interlocked.Increment(ref decisions);
            return Task.FromResult(new HandshakeDecision(true, 0, CancellationToken.None));
        };
        var senders = new[] { new Protocol(Guid.NewGuid(), false), new Protocol(Guid.NewGuid(), false) };
        var receivers = senders.Select(x => new Protocol(x.ProtocolId, true)).ToArray();
        foreach (var handler in senders) a.RegisterProtocol(handler);
        foreach (var handler in receivers) b.RegisterProtocol(handler);
        a.TrustPeer(b.CurrentPeer.CertHash); b.TrustPeer(a.CurrentPeer.CertHash);
        await a.ProcessIncomingPacketAsync(b.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true),
            new IPEndPoint(address, b.LocalBoundPort), QP.TransportType.Wan);
        await b.ProcessIncomingPacketAsync(a.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true),
            new IPEndPoint(address, a.LocalBoundPort), QP.TransportType.Wan);
        var peer = a.AvailablePeers[b.CurrentPeer.Id];
        // The home screen can still show QUIC connected while the remote side
        // has stopped answering. Both first application requests must survive
        // replacing that tunnel, without needing another user click.
        using SilentStream staleStream = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? new DropSessionResponseStream() : new SilentStream();
        await using var stale = new QuicPunch.Multiplexer.QuicPeerMultiplexer(
            QuicConnection.FromTransport(new SilentConnection()), peer, true,
            id => senders.FirstOrDefault(p => p.ProtocolId == id), _ => true,
            initialControlStream: staleStream, transportOptions: a.PeerTransport);
        await stale.StartAsync();
        a.RegisterPeerMultiplexer(peer.Id, stale);
        Check(!stale.IsDisposed, "first application requests see an apparently connected but silent tunnel");
        Task[] jobs = senders.Select(x => a.InitQuicConnection(x.ProtocolId, peer, cancellationToken: deadline.Token)).ToArray();
        try
        {
            var original = new Guid[2];
            for (int i = 0; i < 2; i++)
            {
                var sent = await senders[i].Entries.Reader.ReadAsync(deadline.Token).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(12));
                var received = await receivers[i].Entries.Reader.ReadAsync(deadline.Token);
                original[i] = sent.Session;
                Check(sent.Session == received.Session && !sent.Recovered && sent.Value == 1, "each protocol establishes its own logical session");
                await ExpectDatagram(receivers[i], 1, deadline.Token);
            }
            Check(decisions == 2, "initial sessions require separate explicit decisions");
            if (address.Equals(IPAddress.IPv6Loopback))
            {
                a.TryGetPeerMultiplexer(peer.Id, out var ipv6Mux);
                Check(ipv6Mux?.RemoteEndPoint is IPEndPoint endpoint && endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
                    "the full discovery/handshake/session path establishes a native IPv6 transport");
            }
            Check(stale.IsDisposed, "first application requests replace the silent tunnel without manual retry");
            if (staleStream is DropSessionResponseStream pending)
                Check(pending.Requested, "a lost initial SessionResponse recovers even after a successful liveness reply");
            a.RegisterProtocol(new Protocol(rejectedProtocol, false));
            b.RegisterProtocol(new Protocol(rejectedProtocol, true));
            try
            {
                await a.InitQuicConnection(rejectedProtocol, peer, cancellationToken: deadline.Token);
                throw new Exception("Rejected initial session unexpectedly started.");
            }
            catch (SessionRejectedException) { }
            Check(rejections == 1 && !a.HasLogicalProtocolSession(peer.Id, rejectedProtocol),
                "explicit rejection ends the first request without retrying or repeating consent");
            a.TryGetPeerMultiplexer(peer.Id, out var old);
            await old!.DisposeAsync();
            for (int i = 0; i < 2; i++)
            {
                var sent = await senders[i].Entries.Reader.ReadAsync(deadline.Token);
                var received = await receivers[i].Entries.Reader.ReadAsync(deadline.Token);
                Check(sent.Session == original[i] && received.Session == original[i] && sent.Recovered && received.Recovered && sent.Value == 2,
                    "distinct protocols recover on a replacement native QUIC tunnel without replaying old data");
                await ExpectDatagram(receivers[i], 2, deadline.Token);
            }
            Check(decisions == 2, "recovery continues accepted sessions without asking again");
            Check(states.Any(x => x.State == SessionState.Reconnecting) && states.Any(x => x.State == SessionState.Recovered),
                "generic observers receive reconnecting and recovered states");
            a.TryGetPeerMultiplexer(peer.Id, out var current);
            try
            {
                await current!.OpenVirtualSessionAsync(senders[0].ProtocolId, Guid.NewGuid(), true, deadline.Token);
                throw new Exception("Unknown continuation was accepted.");
            }
            catch (SessionRejectedException) { }
            try
            {
                await current!.OpenVirtualSessionAsync(senders[1].ProtocolId, original[0], true, deadline.Token);
                throw new Exception("Continuation for another protocol was accepted.");
            }
            catch (SessionRejectedException) { }
            Check(decisions == 2, "invalid continuations are rejected without raising new consent requests");
            await a.CloseProtocolSessionAsync(peer.Id, senders[0].ProtocolId);
            await jobs[0].WaitAsync(TimeSpan.FromSeconds(4));
            Check(!a.HasLogicalProtocolSession(peer.Id, senders[0].ProtocolId) && !jobs[1].IsCompleted,
                "explicit close ends one logical session while another remains active");
            while (!receiverStates.Any(x => x.SessionId == original[0] && x.State == SessionState.Closed))
                await Task.Delay(20, deadline.Token);
            Check(true, "the receiving node reports normal logical session closure");
            void RevokeDuringRecovery(SessionStateChange change)
            {
                if (change.ProtocolId == senders[1].ProtocolId && change.State == SessionState.Reconnecting) a.UntrustPeer(peer.CertHash);
            }
            a.OnSessionStateChanged += RevokeDuringRecovery;
            await current!.DisposeAsync();
            try { await jobs[1].WaitAsync(TimeSpan.FromSeconds(4)); } catch (OperationCanceledException) { }
            Check(!a.HasLogicalProtocolSession(peer.Id, senders[1].ProtocolId), "revocation cancels all recovery intent for the peer");
            await Task.Delay(300);
            Check(!senders[1].Entries.Reader.TryRead(out _), "revocation during transport loss cannot resurrect an application session");
        }
        finally
        {
            deadline.Cancel();
            foreach (var job in jobs) try { await job; } catch (OperationCanceledException) { }
        }
    }

    private static async Task ExpectDatagram(Protocol protocol, byte expected, CancellationToken ct)
    {
        byte value;
        do { value = await protocol.Datagrams.Reader.ReadAsync(ct); } while (value != expected);
        Check(value == expected, "categorized datagrams use the current protocol session after recovery");
        do { value = await protocol.VpnFrames.Reader.ReadAsync(ct); } while (value != expected);
        Check(value == expected, "VPN packets larger than the QUIC payload arrive intact through fragmented datagrams, including after recovery");
    }
}
