using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch;

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

        public static ConcurrentDictionary<Guid, (PeerInfo Peer, Stream Stream, QuicConnection Connection)> ActiveCalls { get; } = new();

        public async Task DeniedAsync(PeerInfo peer, CancellationToken ct)
        {
            Console.WriteLine($"\n[VOICE CALL] Call with {peer.Name} ({peer.ActiveEndPoint}) was rejected or failed.");
        }

        public async Task HandleAsync(
            QuicConnection connection,
            Stream stream,
            PeerInfo peer,
            CancellationToken ct)
        {
            Console.WriteLine($"\n--- P2P VOICE CALL ESTABLISHED with {peer.Name} ({peer.ActiveEndPoint}) ---");
            ActiveCalls[peer.Id] = (peer, stream, connection);
            OnCallEstablished?.Invoke(peer);

            _ = Task.Run(async () =>
            {
                byte[] lengthBuffer = new byte[4];
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        int read = 0;
                        while (read < 4)
                        {
                            int r = await stream.ReadAsync(lengthBuffer.AsMemory(read, 4 - read), ct);
                            if (r == 0) goto callEnded;
                            read += r;
                        }

                        int length = BitConverter.ToInt32(lengthBuffer, 0);
                        if (length <= 0 || length > 65536) continue;

                        byte[] payload = new byte[length];
                        int payloadRead = 0;
                        while (payloadRead < length)
                        {
                            int r = await stream.ReadAsync(payload.AsMemory(payloadRead, length - payloadRead), ct);
                            if (r == 0) goto callEnded;
                            payloadRead += r;
                        }

                        OnAudioDatagramReceived?.Invoke(peer, payload);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VOICE CALL] Error reading audio datagram stream: {ex.Message}");
                }

            callEnded:
                ActiveCalls.TryRemove(peer.Id, out _);
                OnCallEnded?.Invoke(peer);
                Console.WriteLine($"\n[VOICE CALL] Voice call with {peer.Name} ended.");
            }, ct);

            while (!ct.IsCancellationRequested && ActiveCalls.ContainsKey(peer.Id))
            {
                await Task.Delay(1000, ct);
            }
        }

        public static async Task SendAudioDatagramAsync(Guid peerId, byte[] audioData)
        {
            if (ActiveCalls.TryGetValue(peerId, out var call))
            {
                try
                {
                    byte[] lengthHeader = BitConverter.GetBytes(audioData.Length);
                    await call.Stream.WriteAsync(lengthHeader);
                    await call.Stream.WriteAsync(audioData);
                    await call.Stream.FlushAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VOICE CALL] Error sending audio datagram: {ex.Message}");
                }
            }
        }

        public static async Task BroadcastAudioDatagramAsync(byte[] audioData)
        {
            byte[] lengthHeader = BitConverter.GetBytes(audioData.Length);
            foreach (var kvp in ActiveCalls)
            {
                try
                {
                    await kvp.Value.Stream.WriteAsync(lengthHeader);
                    await kvp.Value.Stream.WriteAsync(audioData);
                    await kvp.Value.Stream.FlushAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VOICE CALL] Broadcast error to {kvp.Value.Peer.Name}: {ex.Message}");
                }
            }
        }
    }
}
