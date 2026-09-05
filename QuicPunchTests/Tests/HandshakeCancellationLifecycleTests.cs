using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicPunchTests;

public static class HandshakeCancellationLifecycleTests
{
        public static async Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   HANDSHAKE CANCELLATION & LIFECYCLE TESTS (P0-2)");
            Console.WriteLine("==================================================");

            await TestStopWhileWaitingForManualDecision();
            await TestStopDuringCandidateGathering();
            await TestStopDuringHolePunch();
            await TestStopWhileQuicConnecting();
            await TestStopStartImmediatelyWithActiveHandshake();

            Console.WriteLine("==================================================");
            Console.WriteLine("   ALL LIFECYCLE & CANCELLATION TESTS PASSED!     ");
            Console.WriteLine("==================================================");
        }

        private static void AssertCleanState(QuicPunch.QuicPunch qp, string context)
        {
            if (qp.ActiveIncomingWorkerCount != 0)
                throw new Exception($"[{context}] Expected 0 active incoming workers, got {qp.ActiveIncomingWorkerCount}");

            if (qp.ActiveOutboundNegotiationCount != 0)
                throw new Exception($"[{context}] Expected 0 active outbound negotiations, got {qp.ActiveOutboundNegotiationCount}");

            if (qp.ActiveProtocolSessionCount != 0)
                throw new Exception($"[{context}] Expected 0 active protocol sessions, got {qp.ActiveProtocolSessionCount}");

            if (qp.PendingQuicReadyCount != 0)
                throw new Exception($"[{context}] Expected 0 pending QUIC_READY, got {qp.PendingQuicReadyCount}");

            if (qp.IncomingHandshakeSessions.Count != 0)
                throw new Exception($"[{context}] Expected 0 transient incoming handshake sessions, got {qp.IncomingHandshakeSessions.Count}");

            if (qp.Manager.PendingDecisionsCount != 0)
                throw new Exception($"[{context}] Expected 0 pending decisions in HandshakeManager, got {qp.Manager.PendingDecisionsCount}");
        }

        private static async Task TestStopWhileWaitingForManualDecision()
        {
            Console.Write("[TEST 1] Stop while waiting for manual decision... ");
            string dirA = Path.Combine(Path.GetTempPath(), "qp_lc_test1_a_" + Guid.NewGuid().ToString("N"));
            string dirB = Path.Combine(Path.GetTempPath(), "qp_lc_test1_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: dirA, listeningPort: 0);
                var qpB = new QuicPunch.QuicPunch(appDataPath: dirB, listeningPort: 0);

                await qpA.StartAsync();
                await qpB.StartAsync();

                qpB.AutoAcceptConnections = false;
                qpB.AutoAcceptUntrustedConnections = false;
                qpB.TrustPeer(qpA.CurrentPeer.CertHash);

                var decisionBlocker = new TaskCompletionSource();
                qpB.Manager.HandshakeRequested += async (req, ct) =>
                {
                    decisionBlocker.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return new HandshakeDecision(false, null, null);
                };

                var protoId = Guid.NewGuid();
                qpB.RegisterProtocol(new DummyProtocolHandler(protoId, () => { }));

                var epA = new IPEndPoint(IPAddress.Loopback, qpA.LocalBoundPort);
                byte[] helloA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                var connGuid = Guid.NewGuid();
                byte[] req = qpA.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, (ushort)epA.Port, protoId, connGuid, null, QuicPunch.QuicPunch.TransportType.Wan);

                _ = qpB.ProcessIncomingPacketAsync(req, epA, QuicPunch.QuicPunch.TransportType.Wan);

                // Wait until manual decision is requested
                await decisionBlocker.Task.WaitAsync(TimeSpan.FromSeconds(5));

                if (qpB.ActiveIncomingWorkerCount == 0)
                    throw new Exception("Expected ActiveIncomingWorkerCount > 0 while waiting for manual decision");

                // Stop instance
                await qpB.StopAsync();

                // Assert clean state immediately after StopAsync
                AssertCleanState(qpB, "Test 1 post-stop");

                // Wait 2 seconds and verify no zombie session appears
                await Task.Delay(2000);
                AssertCleanState(qpB, "Test 1 delayed");

                await qpB.DisposeAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(dirA, true); } catch { }
                try { Directory.Delete(dirB, true); } catch { }
            }
        }

        private static async Task TestStopDuringCandidateGathering()
        {
            Console.Write("[TEST 2] Stop during candidate gathering... ");
            string dirA = Path.Combine(Path.GetTempPath(), "qp_lc_test2_a_" + Guid.NewGuid().ToString("N"));
            string dirB = Path.Combine(Path.GetTempPath(), "qp_lc_test2_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: dirA, listeningPort: 0);
                var qpB = new QuicPunch.QuicPunch(appDataPath: dirB, listeningPort: 0);

                // Point STUN servers to a blackhole IP so candidate gathering takes time (~800ms)
                qpB.StunServerEndpoints = new[] { new IPEndPoint(IPAddress.Parse("192.0.2.1"), 3478) };

                await qpA.StartAsync();
                await qpB.StartAsync();

                qpB.AutoAcceptConnections = true;
                qpB.AutoAcceptUntrustedConnections = true;

                var protoId = Guid.NewGuid();
                qpB.RegisterProtocol(new DummyProtocolHandler(protoId, () => { }));

                var epA = new IPEndPoint(IPAddress.Loopback, qpA.LocalBoundPort);
                byte[] helloA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                var connGuid = Guid.NewGuid();
                byte[] req = qpA.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, (ushort)epA.Port, protoId, connGuid, null, QuicPunch.QuicPunch.TransportType.Wan);

                _ = qpB.ProcessIncomingPacketAsync(req, epA, QuicPunch.QuicPunch.TransportType.Wan);

                // Wait until worker is active in candidate gathering
                int retries = 0;
                while (qpB.ActiveIncomingWorkerCount == 0 && retries++ < 50)
                {
                    await Task.Delay(10);
                }

                if (qpB.ActiveIncomingWorkerCount == 0)
                    throw new Exception("Expected ActiveIncomingWorkerCount > 0 during candidate gathering");

                // Stop instance while gathering candidates
                await qpB.StopAsync();

                // Assertions
                AssertCleanState(qpB, "Test 2 post-stop");

                await Task.Delay(2000);
                AssertCleanState(qpB, "Test 2 delayed");

                await qpB.DisposeAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(dirA, true); } catch { }
                try { Directory.Delete(dirB, true); } catch { }
            }
        }

        private static async Task TestStopDuringHolePunch()
        {
            Console.Write("[TEST 3] Stop during hole punch... ");
            string dirA = Path.Combine(Path.GetTempPath(), "qp_lc_test3_a_" + Guid.NewGuid().ToString("N"));
            string dirB = Path.Combine(Path.GetTempPath(), "qp_lc_test3_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: dirA, listeningPort: 0);
                var qpB = new QuicPunch.QuicPunch(appDataPath: dirB, listeningPort: 0);

                // No STUN servers so candidate gathering is immediate
                qpB.StunServerEndpoints = Array.Empty<IPEndPoint>();
                qpA.StunServerEndpoints = Array.Empty<IPEndPoint>();

                await qpA.StartAsync();
                await qpB.StartAsync();

                qpB.AutoAcceptConnections = true;
                qpB.AutoAcceptUntrustedConnections = true;

                var protoId = Guid.NewGuid();
                qpB.RegisterProtocol(new DummyProtocolHandler(protoId, () => { }));

                var epA = new IPEndPoint(IPAddress.Loopback, qpA.LocalBoundPort);
                byte[] helloA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                // Provide a remote candidate with an unused port that won't respond to hole punch
                var dummyCandidate = new List<CandidateEndpoint> { new CandidateEndpoint(new IPEndPoint(IPAddress.Loopback, 59999), CandidateType.Host) };
                var connGuid = Guid.NewGuid();
                byte[] req = qpA.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, 59999, protoId, connGuid, dummyCandidate, QuicPunch.QuicPunch.TransportType.Wan);

                _ = qpB.ProcessIncomingPacketAsync(req, epA, QuicPunch.QuicPunch.TransportType.Wan);

                // Wait until worker is in hole punch (Accept payload generated and worker active)
                int retries = 0;
                while (retries++ < 100 && (qpB.ActiveIncomingWorkerCount == 0 || !qpB.IncomingHandshakeSessions.TryGetValue(connGuid, out var s) || !s.ResponsePayloadTcs.Task.IsCompleted))
                {
                    await Task.Delay(20);
                }

                if (qpB.ActiveIncomingWorkerCount == 0)
                    throw new Exception("Expected ActiveIncomingWorkerCount > 0 during hole punch");

                // Stop instance during hole punch
                await qpB.StopAsync();

                // Assertions
                AssertCleanState(qpB, "Test 3 post-stop");

                await Task.Delay(2000);
                AssertCleanState(qpB, "Test 3 delayed");

                await qpB.DisposeAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(dirA, true); } catch { }
                try { Directory.Delete(dirB, true); } catch { }
            }
        }

        private static async Task TestStopWhileQuicConnecting()
        {
            Console.Write("[TEST 4] Stop while QUIC is connecting... ");
            string dirA = Path.Combine(Path.GetTempPath(), "qp_lc_test4_a_" + Guid.NewGuid().ToString("N"));
            string dirB = Path.Combine(Path.GetTempPath(), "qp_lc_test4_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: dirA, listeningPort: 0);
                var qpB = new QuicPunch.QuicPunch(appDataPath: dirB, listeningPort: 0);

                qpB.StunServerEndpoints = Array.Empty<IPEndPoint>();
                qpA.StunServerEndpoints = Array.Empty<IPEndPoint>();

                await qpA.StartAsync();
                await qpB.StartAsync();

                qpB.AutoAcceptConnections = true;
                qpB.AutoAcceptUntrustedConnections = true;

                var protoId = Guid.NewGuid();
                qpB.RegisterProtocol(new DummyProtocolHandler(protoId, () => { }));

                var epA = new IPEndPoint(IPAddress.Loopback, qpA.LocalBoundPort);
                byte[] helloA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                // Use udp listener for Node A to receive B's accept and send final hole punch
                using var dummyUdpA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                ushort dummyPortA = (ushort)((IPEndPoint)dummyUdpA.Client.LocalEndPoint!).Port;

                var candA = new List<CandidateEndpoint> { new CandidateEndpoint(new IPEndPoint(IPAddress.Loopback, dummyPortA), CandidateType.Host) };
                var connGuid = Guid.NewGuid();
                byte[] req = qpA.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, dummyPortA, protoId, connGuid, candA, QuicPunch.QuicPunch.TransportType.Wan);

                _ = qpB.ProcessIncomingPacketAsync(req, epA, QuicPunch.QuicPunch.TransportType.Wan);

                // Wait for B to generate accept response
                int retries = 0;
                while (retries++ < 100 && (!qpB.IncomingHandshakeSessions.TryGetValue(connGuid, out var s) || !s.ResponsePayloadTcs.Task.IsCompleted))
                {
                    await Task.Delay(20);
                }

                if (!qpB.IncomingHandshakeSessions.TryGetValue(connGuid, out var session) || !session.ResponsePayloadTcs.Task.IsCompleted)
                    throw new Exception("Node B did not complete Accept payload in time");

                // Parse B's bound port from accept payload
                byte[] acceptPayload = await session.ResponsePayloadTcs.Task;
                using var ms = new MemoryStream(acceptPayload);
                using var r = new BinaryReader(ms);
                ms.Position = QuicPunch.QuicPunch.MagicHeader.Length + 1; // skip header + type
                var bPeerId = new Guid(r.ReadBytes(16));
                var bType = (QuicPunchStructures.HandShakeType)r.ReadByte();
                var bPort = r.ReadUInt16();

                // Send FinalHandshake from Node A's dummy UDP socket to B's bound port to satisfy hole punch
                byte[] finalHs = new byte[QuicPunch.QuicPunch.MagicHeader.Length + 1 + 16 + 16];
                Buffer.BlockCopy(QuicPunch.QuicPunch.MagicHeader, 0, finalHs, 0, QuicPunch.QuicPunch.MagicHeader.Length);
                finalHs[QuicPunch.QuicPunch.MagicHeader.Length] = (byte)QuicPunchStructures.MessageType.FinalHandshake;
                connGuid.ToByteArray().CopyTo(finalHs.AsSpan(QuicPunch.QuicPunch.MagicHeader.Length + 1, 16));
                qpA.CurrentPeer.IdRaw.CopyTo(finalHs.AsSpan(QuicPunch.QuicPunch.MagicHeader.Length + 1 + 16, 16));

                var bEp = new IPEndPoint(IPAddress.Loopback, bPort);
                for (int i = 0; i < 5; i++)
                {
                    await dummyUdpA.SendAsync(finalHs, bEp);
                    await Task.Delay(10);
                }

                // Node B now completes hole punch and enters QUIC connect phase
                await Task.Delay(100);

                // Stop instance while QUIC is connecting
                await qpB.StopAsync();

                // Assertions
                AssertCleanState(qpB, "Test 4 post-stop");

                await Task.Delay(2000);
                AssertCleanState(qpB, "Test 4 delayed");

                await qpB.DisposeAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(dirA, true); } catch { }
                try { Directory.Delete(dirB, true); } catch { }
            }
        }

        private static async Task TestStopStartImmediatelyWithActiveHandshake()
        {
            Console.Write("[TEST 5] Stop -> Start immediately while previous handshake was active... ");
            string dirA = Path.Combine(Path.GetTempPath(), "qp_lc_test5_a_" + Guid.NewGuid().ToString("N"));
            string dirB = Path.Combine(Path.GetTempPath(), "qp_lc_test5_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: dirA, listeningPort: 0);
                var qpB = new QuicPunch.QuicPunch(appDataPath: dirB, listeningPort: 0);

                await qpA.StartAsync();
                await qpB.StartAsync();

                qpB.AutoAcceptConnections = false;
                qpB.AutoAcceptUntrustedConnections = false;
                qpB.TrustPeer(qpA.CurrentPeer.CertHash);

                var decisionBlocker = new TaskCompletionSource();
                qpB.Manager.HandshakeRequested += async (req, ct) =>
                {
                    decisionBlocker.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return new HandshakeDecision(false, null, null);
                };

                var protoId = Guid.NewGuid();
                qpB.RegisterProtocol(new DummyProtocolHandler(protoId, () => { }));

                var epA = new IPEndPoint(IPAddress.Loopback, qpA.LocalBoundPort);
                byte[] helloA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                var connGuid = Guid.NewGuid();
                byte[] req = qpA.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, (ushort)epA.Port, protoId, connGuid, null, QuicPunch.QuicPunch.TransportType.Wan);

                _ = qpB.ProcessIncomingPacketAsync(req, epA, QuicPunch.QuicPunch.TransportType.Wan);

                await decisionBlocker.Task.WaitAsync(TimeSpan.FromSeconds(5));

                long gen1 = qpB.LifecycleGeneration;

                // Stop instance
                await qpB.StopAsync();

                AssertCleanState(qpB, "Test 5 post-stop");

                // Immediately restart instance
                await qpB.StartAsync();

                long gen2 = qpB.LifecycleGeneration;
                if (gen2 <= gen1)
                    throw new Exception($"Expected LifecycleGeneration to increment after restart (gen1={gen1}, gen2={gen2})");

                // Assert fresh state in new generation
                AssertCleanState(qpB, "Test 5 post-start");

                // Verify that trying to register a session with the old generation (gen1) fails
                bool oldRegResult = await qpB.RegisterProtocolSessionAsync(Guid.NewGuid(), protoId, null!, null!, gen1);
                if (oldRegResult)
                    throw new Exception("RegisterProtocolSessionAsync unexpectedly allowed registration with obsolete generation!");

                // Wait 2-3 seconds to verify no zombie sessions ever appear from gen1
                await Task.Delay(2500);
                AssertCleanState(qpB, "Test 5 delayed");

                await qpB.StopAsync();
                await qpB.DisposeAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(dirA, true); } catch { }
                try { Directory.Delete(dirB, true); } catch { }
            }
        }

        private sealed class DummyProtocolHandler : QuicPunch.QuicPunch.IProtocolHandler
        {
            public Guid ProtocolId { get; }
            public string ProtocolName => "LifecycleDummyProtocol";
            public System.IO.Compression.ZstandardCompressionOptions? CompressionOptions => null;

            private readonly Action? _onHandle;

            public DummyProtocolHandler(Guid protocolId, Action onHandle)
            {
                ProtocolId = protocolId;
                _onHandle = onHandle;
            }

            public Task HandleAsync(QuicPunch.QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
            {
                _onHandle?.Invoke();
                return Task.CompletedTask;
            }

            public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
        }
    }
