using System.Net;
using System.Threading.Channels;
using QuicNet.Protocols;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

internal static class VoiceMobilityTests
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qp-voice-mobility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var node = new QuicPunch.QuicPunch(appDataPath: directory, listeningPort: 0);
            using var handler = new VoiceCallHandler(node, new QuicPunch.Audio.NullAudioBackend());
            using var peer = new PeerInfo();
            var control = new IPEndPoint(IPAddress.Loopback, 12345);
            var migrated = new IPEndPoint(IPAddress.Loopback, 54321);
            peer.ActiveEndPoint = control;
            using var stream = new SessionRecoveryTests.SilentStream();
            var transport = new Transport();
            await using var connection = QuicConnection.FromTransport(transport);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var established = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.OnCallEstablished += _ => established.TrySetResult();
            Task call = handler.HandleAsync(connection, stream, peer, lifetime.Token);
            try
            {
                await established.Task.WaitAsync(lifetime.Token);
                var session = VoiceCallHandler.ActiveCalls[peer.Id];
                const byte video = VoiceCallHandler.FrameTypeScreenShareVideo;
                if (session.AcceptMediaSequence(video, 1, new byte[] { 0 }) ||
                    !session.AcceptMediaSequence(video, 2, new byte[] { 1 }) ||
                    session.AcceptMediaSequence(video, 4, new byte[] { 0 }) ||
                    session.AcceptMediaSequence(video, 3, new byte[] { 1 }) ||
                    !session.AcceptMediaSequence(video, 5, new byte[] { 1 }))
                    throw new Exception("Missing/out-of-order media did not wait for a fresh keyframe.");
                session.BeginRemoteMediaEpoch(2);
                if (!session.AcceptMediaSequence(video, 1, new byte[] { 1 }))
                    throw new Exception("A new screen encoder retained the previous sequence window.");
                Console.WriteLine("PASS: media loss skips stale/dependent frames and recovers on a new keyframe or encoder epoch");
                transport.Datagrams.Migrate(migrated);
                if (!control.Equals(peer.ActiveEndPoint) || !migrated.Equals(peer.QuicEndPoint))
                    throw new Exception("Voice migration overwrote the control route or failed to update the QUIC route.");
                Console.WriteLine("PASS: migration during an active voice call preserves the control route");
            }
            finally
            {
                lifetime.Cancel();
                try { await call.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Transport : IQuicConnectionTransport
    {
        private readonly SessionRecoveryTests.SilentConnection _inner = new();
        public Datagrams Datagrams { get; } = new();
        public IQuicDatagramChannel DatagramChannel => Datagrams;
        public EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
        public ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken ct = default) => _inner.OpenOutboundStreamAsync(type, ct);
        public ValueTask<IQuicStreamTransport> AcceptInboundStreamAsync(CancellationToken ct = default) => _inner.AcceptInboundStreamAsync(ct);
        public ValueTask CloseAsync(long code, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Datagrams.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Datagrams : IQuicDatagramChannel
    {
        public bool IsSendEnabled => true;
        public bool IsReceiveEnabled => true;
        public ushort MaxSendDatagramLength => 1200;
        public bool Send(ReadOnlySpan<byte> bytes) => true;
        public Channel<byte[]> IncomingDatagrams { get; } = Channel.CreateBounded<byte[]>(1);
        public event Action<byte[]>? OnDatagramReceived { add { } remove { } }
        public event Action<IPEndPoint>? OnPeerAddressChanged;
        public void Migrate(IPEndPoint endpoint) => OnPeerAddressChanged?.Invoke(endpoint);
        public void Dispose() => IncomingDatagrams.Writer.TryComplete();
    }
}
