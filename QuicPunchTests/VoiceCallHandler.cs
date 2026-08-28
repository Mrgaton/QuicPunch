using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicPunchTests
{
    internal class VoiceCallHandler : QuicPunch.QuicPunch.IProtocolHandler
    {
        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000003");
        public ushort PreferredPort => 0; 
        public string ProtocolName => "VoiceCall";

        public ZstandardCompressionOptions? CompressionOptions => null;

        public event Action<PeerInfo, byte[]>? OnAudioDatagramReceived;
        public event Action<PeerInfo>? OnCallEstablished;
        public event Action<PeerInfo>? OnCallEnded;

        public class VoiceCallSession : IDisposable
        {
            public PeerInfo Peer { get; }
            public Stream Stream { get; }
            public QuicConnection Connection { get; }
            public SemaphoreSlim WriteLock { get; } = new(1, 1);

            public VoiceCallSession(PeerInfo peer, Stream stream, QuicConnection connection)
            {
                Peer = peer;
                Stream = stream;
                Connection = connection;
            }

            public async Task SendDatagramAsync(byte[] audioData, CancellationToken ct = default)
            {
                await WriteLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    byte[] lengthHeader = BitConverter.GetBytes(audioData.Length);
                    await Stream.WriteAsync(lengthHeader, ct).ConfigureAwait(false);
                    await Stream.WriteAsync(audioData, ct).ConfigureAwait(false);
                    await Stream.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    WriteLock.Release();
                }
            }

            public void Dispose()
            {
                WriteLock.Dispose();
            }
        }

        public static ConcurrentDictionary<Guid, VoiceCallSession> ActiveCalls { get; } = new();

        public async Task DeniedAsync(PeerInfo peer, CancellationToken ct)
        {
            Console.WriteLine($"\n[VOICE CALL] Call with {peer.Name} ({peer.ActiveEndPoint}) was rejected or failed.");
            await Task.CompletedTask;
        }

        public async Task HandleAsync(
            QuicConnection connection,
            Stream stream,
            PeerInfo peer,
            CancellationToken ct)
        {
            Console.WriteLine($"\n--- P2P VOICE CALL ESTABLISHED with {peer.Name} ({peer.ActiveEndPoint}) ---");
            var session = new VoiceCallSession(peer, stream, connection);
            ActiveCalls[peer.Id] = session;
            OnCallEstablished?.Invoke(peer);

            byte[] lengthBuffer = new byte[4];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = 0;
                    while (read < 4)
                    {
                        int r = await stream.ReadAsync(lengthBuffer.AsMemory(read, 4 - read), ct);
                        if (r == 0) return;
                        read += r;
                    }

                    int length = BitConverter.ToInt32(lengthBuffer, 0);
                    if (length <= 0 || length > 65536) continue;

                    byte[] payload = new byte[length];
                    int payloadRead = 0;
                    while (payloadRead < length)
                    {
                        int r = await stream.ReadAsync(payload.AsMemory(payloadRead, length - payloadRead), ct);
                        if (r == 0) return;
                        payloadRead += r;
                    }

                    OnAudioDatagramReceived?.Invoke(peer, payload);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VOICE CALL] Error reading audio datagram stream: {ex.Message}");
            }
            finally
            {
                if (ActiveCalls.TryRemove(peer.Id, out var removedSession))
                {
                    removedSession.Dispose();
                }
                OnCallEnded?.Invoke(peer);
                Console.WriteLine($"\n[VOICE CALL] Voice call with {peer.Name} ended.");
            }
        }

        public static async Task SendAudioDatagramAsync(Guid peerId, byte[] audioData)
        {
            if (ActiveCalls.TryGetValue(peerId, out var call))
            {
                try
                {
                    await call.SendDatagramAsync(audioData);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VOICE CALL] Error sending audio datagram: {ex.Message}");
                }
            }
        }

        public static async Task BroadcastAudioDatagramAsync(byte[] audioData)
        {
            foreach (var kvp in ActiveCalls)
            {
                try
                {
                    await kvp.Value.SendDatagramAsync(audioData);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VOICE CALL] Broadcast error to {kvp.Value.Peer.Name}: {ex.Message}");
                }
            }
        }
    }
}
