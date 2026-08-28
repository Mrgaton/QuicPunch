using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicPunchTests
{
    public static class SecurityDiscoveryTests
    {
        public static async Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   SECURITY & DISCOVERY MODEL VERIFICATION TESTS  ");
            Console.WriteLine("==================================================");

            await TestHelloDiscoversWithoutAutoPinningOrAutoSaving();
            await TestPingIgnoredForUnknownPeers();
            await TestExplicitSaveAndTrustPeer();
            await TestDataChannelBidirectionalEncryption();
            await TestAtomicIdentityPersistenceAndPermissions();
            await TestIPAddressValidationAndFiltering();
            await TestBigSendZeroAllocStochasticSampling();
            await TestDeterministicRoleAssignmentAndPreciseTimeResilience();
            await TestQuicConnectionTransportIntrospectionAndStunBatching();
            await TestHelloAntiReplayAndSaltDerivation();
            await TestAutoAcceptSettingsAndPeerTrust();
            await TestAsymmetricUnrelatedPortHolePunch();
            await TestLanDiscoveryWithDifferentControlPorts();
            await TestIdempotentHandshakeRetransmissions();
            await TestClockSkewResilienceWithChallengeResponse();
            await TestStunAndTrackerAutoRecoveryAfterNetworkRestoration();
            await TestTrackerScannerDynamicPublicPortAnnouncementAndRebind();
            await TestSessionKeyUniquenessAndNoNonceReuseAcrossRestarts();
            await TestInvalidHandshakeCannotMutatePeerEndpointState();
            await TestFullLifecycleStartStopRestartAndUnconditionalDispose();
            await TestHelloHandlerNoCertificateLeaksOnInvalidOrRepeatedPackets();
            await TestIpRateLimiterEvictionAndBoundedCardinality();
            await TestMultiPeerIsolationAndNoDesynchronizationOnPeerRemoval();
            await TestUnilateralInterrogationDiscoversPeerBidirectionally();
            await TestUntrustedDiscoveredPeerCannotAutoAcceptProtocolHandshake();
            await TestIncomingHandshakeSessionsLifecycleAndHardCap();
            await TestTrackerScannerLifecycleAndIdempotency();
            await TestEndpointCacheBinaryProtocolSerializationAndLoading();
            await TestSingleFlightConcurrentInitQuicConnection();
            await TestActiveSessionReuse();

            Console.WriteLine("==================================================");
            Console.WriteLine("   ALL SECURITY & DISCOVERY TESTS PASSED!         ");
            Console.WriteLine("==================================================");
        }

        private static async Task TestDataChannelBidirectionalEncryption()
        {
            Console.Write("[TEST] WireGuard-style directional AEAD cipher (AES-256-GCM + deterministic nonces)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_cipher_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_cipher_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);

                await qpA.StartAsync();
                await qpB.StartAsync();

                var epA = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 40001);
                var epB = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 40002);

                byte[] helloFromB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloFromB, epB, QuicPunch.QuicPunch.TransportType.Wan);

                byte[] helloFromA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloFromA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB_on_A) || !peerB_on_A.IsSessionReady)
                    throw new Exception("Peer B session is not ready on Node A.");

                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_on_B) || !peerA_on_B.IsSessionReady)
                    throw new Exception("Peer A session is not ready on Node B.");

                if (!CryptographicOperations.FixedTimeEquals(peerB_on_A.TxSalt, peerA_on_B.RxSalt))
                    throw new Exception("Directional salt mismatch: Node A TxSalt does not match Node B RxSalt.");

                if (!CryptographicOperations.FixedTimeEquals(peerB_on_A.RxSalt, peerA_on_B.TxSalt))
                    throw new Exception("Directional salt mismatch: Node A RxSalt does not match Node B TxSalt.");

                var readerB = qpB.GetPacketReader(100);
                var readerA = qpA.GetPacketReader(200);

                byte[] msgA2B = System.Text.Encoding.UTF8.GetBytes("Secret payload from A to B");
                ulong seqA = peerB_on_A.GetNextOutboundSequence();
                int headerAadSize = QuicPunch.QuicPunch.MagicHeader.Length + sizeof(byte) + sizeof(ushort) + 16 + sizeof(ulong);
                int packetLenA = headerAadSize + 16 + msgA2B.Length;
                byte[] packetA = new byte[packetLenA];

                int offset = 0;
                QuicPunch.QuicPunch.MagicHeader.CopyTo(packetA.AsSpan(offset));
                offset += QuicPunch.QuicPunch.MagicHeader.Length;
                packetA[offset++] = (byte)QuicPunchStructures.MessageType.Data;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packetA.AsSpan(offset, 2), 100);
                offset += 2;
                qpA.CurrentPeer.IdRaw.CopyTo(packetA.AsSpan(offset, 16));
                offset += 16;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(packetA.AsSpan(offset, 8), seqA);
                offset += 8;

                Span<byte> tagA = packetA.AsSpan(offset, 16);
                offset += 16;
                Span<byte> cipherA = packetA.AsSpan(offset);

                Span<byte> nonceA = stackalloc byte[12];
                peerB_on_A.TxSalt.CopyTo(nonceA.Slice(0, 4));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonceA.Slice(4, 8), seqA);

                peerB_on_A.TxCipher!.Encrypt(nonceA, msgA2B, cipherA, tagA, packetA.AsSpan(0, headerAadSize));

                await qpB.ProcessIncomingPacketAsync(packetA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                using var ctsB = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                var (senderGuidB, receivedPayloadB) = await readerB.ReadAsync(ctsB.Token);

                if (senderGuidB != qpA.CurrentPeer.Id)
                    throw new Exception("Sender ID mismatch on Node B.");

                if (!System.Text.Encoding.UTF8.GetString(receivedPayloadB).Equals("Secret payload from A to B"))
                    throw new Exception("Payload corruption in A -> B transmission.");

                byte[] msgB2A = System.Text.Encoding.UTF8.GetBytes("Secret payload from B to A");
                ulong seqB = peerA_on_B.GetNextOutboundSequence();
                int packetLenB = headerAadSize + 16 + msgB2A.Length;
                byte[] packetB = new byte[packetLenB];

                offset = 0;
                QuicPunch.QuicPunch.MagicHeader.CopyTo(packetB.AsSpan(offset));
                offset += QuicPunch.QuicPunch.MagicHeader.Length;
                packetB[offset++] = (byte)QuicPunchStructures.MessageType.Data;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packetB.AsSpan(offset, 2), 200);
                offset += 2;
                qpB.CurrentPeer.IdRaw.CopyTo(packetB.AsSpan(offset, 16));
                offset += 16;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(packetB.AsSpan(offset, 8), seqB);
                offset += 8;

                Span<byte> tagB = packetB.AsSpan(offset, 16);
                offset += 16;
                Span<byte> cipherB = packetB.AsSpan(offset);

                Span<byte> nonceB = stackalloc byte[12];
                peerA_on_B.TxSalt.CopyTo(nonceB.Slice(0, 4));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonceB.Slice(4, 8), seqB);

                peerA_on_B.TxCipher!.Encrypt(nonceB, msgB2A, cipherB, tagB, packetB.AsSpan(0, headerAadSize));

                await qpA.ProcessIncomingPacketAsync(packetB, epB, QuicPunch.QuicPunch.TransportType.Wan);

                using var ctsA = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                var (senderGuidA, receivedPayloadA) = await readerA.ReadAsync(ctsA.Token);

                if (senderGuidA != qpB.CurrentPeer.Id)
                    throw new Exception("Sender ID mismatch on Node A.");

                if (!System.Text.Encoding.UTF8.GetString(receivedPayloadA).Equals("Secret payload from B to A"))
                    throw new Exception("Payload corruption in B -> A transmission.");

                byte[] tamperedPacket = (byte[])packetA.Clone();
                tamperedPacket[tamperedPacket.Length - 1] ^= 0xFF;

                await qpB.ProcessIncomingPacketAsync(tamperedPacket, epA, QuicPunch.QuicPunch.TransportType.Wan);
                if (readerB.TryRead(out _))
                    throw new Exception("Tampered AEAD ciphertext was accepted and decrypted!");

                await qpB.ProcessIncomingPacketAsync(packetA, epA, QuicPunch.QuicPunch.TransportType.Wan);
                if (readerB.TryRead(out _))
                    throw new Exception("Replayed packet was accepted a second time!");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestHelloDiscoversWithoutAutoPinningOrAutoSaving()
        {
            Console.Write("[TEST] Hello/Interrogation adds to AvailablePeers WITHOUT ExpectedPeerCerts or PeerStore auto-save... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_sec_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_sec_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);

                await qpA.StartAsync();
                await qpB.StartAsync();

                var bCertHash = qpB.CurrentPeer.CertHash;
                var bId = qpB.CurrentPeer.Id;

                if (qpA.ExpectedPeerCerts.Contains(bCertHash))
                    throw new Exception("Node A already contains Node B cert hash in ExpectedPeerCerts.");

                if (qpA.PeerStore.SavedPeers.Count > 0)
                    throw new Exception("Node A PeerStore is not empty at start.");

                byte[] helloPayload = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                var fakeEndpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 55555);

                await qpA.ProcessIncomingPacketAsync(helloPayload, fakeEndpoint, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.ContainsKey(bId))
                    throw new Exception("Peer B was not added to AvailablePeers after valid Interrogation packet.");

                if (qpA.ExpectedPeerCerts.Contains(bCertHash))
                    throw new Exception("SECURITY VULNERABILITY: Peer B certificate was auto-pinned into ExpectedPeerCerts!");

                if (qpA.PeerStore.SavedPeers.Count > 0)
                    throw new Exception("SECURITY VULNERABILITY: Peer B was automatically saved to PeerStore database!");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestPingIgnoredForUnknownPeers()
        {
            Console.Write("[TEST] Ping handler ignores and does not amplify/respond to unknown peers... ");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_ping_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0);
                await qp.StartAsync();

                long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                byte[] fakePingPacket = qp.BuildPingPacket(timestamp, isResponse: false);

                Guid fakeGuid = Guid.NewGuid();
                fakeGuid.ToByteArray().CopyTo(fakePingPacket.AsSpan(QuicPunch.QuicPunch.MagicHeader.Length + 2, 16));

                var fakeEndpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 44444);

                await qp.ProcessIncomingPacketAsync(fakePingPacket, fakeEndpoint, QuicPunch.QuicPunch.TransportType.Wan);

                if (qp.AvailablePeers.ContainsKey(fakeGuid))
                    throw new Exception("Fake peer GUID was added to AvailablePeers via unauthenticated Ping!");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestExplicitSaveAndTrustPeer()
        {
            Console.Write("[TEST] Explicit SavePeer, TrustPeer, and RemoveSavedPeer functionality... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_save_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_save_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);

                await qpA.StartAsync();
                await qpB.StartAsync();

                qpB.CurrentPeer.Addresses = new[] { IPAddress.Parse("127.0.0.1") };
                qpB.CurrentPeer.MinPort = 50000;
                qpB.CurrentPeer.MaxPort = 50000;

                var bCertHash = qpB.CurrentPeer.CertHash;

                bool saved = qpA.SavePeer(qpB.CurrentPeer);
                if (!saved)
                    throw new Exception("SavePeer returned false for valid PeerInfo.");

                if (!qpA.ExpectedPeerCerts.Contains(bCertHash))
                    throw new Exception("ExpectedPeerCerts does not contain peer certificate hash after SavePeer.");

                if (!qpA.PeerStore.TryGet(bCertHash, out _))
                    throw new Exception("PeerStore does not contain saved peer after SavePeer.");

                bool removed = qpA.RemoveSavedPeer(bCertHash);
                if (!removed)
                    throw new Exception("RemoveSavedPeer returned false for existing saved peer.");

                if (qpA.ExpectedPeerCerts.Contains(bCertHash))
                    throw new Exception("ExpectedPeerCerts still contains peer certificate hash after RemoveSavedPeer.");

                if (qpA.PeerStore.TryGet(bCertHash, out _))
                    throw new Exception("PeerStore still contains peer after RemoveSavedPeer.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestAtomicIdentityPersistenceAndPermissions()
        {
            Console.Write("[TEST] Atomic identity persistence, cross-platform permissions & ECDH agreement... ");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_ident_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                byte[] hash1, ecdhPub1, curveHash1;
                byte[] sharedSecret1, sharedSecret2;

                using (var cm1 = new CertManager(tempDir, "wan"))
                {
                    var cert1 = cm1.PeerCertificate;
                    if (!File.Exists(cm1.IdentityPath))
                        throw new Exception("Identity bundle was not created on first access.");

                    hash1 = cm1.CertPublicHash;
                    ecdhPub1 = cm1.EcdhPublicKeyRaw;
                    curveHash1 = cm1.CurveHash;

                    var ext = cm1.PeerCertificate.Extensions[CertManager.EcdhExtensionOid];
                    if (ext == null || !CryptographicOperations.FixedTimeEquals(ext.RawData, ecdhPub1))
                        throw new Exception("Certificate ECDH extension does not match ECDH public key raw data.");

                    if (!OperatingSystem.IsWindows())
                    {
                        var mode = File.GetUnixFileMode(cm1.IdentityPath);
                        if (!mode.HasFlag(UnixFileMode.UserRead) || !mode.HasFlag(UnixFileMode.UserWrite))
                            throw new Exception($"Unexpected UnixFileMode on identity file: {mode}");
                    }

                    using var otherEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
                    sharedSecret1 = cm1.EcdhKey.DeriveRawSecretAgreement(otherEcdh.PublicKey);
                    sharedSecret2 = otherEcdh.DeriveRawSecretAgreement(cm1.EcdhKey.PublicKey);

                    if (!CryptographicOperations.FixedTimeEquals(sharedSecret1, sharedSecret2))
                        throw new Exception("ECDH secret agreement failed with freshly generated identity.");
                }

                using (var cm2 = new CertManager(tempDir, "wan"))
                {
                    if (!CryptographicOperations.FixedTimeEquals(cm2.CertPublicHash, hash1))
                        throw new Exception("CertPublicHash changed after reloading from atomic identity bundle.");

                    if (!CryptographicOperations.FixedTimeEquals(cm2.EcdhPublicKeyRaw, ecdhPub1))
                        throw new Exception("EcdhPublicKeyRaw changed after reloading from atomic identity bundle.");

                    if (!CryptographicOperations.FixedTimeEquals(cm2.CurveHash, curveHash1))
                        throw new Exception("CurveHash changed after reloading from atomic identity bundle.");
                }

                Console.WriteLine("PASSED");
                await Task.CompletedTask;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestIPAddressValidationAndFiltering()
        {
            Console.Write("[TEST] IP address validation rules & token filtering... ");

            if (!Utilities.IsValidPeerAddress(IPAddress.Parse("127.0.0.1")))
                throw new Exception("IsValidPeerAddress incorrectly rejected localhost 127.0.0.1.");

            if (!Utilities.IsValidPeerAddress(IPAddress.Parse("10.0.0.1")))
                throw new Exception("IsValidPeerAddress incorrectly rejected private IP 10.0.0.1.");

            if (!Utilities.IsValidPeerAddress(IPAddress.Parse("192.168.1.100")))
                throw new Exception("IsValidPeerAddress incorrectly rejected private IP 192.168.1.100.");

            if (!Utilities.IsValidPeerAddress(IPAddress.Parse("8.8.8.8")))
                throw new Exception("IsValidPeerAddress incorrectly rejected public IP 8.8.8.8.");

            if (!Utilities.IsValidPeerAddress(IPAddress.IPv6Loopback))
                throw new Exception("IsValidPeerAddress incorrectly rejected IPv6 loopback ::1.");

            if (Utilities.IsValidPeerAddress(IPAddress.Any))
                throw new Exception("IsValidPeerAddress failed to reject 0.0.0.0.");

            if (Utilities.IsValidPeerAddress(IPAddress.Parse("0.1.2.3")))
                throw new Exception("IsValidPeerAddress failed to reject 0.1.2.3.");

            if (Utilities.IsValidPeerAddress(IPAddress.Broadcast))
                throw new Exception("IsValidPeerAddress failed to reject broadcast 255.255.255.255.");

            if (Utilities.IsValidPeerAddress(IPAddress.Parse("224.0.0.1")))
                throw new Exception("IsValidPeerAddress failed to reject multicast 224.0.0.1.");

            if (Utilities.IsValidPeerAddress(IPAddress.Parse("239.255.255.250")))
                throw new Exception("IsValidPeerAddress failed to reject multicast 239.255.255.250.");

            if (Utilities.IsValidPeerAddress(IPAddress.Parse("240.0.0.1")))
                throw new Exception("IsValidPeerAddress failed to reject reserved 240.0.0.1.");

            byte[] fakeHash = new byte[32];
            var validPeer = new PeerInfo
            {
                NetworkType = QuicPunch.QuicPunch.NetworkType.DynamicAddress,
                MinPort = 5000,
                MaxPort = 5000,
                Addresses = new[] { IPAddress.Parse("127.0.0.1") }
            };
            validPeer.SetCertificateHash(fakeHash);

            string validToken = Utilities.EncodeEndpointToken(validPeer);
            var decoded = Utilities.DecodeEndpointToken(validToken);
            if (!decoded.Addresses.Contains(IPAddress.Parse("127.0.0.1")))
                throw new Exception("Valid token was not decoded properly.");

            var badPeer = new PeerInfo
            {
                NetworkType = QuicPunch.QuicPunch.NetworkType.DynamicAddress,
                MinPort = 5000,
                MaxPort = 5000,
                Addresses = new[] { IPAddress.Parse("255.255.255.255") }
            };
            badPeer.SetCertificateHash(fakeHash);

            string badToken = Utilities.EncodeEndpointToken(badPeer);
            bool caught = false;
            try
            {
                Utilities.DecodeEndpointToken(badToken);
            }
            catch (InvalidDataException)
            {
                caught = true;
            }

            if (!caught)
                throw new Exception("DecodeEndpointToken failed to throw InvalidDataException for dangerous broadcast IP.");

            Console.WriteLine("PASSED");
            await Task.CompletedTask;
        }

        private static async Task TestBigSendZeroAllocStochasticSampling()
        {
            Console.Write("[TEST] BigSendAsync zero-allocation & stochastic port sampling... ");

            using var udp = new System.Net.Sockets.UdpClient(0);
            var dummyPayload = new byte[] { 1, 2, 3, 4 };

            var peerInfo = new PeerInfo
            {
                Addresses = new[] { IPAddress.Parse("127.0.0.1") },
                MinPort = 1000,
                MaxPort = 60000
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await udp.BigSendAsync(dummyPayload, peerInfo);
            sw.Stop();

            if (sw.ElapsedMilliseconds > 1500)
                throw new Exception($"BigSendAsync took too long ({sw.ElapsedMilliseconds}ms), indicating unbounded flood.");

            peerInfo.ActiveEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), ((IPEndPoint)udp.Client.LocalEndPoint!).Port);
            sw.Restart();
            await udp.BigSendAsync(dummyPayload, peerInfo);
            sw.Stop();

            if (sw.ElapsedMilliseconds > 500)
                throw new Exception("BigSendAsync ActiveEndPoint fast-path was too slow.");

            Console.WriteLine("PASSED");
        }

        private static async Task TestDeterministicRoleAssignmentAndPreciseTimeResilience()
        {
            Console.Write("[TEST] Deterministic QUIC role assignment & PreciseTime resilience... ");

            byte[] hashA = new byte[32];
            hashA[0] = 0xAA;
            var peerA = new PeerInfo();
            peerA.SetCertificateHash(hashA);

            byte[] hashB = new byte[32];
            hashB[0] = 0xBB;
            var peerB = new PeerInfo();
            peerB.SetCertificateHash(hashB);

            bool aIsServer = QuicPunchConnection.AmIServer(peerA, peerB);
            bool bIsServer = QuicPunchConnection.AmIServer(peerB, peerA);

            if (aIsServer == bIsServer)
                throw new Exception("AmIServer failed: both peers calculated identical roles!");

            if (!aIsServer && !bIsServer)
                throw new Exception("AmIServer failed: neither peer is server!");

            var time = PreciseTime.GetCorrectTime();
            if (time.Year < 2024)
                throw new Exception("PreciseTime.GetCorrectTime returned an invalid year.");

            await PreciseTime.WaitNextTrigger(10);
            await PreciseTime.WaitNextTrigger(-5);
            await PreciseTime.WaitNextTrigger(0);

            Console.WriteLine("PASSED");
        }

        private static async Task TestQuicConnectionTransportIntrospectionAndStunBatching()
        {
            Console.Write("[TEST] QuicConnection TransportType introspection & STUN batching... ");

            using var ms = new MemoryStream();
            var dummyConn = QuicConnection.CreateDummy(ms, QuicConnectionRole.Client);
            if (dummyConn.TransportType != QuicPunch.QuicPunch.TransportType.Tor)
                throw new Exception($"Expected Tor transport type on dummy connection, got {dummyConn.TransportType}");

            if (!dummyConn.IsTor || dummyConn.IsNativeQuic)
                throw new Exception("IsTor or IsNativeQuic helper flags incorrect on dummy connection.");

            using var udp = new System.Net.Sockets.UdpClient();
            var serverList = Enumerable.Range(1, 120)
                .Select(i => new IPEndPoint(IPAddress.Parse($"10.0.0.{i % 250 + 1}"), 3478))
                .ToList();

            var stunClient = new SimpleStunClient(udp, serverList)
            {
                DefaultBatchSize = 50
            };

            if (stunClient.DefaultBatchSize != 50)
                throw new Exception("DefaultBatchSize on SimpleStunClient failed to set.");

            var epFresh = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 5001);
            var epStale = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 5002);

            stunClient.StunResponseEndpointHits[epFresh] = new StunEndpointHit(3, Environment.TickCount64);
            stunClient.StunResponseEndpointHits[epStale] = new StunEndpointHit(1, Environment.TickCount64 - 10000);

            stunClient.PruneExpiredHits(TimeSpan.FromSeconds(4));

            if (!stunClient.StunResponseEndpointHits.ContainsKey(epFresh))
                throw new Exception("Fresh STUN response was unexpectedly pruned from sliding buffer.");

            if (stunClient.StunResponseEndpointHits.ContainsKey(epStale))
                throw new Exception("Stale STUN response was NOT pruned from sliding buffer.");

            var activeHits = stunClient.GetActiveHitsSnapshot();
            if (activeHits.Count != 1 || !activeHits.ContainsKey(epFresh) || activeHits[epFresh] != 3)
                throw new Exception("GetActiveHitsSnapshot returned invalid snapshot of active hits.");

            Console.WriteLine("PASSED");
        }

        private static async Task TestHelloAntiReplayAndSaltDerivation()
        {
            Console.Write("[TEST] Hello Anti-Replay protection & 256-bit PBKDF2 salt derivation... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_replay_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_replay_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                byte[] pwd = System.Text.Encoding.UTF8.GetBytes("SuperSecretPassword123!");

                using var qpStandaloneA = new QuicPunch.QuicPunch(appDataPath: tempDirA, connectionPassword: pwd);
                using var qpStandaloneB = new QuicPunch.QuicPunch(appDataPath: tempDirB, connectionPassword: pwd);

                if (qpStandaloneA.PasswordHash == null || qpStandaloneB.PasswordHash == null)
                    throw new Exception("PasswordHash was null on standalone instances.");

                if (!CryptographicOperations.FixedTimeEquals(qpStandaloneA.PasswordHash, qpStandaloneB.PasswordHash))
                    throw new Exception("Standalone instances with the same password did not derive identical PasswordHash.");

                using var qpWrongPwd = new QuicPunch.QuicPunch(appDataPath: Path.Combine(tempDirA, "wrong"), connectionPassword: System.Text.Encoding.UTF8.GetBytes("WrongPassword!"));
                if (CryptographicOperations.FixedTimeEquals(qpStandaloneA.PasswordHash, qpWrongPwd.PasswordHash))
                    throw new Exception("Different passwords derived identical PasswordHash!");

                byte[] sharedPoolId = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes("TestPool256"));
                using var qpA = new QuicPunch.QuicPunch(appDataPath: Path.Combine(tempDirA, "nodeA"), discoveryId: sharedPoolId, connectionPassword: pwd);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: Path.Combine(tempDirB, "nodeB"), discoveryId: sharedPoolId, connectionPassword: pwd);

                var epB1 = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 4001);
                var epAttacker = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 9999);

                byte[] helloFromB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);

                await qpA.ProcessIncomingPacketAsync(helloFromB, epB1, QuicPunch.QuicPunch.TransportType.Wan);

                var peerB = qpA.AvailablePeers.Values.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(p.CertHash, qpB.CurrentPeer.CertHash));
                if (peerB == null)
                    throw new Exception("Legitimate Hello packet from B was not processed.");

                if (peerB.ActiveEndPoint?.Port != epB1.Port)
                    throw new Exception("ActiveEndPoint did not point to B1.");

                await qpA.ProcessIncomingPacketAsync(helloFromB, epAttacker, QuicPunch.QuicPunch.TransportType.Wan);

                if (peerB.ActiveEndPoint?.Address.ToString() == epAttacker.Address.ToString())
                    throw new Exception("Anti-replay FAILED: Attacker successfully hijacked ActiveEndPoint via replayed Hello packet!");

                var epStandB = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 4002);
                byte[] helloStandB = qpStandaloneB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpStandaloneA.ProcessIncomingPacketAsync(helloStandB, epStandB, QuicPunch.QuicPunch.TransportType.Wan);

                var peerStandB = qpStandaloneA.AvailablePeers.Values.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(p.CertHash, qpStandaloneB.CurrentPeer.CertHash));
                if (peerStandB == null)
                    throw new Exception("Standalone Hello packet between nodes with same password was rejected.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestAutoAcceptSettingsAndPeerTrust()
        {
            Console.Write("[TEST] Auto-Accept settings (global toggle & peer-specific trust)... ");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_autoaccept_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, autoAcceptConnections: false);

                var peerId1 = Guid.NewGuid();
                var peerId2 = Guid.NewGuid();
                var certHash1 = SHA3_256.HashData(System.Text.Encoding.UTF8.GetBytes("cert-1"));
                var certHash2 = SHA3_256.HashData(System.Text.Encoding.UTF8.GetBytes("cert-2"));

                if (qp.AutoAcceptConnections)
                    throw new Exception("AutoAcceptConnections was expected to be false.");

                if (qp.IsPeerAutoAccepted(peerId1))
                    throw new Exception("peerId1 was unexpectedly auto-accepted initially.");

                qp.SetPeerAutoAccept(peerId1, true);
                if (!qp.IsPeerAutoAccepted(peerId1))
                    throw new Exception("peerId1 should be auto-accepted after SetPeerAutoAccept(true).");

                if (qp.IsPeerAutoAccepted(peerId2))
                    throw new Exception("peerId2 should NOT be auto-accepted.");

                qp.SetPeerAutoAccept(peerId1, false);
                if (qp.IsPeerAutoAccepted(peerId1))
                    throw new Exception("peerId1 should NOT be auto-accepted after SetPeerAutoAccept(false).");

                qp.SetPeerAutoAccept(certHash1, true);
                if (!qp.IsPeerAutoAccepted(certHash1))
                    throw new Exception("certHash1 should be auto-accepted after SetPeerAutoAccept(certHash1, true).");

                if (qp.IsPeerAutoAccepted(certHash2))
                    throw new Exception("certHash2 should NOT be auto-accepted.");

                qp.SetAutoAcceptAll(true);
                if (!qp.AutoAcceptConnections)
                    throw new Exception("AutoAcceptConnections should be true after SetAutoAcceptAll(true);");

                if (!qp.IsPeerAutoAccepted(peerId2))
                    throw new Exception("peerId2 should be auto-accepted when global AutoAcceptConnections is true.");

                if (!qp.IsPeerAutoAccepted(certHash2))
                    throw new Exception("certHash2 should be auto-accepted when global AutoAcceptConnections is true.");

                qp.SetPeerAutoAccept(certHash1, false);
                qp.SetAutoAcceptAll(false);
                qp.AutoAcceptConnections = true;
                qp.AutoAcceptUntrustedConnections = false;

                if (qp.IsTrustedPeer(certHash1))
                    throw new Exception("certHash1 should NOT be trusted initially.");

                if (qp.IsPeerAutoAccepted(certHash1))
                    throw new Exception("certHash1 should NOT be auto-accepted when untrusted even if AutoAcceptConnections is true.");

                qp.TrustPeer(certHash1);
                if (!qp.IsTrustedPeer(certHash1))
                    throw new Exception("certHash1 should be trusted after TrustPeer.");

                if (!qp.IsPeerAutoAccepted(certHash1))
                    throw new Exception("certHash1 should be auto-accepted when trusted and AutoAcceptConnections is true.");

                if (qp.IsPeerAutoAccepted(certHash2))
                    throw new Exception("certHash2 (untrusted) must NOT be auto-accepted when AutoAcceptUntrustedConnections is false.");

                qp.UntrustPeer(certHash1);
                if (qp.IsTrustedPeer(certHash1))
                    throw new Exception("certHash1 should NOT be trusted after UntrustPeer.");

                if (qp.IsPeerAutoAccepted(certHash1))
                    throw new Exception("certHash1 should NOT be auto-accepted after UntrustPeer.");

                Console.WriteLine("PASSED");
                await Task.CompletedTask;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestAsymmetricUnrelatedPortHolePunch()
        {
            Console.Write("[TEST] Asymmetric unrelated port candidate hole punch (A:61017 <-> B:42891)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_asym_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_asym_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);

                await qpA.StartAsync();
                await qpB.StartAsync();

                var epA = new IPEndPoint(IPAddress.Loopback, 40010);
                var epB = new IPEndPoint(IPAddress.Loopback, 40020);

                byte[] helloB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloB, epB, QuicPunch.QuicPunch.TransportType.Wan);

                byte[] helloA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                var peerAInB = qpB.AvailablePeers[qpA.CurrentPeer.Id];
                var peerBInA = qpA.AvailablePeers[qpB.CurrentPeer.Id];

                ushort portA = 61017;
                ushort portB = 42891;

                using var nudpA = new System.Net.Sockets.UdpClient();
                using var nudpB = new System.Net.Sockets.UdpClient();

                nudpA.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
                nudpB.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);

                nudpA.Client.Bind(new IPEndPoint(IPAddress.Loopback, portA));
                nudpB.Client.Bind(new IPEndPoint(IPAddress.Loopback, portB));

                var candidateA = new CandidateEndpoint(new IPEndPoint(IPAddress.Loopback, portA), CandidateType.ServerReflexive, 100);
                var candidateB = new CandidateEndpoint(new IPEndPoint(IPAddress.Loopback, portB), CandidateType.ServerReflexive, 100);

                var connectionGuid = Guid.NewGuid();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                var punchTaskA = QuicPunch.QuicPunchConnection.OpenPortCore(
                    qpA.CurrentPeer, nudpA, peerBInA, new[] { candidateB }, portB, connectionGuid, cts.Token);

                var punchTaskB = QuicPunch.QuicPunchConnection.OpenPortCore(
                    qpB.CurrentPeer, nudpB, peerAInB, new[] { candidateA }, portA, connectionGuid, cts.Token);

                await Task.WhenAll(punchTaskA, punchTaskB);

                var resA = await punchTaskA;
                var resB = await punchTaskB;

                if (!resA.Success || resA.remoteEndpoint == null)
                    throw new Exception("Hole punch on Node A failed.");

                if (!resB.Success || resB.remoteEndpoint == null)
                    throw new Exception("Hole punch on Node B failed.");

                if (resA.remoteEndpoint.Port != portB)
                    throw new Exception($"Node A nominated endpoint port mismatch: got {resA.remoteEndpoint.Port}, expected {portB}");

                if (resB.remoteEndpoint.Port != portA)
                    throw new Exception($"Node B nominated endpoint port mismatch: got {resB.remoteEndpoint.Port}, expected {portA}");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestLanDiscoveryWithDifferentControlPorts()
        {
            Console.Write("[TEST] LAN multicast/broadcast discovery across different control ports (41892 vs 52713)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_lan_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_lan_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 41892);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 52713);

                await qpA.StartAsync();
                await qpB.StartAsync();

                await qpA.SendLocalLanDiscoveryAsync();
                await qpB.SendLocalLanDiscoveryAsync();

                int tries = 0;
                while (tries < 40 && (!qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id) || !qpB.AvailablePeers.ContainsKey(qpA.CurrentPeer.Id)))
                {
                    tries++;
                    await Task.Delay(50);
                    if (tries % 5 == 0)
                    {
                        await qpA.SendLocalLanDiscoveryAsync();
                        await qpB.SendLocalLanDiscoveryAsync();
                    }
                }

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerBInA))
                    throw new Exception("Node A failed to discover Node B via LAN discovery.");

                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerAInB))
                    throw new Exception("Node B failed to discover Node A via LAN discovery.");

                if (peerBInA.ActiveEndPoint == null || peerBInA.ActiveEndPoint.Port != 52713)
                    throw new Exception($"Node A mapped Node B to port {peerBInA.ActiveEndPoint?.Port}, expected 52713");

                if (peerAInB.ActiveEndPoint == null || peerAInB.ActiveEndPoint.Port != 41892)
                    throw new Exception($"Node B mapped Node A to port {peerAInB.ActiveEndPoint?.Port}, expected 41892");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private sealed class DummyProtocolHandler : QuicPunch.QuicPunch.IProtocolHandler
        {
            public Guid ProtocolId { get; }
            public string ProtocolName => "DummyTestProtocol";
            public System.IO.Compression.ZstandardCompressionOptions? CompressionOptions => null;

            private readonly Action? _onHandle;
            private readonly Func<Task>? _onHandleAsync;

            public DummyProtocolHandler(Guid protocolId, Action onHandle)
            {
                ProtocolId = protocolId;
                _onHandle = onHandle;
            }

            public DummyProtocolHandler(Guid protocolId, Func<Task> onHandleAsync)
            {
                ProtocolId = protocolId;
                _onHandleAsync = onHandleAsync;
            }

            public async Task HandleAsync(QuicPunch.QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
            {
                _onHandle?.Invoke();
                if (_onHandleAsync != null)
                {
                    await _onHandleAsync().ConfigureAwait(false);
                }
            }

            public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
        }

        private static async Task TestIdempotentHandshakeRetransmissions()
        {
            Console.Write("[TEST] Handshake Request idempotency (50 identical retransmissions -> 1 socket, 1 session)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_idem_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_idem_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 42101, autoAcceptConnections: true);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 42102, autoAcceptConnections: true);

                await qpA.StartAsync();
                await qpB.StartAsync();

                byte[] helloA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                var epA = new IPEndPoint(IPAddress.Loopback, qpA.LocalBoundPort);
                var epB = new IPEndPoint(IPAddress.Loopback, qpB.LocalBoundPort);
                await qpB.ProcessIncomingPacketAsync(helloA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                var protocolGuid = Guid.NewGuid();
                int handlerCalledCount = 0;
                var dummyHandler = new DummyProtocolHandler(protocolGuid, () => Interlocked.Increment(ref handlerCalledCount));
                qpB.RegisterProtocol(dummyHandler);

                var connectionGuid = Guid.NewGuid();
                var candidateA = new List<CandidateEndpoint> { new CandidateEndpoint(epA, CandidateType.Host) };
                byte[] requestPayload = qpA.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, (ushort)epA.Port, protocolGuid, connectionGuid, candidateA, QuicPunch.QuicPunch.TransportType.Wan);

                var tasks = new List<Task>();
                for (int i = 0; i < 50; i++)
                {
                    tasks.Add(qpB.ProcessIncomingPacketAsync(requestPayload, epA, QuicPunch.QuicPunch.TransportType.Wan));
                }
                await Task.WhenAll(tasks);

                int waitTries = 0;
                while (waitTries < 40 && (!qpB.IncomingHandshakeSessions.TryGetValue(connectionGuid, out var s) || !s.ResponsePayloadTcs.Task.IsCompleted))
                {
                    waitTries++;
                    await Task.Delay(50);
                }

                if (!qpB.IncomingHandshakeSessions.TryGetValue(connectionGuid, out var finalSession))
                    throw new Exception("Session was not created for connectionGuid.");

                if (qpB.IncomingHandshakeSessions.Count != 1)
                    throw new Exception($"Expected exactly 1 incoming handshake session, found {qpB.IncomingHandshakeSessions.Count}");

                var cachedResponse = await finalSession.ResponsePayloadTcs.Task;
                if (cachedResponse == null || cachedResponse.Length == 0)
                    throw new Exception("Cached response payload was empty or not generated.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestClockSkewResilienceWithChallengeResponse()
        {
            Console.Write("[TEST] Clock skew resilience (15+ years difference, no NTP) via Challenge-Response... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_skew_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_skew_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                byte[] pwd = System.Text.Encoding.UTF8.GetBytes("ResilientPassword2026!");
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 43001, connectionPassword: pwd);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 43002, connectionPassword: pwd);

                await qpA.StartAsync();
                await qpB.StartAsync();

                var epA = new IPEndPoint(IPAddress.Loopback, 43001);
                var epB = new IPEndPoint(IPAddress.Loopback, 43002);

                byte[] challengeNonceFromA = qpA.CreatePendingChallenge(epB);
                byte[] interrogationA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true, challengeNonceFromA, QuicPunch.QuicPunch.TransportType.Wan);

                await qpB.ProcessIncomingPacketAsync(interrogationA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                byte[] helloFromB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Hello, true, challengeNonceFromA, QuicPunch.QuicPunch.TransportType.Wan);

                await qpA.ProcessIncomingPacketAsync(helloFromB, epB, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerBInA))
                    throw new Exception("Peer A failed to discover and authenticate Peer B under extreme clock skew!");

                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerAInB))
                    throw new Exception("Peer B failed to discover and authenticate Peer A under extreme clock skew!");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestStunAndTrackerAutoRecoveryAfterNetworkRestoration()
        {
            Console.Write("[TEST] STUN & Tracker auto-recovery and bootstrap resilience (offline start -> online auto-heal)... ");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_bootstrap_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string customStunCache = Path.Combine(tempDir, "test_stun_cache.epl");
            string originalCachePath = QuicPunch.StunGatherer.StunEndpointsCachePath;
            string customTrackerCache = Path.Combine(tempDir, "test_tracker_cache.epl");
            string originalTrackerCachePath = QuicPunch.TrackerScanner.TrackerEndpointsCachePath;

            try
            {
                QuicPunch.StunGatherer.StunEndpointsCachePath = customStunCache;
                QuicPunch.TrackerScanner.TrackerEndpointsCachePath = customTrackerCache;

                File.WriteAllText(customStunCache, "");
                var gathered = await QuicPunch.StunGatherer.GatherStunEndpoints(forceRefresh: false);
                if (gathered.IsEmpty)
                    throw new Exception("StunGatherer failed to fallback to seeds when cache file was empty.");

                if (new FileInfo(customStunCache).Length == 0)
                    throw new Exception("StunGatherer retained an empty cache file instead of writing valid seeds.");

                byte[] mockInfoHash = new byte[20];
                RandomNumberGenerator.Fill(mockInfoHash);

                using var trackerScanner = new QuicPunch.TrackerScanner(mockInfoHash, 55555);
                
                await trackerScanner.Start(customTrackers: Array.Empty<string>());

                int resolvedCount = await trackerScanner.LoadAndResolveTrackersAsync();
                if (resolvedCount <= 0)
                    throw new Exception("TrackerScanner failed to resolve built-in tracker seeds on recovery.");

                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 43123);
                await qp.StartAsync();

                bool refreshed = await qp.RefreshStunEndpointsAsync(force: true);
                if (!refreshed || qp.StunServerEndpoints.Count == 0)
                    throw new Exception("QuicPunch dynamic STUN endpoint refresh failed to populate endpoints.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                if (!File.Exists(originalCachePath) && File.Exists(customStunCache))
                {
                    try { File.Copy(customStunCache, originalCachePath); } catch { }
                }

                if (!File.Exists(originalTrackerCachePath) && File.Exists(customTrackerCache))
                {
                    try { File.Copy(customTrackerCache, originalTrackerCachePath); } catch { }
                }

                QuicPunch.StunGatherer.StunEndpointsCachePath = originalCachePath;
                QuicPunch.TrackerScanner.TrackerEndpointsCachePath = originalTrackerCachePath;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestTrackerScannerDynamicPublicPortAnnouncementAndRebind()
        {
            Console.Write("[TEST] TrackerScanner dynamic public NAT port announcement & RebindListenerPort... ");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_tracker_dyn_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                byte[] mockInfoHash = new byte[20];
                RandomNumberGenerator.Fill(mockInfoHash);

                using var scanner = new QuicPunch.TrackerScanner(mockInfoHash, 50000);
                if (scanner.AnnouncedPort != 50000)
                    throw new Exception($"Expected initial announced port 50000, got {scanner.AnnouncedPort}");

                var publicIps = new[] { IPAddress.Parse("198.51.100.25") };
                scanner.UpdateAnnouncement(publicIps, 61291);

                if (scanner.AnnouncedPort != 61291)
                    throw new Exception($"Expected dynamic announced port 61291 after STUN update, got {scanner.AnnouncedPort}");

                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, discoveryId: mockInfoHash, listeningPort: 43200);
                await qp.StartAsync();

                if (qp.TrackerScanner == null)
                    throw new Exception("QuicPunch TrackerScanner was null after StartAsync.");

                qp.TrackerScanner.UpdateAnnouncement(new[] { IPAddress.Loopback }, 55555);
                if (qp.TrackerScanner.AnnouncedPort != 55555)
                    throw new Exception($"Expected TrackerScanner to simulate public port 55555, got {qp.TrackerScanner.AnnouncedPort}");

                bool rebound = qp.RebindListenerPort(43201);
                if (!rebound)
                    throw new Exception("QuicPunch RebindListenerPort failed.");

                if (qp.LocalBoundPort != 43201)
                    throw new Exception($"Expected LocalBoundPort to be 43201 after rebind, got {qp.LocalBoundPort}");

                if (qp.TrackerScanner == null)
                    throw new Exception("QuicPunch TrackerScanner became null after rebind.");

                if (qp.TrackerScanner.AnnouncedPort == 55555)
                    throw new Exception("CRITICAL: TrackerScanner retained obsolete NAT port 55555 after RebindListenerPort!");

                if (qp.TrackerScanner.AnnouncedPort != 43201)
                    throw new Exception($"Expected TrackerScanner to announce newly rebound port 43201, got {qp.TrackerScanner.AnnouncedPort}");

                await Task.Delay(1200);

                if (qp.TrackerScanner.AnnouncedPort == 55555)
                    throw new Exception("CRITICAL: Post-rebind STUN revived stale NAT port 55555!");

                if (qp.TrackerScanner.AnnouncedPort != 43201)
                    throw new Exception($"Expected TrackerScanner AnnouncedPort to remain 43201 after STUN failure, got {qp.TrackerScanner.AnnouncedPort}");

                using var senderUdp = new System.Net.Sockets.UdpClient();
                senderUdp.Connect(IPAddress.Loopback, 43201);
                byte[] pingPacket = qp.BuildPingPacket(PreciseTime.GetCorrectTime().Ticks, false, QuicPunch.QuicPunch.TransportType.Wan);
                await senderUdp.SendAsync(pingPacket, pingPacket.Length);

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestSessionKeyUniquenessAndNoNonceReuseAcrossRestarts()
        {
            Console.Write("[TEST] AES-GCM session key uniqueness & zero nonce reuse across restarts & peer reconnections (P0 fix)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_session_uniq_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_session_uniq_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                var epA = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 45001);
                var epB = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 45002);

                byte[] s1_TxSalt_A;
                byte[] s1_RxSalt_A;
                byte[] s1_KeyId_A;
                byte[] s1_PacketA;
                byte[] s2_TxSalt_A;
                byte[] s2_RxSalt_A;
                byte[] s2_KeyId_A;
                byte[] s2_PacketA;
                byte[] s3_TxSalt_A;
                byte[] s3_RxSalt_A;
                byte[] s3_KeyId_A;
                byte[] s3_PacketA;
                int headerAadSize = QuicPunch.QuicPunch.MagicHeader.Length + sizeof(byte) + sizeof(ushort) + 16 + sizeof(ulong);

                using (var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0))
                using (var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0))
                {
                    await qpA.StartAsync();
                    await qpB.StartAsync();

                    byte[] helloB1 = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                    await qpA.ProcessIncomingPacketAsync(helloB1, epB, QuicPunch.QuicPunch.TransportType.Wan);

                    byte[] helloA1 = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                    await qpB.ProcessIncomingPacketAsync(helloA1, epA, QuicPunch.QuicPunch.TransportType.Wan);

                    if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB_s1) || !peerB_s1.IsSessionReady)
                        throw new Exception("Session 1: Peer B is not ready on Node A.");
                    if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_s1) || !peerA_s1.IsSessionReady)
                        throw new Exception("Session 1: Peer A is not ready on Node B.");

                    s1_TxSalt_A = (byte[])peerB_s1.TxSalt!.Clone();
                    s1_RxSalt_A = (byte[])peerB_s1.RxSalt!.Clone();
                    s1_KeyId_A = (byte[])peerB_s1.ActiveSessionKeyId!.Clone();

                    byte[] msgS1 = System.Text.Encoding.UTF8.GetBytes("Session 1 Message");
                    ulong seqS1 = peerB_s1.GetNextOutboundSequence();
                    s1_PacketA = new byte[headerAadSize + 16 + msgS1.Length];

                    int offset1 = 0;
                    QuicPunch.QuicPunch.MagicHeader.CopyTo(s1_PacketA.AsSpan(offset1));
                    offset1 += QuicPunch.QuicPunch.MagicHeader.Length;
                    s1_PacketA[offset1++] = (byte)QuicPunchStructures.MessageType.Data;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s1_PacketA.AsSpan(offset1, 2), 101);
                    offset1 += 2;
                    qpA.CurrentPeer.IdRaw.CopyTo(s1_PacketA.AsSpan(offset1, 16));
                    offset1 += 16;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(s1_PacketA.AsSpan(offset1, 8), seqS1);
                    offset1 += 8;

                    Span<byte> tag1 = s1_PacketA.AsSpan(offset1, 16);
                    offset1 += 16;
                    Span<byte> cipher1 = s1_PacketA.AsSpan(offset1);

                    Span<byte> nonce1 = stackalloc byte[12];
                    peerB_s1.TxSalt.CopyTo(nonce1.Slice(0, 4));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce1.Slice(4, 8), seqS1);

                    peerB_s1.TxCipher!.Encrypt(nonce1, msgS1, cipher1, tag1, s1_PacketA.AsSpan(0, headerAadSize));

                    var readerB1 = qpB.GetPacketReader(101);
                    await qpB.ProcessIncomingPacketAsync(s1_PacketA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                    using var cts1 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var (_, receivedS1) = await readerB1.ReadAsync(cts1.Token);
                    if (!System.Text.Encoding.UTF8.GetString(receivedS1).Equals("Session 1 Message"))
                        throw new Exception("Session 1 payload failed to decrypt on Node B.");

                    await qpA.StopAsync();
                    await qpB.StopAsync();
                    await qpA.StartAsync();
                    await qpB.StartAsync();

                    byte[] helloB2 = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                    await qpA.ProcessIncomingPacketAsync(helloB2, epB, QuicPunch.QuicPunch.TransportType.Wan);

                    byte[] helloA2 = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                    await qpB.ProcessIncomingPacketAsync(helloA2, epA, QuicPunch.QuicPunch.TransportType.Wan);

                    if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB_s2) || !peerB_s2.IsSessionReady)
                        throw new Exception("Session 2: Peer B is not ready on Node A after same-instance restart.");
                    if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_s2) || !peerA_s2.IsSessionReady)
                        throw new Exception("Session 2: Peer A is not ready on Node B after same-instance restart.");

                    s2_TxSalt_A = (byte[])peerB_s2.TxSalt!.Clone();
                    s2_RxSalt_A = (byte[])peerB_s2.RxSalt!.Clone();
                    s2_KeyId_A = (byte[])peerB_s2.ActiveSessionKeyId!.Clone();

                    if (CryptographicOperations.FixedTimeEquals(s1_TxSalt_A, s2_TxSalt_A))
                        throw new Exception("CRITICAL SECURITY VIOLATION: TxSalt identical across Stop->Start on same instance! (Nonce collision hazard)");
                    if (CryptographicOperations.FixedTimeEquals(s1_RxSalt_A, s2_RxSalt_A))
                        throw new Exception("CRITICAL SECURITY VIOLATION: RxSalt identical across Stop->Start on same instance!");
                    if (CryptographicOperations.FixedTimeEquals(s1_KeyId_A, s2_KeyId_A))
                        throw new Exception("CRITICAL SECURITY VIOLATION: Session key ID identical across Stop->Start on same instance!");

                    var readerB2 = qpB.GetPacketReader(101);
                    await qpB.ProcessIncomingPacketAsync(s1_PacketA, epA, QuicPunch.QuicPunch.TransportType.Wan);
                    if (readerB2.TryRead(out _))
                        throw new Exception("CRITICAL SECURITY VIOLATION: Packet from Session 1 was accepted in Session 2 after same-instance restart!");

                    byte[] msgS2 = System.Text.Encoding.UTF8.GetBytes("Session 2 Message");
                    ulong seqS2 = peerB_s2.GetNextOutboundSequence();
                    s2_PacketA = new byte[headerAadSize + 16 + msgS2.Length];

                    int offset2 = 0;
                    QuicPunch.QuicPunch.MagicHeader.CopyTo(s2_PacketA.AsSpan(offset2));
                    offset2 += QuicPunch.QuicPunch.MagicHeader.Length;
                    s2_PacketA[offset2++] = (byte)QuicPunchStructures.MessageType.Data;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s2_PacketA.AsSpan(offset2, 2), 101);
                    offset2 += 2;
                    qpA.CurrentPeer.IdRaw.CopyTo(s2_PacketA.AsSpan(offset2, 16));
                    offset2 += 16;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(s2_PacketA.AsSpan(offset2, 8), seqS2);
                    offset2 += 8;

                    Span<byte> tag2 = s2_PacketA.AsSpan(offset2, 16);
                    offset2 += 16;
                    Span<byte> cipher2 = s2_PacketA.AsSpan(offset2);

                    Span<byte> nonce2 = stackalloc byte[12];
                    peerB_s2.TxSalt.CopyTo(nonce2.Slice(0, 4));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce2.Slice(4, 8), seqS2);

                    peerB_s2.TxCipher!.Encrypt(nonce2, msgS2, cipher2, tag2, s2_PacketA.AsSpan(0, headerAadSize));

                    await qpB.ProcessIncomingPacketAsync(s2_PacketA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                    using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var (_, receivedS2) = await readerB2.ReadAsync(cts2.Token);
                    if (!System.Text.Encoding.UTF8.GetString(receivedS2).Equals("Session 2 Message"))
                        throw new Exception("Session 2 payload failed to decrypt on Node B.");

                    if (!qpA.RemovePeer(qpB.CurrentPeer.Id))
                        throw new Exception("Failed to RemovePeer B on Node A.");
                    if (!qpB.RemovePeer(qpA.CurrentPeer.Id))
                        throw new Exception("Failed to RemovePeer A on Node B.");

                    byte[] helloB3 = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                    await qpA.ProcessIncomingPacketAsync(helloB3, epB, QuicPunch.QuicPunch.TransportType.Wan);

                    byte[] helloA3 = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                    await qpB.ProcessIncomingPacketAsync(helloA3, epA, QuicPunch.QuicPunch.TransportType.Wan);

                    if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB_s3) || !peerB_s3.IsSessionReady)
                        throw new Exception("Session 3: Peer B is not ready on Node A after peer removal/reconnect.");
                    if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_s3) || !peerA_s3.IsSessionReady)
                        throw new Exception("Session 3: Peer A is not ready on Node B after peer removal/reconnect.");

                    s3_TxSalt_A = (byte[])peerB_s3.TxSalt!.Clone();
                    s3_RxSalt_A = (byte[])peerB_s3.RxSalt!.Clone();
                    s3_KeyId_A = (byte[])peerB_s3.ActiveSessionKeyId!.Clone();

                    if (CryptographicOperations.FixedTimeEquals(s1_TxSalt_A, s3_TxSalt_A) || CryptographicOperations.FixedTimeEquals(s2_TxSalt_A, s3_TxSalt_A))
                        throw new Exception("CRITICAL SECURITY VIOLATION: TxSalt reused after peer removal and re-discovery! (Nonce collision hazard)");
                    if (CryptographicOperations.FixedTimeEquals(s1_RxSalt_A, s3_RxSalt_A) || CryptographicOperations.FixedTimeEquals(s2_RxSalt_A, s3_RxSalt_A))
                        throw new Exception("CRITICAL SECURITY VIOLATION: RxSalt reused after peer removal and re-discovery!");
                    if (CryptographicOperations.FixedTimeEquals(s1_KeyId_A, s3_KeyId_A) || CryptographicOperations.FixedTimeEquals(s2_KeyId_A, s3_KeyId_A))
                        throw new Exception("CRITICAL SECURITY VIOLATION: Session key ID reused after peer removal and re-discovery!");

                    var readerB3 = qpB.GetPacketReader(101);
                    await qpB.ProcessIncomingPacketAsync(s1_PacketA, epA, QuicPunch.QuicPunch.TransportType.Wan);
                    if (readerB3.TryRead(out _))
                        throw new Exception("CRITICAL SECURITY VIOLATION: Packet from Session 1 was accepted in Session 3!");

                    await qpB.ProcessIncomingPacketAsync(s2_PacketA, epA, QuicPunch.QuicPunch.TransportType.Wan);
                    if (readerB3.TryRead(out _))
                        throw new Exception("CRITICAL SECURITY VIOLATION: Packet from Session 2 was accepted in Session 3!");

                    byte[] msgS3 = System.Text.Encoding.UTF8.GetBytes("Session 3 Message");
                    ulong seqS3 = peerB_s3.GetNextOutboundSequence();
                    s3_PacketA = new byte[headerAadSize + 16 + msgS3.Length];

                    int offset3 = 0;
                    QuicPunch.QuicPunch.MagicHeader.CopyTo(s3_PacketA.AsSpan(offset3));
                    offset3 += QuicPunch.QuicPunch.MagicHeader.Length;
                    s3_PacketA[offset3++] = (byte)QuicPunchStructures.MessageType.Data;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s3_PacketA.AsSpan(offset3, 2), 101);
                    offset3 += 2;
                    qpA.CurrentPeer.IdRaw.CopyTo(s3_PacketA.AsSpan(offset3, 16));
                    offset3 += 16;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(s3_PacketA.AsSpan(offset3, 8), seqS3);
                    offset3 += 8;

                    Span<byte> tag3 = s3_PacketA.AsSpan(offset3, 16);
                    offset3 += 16;
                    Span<byte> cipher3 = s3_PacketA.AsSpan(offset3);

                    Span<byte> nonce3 = stackalloc byte[12];
                    peerB_s3.TxSalt.CopyTo(nonce3.Slice(0, 4));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce3.Slice(4, 8), seqS3);

                    peerB_s3.TxCipher!.Encrypt(nonce3, msgS3, cipher3, tag3, s3_PacketA.AsSpan(0, headerAadSize));

                    await qpB.ProcessIncomingPacketAsync(s3_PacketA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                    using var cts3 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var (_, receivedS3) = await readerB3.ReadAsync(cts3.Token);
                    if (!System.Text.Encoding.UTF8.GetString(receivedS3).Equals("Session 3 Message"))
                        throw new Exception("Session 3 payload failed to decrypt on Node B.");
                }

                using (var qpA4 = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0))
                using (var qpB4 = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0))
                {
                    await qpA4.StartAsync();
                    await qpB4.StartAsync();

                    byte[] helloB4 = qpB4.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                    await qpA4.ProcessIncomingPacketAsync(helloB4, epB, QuicPunch.QuicPunch.TransportType.Wan);

                    byte[] helloA4 = qpA4.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                    await qpB4.ProcessIncomingPacketAsync(helloA4, epA, QuicPunch.QuicPunch.TransportType.Wan);

                    if (!qpA4.AvailablePeers.TryGetValue(qpB4.CurrentPeer.Id, out var peerB_s4) || !peerB_s4.IsSessionReady)
                        throw new Exception("Session 4: Peer B is not ready on Node A.");

                    byte[] s4_TxSalt_A = peerB_s4.TxSalt!;
                    byte[] s4_KeyId_A = peerB_s4.ActiveSessionKeyId!;

                    if (CryptographicOperations.FixedTimeEquals(s1_TxSalt_A, s4_TxSalt_A) ||
                        CryptographicOperations.FixedTimeEquals(s2_TxSalt_A, s4_TxSalt_A) ||
                        CryptographicOperations.FixedTimeEquals(s3_TxSalt_A, s4_TxSalt_A))
                        throw new Exception("CRITICAL SECURITY VIOLATION: TxSalt reused across separate instance restart!");

                    if (CryptographicOperations.FixedTimeEquals(s1_KeyId_A, s4_KeyId_A) ||
                        CryptographicOperations.FixedTimeEquals(s2_KeyId_A, s4_KeyId_A) ||
                        CryptographicOperations.FixedTimeEquals(s3_KeyId_A, s4_KeyId_A))
                        throw new Exception("CRITICAL SECURITY VIOLATION: Session key ID reused across separate instance restart!");
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestInvalidHandshakeCannotMutatePeerEndpointState()
        {
            Console.Write("[TEST] Invalid/spoofed Handshake signature rejection & zero state mutation (P1 fix)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_hs_spoof_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_hs_spoof_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);

                await qpA.StartAsync();
                await qpB.StartAsync();

                var epA = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 46001);
                var epB = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 46002);
                var epAttacker = new IPEndPoint(IPAddress.Parse("198.51.100.77"), 65530);

                byte[] helloB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloB, epB, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB_on_A) || peerB_on_A == null)
                    throw new Exception("Peer B was not discovered on Node A.");

                Guid testProtocol = Guid.NewGuid();
                Guid testConnGuid = Guid.NewGuid();
                byte[] validHandshake = qpB.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, 5555, testProtocol, testConnGuid, null, QuicPunch.QuicPunch.TransportType.Wan);
                await qpB.StopAsync();

                byte[] forgedHandshake = (byte[])validHandshake.Clone();
                forgedHandshake[forgedHandshake.Length - 1] ^= 0xFF;
                forgedHandshake[forgedHandshake.Length - 2] ^= 0xAA;

                var originalEndpoint = peerB_on_A.ActiveEndPoint;
                if (originalEndpoint == null)
                    throw new Exception("Peer B initial endpoint was null.");

                await qpA.ProcessIncomingPacketAsync(forgedHandshake, epAttacker, QuicPunch.QuicPunch.TransportType.Wan);

                if (peerB_on_A.ActiveEndPoint == null || peerB_on_A.ActiveEndPoint.Address.ToString() == epAttacker.Address.ToString() || peerB_on_A.ActiveEndPoint.Port == epAttacker.Port)
                {
                    throw new Exception("CRITICAL SECURITY VIOLATION: Attacker forged Handshake successfully mutated Peer B's ActiveEndPoint before signature validation!");
                }

                if (!peerB_on_A.ActiveEndPoint.Equals(originalEndpoint))
                {
                    throw new Exception($"Peer B's ActiveEndPoint was modified despite invalid handshake signature! Original: {originalEndpoint}, Current: {peerB_on_A.ActiveEndPoint}");
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestFullLifecycleStartStopRestartAndUnconditionalDispose()
        {
            Console.Write("[TEST] Explicit lifecycle state machine (Start -> Stop -> Restart -> Unconditional Dispose)... ");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_lifecycle_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var unstartedQp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0);
                if (unstartedQp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Created)
                    throw new Exception("Expected Created state for unstarted instance.");
                if (unstartedQp.IsStarted)
                    throw new Exception("Expected IsStarted to be false for unstarted instance.");

                unstartedQp.Dispose();
                if (!unstartedQp.IsDisposed || unstartedQp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Disposed)
                    throw new Exception("Expected Disposed state after calling Dispose on unstarted instance.");

                var unstartedAsyncQp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0);
                await unstartedAsyncQp.DisposeAsync();
                if (!unstartedAsyncQp.IsDisposed || unstartedAsyncQp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Disposed)
                    throw new Exception("Expected Disposed state after calling DisposeAsync on unstarted instance.");

                QuicPunch.QuicPunch? asyncQpRef = null;
                await using (var asyncQp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0))
                {
                    asyncQpRef = asyncQp;
                    await asyncQp.StartAsync();
                    if (!asyncQp.IsStarted || asyncQp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Started)
                        throw new Exception("Expected Started state before await using disposal.");
                    if (asyncQp.udp == null)
                        throw new Exception("Expected active UDP socket on started instance.");
                }

                if (!asyncQpRef.IsDisposed || asyncQpRef.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Disposed)
                    throw new Exception("Expected Disposed state after 'await using' DisposeAsync on running instance.");
                if (asyncQpRef.udp != null)
                    throw new Exception("Expected UDP socket to be cleaned up and set to null after DisposeAsync.");
                if (asyncQpRef.PeerStore != null)
                    throw new Exception("Expected PeerStore to be cleaned up and set to null after DisposeAsync.");
                if (!asyncQpRef.AvailablePeers.IsEmpty)
                    throw new Exception("Expected AvailablePeers to be cleared after DisposeAsync.");

                bool asyncCaughtDisposed = false;
                try
                {
                    await asyncQpRef.StartAsync();
                }
                catch (ObjectDisposedException)
                {
                    asyncCaughtDisposed = true;
                }
                if (!asyncCaughtDisposed)
                    throw new Exception("Expected ObjectDisposedException when calling StartAsync after DisposeAsync.");

                var stoppedAsyncQp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0);
                await stoppedAsyncQp.StartAsync();
                await stoppedAsyncQp.StopAsync();
                if (stoppedAsyncQp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Stopped)
                    throw new Exception("Expected Stopped state after StopAsync.");
                await stoppedAsyncQp.DisposeAsync();
                if (!stoppedAsyncQp.IsDisposed || stoppedAsyncQp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Disposed)
                    throw new Exception("Expected Disposed state after DisposeAsync on stopped instance.");

                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 47100);

                await qp.StartAsync();
                if (!qp.IsStarted || qp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Started)
                    throw new Exception("Expected Started state after StartAsync().");
                if (qp.CancellationSource == null || qp.CancellationSource.IsCancellationRequested)
                    throw new Exception("Expected active uncancelled CancellationSource after StartAsync().");

                await qp.StopAsync();
                if (qp.IsStarted || qp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Stopped)
                    throw new Exception("Expected Stopped state after StopAsync().");
                if (qp.CancellationSource != null && !qp.CancellationSource.IsCancellationRequested)
                    throw new Exception("Expected CancellationSource to be cancelled after StopAsync().");

                await qp.StartAsync();
                if (!qp.IsStarted || qp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Started)
                    throw new Exception("Expected Started state after restarting via StartAsync().");
                if (qp.CancellationSource == null || qp.CancellationSource.IsCancellationRequested)
                    throw new Exception("CRITICAL: Restarted instance still had a cancelled CancellationSource!");

                using var testClient = new System.Net.Sockets.UdpClient();
                testClient.Connect(IPAddress.Loopback, qp.LocalBoundPort);
                byte[] ping = qp.BuildPingPacket(PreciseTime.GetCorrectTime().Ticks, false, QuicPunch.QuicPunch.TransportType.Wan);
                await testClient.SendAsync(ping, ping.Length);

                await qp.StopAsync();
                qp.Dispose();

                if (!qp.IsDisposed || qp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Disposed)
                    throw new Exception("Expected Disposed state after Dispose().");

                bool caughtDisposed = false;
                try
                {
                    await qp.StartAsync();
                }
                catch (ObjectDisposedException)
                {
                    caughtDisposed = true;
                }
                if (!caughtDisposed)
                    throw new Exception("Expected ObjectDisposedException when calling StartAsync on disposed instance.");

                var builderInstance = await new QuicPunch.QuicPunchBuilder()
                    .WithPort(0)
                    .WithAutoAccept(true)
                    .BuildAndStartAsync();

                if (!builderInstance.IsStarted || builderInstance.CancellationSource.IsCancellationRequested)
                    throw new Exception("Builder instance failed to start cleanly or had a prematurely cancelled token.");

                await builderInstance.StopAsync();
                builderInstance.Dispose();

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestHelloHandlerNoCertificateLeaksOnInvalidOrRepeatedPackets()
        {
            Console.Write("[TEST] HelloHandler certificate ownership & zero handle leak on invalid/repeated packets (P1 fix)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_cert_owner_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_cert_owner_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);

                await qpA.StartAsync();
                await qpB.StartAsync();

                var epB = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 48002);

                byte[] validHello = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                byte[] corruptedSigHello = (byte[])validHello.Clone();
                corruptedSigHello[corruptedSigHello.Length - 1] ^= 0xFF;
                corruptedSigHello[corruptedSigHello.Length - 2] ^= 0xAA;

                await qpA.ProcessIncomingPacketAsync(corruptedSigHello, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id))
                    throw new Exception("Corrupted signature Hello was erroneously accepted into AvailablePeers!");

                byte[] corruptedHashHello = (byte[])validHello.Clone();
                int certHashOffset = QuicPunch.QuicPunch.MagicHeader.Length + 1;
                corruptedHashHello[certHashOffset] ^= 0xFF;

                await qpA.ProcessIncomingPacketAsync(corruptedHashHello, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id))
                    throw new Exception("Corrupted cert hash Hello was erroneously accepted into AvailablePeers!");

                await qpA.ProcessIncomingPacketAsync(validHello, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB) || peerB == null || peerB.Certificate == null)
                    throw new Exception("Valid Hello failed to add peer with certificate to AvailablePeers.");

                var originalCert = peerB.Certificate;

                for (int i = 0; i < 5; i++)
                {
                    byte[] repeatedHello = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Hello, true);
                    await qpA.ProcessIncomingPacketAsync(repeatedHello, epB, QuicPunch.QuicPunch.TransportType.Wan);

                    if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var currentPeerB) || currentPeerB == null)
                        throw new Exception("Known peer was removed or corrupted during repeated Hello processing.");

                    if (!ReferenceEquals(currentPeerB.Certificate, originalCert))
                        throw new Exception("Known peer certificate reference was erroneously replaced or reallocated!");
                }

                qpA.RemovePeer(qpB.CurrentPeer.Id);
                if (qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id))
                    throw new Exception("Peer was not removed from AvailablePeers.");

                bool certDisposed = false;
                try
                {
                    _ = originalCert.GetECDsaPublicKey();
                }
                catch (Exception ex) when (ex is ObjectDisposedException || ex is CryptographicException)
                {
                    certDisposed = true;
                }
                if (!certDisposed)
                    throw new Exception("Certificate was not disposed when peer was removed!");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestIpRateLimiterEvictionAndBoundedCardinality()
        {
            Console.Write("[TEST] IpRateLimiter memory eviction & bounded cardinality under unique IP flood (P1/P2 fix)... ");

            const int capacity = 1_000;
            const int maxPerSec = 50;
            var limiter = new QuicPunch.Helpers.IpRateLimiter(maxPerSecond: maxPerSec, maxCapacity: capacity);

            for (uint i = 1; i <= 25_000; i++)
            {
                bool allowed = limiter.IsAllowed(i);
                if (!allowed)
                    throw new Exception($"Expected initial packet from IP {i} to be allowed.");

                if (limiter.Count > capacity)
                    throw new Exception($"CRITICAL: IpRateLimiter exceeded max capacity! Count={limiter.Count}, MaxCapacity={capacity}");
            }

            uint testIp = 0xC0A80101;
            for (int i = 1; i <= maxPerSec; i++)
            {
                if (!limiter.IsAllowed(testIp))
                    throw new Exception($"Expected packet {i} from {testIp} to be allowed under {maxPerSec}/sec limit.");
            }

            if (limiter.IsAllowed(testIp))
                throw new Exception($"Packet {maxPerSec + 1} from {testIp} was erroneously allowed beyond rate limit!");

            await Task.Delay(1100);

            if (!limiter.IsAllowed(testIp))
                throw new Exception($"Packet from {testIp} should be allowed again after 1-second window expired.");

            Console.WriteLine("PASSED");
        }

        private static async Task SendDirectDataPacketAsync(
            QuicPunch.QuicPunch senderQp,
            QuicPunch.PeerInfo targetPeer,
            ushort packetType,
            byte[] payload,
            QuicPunch.QuicPunch receiverQp,
            IPEndPoint senderEndPoint)
        {
            int headerAadSize = QuicPunch.QuicPunch.MagicHeader.Length + sizeof(byte) + sizeof(ushort) + 16 + sizeof(ulong);
            ulong seq = targetPeer.GetNextOutboundSequence();
            byte[] packet = new byte[headerAadSize + 16 + payload.Length];

            int offset = 0;
            QuicPunch.QuicPunch.MagicHeader.CopyTo(packet.AsSpan(offset));
            offset += QuicPunch.QuicPunch.MagicHeader.Length;
            packet[offset++] = (byte)QuicPunchStructures.MessageType.Data;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(offset, 2), packetType);
            offset += 2;
            senderQp.CurrentPeer.IdRaw.CopyTo(packet.AsSpan(offset, 16));
            offset += 16;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(offset, 8), seq);
            offset += 8;

            Span<byte> tag = packet.AsSpan(offset, 16);
            offset += 16;
            Span<byte> cipher = packet.AsSpan(offset);

            Span<byte> nonce = stackalloc byte[12];
            targetPeer.TxSalt!.CopyTo(nonce.Slice(0, 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4, 8), seq);

            targetPeer.TxCipher!.Encrypt(nonce, payload, cipher, tag, packet.AsSpan(0, headerAadSize));

            await receiverQp.ProcessIncomingPacketAsync(packet, senderEndPoint, QuicPunch.QuicPunch.TransportType.Wan);
        }

        private static async Task TestMultiPeerIsolationAndNoDesynchronizationOnPeerRemoval()
        {
            Console.Write("[TEST] Multi-peer session isolation: removing peer B leaves A<->C session synchronized (multi-peer fix)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_mp_iso_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_mp_iso_b_" + Guid.NewGuid().ToString("N"));
            string tempDirC = Path.Combine(Path.GetTempPath(), "qp_test_mp_iso_c_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);
            Directory.CreateDirectory(tempDirC);

            try
            {
                var epA = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 49001);
                var epB = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 49002);
                var epC = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 49003);

                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);
                using var qpC = new QuicPunch.QuicPunch(appDataPath: tempDirC, listeningPort: 0);

                await qpA.StartAsync();
                await qpB.StartAsync();
                await qpC.StartAsync();

                byte[] helloB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloB, epB, QuicPunch.QuicPunch.TransportType.Wan);

                byte[] helloA_to_B = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloA_to_B, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB_on_A) || !peerB_on_A.IsSessionReady)
                    throw new Exception("Peer B is not ready on Node A.");
                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_on_B) || !peerA_on_B.IsSessionReady)
                    throw new Exception("Peer A is not ready on Node B.");

                byte[] helloC = qpC.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloC, epC, QuicPunch.QuicPunch.TransportType.Wan);

                byte[] helloA_to_C = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpC.ProcessIncomingPacketAsync(helloA_to_C, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpC.CurrentPeer.Id, out var peerC_on_A) || !peerC_on_A.IsSessionReady)
                    throw new Exception("Peer C is not ready on Node A.");
                if (!qpC.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_on_C) || !peerA_on_C.IsSessionReady)
                    throw new Exception("Peer A is not ready on Node C.");

                byte[] initialKeyId_AC_on_A = (byte[])peerC_on_A.ActiveSessionKeyId!.Clone();
                byte[] initialKeyId_AC_on_C = (byte[])peerA_on_C.ActiveSessionKeyId!.Clone();

                if (!initialKeyId_AC_on_A.SequenceEqual(initialKeyId_AC_on_C))
                    throw new Exception("Session key ID mismatch between Node A and Node C.");

                var readerC = qpC.GetPacketReader(200);
                byte[] msg1 = System.Text.Encoding.UTF8.GetBytes("Initial message A->C before B removed");
                await SendDirectDataPacketAsync(qpA, peerC_on_A, 200, msg1, qpC, epA);

                using (var cts1 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, received1) = await readerC.ReadAsync(cts1.Token);
                    if (!System.Text.Encoding.UTF8.GetString(received1).Equals("Initial message A->C before B removed"))
                        throw new Exception("Initial payload from A to C failed to decrypt.");
                }

                if (!qpA.RemovePeer(qpB.CurrentPeer.Id))
                    throw new Exception("Failed to remove peer B on Node A.");

                if (qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id))
                    throw new Exception("Peer B was not removed from Node A.");

                byte[] periodicHelloA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Hello, true, targetPeer: peerC_on_A);
                await qpC.ProcessIncomingPacketAsync(periodicHelloA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!peerA_on_C.ActiveSessionKeyId!.SequenceEqual(initialKeyId_AC_on_C))
                    throw new Exception("REGRESSION: Node C mutated its session key for Node A after A removed unrelated peer B!");

                if (!peerC_on_A.ActiveSessionKeyId!.SequenceEqual(initialKeyId_AC_on_A))
                    throw new Exception("REGRESSION: Node A mutated its session key for Node C after A removed unrelated peer B!");

                byte[] msg2 = System.Text.Encoding.UTF8.GetBytes("Payload A->C after B removed");
                await SendDirectDataPacketAsync(qpA, peerC_on_A, 200, msg2, qpC, epA);

                using (var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, received2) = await readerC.ReadAsync(cts2.Token);
                    if (!System.Text.Encoding.UTF8.GetString(received2).Equals("Payload A->C after B removed"))
                        throw new Exception("CRITICAL REGRESSION: Payload A->C failed to decrypt after B was removed!");
                }

                var readerA = qpA.GetPacketReader(201);
                byte[] msg3 = System.Text.Encoding.UTF8.GetBytes("Payload C->A after B removed");
                await SendDirectDataPacketAsync(qpC, peerA_on_C, 201, msg3, qpA, epC);

                using (var cts3 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, received3) = await readerA.ReadAsync(cts3.Token);
                    if (!System.Text.Encoding.UTF8.GetString(received3).Equals("Payload C->A after B removed"))
                        throw new Exception("CRITICAL REGRESSION: Reverse payload C->A failed to decrypt after B was removed!");
                }

                byte[] helloB_reconnect = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloB_reconnect, epB, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB_reconnected) || !peerB_reconnected.IsSessionReady)
                    throw new Exception("Peer B failed to reconnect on Node A.");

                byte[] msg4 = System.Text.Encoding.UTF8.GetBytes("Payload A->C after B reconnected");
                await SendDirectDataPacketAsync(qpA, peerC_on_A, 200, msg4, qpC, epA);

                using (var cts4 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, received4) = await readerC.ReadAsync(cts4.Token);
                    if (!System.Text.Encoding.UTF8.GetString(received4).Equals("Payload A->C after B reconnected"))
                        throw new Exception("Payload A->C failed after B reconnected.");
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
                try { Directory.Delete(tempDirC, true); } catch { }
            }
        }

        private static async Task TestUnilateralInterrogationDiscoversPeerBidirectionally()
        {
            Console.Write("[TEST] Unilateral interrogation: Node A interrogates Node B, both discover and authenticate (P0 fix)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_unilateral_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_unilateral_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);

                await qpA.StartAsync();
                await qpB.StartAsync();

                _ = qpA.PeerInterrogation(qpB.CurrentPeer);

                var timeout = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < timeout)
                {
                    if (qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var pB) && pB.IsSessionReady &&
                        qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var pA) && pA.IsSessionReady)
                    {
                        break;
                    }
                    await Task.Delay(50);
                }

                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerAInB) || !peerAInB.IsSessionReady)
                    throw new Exception("Node B failed to discover and authenticate Node A from inbound interrogation!");

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerBInA) || !peerBInA.IsSessionReady)
                    throw new Exception("CRITICAL P0 FAILURE: Node A failed to discover Node B from unilateral interrogation response!");

                var readerB = qpB.GetPacketReader(300);
                var readerA = qpA.GetPacketReader(301);

                byte[] msgAtoB = System.Text.Encoding.UTF8.GetBytes("Unilateral test message A -> B");
                await SendDirectDataPacketAsync(qpA, peerBInA, 300, msgAtoB, qpB, peerBInA.ActiveEndPoint!);

                using (var cts1 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, receivedAtoB) = await readerB.ReadAsync(cts1.Token);
                    if (!System.Text.Encoding.UTF8.GetString(receivedAtoB).Equals("Unilateral test message A -> B"))
                        throw new Exception("Payload from A to B failed to decrypt after unilateral interrogation!");
                }

                byte[] msgBtoA = System.Text.Encoding.UTF8.GetBytes("Unilateral test message B -> A");
                await SendDirectDataPacketAsync(qpB, peerAInB, 301, msgBtoA, qpA, peerAInB.ActiveEndPoint!);

                using (var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, receivedBtoA) = await readerA.ReadAsync(cts2.Token);
                    if (!System.Text.Encoding.UTF8.GetString(receivedBtoA).Equals("Unilateral test message B -> A"))
                        throw new Exception("Payload from B to A failed to decrypt after unilateral interrogation!");
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestUntrustedDiscoveredPeerCannotAutoAcceptProtocolHandshake()
        {
            Console.Write("[TEST] Untrusted discovered peer cannot auto-accept protocol handshake (P0 fix)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_untrusted_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_untrusted_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);

                var dummyProtocolId = Guid.NewGuid();
                var dummyHandlerB = new DummyProtocolHandler(dummyProtocolId, () => { });
                qpB.RegisterProtocol(dummyHandlerB);

                await qpA.StartAsync();
                await qpB.StartAsync();

                var epA = new IPEndPoint(IPAddress.Loopback, 49501);
                var epB = new IPEndPoint(IPAddress.Loopback, 49502);

                byte[] helloFromA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloFromA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_on_B))
                    throw new Exception("Peer A was not discovered in AvailablePeers on Node B.");

                if (qpB.IsTrustedPeer(peerA_on_B))
                    throw new Exception("Peer A was unexpectedly trusted on Node B without token or TrustPeer!");

                var guid1 = Guid.NewGuid();
                byte[] handshakeReq1 = qpA.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, 0, dummyProtocolId, guid1, null, QuicPunch.QuicPunch.TransportType.Wan);
                await qpB.ProcessIncomingPacketAsync(handshakeReq1, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpB.IncomingHandshakeSessions.TryGetValue(guid1, out var session1))
                    throw new Exception("Node B did not register incoming handshake session.");

                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
                {
                    try
                    {
                        var responsePayload1 = await session1.ResponsePayloadTcs.Task.WaitAsync(cts.Token);
                        byte respType = responsePayload1[QuicPunch.QuicPunch.MagicHeader.Length + 1 + 16];
                        if (respType == (byte)QuicPunchStructures.HandShakeType.Accept)
                            throw new Exception("CRITICAL P0 VULNERABILITY: Node B auto-accepted protocol handshake from untrusted stranger!");
                    }
                    catch (TimeoutException)
                    {
                    }
                }

                qpB.TrustPeer(qpA.CurrentPeer.CertHash);

                if (!qpB.IsTrustedPeer(peerA_on_B))
                    throw new Exception("Peer A should be trusted after TrustPeer.");

                var guid2 = Guid.NewGuid();
                byte[] handshakeReq2 = qpA.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, 0, dummyProtocolId, guid2, null, QuicPunch.QuicPunch.TransportType.Wan);
                await qpB.ProcessIncomingPacketAsync(handshakeReq2, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpB.IncomingHandshakeSessions.TryGetValue(guid2, out var session2))
                    throw new Exception("Node B did not register second handshake session.");

                using (var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var responsePayload2 = await session2.ResponsePayloadTcs.Task.WaitAsync(cts2.Token);
                    byte respType2 = responsePayload2[QuicPunch.QuicPunch.MagicHeader.Length + 1 + 16];
                    if (respType2 != (byte)QuicPunchStructures.HandShakeType.Accept)
                        throw new Exception($"Expected HandShakeType.Accept for trusted peer, got {respType2}");
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestIncomingHandshakeSessionsLifecycleAndHardCap()
        {
            Console.Write("[TEST] IncomingHandshakeSessions lifecycle (Pending/Completed/Rejected TTLs & hard cap)... ");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_hss_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0);

                var peerId = Guid.NewGuid();
                var protoId = Guid.NewGuid();

                qp.HandshakePendingTtl = TimeSpan.FromMilliseconds(150);
                var gPending = Guid.NewGuid();
                var sPending = qp.GetOrAddIncomingHandshakeSession(gPending, peerId, protoId);
                if (sPending == null || sPending.State != HandshakeSessionState.Pending)
                    throw new Exception("Session should start in Pending state.");

                if (qp.PruneExpiredIncomingHandshakeSessions() != 0)
                    throw new Exception("Pending session should not expire immediately.");

                await Task.Delay(200);

                if (qp.PruneExpiredIncomingHandshakeSessions() != 1 || qp.IncomingHandshakeSessions.ContainsKey(gPending))
                    throw new Exception("Pending session should be pruned after Pending TTL.");

                qp.HandshakeCompletedTtl = TimeSpan.FromMilliseconds(200);
                var gCompleted = Guid.NewGuid();
                var sCompleted = qp.GetOrAddIncomingHandshakeSession(gCompleted, peerId, protoId);
                sCompleted!.MarkCompleted();
                if (sCompleted.State != HandshakeSessionState.Completed)
                    throw new Exception("Session state should be Completed after MarkCompleted.");

                if (qp.PruneExpiredIncomingHandshakeSessions() != 0)
                    throw new Exception("Completed session should not expire immediately.");

                await Task.Delay(250);

                if (qp.PruneExpiredIncomingHandshakeSessions() != 1 || qp.IncomingHandshakeSessions.ContainsKey(gCompleted))
                    throw new Exception("Completed session should be pruned after Completed TTL.");

                qp.HandshakeRejectedTtl = TimeSpan.FromMilliseconds(150);
                var gRejected = Guid.NewGuid();
                var sRejected = qp.GetOrAddIncomingHandshakeSession(gRejected, peerId, protoId);
                sRejected!.MarkRejected();
                if (sRejected.State != HandshakeSessionState.Rejected)
                    throw new Exception("Session state should be Rejected after MarkRejected.");

                if (qp.PruneExpiredIncomingHandshakeSessions() != 0)
                    throw new Exception("Rejected session should not expire immediately.");

                await Task.Delay(200);

                if (qp.PruneExpiredIncomingHandshakeSessions() != 1 || qp.IncomingHandshakeSessions.ContainsKey(gRejected))
                    throw new Exception("Rejected session should be pruned after Rejected TTL.");

                qp.MaxIncomingHandshakeSessions = 5;
                var guids = new List<Guid>();
                for (int i = 0; i < 5; i++)
                {
                    var g = Guid.NewGuid();
                    guids.Add(g);
                    var s = qp.GetOrAddIncomingHandshakeSession(g, peerId, protoId);
                    if (i < 2) s?.MarkCompleted();
                }

                if (qp.IncomingHandshakeSessions.Count != 5)
                    throw new Exception($"Expected 5 sessions at hard cap, found {qp.IncomingHandshakeSessions.Count}");

                var g6 = Guid.NewGuid();
                var s6 = qp.GetOrAddIncomingHandshakeSession(g6, peerId, protoId);

                if (qp.IncomingHandshakeSessions.Count > 5)
                    throw new Exception($"IncomingHandshakeSessions exceeded hard cap: {qp.IncomingHandshakeSessions.Count} > 5");

                if (!qp.IncomingHandshakeSessions.ContainsKey(g6))
                    throw new Exception("New session should be present after eviction of older session.");

                for (int i = 0; i < 20; i++)
                {
                    qp.GetOrAddIncomingHandshakeSession(Guid.NewGuid(), peerId, protoId);
                    if (qp.IncomingHandshakeSessions.Count > 5)
                        throw new Exception($"Hard cap violated under load: count was {qp.IncomingHandshakeSessions.Count}");
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestTrackerScannerLifecycleAndIdempotency()
        {
            Console.Write("[TEST] TrackerScanner lifecycle, idempotency & clean StopAsync... ");

            byte[] mockInfoHash = new byte[20];
            Random.Shared.NextBytes(mockInfoHash);

            using (var scanner = new QuicPunch.TrackerScanner(mockInfoHash, 54321))
            {
                if (scanner.IsRunning)
                    throw new Exception("TrackerScanner should not be running before Start.");

                await scanner.StartAsync(new[] { "udp://127.0.0.1:1337/announce" });

                if (!scanner.IsRunning)
                    throw new Exception("TrackerScanner should be running after StartAsync.");

                await scanner.StartAsync(new[] { "udp://127.0.0.1:9999/announce" });

                if (!scanner.IsRunning)
                    throw new Exception("TrackerScanner should still be running after duplicate StartAsync.");

                await scanner.StopAsync();

                if (scanner.IsRunning)
                    throw new Exception("TrackerScanner should not be running after StopAsync.");

                await scanner.StopAsync();

                if (scanner.IsRunning)
                    throw new Exception("TrackerScanner should remain stopped after duplicate StopAsync.");
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_builder_trackers_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var customTrackers = new[] { "udp://127.0.0.1:1337/announce" };
                using var qp = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDir)
                    .UsePool("0123456789abcdef0123456789abcdef01234567")
                    .WithCustomTrackers(customTrackers)
                    .Build();

                if (qp.CustomTrackers == null || qp.CustomTrackers.Length != 1 || qp.CustomTrackers[0] != customTrackers[0])
                    throw new Exception("QuicPunchBuilder.Build did not set CustomTrackers on QuicPunch.");

                await qp.StartAsync();

                if (qp.TrackerScanner == null || !qp.TrackerScanner.IsRunning)
                    throw new Exception("TrackerScanner should be running after QuicPunch.StartAsync.");

                await qp.StopAsync();

                if (qp.TrackerScanner != null && qp.TrackerScanner.IsRunning)
                    throw new Exception("TrackerScanner should be stopped after QuicPunch.StopAsync.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestEndpointCacheBinaryProtocolSerializationAndLoading()
        {
            Console.Write("[TEST] EndpointCache raw static binary protocol ('QPEP') & legacy upgrade... ");

            string tempFile = Path.Combine(Path.GetTempPath(), "qp_test_raw_cache_" + Guid.NewGuid().ToString("N") + ".epl");
            try
            {
                var originalEndpoints = new List<IPEndPoint>
                {
                    new IPEndPoint(IPAddress.Parse("192.168.1.100"), 3478),
                    new IPEndPoint(IPAddress.Parse("10.0.0.1"), 19302),
                    new IPEndPoint(IPAddress.Parse("172.16.254.1"), 443),
                    new IPEndPoint(IPAddress.Parse("127.0.0.1"), 55555),
                    new IPEndPoint(IPAddress.Parse("2001:db8::1"), 3478),
                    new IPEndPoint(IPAddress.IPv6Loopback, 8080)
                };

                QuicPunch.EndpointCache.Save(tempFile, originalEndpoints);

                if (!File.Exists(tempFile))
                    throw new Exception("Cache file was not created by EndpointCache.Save.");

                byte[] bytes = await File.ReadAllBytesAsync(tempFile);
                if (bytes.Length < 18)
                    throw new Exception($"Cache file is too small for QPEP header: {bytes.Length} bytes.");

                if (bytes[0] != (byte)'Q' || bytes[1] != (byte)'P' || bytes[2] != (byte)'E' || bytes[3] != (byte)'P')
                    throw new Exception("Cache file header missing 'QPEP' binary magic signature.");

                if (!QuicPunch.EndpointCache.TryRead(tempFile, TimeSpan.FromHours(1), out var loaded, checkTtl: true))
                    throw new Exception("EndpointCache.TryRead failed to read raw binary cache.");

                if (loaded.Count != originalEndpoints.Count)
                    throw new Exception($"Endpoint count mismatch: expected {originalEndpoints.Count}, got {loaded.Count}");

                for (int i = 0; i < originalEndpoints.Count; i++)
                {
                    if (!originalEndpoints[i].Equals(loaded[i]))
                        throw new Exception($"Endpoint mismatch at index {i}: expected {originalEndpoints[i]}, got {loaded[i]}");
                }

                string legacyFile = Path.Combine(Path.GetTempPath(), "qp_test_legacy_cache_" + Guid.NewGuid().ToString("N") + ".epl");
                try
                {
                    await File.WriteAllLinesAsync(legacyFile, new[]
                    {
                        "1.2.3.4:1234",
                        "8.8.8.8:53"
                    });

                    if (!QuicPunch.EndpointCache.TryRead(legacyFile, TimeSpan.FromHours(1), out var legacyLoaded, checkTtl: true))
                        throw new Exception("Failed to load legacy plain text .epl file.");

                    if (legacyLoaded.Count != 2 || legacyLoaded[0].Port != 1234 || legacyLoaded[1].Port != 53)
                        throw new Exception("Legacy plain text endpoints were not parsed accurately.");

                    byte[] upgradedBytes = await File.ReadAllBytesAsync(legacyFile);
                    if (upgradedBytes.Length < 18 || upgradedBytes[0] != (byte)'Q' || upgradedBytes[1] != (byte)'P' ||
                        upgradedBytes[2] != (byte)'E' || upgradedBytes[3] != (byte)'P')
                    {
                        throw new Exception("Legacy file was not automatically upgraded to raw binary 'QPEP' format.");
                    }
                }
                finally
                {
                    try { File.Delete(legacyFile); } catch { }
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        public static async Task RunSingleFlightTestAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   SINGLE-FLIGHT PEER+PROTOCOL VERIFICATION TEST  ");
            Console.WriteLine("==================================================");
            await TestSingleFlightConcurrentInitQuicConnection();
            await TestActiveSessionReuse();
            Console.WriteLine("==================================================");
            Console.WriteLine("   SINGLE-FLIGHT TEST PASSED SUCCESSFULLY!        ");
            Console.WriteLine("==================================================");
        }

        private static async Task TestSingleFlightConcurrentInitQuicConnection()
        {
            Console.Write("[TEST] Single-flight concurrent InitQuicConnection (50 simultaneous calls -> 1 handshake, 1 QUIC session, no orphans)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_sf_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_sf_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0, autoAcceptConnections: true) { AutoAcceptUntrustedConnections = true };
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0, autoAcceptConnections: true) { AutoAcceptUntrustedConnections = true };

                await qpA.StartAsync();
                await qpB.StartAsync();

                var epA = new IPEndPoint(IPAddress.Loopback, qpA.LocalBoundPort);
                var epB = new IPEndPoint(IPAddress.Loopback, qpB.LocalBoundPort);

                byte[] helloFromA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloFromA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                byte[] helloFromB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloFromB, epB, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB_on_A))
                    throw new Exception("Peer B was not discovered by Peer A.");
                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_on_B))
                    throw new Exception("Peer A was not discovered by Peer B.");

                peerB_on_A.ActiveEndPoint = epB;
                peerA_on_B.ActiveEndPoint = epA;

                if (peerB_on_A.CertHash != null) qpA.TrustPeer(peerB_on_A.CertHash);
                if (peerA_on_B.CertHash != null) qpB.TrustPeer(peerA_on_B.CertHash);

                var protocolGuid = Guid.NewGuid();
                int handleCountA = 0;
                int handleCountB = 0;

                var handlerA = new DummyProtocolHandler(protocolGuid, () => Interlocked.Increment(ref handleCountA));
                var handlerB = new DummyProtocolHandler(protocolGuid, () => Interlocked.Increment(ref handleCountB));

                qpA.RegisterProtocol(handlerA);
                qpB.RegisterProtocol(handlerB);

                int concurrentCalls = 50;
                var tasks = new List<Task>();
                for (int i = 0; i < concurrentCalls; i++)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await qpA.InitQuicConnection(protocolGuid, peerB_on_A);
                    }));
                }

                await Task.WhenAll(tasks);

                int waitTries = 0;
                while (waitTries < 100 && handleCountB == 0)
                {
                    waitTries++;
                    await Task.Delay(100);
                }

                if (qpB.IncomingHandshakeSessions.Count != 1)
                    throw new Exception($"Expected exactly 1 incoming handshake session on Node B, found {qpB.IncomingHandshakeSessions.Count}");

                if (handleCountA != 1)
                    throw new Exception($"Expected handleCountA == 1, got {handleCountA}");
                if (handleCountB != 1)
                    throw new Exception($"Expected handleCountB == 1, got {handleCountB}");

                if (qpA.ActiveOutboundNegotiationsCount != 0)
                    throw new Exception($"Expected ActiveOutboundNegotiationsCount == 0, got {qpA.ActiveOutboundNegotiationsCount}");

                if (qpA.ActiveConnectionFlightsCount != 0)
                    throw new Exception($"Expected ActiveConnectionFlightsCount == 0, got {qpA.ActiveConnectionFlightsCount}");

                await qpA.InitQuicConnection(protocolGuid, peerB_on_A);

                waitTries = 0;
                while (waitTries < 100 && handleCountB < 2)
                {
                    waitTries++;
                    await Task.Delay(100);
                }

                if (qpB.IncomingHandshakeSessions.Count != 2)
                    throw new Exception($"Expected exactly 2 incoming handshake sessions on Node B after second connection, found {qpB.IncomingHandshakeSessions.Count}");

                if (handleCountA != 2)
                    throw new Exception($"Expected handleCountA == 2 after second connection, got {handleCountA}");
                if (handleCountB != 2)
                    throw new Exception($"Expected handleCountB == 2 after second connection, got {handleCountB}");

                if (qpA.ActiveOutboundNegotiationsCount != 0)
                    throw new Exception($"Expected ActiveOutboundNegotiationsCount == 0 after second connection, got {qpA.ActiveOutboundNegotiationsCount}");
                if (qpA.ActiveConnectionFlightsCount != 0)
                    throw new Exception($"Expected ActiveConnectionFlightsCount == 0 after second connection, got {qpA.ActiveConnectionFlightsCount}");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestActiveSessionReuse()
        {
            Console.Write("[TEST] Active protocol session reuse (concurrent InitQuicConnection reuses existing session without renegotiation)... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_reuse_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_reuse_b_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0, autoAcceptConnections: true) { AutoAcceptUntrustedConnections = true };
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0, autoAcceptConnections: true) { AutoAcceptUntrustedConnections = true };

                await qpA.StartAsync();
                await qpB.StartAsync();

                var epA = new IPEndPoint(IPAddress.Loopback, qpA.LocalBoundPort);
                var epB = new IPEndPoint(IPAddress.Loopback, qpB.LocalBoundPort);

                byte[] helloFromA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloFromA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                byte[] helloFromB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloFromB, epB, QuicPunch.QuicPunch.TransportType.Wan);

                var peerB_on_A = qpA.AvailablePeers[qpB.CurrentPeer.Id];
                var peerA_on_B = qpB.AvailablePeers[qpA.CurrentPeer.Id];

                peerB_on_A.ActiveEndPoint = epB;
                peerA_on_B.ActiveEndPoint = epA;

                if (peerB_on_A.CertHash != null) qpA.TrustPeer(peerB_on_A.CertHash);
                if (peerA_on_B.CertHash != null) qpB.TrustPeer(peerA_on_B.CertHash);

                var protocolGuid = Guid.NewGuid();
                var holdTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                int handleCountA = 0;
                int handleCountB = 0;

                var handlerA = new DummyProtocolHandler(protocolGuid, async () =>
                {
                    Interlocked.Increment(ref handleCountA);
                    await holdTcs.Task;
                });
                var handlerB = new DummyProtocolHandler(protocolGuid, async () =>
                {
                    Interlocked.Increment(ref handleCountB);
                    await holdTcs.Task;
                });

                qpA.RegisterProtocol(handlerA);
                qpB.RegisterProtocol(handlerB);

                var firstConnTask = Task.Run(async () => await qpA.InitQuicConnection(protocolGuid, peerB_on_A));

                int waitTries = 0;
                while (waitTries < 150 && (!qpA.HasActiveProtocolSession(peerB_on_A.Id, protocolGuid) || handleCountA == 0))
                {
                    waitTries++;
                    await Task.Delay(100);
                }

                if (!qpA.HasActiveProtocolSession(peerB_on_A.Id, protocolGuid))
                    throw new Exception("Session should be active on Node A.");

                await qpA.InitQuicConnection(protocolGuid, peerB_on_A);

                if (qpB.IncomingHandshakeSessions.Count != 1)
                    throw new Exception($"Expected 1 incoming handshake session on Node B, got {qpB.IncomingHandshakeSessions.Count}");

                if (handleCountA != 1)
                    throw new Exception($"Expected handleCountA == 1, got {handleCountA}");

                holdTcs.TrySetResult();
                await firstConnTask;

                waitTries = 0;
                while (waitTries < 100 && qpA.HasActiveProtocolSession(peerB_on_A.Id, protocolGuid))
                {
                    waitTries++;
                    await Task.Delay(100);
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }
    }
}
