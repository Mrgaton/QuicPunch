using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;
    public static class TestTorFileShare
    {
        public static async Task RunAsync(string[]? args = null)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("  QUICPUNCH TOR TWO-NODE FILE SHARING TEST");
            Console.WriteLine("==================================================");

            string dirA = Path.Combine(Path.GetTempPath(), "qp_nodeA_" + Guid.NewGuid().ToString("N"));
            string dirB = Path.Combine(Path.GetTempPath(), "qp_nodeB_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            QuicPunch.QuicPunch? nodeA = null;
            QuicPunch.QuicPunch? nodeB = null;

            try
            {
                using var ctsA = new CancellationTokenSource();
                using var ctsB = new CancellationTokenSource();

                byte[] poolId = Encoding.UTF8.GetBytes("TorTestPool123456789");

                Console.WriteLine("\n[Node A] Initializing Node A...");
                nodeA = new QuicPunch.QuicPunch(ctsA.Token, poolId, null, true, 0, dirA);
                await nodeA.StartAsync(ctsA.Token);

                Console.WriteLine("[Node A] Starting Tor service...");
                await nodeA.StartTorAsync(0, cancellationToken: ctsA.Token);
                string onionA = nodeA.TorOnionAddress!;
                int portA = nodeA.TorHub!.VirtualPort;
                Console.WriteLine($"[Node A] Active Onion Address: {onionA}:{portA}");
                Console.WriteLine($"[Node A] WAN Cert Hash: {Convert.ToHexString(nodeA.CertManager.CertPublicHash)[..16]}... PeerId: {nodeA.CurrentPeer.Id}");
                Console.WriteLine($"[Node A] Tor Cert Hash: {Convert.ToHexString(nodeA.TorCertManager.CertPublicHash)[..16]}... PeerId: {nodeA.TorCurrentPeer.Id}");

                if (nodeA.TorCertManager == null || nodeA.TorCurrentPeer == null)
                {
                    throw new InvalidOperationException("Node A Tor transport was not initialized properly.");
                }

                Console.WriteLine("\n[Node B] Initializing Node B...");
                nodeB = new QuicPunch.QuicPunch(ctsB.Token, poolId, null, true, 0, dirB);
                await nodeB.StartAsync(ctsB.Token);

                Console.WriteLine("[Node B] Starting Tor service...");
                await nodeB.StartTorAsync(0, cancellationToken: ctsB.Token);
                string onionB = nodeB.TorOnionAddress!;
                int portB = nodeB.TorHub!.VirtualPort;
                Console.WriteLine($"[Node B] Active Onion Address: {onionB}:{portB}");
                Console.WriteLine($"[Node B] WAN Cert Hash: {Convert.ToHexString(nodeB.CertManager.CertPublicHash)[..16]}... PeerId: {nodeB.CurrentPeer.Id}");
                Console.WriteLine($"[Node B] Tor Cert Hash: {Convert.ToHexString(nodeB.TorCertManager.CertPublicHash)[..16]}... PeerId: {nodeB.TorCurrentPeer.Id}");

                if (nodeB.TorCertManager == null || nodeB.TorCurrentPeer == null)
                {
                    throw new InvalidOperationException("Node B Tor transport was not initialized properly.");
                }

                var receiverProtocol = new TorFileShareReceiverProtocol();
                nodeA.RegisterProtocol(receiverProtocol);

                Console.WriteLine("\nWaiting 45s for Tor onion service descriptors to propagate across relays...");
                await Task.Delay(45000, ctsB.Token);

                Console.WriteLine($"\n[Node B] Initiating Tor connection to Node A ({onionA}:{portA})...");
                for (int attempt = 1; attempt <= 15; attempt++)
                {
                    try
                    {
                        Console.WriteLine($"[Attempt {attempt}] Connecting to Tor onion {onionA}:{portA}...");
                        await nodeB.ConnectTorAsync(onionA, checked((ushort)portA), ctsB.Token);
                        Console.WriteLine($"[Attempt {attempt}] Connected!");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Attempt {attempt} failed] {ex.Message}");
                        await Task.Delay(6000, ctsB.Token);
                    }
                }

                Console.WriteLine("Waiting for Tor peer handshake and session key exchange...");
                PeerInfo? peerA_on_B = null;
                for (int i = 0; i < 90; i++)
                {
                    if (i % 5 == 0)
                    {
                        Console.WriteLine($"[Loop {i}] Node B peers count: {nodeB.AvailablePeers.Count}, Node A peers count: {nodeA.AvailablePeers.Count}");
                        foreach (var kv in nodeB.AvailablePeers)
                        {
                            Console.WriteLine($"  Node B peer {kv.Key}: Name={kv.Value.Name}, Onion={kv.Value.OnionAddress}, Cipher={kv.Value.PeerCipher != null}");
                        }
                        foreach (var kv in nodeA.AvailablePeers)
                        {
                            Console.WriteLine($"  Node A peer {kv.Key}: Name={kv.Value.Name}, Onion={kv.Value.OnionAddress}, Cipher={kv.Value.PeerCipher != null}");
                        }
                    }

                    peerA_on_B = nodeB.AvailablePeers.Values.FirstOrDefault(p => p.PeerCipher != null);
                    if (peerA_on_B != null && nodeA.AvailablePeers.Values.Any(p => p.PeerCipher != null))
                        break;

                    await Task.Delay(1000);
                }

                if (peerA_on_B == null || peerA_on_B.PeerCipher == null)
                {
                    Console.WriteLine("\n[ERROR] Peer discovery/handshake timed out over Tor.");
                    return;
                }

                Console.WriteLine($"\n[SUCCESS] Node B established session with Node A ({peerA_on_B.Name}) over Tor!");
                Console.WriteLine($"[VERIFICATION] Peer A identified on Node B with Tor ID: {peerA_on_B.Id} (Matches Node A Tor ID: {peerA_on_B.Id == nodeA.TorCurrentPeer.Id})");

                var peerB_on_A = nodeA.AvailablePeers.Values.FirstOrDefault(p => p.PeerCipher != null);
                if (peerB_on_A == null)
                {
                    Console.WriteLine("\n[ERROR] Node A did not retain the authenticated Tor peer.");
                    return;
                }

                nodeB.TrustPeer(peerA_on_B.CertHash);
                nodeA.TrustPeer(peerB_on_A.CertHash);

                byte[] testFileData = new byte[100 * 1024];
                RandomNumberGenerator.Fill(testFileData);
                byte[] originalHash = SHA256.HashData(testFileData);

                var senderProtocol = new TorFileShareSenderProtocol(testFileData);
                nodeB.RegisterProtocol(senderProtocol);

                Console.WriteLine($"\n[Node B] Transmitting {testFileData.Length} bytes payload over Tor to Node A via QUIC stream...");
                await nodeB.InitQuicConnection(senderProtocol.ProtocolId, peerA_on_B, cancellationToken: ctsB.Token);

                Console.WriteLine("[Node A] Waiting for incoming encrypted payload over Tor...");
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                byte[] receivedData = await receiverProtocol.DataReceived.Task.WaitAsync(timeoutCts.Token);

                byte[] receivedHash = SHA256.HashData(receivedData);
                Console.WriteLine($"[Node A] Successfully received {receivedData.Length} bytes over Tor QUIC stream!");

                if (originalHash.SequenceEqual(receivedHash))
                {
                    Console.WriteLine("\n==================================================");
                    Console.WriteLine("  [PASSED] FILE SHARE OVER TOR SUCCESSFUL!");
                    Console.WriteLine("  SHA256 checksums match perfectly!");
                    Console.WriteLine("==================================================");
                }
                else
                {
                    Console.WriteLine("\n[ERROR] File checksum mismatch!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[TEST ERROR] {ex}");
            }
            finally
            {
                if (nodeA != null) { try { await nodeA.DisposeAsync(); } catch { } }
                if (nodeB != null) { try { await nodeB.DisposeAsync(); } catch { } }
                try { Directory.Delete(dirA, true); } catch { }
                try { Directory.Delete(dirB, true); } catch { }
            }
        }

        private static readonly Guid TestTorProtocolId = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");

        private sealed class TorFileShareReceiverProtocol : QuicPunch.QuicPunch.IProtocolHandler
        {
            public Guid ProtocolId => TestTorProtocolId;
            public ushort PreferredPort => 0;
            public string ProtocolName => "TorFileShare";
            public ushort StreamPriority => (ushort)QuicStreamPriority.Normal;
            public ZstandardCompressionOptions? CompressionOptions => null;

            public TaskCompletionSource<byte[]> DataReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;

            public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                DataReceived.TrySetResult(ms.ToArray());
            }
        }

        private sealed class TorFileShareSenderProtocol : QuicPunch.QuicPunch.IProtocolHandler
        {
            private readonly byte[] _dataToSend;
            public Guid ProtocolId => TestTorProtocolId;
            public ushort PreferredPort => 0;
            public string ProtocolName => "TorFileShare";
            public ushort StreamPriority => (ushort)QuicStreamPriority.Normal;
            public ZstandardCompressionOptions? CompressionOptions => null;

            public TorFileShareSenderProtocol(byte[] dataToSend) => _dataToSend = dataToSend;

            public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;

            public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
            {
                await stream.WriteAsync(_dataToSend, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Close();
            }
        }
    }
