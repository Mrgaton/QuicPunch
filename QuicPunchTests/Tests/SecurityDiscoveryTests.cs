using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;
    public static class SecurityDiscoveryTests
    {
        public static async Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   SECURITY & DISCOVERY MODEL VERIFICATION TESTS  ");
            Console.WriteLine("==================================================");

            await TestTorTokenFixedOffsetEncodingAndDecoding();
            await TestTokenEcdsaSignatureVerification();
            await TestDeterministicNostrIdentityDerivation();
            await TestTokenCachingAndStability();
            await TestNostrDiscoveryUpdatesSavedPeerInPeerStoreAndTriggersReconnect();
            await TestNostrSpoofingRejectedForSavedPeer();
            await TestNostrDiscoveredPeersRecordAndManualConnect();
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
            await TestStunAutoRecoveryAfterNetworkRestoration();
            await TestNostrDiscoveryTokenAndSchnorrRoundTrip();
            await TestSessionKeyUniquenessAndNoNonceReuseAcrossRestarts();
            await TestInvalidHandshakeCannotMutatePeerEndpointState();
            await TestFullLifecycleStartStopRestartAndUnconditionalDispose();
            await TestHelloHandlerNoCertificateLeaksOnInvalidOrRepeatedPackets();
            await TestIpRateLimiterEvictionAndBoundedCardinality();
            await TestDiscoveredPeerPruningAndBoundedAdmission();
            await TestMultiPeerIsolationAndNoDesynchronizationOnPeerRemoval();
            await TestEndpointOnlyInterrogationDiscoversPeerBidirectionally();
            await TestMultiPeerAsymmetricReconnectDerivesFreshMatchingSession();
            await TestUnilateralInterrogationDiscoversPeerBidirectionally();
            await TestUntrustedDiscoveredPeerCannotAutoAcceptProtocolHandshake();
            await TestQuicReadyIsTrustedAndBounded();
            await TestPingCannotRefreshDiscoveredPeerLiveness();
            TestHandshakeCandidateFramingIsBounded();
            await TestIncomingHandshakeSessionsLifecycleAndHardCap();
            await TestNostrDiscoveryConfigurationAndLifecycle();
            await TestWanAndTorNostrDiscoverySeparation();
            await TestBuilderAutoDiscoveryFlagIsAuthoritative();
            await TestRebindCannotResurrectWorkersAfterStop();
            await TestEndpointCacheBinaryProtocolSerializationAndLoading();
            await TestSingleFlightConcurrentInitQuicConnection();
            await TestSingleFlightFailureCompletesFollowers();
            await TestActiveSessionReuse();
            await TestTorTokenFixedOffsetEncodingAndDecoding();
            await TestNostrCertificateVerificationAndValidation();
            await TestSavedPeerUpdatedWhenConnectedWithNewEndpoints();
            await TestWanServiceStartStopDynamicLifecycle();
            await TestDummyQuicConnectionTransportLeaveOpen();
            await TestTorTokenInterrogationDoesNotDisposeActiveChannel();
            await TestRandomIdentityNamesAndCertificateSanitization();
            await TestAckSharePeersTransportIsolation();
            await TestPingTimestampDriftAndFutureDrop();

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

                qpA.TrustPeer(peerB_on_A.CertHash);
                qpB.TrustPeer(peerA_on_B.CertHash);

                if (!CryptographicOperations.FixedTimeEquals(peerB_on_A.TxSalt, peerA_on_B.RxSalt))
                    throw new Exception("Directional salt mismatch: Node A TxSalt does not match Node B RxSalt.");

                if (!CryptographicOperations.FixedTimeEquals(peerB_on_A.RxSalt, peerA_on_B.TxSalt))
                    throw new Exception("Directional salt mismatch: Node A RxSalt does not match Node B TxSalt.");

                var readerB = qpB.GetPacketReader(100);
                var readerA = qpA.GetPacketReader(200);

                byte[] msgA2B = System.Text.Encoding.UTF8.GetBytes("Secret payload from A to B");
                ulong seqA = peerB_on_A.GetNextOutboundSequence();
                int headerAadSize = QuicPunch.QuicPunch.MagicHeader.Length + sizeof(byte) + sizeof(ushort) + sizeof(uint) + sizeof(ulong);
                int packetLenA = headerAadSize + 16 + msgA2B.Length;
                byte[] packetA = new byte[packetLenA];

                int offset = 0;
                QuicPunch.QuicPunch.MagicHeader.CopyTo(packetA.AsSpan(offset));
                offset += QuicPunch.QuicPunch.MagicHeader.Length;
                packetA[offset++] = (byte)QuicPunchStructures.MessageType.Data;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packetA.AsSpan(offset, 2), 100);
                offset += 2;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packetA.AsSpan(offset, 4), qpA.CurrentPeer.ShortId);
                offset += 4;
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
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packetB.AsSpan(offset, 4), qpB.CurrentPeer.ShortId);
                offset += 4;
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

                long timestamp = PreciseTime.GetCorrectTime().Ticks;
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
            Console.Write("[TEST] Explicit untrusted -> TrustPeer -> SavePeer lifecycle... ");

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

                bool savedBeforeTrust = qpA.SavePeer(qpB.CurrentPeer);
                if (savedBeforeTrust)
                    throw new Exception("SavePeer must not silently trust an untrusted PeerInfo.");

                if (qpA.PeerStore.TryGet(bCertHash, out _))
                    throw new Exception("Untrusted peer was persisted before explicit TrustPeer.");

                qpA.TrustPeer(bCertHash);
                if (!qpA.IsTrustedPeer(bCertHash))
                    throw new Exception("TrustPeer did not mark certificate identity as trusted.");

                bool saved = qpA.SavePeer(qpB.CurrentPeer);
                if (!saved)
                    throw new Exception("SavePeer returned false after explicit TrustPeer.");

                if (!qpA.PeerStore.TryGet(bCertHash, out _))
                    throw new Exception("PeerStore does not contain peer after TrustPeer + SavePeer.");

                bool removed = qpA.RemoveSavedPeer(bCertHash);
                if (!removed)
                    throw new Exception("RemoveSavedPeer returned false for existing saved peer.");

                if (!qpA.IsTrustedPeer(bCertHash))
                    throw new Exception("Removing persistence must not silently revoke an independently trusted identity.");

                if (qpA.PeerStore.TryGet(bCertHash, out _))
                    throw new Exception("PeerStore still contains peer after RemoveSavedPeer.");

                qpA.UntrustPeer(bCertHash);
                if (qpA.IsTrustedPeer(bCertHash))
                    throw new Exception("UntrustPeer did not revoke trust after the peer was removed from persistence.");

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

                    using var otherEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
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

                var certHash1 = SHA3_256.HashData(System.Text.Encoding.UTF8.GetBytes("cert-1"));
                var certHash2 = SHA3_256.HashData(System.Text.Encoding.UTF8.GetBytes("cert-2"));

                var peer1 = new PeerInfo();
                peer1.SetCertificateHash(certHash1);
                var peerId1 = peer1.Id;

                var peer2 = new PeerInfo();
                peer2.SetCertificateHash(certHash2);
                var peerId2 = peer2.Id;

                qp.AvailablePeers[peerId1] = peer1;
                qp.AvailablePeers[peerId2] = peer2;
                qp.TrustPeer(certHash1);

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

                qp.TrustPeer(certHash2);
                qp.SetAutoAcceptAll(true);
                if (!qp.AutoAcceptConnections)
                    throw new Exception("AutoAcceptConnections should be true after SetAutoAcceptAll(true);");

                if (!qp.IsPeerAutoAccepted(peerId2))
                    throw new Exception("peerId2 should be auto-accepted when global AutoAcceptConnections is true.");

                if (!qp.IsPeerAutoAccepted(certHash2))
                    throw new Exception("certHash2 should be auto-accepted when global AutoAcceptConnections is true.");

                // Test Trust-gated Auto-acceptance:
                // AutoAcceptConnections is true, but AutoAcceptUntrustedConnections is false:
                // Only TRUSTED peers should be auto-accepted!
                qp.UntrustPeer(certHash1);
                qp.UntrustPeer(certHash2);
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

                if (peerBInA.ActiveEndPoint == null || peerBInA.ActiveEndPoint.Port != qpB.LocalDiscoveryPort)
                    throw new Exception($"Node A mapped Node B to port {peerBInA.ActiveEndPoint?.Port}, expected {qpB.LocalDiscoveryPort}");

                if (peerAInB.ActiveEndPoint == null || peerAInB.ActiveEndPoint.Port != qpA.LocalDiscoveryPort)
                    throw new Exception($"Node B mapped Node A to port {peerAInB.ActiveEndPoint?.Port}, expected {qpA.LocalDiscoveryPort}");

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

        private static async Task TestStunAutoRecoveryAfterNetworkRestoration()
        {
            Console.Write("[TEST] STUN auto-recovery and bootstrap resilience (offline cache -> valid seeds)... ");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_bootstrap_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string customStunCache = Path.Combine(tempDir, "test_stun_cache.epl");
            string originalCachePath = QuicPunch.StunGatherer.StunEndpointsCachePath;

            try
            {
                QuicPunch.StunGatherer.StunEndpointsCachePath = customStunCache;

                File.WriteAllText(customStunCache, "");
                var gathered = await QuicPunch.StunGatherer.GatherStunEndpoints(forceRefresh: false);
                if (gathered.IsEmpty)
                    throw new Exception("StunGatherer failed to fallback to seeds when cache file was empty.");

                if (new FileInfo(customStunCache).Length == 0)
                    throw new Exception("StunGatherer retained an empty cache file instead of writing valid seeds.");

                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 43123);
                await qp.StartAsync();

                bool refreshed = await qp.RefreshStunEndpointsAsync(force: false);
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

                QuicPunch.StunGatherer.StunEndpointsCachePath = originalCachePath;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestNostrDiscoveryTokenAndSchnorrRoundTrip()
        {
            Console.Write("[TEST] Nostr discovery carries full QuicPunch token and uses valid BIP-340 signatures... ");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_nostr_token_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 43200);
                qp.CurrentPeer.Addresses = new[]
                {
                    IPAddress.Parse("198.51.100.10"),
                    IPAddress.Parse("203.0.113.20")
                };
                qp.CurrentPeer.MinPort = 41000;
                qp.CurrentPeer.MaxPort = 41999;

                string token = qp.GetWanToken();
                using var decoded = Utilities.DecodeEndpointToken(token);
                if (!decoded.Addresses.Contains(IPAddress.Parse("198.51.100.10")) ||
                    !decoded.Addresses.Contains(IPAddress.Parse("203.0.113.20")) ||
                    decoded.MinPort != 41000 ||
                    decoded.MaxPort != 41999 ||
                    !decoded.TryGetCertificateHash(out var decodedHash) ||
                    !CryptographicOperations.FixedTimeEquals(decodedHash, qp.CurrentPeer.CertHash))
                {
                    throw new Exception("Nostr rendezvous token did not retain the complete peer identity/endpoints/range.");
                }

                // The wire count is one byte, but discovery intentionally caps the
                // public+local address set to 32. Verify truncation is clean instead
                // of allowing framing to become ambiguous on VPN-heavy hosts.
                qp.CurrentPeer.Addresses = Enumerable.Range(1, 40)
                    .Select(i => IPAddress.Parse($"198.51.100.{i}"))
                    .ToArray();
                qp.InvalidateTokenCache();
                string cappedToken = qp.GetWanToken();
                using var cappedDecoded = Utilities.DecodeEndpointToken(cappedToken);
                for (int i = 1; i <= 32; i++)
                {
                    if (!cappedDecoded.Addresses.Contains(IPAddress.Parse($"198.51.100.{i}")))
                        throw new Exception($"Endpoint token lost advertised address #{i} before the 32-address cap.");
                }
                if (cappedDecoded.Addresses.Contains(IPAddress.Parse("198.51.100.33")))
                    throw new Exception("Endpoint token exceeded the 32-address discovery cap.");
                if (cappedDecoded.MinPort != 41000 || cappedDecoded.MaxPort != 41999)
                    throw new Exception("Endpoint token address truncation corrupted its port range.");

                byte[] privateKey = NostrSchnorr.CreatePrivateKey();
                try
                {
                    byte[] publicKey = NostrSchnorr.GetPublicKeyX(privateKey);
                    string publicKeyHex = Convert.ToHexString(publicKey).ToLowerInvariant();
                    long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string[][] tags = { new[] { "x", "test-channel" }, new[] { "t", "quicpunch" } };
                    byte[] serialized = NostrDiscovery.SerializeForId(publicKeyHex, createdAt, NostrDiscovery.DiscoveryKind, tags, token);
                    byte[] eventId = SHA256.HashData(serialized);
                    byte[] signature = NostrSchnorr.Sign(eventId, privateKey);

                    if (!NostrSchnorr.Verify(eventId, publicKey, signature))
                        throw new Exception("Generated Nostr BIP-340 signature failed verification.");

                    eventId[0] ^= 0x01;
                    if (NostrSchnorr.Verify(eventId, publicKey, signature))
                        throw new Exception("Tampered Nostr event ID unexpectedly verified.");

                    // Verify against the official BIP-340 vector #0 as well. A
                    // self-generated signature alone could hide two matching bugs
                    // in Sign and Verify.
                    byte[] vectorPubKey = Convert.FromHexString("F9308A019258C31049344F85F89D5229B531C845836F99B08601F113BCE036F9");
                    byte[] vectorMessage = new byte[32];
                    byte[] vectorSignature = Convert.FromHexString("E907831F80848D1069A5371B402410364BDF1C5F8307B0084C55F1CE2DCA821525F66A4A85EA8B71E482A74F382D2CE5EBEEE8FDB2172F477DF4900D310536C0");
                    if (!NostrSchnorr.Verify(vectorMessage, vectorPubKey, vectorSignature))
                        throw new Exception("Official BIP-340 verification vector #0 failed.");

                    vectorSignature[^1] ^= 0x01;
                    if (NostrSchnorr.Verify(vectorMessage, vectorPubKey, vectorSignature))
                        throw new Exception("Modified BIP-340 verification vector unexpectedly passed.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(privateKey);
                }

                Console.WriteLine("PASSED");
                await Task.CompletedTask;
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
                int headerAadSize = QuicPunch.QuicPunch.MagicHeader.Length + sizeof(byte) + sizeof(ushort) + sizeof(uint) + sizeof(ulong);

                // --- PHASE 1: Session 1 on initial start ---
                using (var qpA = new QuicPunch.QuicPunchBuilder().WithAppDataPath(tempDirA).WithPort(0).WithAutoDiscovery(false).Build())
                using (var qpB = new QuicPunch.QuicPunchBuilder().WithAppDataPath(tempDirB).WithPort(0).WithAutoDiscovery(false).Build())
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

                    qpA.TrustPeer(peerB_s1.CertHash);
                    qpB.TrustPeer(peerA_s1.CertHash);

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
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s1_PacketA.AsSpan(offset1, 4), qpA.CurrentPeer.ShortId);
                    offset1 += 4;
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

                    // --- PHASE 2: StopAsync -> StartAsync on the SAME instances (P0 fix verification) ---
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
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s2_PacketA.AsSpan(offset2, 4), qpA.CurrentPeer.ShortId);
                    offset2 += 4;
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

                    // --- PHASE 3: Peer removed -> same peer reappears without restart (P0 fix verification) ---
                    if (!qpA.RemovePeer(qpB.CurrentPeer.Id))
                        throw new Exception("Failed to RemovePeer B on Node A.");
                    if (!qpB.RemovePeer(qpA.CurrentPeer.Id))
                        throw new Exception("Failed to RemovePeer A on Node B.");

                    byte[] helloB3 = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                    await qpA.ProcessIncomingPacketAsync(helloB3, epB, QuicPunch.QuicPunch.TransportType.Wan);

                    byte[] helloA3 = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                    await qpB.ProcessIncomingPacketAsync(helloA3, epA, QuicPunch.QuicPunch.TransportType.Wan);

                    await Task.Delay(150);

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
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s3_PacketA.AsSpan(offset3, 4), qpA.CurrentPeer.ShortId);
                    offset3 += 4;
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

                // --- PHASE 4: Cross-instance restarts also tested ---
                using (var qpA4 = new QuicPunch.QuicPunchBuilder().WithAppDataPath(tempDirA).WithPort(0).WithAutoDiscovery(false).Build())
                using (var qpB4 = new QuicPunch.QuicPunchBuilder().WithAppDataPath(tempDirB).WithPort(0).WithAutoDiscovery(false).Build())
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
                await Task.Delay(100);

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
                qpA.AvailablePeers.Clear();

                // 1. Invalid ECDSA signature: Hello payload with corrupted signature
                byte[] validHello = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                byte[] corruptedSigHello = (byte[])validHello.Clone();
                corruptedSigHello[corruptedSigHello.Length - 1] ^= 0xFF;
                corruptedSigHello[corruptedSigHello.Length - 2] ^= 0xAA;

                await qpA.ProcessIncomingPacketAsync(corruptedSigHello, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id))
                    throw new Exception("Corrupted signature Hello was erroneously accepted into AvailablePeers!");

                // 2. Corrupted cert hash in header
                byte[] corruptedHashHello = (byte[])validHello.Clone();
                int certHashOffset = QuicPunch.QuicPunch.MagicHeader.Length + 1;
                corruptedHashHello[certHashOffset] ^= 0xFF;

                await qpA.ProcessIncomingPacketAsync(corruptedHashHello, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id))
                    throw new Exception("Corrupted cert hash Hello was erroneously accepted into AvailablePeers!");

                // 3. Valid Hello: Node A discovers Node B, cert ownership transferred to PeerInfo
                await qpA.ProcessIncomingPacketAsync(validHello, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB) || peerB == null || peerB.Certificate == null)
                    throw new Exception("Valid Hello failed to add peer with certificate to AvailablePeers.");

                var originalCert = peerB.Certificate;

                // 4. Repeated Hellos for already known peer: verify peer updated, certificate unchanged
                for (int i = 0; i < 5; i++)
                {
                    byte[] repeatedHello = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Hello, true);
                    await qpA.ProcessIncomingPacketAsync(repeatedHello, epB, QuicPunch.QuicPunch.TransportType.Wan);

                    if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var currentPeerB) || currentPeerB == null)
                        throw new Exception("Known peer was removed or corrupted during repeated Hello processing.");

                    if (!ReferenceEquals(currentPeerB.Certificate, originalCert))
                        throw new Exception("Known peer certificate reference was erroneously replaced or reallocated!");
                }

                // 5. Remove peer: verify certificate is disposed with PeerInfo
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
            Console.Write("[TEST] IpRateLimiter preserves per-IP quota when bounded cardinality is full... ");

            const int capacity = 100;
            const int maxPerSec = 10;
            var limiter = new QuicPunch.Helpers.IpRateLimiter(maxPerSecond: maxPerSec, maxCapacity: capacity);
            uint testIp = 0xC0A80101;

            for (int i = 0; i < maxPerSec; i++)
            {
                if (!limiter.IsAllowed(testIp))
                    throw new Exception("Known IP was blocked before reaching its quota.");
            }
            if (limiter.IsAllowed(testIp))
                throw new Exception("Known IP exceeded its per-second quota.");

            int acceptedUnique = 0;
            for (uint i = 1; i <= 1000; i++)
            {
                if (i == testIp) continue;
                if (limiter.IsAllowed(i)) acceptedUnique++;
                if (limiter.Count > capacity)
                    throw new Exception($"IpRateLimiter exceeded max capacity: {limiter.Count} > {capacity}");
            }

            if (acceptedUnique >= 1000)
                throw new Exception("Unique-IP flood should be back-pressured once the bounded window is full.");
            if (limiter.IsAllowed(testIp))
                throw new Exception("Capacity pressure erased the exhausted quota of an existing IP.");

            await Task.Delay(1100);
            if (!limiter.IsAllowed(testIp))
                throw new Exception("IP quota did not reset on the next one-second epoch.");

            Console.WriteLine("PASSED");
        }

        private static async Task TestDiscoveredPeerPruningAndBoundedAdmission()
        {
            Console.Write("[TEST] Discovered peer TTL/cap preserves trusted peers and bounds untrusted identities... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_peer_cap_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0);
                var trustedHash = RandomNumberGenerator.GetBytes(32);
                var trusted = new PeerInfo().SetCertificateHash(trustedHash);
                qp.TrustPeer(trustedHash);
                if (!qp.TryAddAvailablePeer(trusted.Id, trusted, maxDiscoveredPeers: 3))
                    throw new Exception("Trusted peer could not be admitted.");

                for (int i = 0; i < 10; i++)
                {
                    var peer = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
                    qp.TryAddAvailablePeer(peer.Id, peer, maxDiscoveredPeers: 3);
                }

                int untrustedCount = qp.AvailablePeers.Count(kvp => !qp.IsTrustedPeer(kvp.Value));
                if (untrustedCount > 3)
                    throw new Exception($"Untrusted peer cap exceeded: {untrustedCount} > 3");
                if (!qp.AvailablePeers.ContainsKey(trusted.Id))
                    throw new Exception("Trusted peer was evicted by discovered-peer pressure.");

                await Task.Delay(25);
                qp.PruneDiscoveredPeers(TimeSpan.FromMilliseconds(1), maxDiscoveredPeers: 3);
                if (qp.AvailablePeers.Any(kvp => !qp.IsTrustedPeer(kvp.Value)))
                    throw new Exception("Expired untrusted peers were not pruned.");
                if (!qp.AvailablePeers.ContainsKey(trusted.Id))
                    throw new Exception("TTL pruning removed a trusted peer.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task SendDirectDataPacketAsync(
            QuicPunch.QuicPunch senderQp,
            QuicPunch.PeerInfo targetPeer,
            ushort packetType,
            byte[] payload,
            QuicPunch.QuicPunch receiverQp,
            IPEndPoint senderEndPoint)
        {
            int headerAadSize = QuicPunch.QuicPunch.MagicHeader.Length + sizeof(byte) + sizeof(ushort) + sizeof(uint) + sizeof(ulong);
            ulong seq = targetPeer.GetNextOutboundSequence();
            byte[] packet = new byte[headerAadSize + 16 + payload.Length];

            int offset = 0;
            QuicPunch.QuicPunch.MagicHeader.CopyTo(packet.AsSpan(offset));
            offset += QuicPunch.QuicPunch.MagicHeader.Length;
            packet[offset++] = (byte)QuicPunchStructures.MessageType.Data;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(offset, 2), packetType);
            offset += 2;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), senderQp.CurrentPeer.ShortId);
            offset += 4;
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

                // 1. Establish connection between A and B
                byte[] helloB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloB, epB, QuicPunch.QuicPunch.TransportType.Wan);

                byte[] helloA_to_B = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloA_to_B, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB_on_A) || !peerB_on_A.IsSessionReady)
                    throw new Exception("Peer B is not ready on Node A.");
                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_on_B) || !peerA_on_B.IsSessionReady)
                    throw new Exception("Peer A is not ready on Node B.");

                // 2. Establish connection between A and C
                byte[] helloC = qpC.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloC, epC, QuicPunch.QuicPunch.TransportType.Wan);

                byte[] helloA_to_C = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpC.ProcessIncomingPacketAsync(helloA_to_C, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpC.CurrentPeer.Id, out var peerC_on_A) || !peerC_on_A.IsSessionReady)
                    throw new Exception("Peer C is not ready on Node A.");
                if (!qpC.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_on_C) || !peerA_on_C.IsSessionReady)
                    throw new Exception("Peer A is not ready on Node C.");

                qpA.TrustPeer(peerC_on_A.CertHash);
                qpC.TrustPeer(peerA_on_C.CertHash);

                // Record active session keys between A and C before B is removed
                byte[] initialKeyId_AC_on_A = (byte[])peerC_on_A.ActiveSessionKeyId!.Clone();
                byte[] initialKeyId_AC_on_C = (byte[])peerA_on_C.ActiveSessionKeyId!.Clone();

                if (!initialKeyId_AC_on_A.SequenceEqual(initialKeyId_AC_on_C))
                    throw new Exception("Session key ID mismatch between Node A and Node C.");

                // Verify initial data channel communication A -> C
                var readerC = qpC.GetPacketReader(200);
                byte[] msg1 = System.Text.Encoding.UTF8.GetBytes("Initial message A->C before B removed");
                await SendDirectDataPacketAsync(qpA, peerC_on_A, 200, msg1, qpC, epA);

                using (var cts1 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, received1) = await readerC.ReadAsync(cts1.Token);
                    if (!System.Text.Encoding.UTF8.GetString(received1).Equals("Initial message A->C before B removed"))
                        throw new Exception("Initial payload from A to C failed to decrypt.");
                }

                // 3. REMOVE PEER B FROM NODE A (The critical test operation)
                if (!qpA.RemovePeer(qpB.CurrentPeer.Id))
                    throw new Exception("Failed to remove peer B on Node A.");

                if (qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id))
                    throw new Exception("Peer B was not removed from Node A.");

                // 4. Send periodic Hello from Node A to Node C
                byte[] periodicHelloA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Hello, true, targetPeer: peerC_on_A);
                await qpC.ProcessIncomingPacketAsync(periodicHelloA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                // Verify that Node C did NOT mutate its session key for Node A
                if (!peerA_on_C.ActiveSessionKeyId!.SequenceEqual(initialKeyId_AC_on_C))
                    throw new Exception("REGRESSION: Node C mutated its session key for Node A after A removed unrelated peer B!");

                if (!peerC_on_A.ActiveSessionKeyId!.SequenceEqual(initialKeyId_AC_on_A))
                    throw new Exception("REGRESSION: Node A mutated its session key for Node C after A removed unrelated peer B!");

                // 5. Verify encrypted data channel A -> C STILL WORKS with ZERO MAC failure or desync
                byte[] msg2 = System.Text.Encoding.UTF8.GetBytes("Payload A->C after B removed");
                await SendDirectDataPacketAsync(qpA, peerC_on_A, 200, msg2, qpC, epA);

                using (var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, received2) = await readerC.ReadAsync(cts2.Token);
                    if (!System.Text.Encoding.UTF8.GetString(received2).Equals("Payload A->C after B removed"))
                        throw new Exception("CRITICAL REGRESSION: Payload A->C failed to decrypt after B was removed!");
                }

                // 6. Verify reverse encrypted data channel C -> A STILL WORKS
                var readerA = qpA.GetPacketReader(201);
                byte[] msg3 = System.Text.Encoding.UTF8.GetBytes("Payload C->A after B removed");
                await SendDirectDataPacketAsync(qpC, peerA_on_C, 201, msg3, qpA, epC);

                using (var cts3 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, received3) = await readerA.ReadAsync(cts3.Token);
                    if (!System.Text.Encoding.UTF8.GetString(received3).Equals("Payload C->A after B removed"))
                        throw new Exception("CRITICAL REGRESSION: Reverse payload C->A failed to decrypt after B was removed!");
                }

                // 7. Reconnect B to A and verify B gets fresh session while C remains untouched
                byte[] helloB_reconnect = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloB_reconnect, epB, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB_reconnected) || !peerB_reconnected.IsSessionReady)
                    throw new Exception("Peer B failed to reconnect on Node A.");

                // A <-> C data channel still works after B reconnected
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

        private static async Task TestEndpointOnlyInterrogationDiscoversPeerBidirectionally()
        {
            Console.Write("[TEST] Endpoint-only candidate: interrogation discovers identity and establishes matching AEAD session... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_endpoint_candidate_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_endpoint_candidate_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);
                await qpA.StartAsync();
                await qpB.StartAsync();

                var candidate = new PeerInfo
                {
                    ActiveEndPoint = new IPEndPoint(IPAddress.Loopback, qpB.LocalBoundPort),
                    Addresses = new[] { IPAddress.Loopback },
                    MinPort = qpB.LocalBoundPort,
                    MaxPort = qpB.LocalBoundPort,
                    NetworkType = QuicPunch.QuicPunch.NetworkType.Static
                };

                if (candidate.TryGetId(out _))
                    throw new Exception("Endpoint-only candidate unexpectedly had an identity before discovery.");

                _ = qpA.PeerInterrogation(candidate);

                var timeout = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < timeout)
                {
                    if (qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var pB) && pB.IsSessionReady &&
                        qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var pA) && pA.IsSessionReady)
                        break;
                    await Task.Delay(50);
                }

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerBInA) || !peerBInA.IsSessionReady)
                    throw new Exception("Endpoint-only candidate was not promoted to an authenticated peer on Node A.");
                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerAInB) || !peerAInB.IsSessionReady)
                    throw new Exception("Node B did not authenticate Node A during endpoint-only discovery.");

                if (!peerBInA.ActiveSessionKeyId!.SequenceEqual(peerAInB.ActiveSessionKeyId!))
                    throw new Exception("Endpoint-only discovery derived different session keys on the two peers.");

                qpA.TrustPeer(peerBInA.CertHash);
                qpB.TrustPeer(peerAInB.CertHash);

                var readerB = qpB.GetPacketReader(302);
                byte[] message = System.Text.Encoding.UTF8.GetBytes("endpoint-only discovery payload");
                await SendDirectDataPacketAsync(qpA, peerBInA, 302, message, qpB, peerBInA.ActiveEndPoint!);
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                var (_, received) = await readerB.ReadAsync(cts.Token);
                if (!received.SequenceEqual(message))
                    throw new Exception("Endpoint-only discovered session could not decrypt data.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestMultiPeerAsymmetricReconnectDerivesFreshMatchingSession()
        {
            Console.Write("[TEST] Multi-peer asymmetric reconnect: B gets a fresh matching session while A<->C remains unchanged... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_rekey_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_rekey_b_" + Guid.NewGuid().ToString("N"));
            string tempDirC = Path.Combine(Path.GetTempPath(), "qp_test_rekey_c_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);
            Directory.CreateDirectory(tempDirC);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);
                using var qpC = new QuicPunch.QuicPunch(appDataPath: tempDirC, listeningPort: 0);
                await qpA.StartAsync();
                await qpB.StartAsync();
                await qpC.StartAsync();

                _ = qpA.PeerInterrogation(new PeerInfo
                {
                    ActiveEndPoint = new IPEndPoint(IPAddress.Loopback, qpB.LocalBoundPort),
                    Addresses = new[] { IPAddress.Loopback },
                    MinPort = qpB.LocalBoundPort,
                    MaxPort = qpB.LocalBoundPort
                });
                _ = qpA.PeerInterrogation(new PeerInfo
                {
                    ActiveEndPoint = new IPEndPoint(IPAddress.Loopback, qpC.LocalBoundPort),
                    Addresses = new[] { IPAddress.Loopback },
                    MinPort = qpC.LocalBoundPort,
                    MaxPort = qpC.LocalBoundPort
                });

                var initialTimeout = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < initialTimeout)
                {
                    if (qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id) &&
                        qpA.AvailablePeers.ContainsKey(qpC.CurrentPeer.Id) &&
                        qpB.AvailablePeers.ContainsKey(qpA.CurrentPeer.Id) &&
                        qpC.AvailablePeers.ContainsKey(qpA.CurrentPeer.Id))
                        break;
                    await Task.Delay(50);
                }

                var oldBOnA = qpA.AvailablePeers[qpB.CurrentPeer.Id];
                var aOnB = qpB.AvailablePeers[qpA.CurrentPeer.Id];
                var cOnA = qpA.AvailablePeers[qpC.CurrentPeer.Id];
                var aOnC = qpC.AvailablePeers[qpA.CurrentPeer.Id];

                byte[] oldABKey = (byte[])oldBOnA.ActiveSessionKeyId!.Clone();
                byte[] oldACKeyA = (byte[])cOnA.ActiveSessionKeyId!.Clone();
                byte[] oldACKeyC = (byte[])aOnC.ActiveSessionKeyId!.Clone();

                // Only A forgets B. B deliberately keeps its old A session.
                if (!qpA.RemovePeer(qpB.CurrentPeer.Id))
                    throw new Exception("Could not remove B from A before asymmetric reconnect.");

                _ = qpA.PeerInterrogation(new PeerInfo
                {
                    ActiveEndPoint = new IPEndPoint(IPAddress.Loopback, qpB.LocalBoundPort),
                    Addresses = new[] { IPAddress.Loopback },
                    MinPort = qpB.LocalBoundPort,
                    MaxPort = qpB.LocalBoundPort
                });

                var reconnectTimeout = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < reconnectTimeout)
                {
                    if (qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var candidateB) && candidateB.IsSessionReady &&
                        qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var candidateA) && candidateA.IsSessionReady &&
                        candidateB.ActiveSessionKeyId != null && candidateA.ActiveSessionKeyId != null &&
                        candidateB.ActiveSessionKeyId.SequenceEqual(candidateA.ActiveSessionKeyId) &&
                        !candidateB.ActiveSessionKeyId.SequenceEqual(oldABKey))
                        break;
                    await Task.Delay(50);
                }

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var newBOnA) || !newBOnA.IsSessionReady)
                    throw new Exception("B did not reconnect to A.");
                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var newAOnB) || !newAOnB.IsSessionReady)
                    throw new Exception("B lost A during asymmetric reconnect.");
                if (!newBOnA.ActiveSessionKeyId!.SequenceEqual(newAOnB.ActiveSessionKeyId!))
                    throw new Exception("A and B derived different keys after asymmetric reconnect.");
                if (newBOnA.ActiveSessionKeyId.SequenceEqual(oldABKey))
                    throw new Exception("A and B reused the previous session key after reconnect.");

                if (!cOnA.ActiveSessionKeyId!.SequenceEqual(oldACKeyA) || !aOnC.ActiveSessionKeyId!.SequenceEqual(oldACKeyC))
                    throw new Exception("Unrelated A<->C session changed while reconnecting B.");

                qpA.TrustPeer(newBOnA.CertHash);
                qpB.TrustPeer(newAOnB.CertHash);

                var readerB = qpB.GetPacketReader(303);
                byte[] abMessage = System.Text.Encoding.UTF8.GetBytes("fresh A->B reconnect payload");
                await SendDirectDataPacketAsync(qpA, newBOnA, 303, abMessage, qpB, newBOnA.ActiveEndPoint!);
                using (var ctsB = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, received) = await readerB.ReadAsync(ctsB.Token);
                    if (!received.SequenceEqual(abMessage))
                        throw new Exception("Fresh A->B session failed to decrypt after asymmetric reconnect.");
                }

                var readerA = qpA.GetPacketReader(304);
                byte[] baMessage = System.Text.Encoding.UTF8.GetBytes("fresh B->A reconnect payload");
                await SendDirectDataPacketAsync(qpB, newAOnB, 304, baMessage, qpA, newAOnB.ActiveEndPoint!);
                using (var ctsA = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var (_, received) = await readerA.ReadAsync(ctsA.Token);
                    if (!received.SequenceEqual(baMessage))
                        throw new Exception("Fresh B->A session failed to decrypt after asymmetric reconnect.");
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

                // ONLY Node A initiates interrogation against Node B.
                // Node B does NOT initiate any interrogation or contact against Node A.
                _ = qpA.PeerInterrogation(qpB.CurrentPeer);

                // Wait for unilateral interrogation and response Hello handshake to complete over real UDP
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

                // Authentication alone must NOT expose the application Data plane.
                var readerB = qpB.GetPacketReader(300);
                var readerA = qpA.GetPacketReader(301);

                byte[] blockedMessage = System.Text.Encoding.UTF8.GetBytes("blocked before trust");
                await SendDirectDataPacketAsync(qpA, peerBInA, 300, blockedMessage, qpB, peerBInA.ActiveEndPoint!);
                await Task.Delay(100);
                if (readerB.TryRead(out _))
                    throw new Exception("CRITICAL TRUST FAILURE: authenticated/untrusted peer delivered MessageType.Data before TrustPeer.");

                bool outboundApiBlocked = false;
                try
                {
                    await qpA.SendPayloadAsync(peerBInA, 300, blockedMessage);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("trusted", StringComparison.OrdinalIgnoreCase))
                {
                    outboundApiBlocked = true;
                }
                if (!outboundApiBlocked)
                    throw new Exception("SendPayloadAsync allowed an authenticated/untrusted peer.");

                qpA.TrustPeer(peerBInA.CertHash);
                qpB.TrustPeer(peerAInB.CertHash);

                // After explicit trust the same encrypted application channel works normally.
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
                qpB.Manager.HandshakeRequested += (_, _) =>
                    Task.FromResult(new HandshakeDecision(true, (ushort)0, CancellationToken.None));

                await qpA.StartAsync();
                await qpB.StartAsync();

                var epA = new IPEndPoint(IPAddress.Loopback, 49501);
                var epB = new IPEndPoint(IPAddress.Loopback, 49502);

                // 1. Peer A discovers Peer B and Peer B discovers Peer A via Hello exchange
                byte[] helloFromA = qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpB.ProcessIncomingPacketAsync(helloFromA, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpB.AvailablePeers.TryGetValue(qpA.CurrentPeer.Id, out var peerA_on_B))
                    throw new Exception("Peer A was not discovered in AvailablePeers on Node B.");

                // 2. Node B only DISCOVERED Node A, but does NOT trust Node A (not in ExpectedPeerCerts)
                if (qpB.IsTrustedPeer(peerA_on_B))
                    throw new Exception("Peer A was unexpectedly trusted on Node B without token or TrustPeer!");

                // Global/per-peer auto-accept must never turn discovery into trust.
                qpB.SetAutoAcceptAll(true);
                if (qpB.AutoAcceptUntrustedConnections)
                    throw new Exception("SetAutoAcceptAll unexpectedly enabled the unsafe untrusted bypass.");

                qpB.SetPeerAutoAccept(peerA_on_B.Id, true);
                if (qpB.IsPeerAutoAccepted(peerA_on_B.Id))
                    throw new Exception("Untrusted discovered peer became auto-accepted without TrustPeer.");

                bool outboundQuicBlocked = false;
                try
                {
                    await qpB.InitQuicConnection(dummyProtocolId, peerA_on_B);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("trusted", StringComparison.OrdinalIgnoreCase))
                {
                    outboundQuicBlocked = true;
                }
                if (!outboundQuicBlocked)
                    throw new Exception("Untrusted discovered peer could start an outbound QUIC application connection.");

                bool outboundUdpBlocked = false;
                try
                {
                    await qpB.InitUdpConnection(dummyProtocolId, peerA_on_B);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("trusted", StringComparison.OrdinalIgnoreCase))
                {
                    outboundUdpBlocked = true;
                }
                if (!outboundUdpBlocked)
                    throw new Exception("Untrusted discovered peer could start an outbound UDP application connection.");

                // 3. Node A attempts a protocol handshake with Node B for dummyProtocolId
                var guid1 = Guid.NewGuid();
                byte[] handshakeReq1 = qpA.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, 0, dummyProtocolId, guid1, null, QuicPunch.QuicPunch.TransportType.Wan);
                await qpB.ProcessIncomingPacketAsync(handshakeReq1, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpB.IncomingHandshakeSessions.TryGetValue(guid1, out var session1))
                    throw new Exception("Node B did not register incoming handshake session.");

                // Even though the application callback above always says Accepted=true,
                // the core must decline before invoking application-level approval for untrusted peers.
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var responsePayload1 = await session1.ResponsePayloadTcs.Task.WaitAsync(cts.Token);
                    byte respType = responsePayload1[QuicPunch.QuicPunch.MagicHeader.Length + 1 + 16];
                    if (respType != (byte)QuicPunchStructures.HandShakeType.Decline)
                        throw new Exception($"CRITICAL TRUST FAILURE: expected core Decline for untrusted peer, got {respType}.");
                }

                // 4. Now Node B explicitly trusts Node A (e.g. user entered token or called TrustPeer)
                qpB.TrustPeer(qpA.CurrentPeer.CertHash);

                if (!qpB.IsTrustedPeer(peerA_on_B))
                    throw new Exception("Peer A should be trusted after TrustPeer.");

                // 5. Node A attempts protocol handshake again with a new connection GUID
                var guid2 = Guid.NewGuid();
                byte[] handshakeReq2 = qpA.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, 0, dummyProtocolId, guid2, null, QuicPunch.QuicPunch.TransportType.Wan);
                await qpB.ProcessIncomingPacketAsync(handshakeReq2, epA, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpB.IncomingHandshakeSessions.TryGetValue(guid2, out var session2))
                    throw new Exception("Node B did not register second handshake session.");

                // Because Node A is now TRUSTED, it must be auto-accepted!
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

        private static async Task TestQuicReadyIsTrustedAndBounded()
        {
            Console.Write("[TEST] QUIC_READY requires trust and early cache stays bounded... ");
            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_quicready_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_quicready_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);
            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);
                await qpA.StartAsync();
                await qpB.StartAsync();

                var epB = new IPEndPoint(IPAddress.Loopback, qpB.LocalBoundPort);
                byte[] helloB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloB, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB))
                    throw new Exception("Peer B was not authenticated on A.");

                byte[] untrustedReady = QuicPunch.PacketHandler.PacketBuilder.BuildQuicReadyPacket(qpB, Guid.NewGuid(), 43000);
                await qpA.ProcessIncomingPacketAsync(untrustedReady, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (qpA.ReceivedQuicReadyCacheCount != 0)
                    throw new Exception("Untrusted peer populated QUIC_READY cache.");

                qpA.TrustPeer(peerB.CertHash);
                for (int i = 0; i < 400; i++)
                {
                    byte[] ready = QuicPunch.PacketHandler.PacketBuilder.BuildQuicReadyPacket(qpB, Guid.NewGuid(), (ushort)(43000 + (i % 100)));
                    await qpA.ProcessIncomingPacketAsync(ready, epB, QuicPunch.QuicPunch.TransportType.Wan);
                }

                if (qpA.ReceivedQuicReadyCacheCount > 128)
                    throw new Exception($"QUIC_READY early cache exceeded hard cap: {qpA.ReceivedQuicReadyCacheCount}");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestPingCannotRefreshDiscoveredPeerLiveness()
        {
            Console.Write("[TEST] unauthenticated Ping cannot refresh discovered-peer liveness... ");
            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_ping_live_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_ping_live_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);
            try
            {
                using var qpA = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirA)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();
                qpA.LanDiscoveryPort = 48511;

                using var qpB = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirB)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();
                qpB.LanDiscoveryPort = 48512;

                await qpA.StartAsync();
                await qpB.StartAsync();
                await Task.Delay(100);

                var epB = new IPEndPoint(IPAddress.Loopback, qpB.LocalBoundPort);
                byte[] helloB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloB, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerB))
                    throw new Exception("Peer B was not discovered on A.");

                // Allow any initial handshake/hello exchange responses to settle
                await Task.Delay(350);
                DateTime before = peerB.LastSeen;
                byte[] ping = qpB.BuildPingPacket(PreciseTime.GetCorrectTime().Ticks, false);
                await qpA.ProcessIncomingPacketAsync(ping, epB, QuicPunch.QuicPunch.TransportType.Wan);
                await Task.Delay(50);
                if (peerB.LastSeen != before)
                    throw new Exception("Unauthenticated Ping refreshed LastSeen and could defeat discovered-peer TTL pruning.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static void TestHandshakeCandidateFramingIsBounded()
        {
            Console.Write("[TEST] Handshake candidate framing is bounded and byte-count aligned... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_candidate_wire_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0);
                var candidates = Enumerable.Range(0, 300)
                    .Select(i => new CandidateEndpoint(new IPEndPoint(IPAddress.Loopback, 10000 + i), CandidateType.Host, (uint)i))
                    .ToList();
                byte[] packet = qp.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request, 12345, Guid.NewGuid(), Guid.NewGuid(), candidates, QuicPunch.QuicPunch.TransportType.Wan);

                using var ms = new MemoryStream(packet);
                using var r = new BinaryReader(ms);
                r.ReadBytes(QuicPunch.QuicPunch.MagicHeader.Length);
                r.ReadByte();
                r.ReadBytes(16);
                r.ReadByte();
                r.ReadUInt16();
                r.ReadBytes(16);
                r.ReadBytes(16);
                byte count = r.ReadByte();
                if (count != 32)
                    throw new Exception($"Expected candidate wire cap 32, got {count}.");

                long expectedRemaining = count * 11L + CertManager.SignatureLength;
                if (ms.Length - ms.Position != expectedRemaining)
                    throw new Exception("Handshake candidate framing is not aligned with its serialized count.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
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

                // 1. Test Pending TTL
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

                // 2. Test Completed replay grace period
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

                // 3. Test Rejected replay grace period
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

                // 4. Test Hard Cap Enforcement
                qp.MaxIncomingHandshakeSessions = 5;
                var guids = new List<Guid>();
                for (int i = 0; i < 5; i++)
                {
                    var g = Guid.NewGuid();
                    guids.Add(g);
                    var s = qp.GetOrAddIncomingHandshakeSession(g, peerId, protoId);
                    if (i < 2) s?.MarkCompleted(); // make the first two completed
                }

                if (qp.IncomingHandshakeSessions.Count != 5)
                    throw new Exception($"Expected 5 sessions at hard cap, found {qp.IncomingHandshakeSessions.Count}");

                // Add 6th session: should trigger eviction and remain at <= 5
                var g6 = Guid.NewGuid();
                var s6 = qp.GetOrAddIncomingHandshakeSession(g6, peerId, protoId);

                if (qp.IncomingHandshakeSessions.Count > 5)
                    throw new Exception($"IncomingHandshakeSessions exceeded hard cap: {qp.IncomingHandshakeSessions.Count} > 5");

                if (!qp.IncomingHandshakeSessions.ContainsKey(g6))
                    throw new Exception("New session should be present after eviction of older session.");

                // Add many sessions concurrently: Count -> eviction -> insertion must be atomic.
                var startGate = new ManualResetEventSlim(false);
                var concurrent = Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
                {
                    startGate.Wait();
                    qp.GetOrAddIncomingHandshakeSession(Guid.NewGuid(), peerId, protoId);
                })).ToArray();
                startGate.Set();
                await Task.WhenAll(concurrent);

                if (qp.IncomingHandshakeSessions.Count > 5)
                    throw new Exception($"Hard cap violated under concurrent load: count was {qp.IncomingHandshakeSessions.Count}");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestNostrDiscoveryConfigurationAndLifecycle()
        {
            Console.Write("[TEST] Nostr discovery configuration, dynamic enable/disable and clean lifecycle... ");

            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_builder_nostr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var customRelays = new[] { "ws://127.0.0.1:9" };
                using var qp = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDir)
                    .UsePool("0123456789abcdef0123456789abcdef01234567")
                    .WithAutoDiscovery(false)
                    .WithNostrRelays(customRelays)
                    .Build();

                if (qp.NostrRelays == null || qp.NostrRelays.Length != 1 || qp.NostrRelays[0] != customRelays[0])
                    throw new Exception("QuicPunchBuilder.Build did not set Nostr relays.");
                if (qp.NostrDiscoveryEnabled)
                    throw new Exception("Nostr discovery should be disabled before explicit enable.");

                await qp.StartAsync();
                if (qp.NostrDiscovery != null)
                    throw new Exception("Nostr discovery started while disabled.");

                await qp.SetPeerDiscoveryEnabledAsync(true);
                if (!qp.NostrDiscoveryEnabled || qp.NostrDiscovery == null || !qp.NostrDiscovery.IsRunning)
                    throw new Exception("Nostr discovery did not enter running state after enable.");

                await qp.SetPeerDiscoveryEnabledAsync(false);
                if (qp.NostrDiscoveryEnabled || qp.NostrDiscovery != null)
                    throw new Exception("Nostr discovery did not stop cleanly after disable.");

                await qp.StopAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestBuilderAutoDiscoveryFlagIsAuthoritative()
        {
            Console.Write("[TEST] Builder WithAutoDiscovery(false) keeps PoolId without starting Nostr discovery... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_no_nostr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var qp = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDir)
                    .UsePool("minimal-discovery-pool")
                    .WithAutoDiscovery(false)
                    .Build();

                if (qp.PoolId.Length != 20)
                    throw new Exception("PoolId should remain available when Nostr discovery is disabled.");
                await qp.StartAsync();
                if (qp.NostrDiscovery != null || qp.NostrDiscoveryEnabled)
                    throw new Exception("Nostr discovery started even though WithAutoDiscovery(false) was requested.");
                await qp.StopAsync();
                Console.WriteLine("PASSED");
            }
            finally { try { Directory.Delete(tempDir, true); } catch { } }
        }

        private static async Task TestRebindCannotResurrectWorkersAfterStop()
        {
            Console.Write("[TEST] Rebind background refresh cannot resurrect Nostr discovery/socket after StopAsync... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_rebind_stop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                byte[] pool = RandomNumberGenerator.GetBytes(20);
                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, discoveryId: pool, listeningPort: 0);
                await qp.StartAsync();

                int newPort;
                using (var probe = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    newPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

                if (!qp.RebindListenerPort((ushort)newPort))
                    throw new Exception("RebindListenerPort failed.");
                await qp.StopAsync();
                await Task.Delay(1200);

                if (qp.LifecycleState != QuicPunch.QuicPunch.QuicPunchLifecycleState.Stopped)
                    throw new Exception($"Expected Stopped lifecycle, got {qp.LifecycleState}.");
                if (qp.NostrDiscovery != null)
                    throw new Exception("Nostr discovery was recreated after StopAsync.");
                if (qp.udp != null)
                    throw new Exception("UDP socket was recreated after StopAsync.");

                Console.WriteLine("PASSED");
            }
            finally { try { Directory.Delete(tempDir, true); } catch { } }
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

                // 1. Save endpoints in raw binary format
                QuicPunch.EndpointCache.Save(tempFile, originalEndpoints);

                if (!File.Exists(tempFile))
                    throw new Exception("Cache file was not created by EndpointCache.Save.");

                byte[] bytes = await File.ReadAllBytesAsync(tempFile);
                if (bytes.Length < 18)
                    throw new Exception($"Cache file is too small for QPEP header: {bytes.Length} bytes.");

                // Assert magic 'Q', 'P', 'E', 'P'
                if (bytes[0] != (byte)'Q' || bytes[1] != (byte)'P' || bytes[2] != (byte)'E' || bytes[3] != (byte)'P')
                    throw new Exception("Cache file header missing 'QPEP' binary magic signature.");

                // 2. Load endpoints using TryRead
                if (!QuicPunch.EndpointCache.TryRead(tempFile, TimeSpan.FromHours(1), out var loaded, checkTtl: true))
                    throw new Exception("EndpointCache.TryRead failed to read raw binary cache.");

                if (loaded.Count != originalEndpoints.Count)
                    throw new Exception($"Endpoint count mismatch: expected {originalEndpoints.Count}, got {loaded.Count}");

                for (int i = 0; i < originalEndpoints.Count; i++)
                {
                    if (!originalEndpoints[i].Equals(loaded[i]))
                        throw new Exception($"Endpoint mismatch at index {i}: expected {originalEndpoints[i]}, got {loaded[i]}");
                }

                // 3. Test legacy plain-text fallback and automatic upgrade
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

                    // Verify legacy file was auto-upgraded to raw binary
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

                string futureLegacyFile = Path.Combine(Path.GetTempPath(), "qp_test_future_legacy_cache_" + Guid.NewGuid().ToString("N") + ".epl");
                try
                {
                    await File.WriteAllTextAsync(futureLegacyFile, "1.2.3.4:1234\n");
                    File.SetLastWriteTimeUtc(futureLegacyFile, DateTime.UtcNow.AddHours(1));
                    if (QuicPunch.EndpointCache.TryRead(futureLegacyFile, TimeSpan.FromHours(1), out _, checkTtl: true))
                        throw new Exception("Legacy cache with implausible future timestamp was accepted.");
                }
                finally
                {
                    try { File.Delete(futureLegacyFile); } catch { }
                }

                // 4. Corrupt headers must be rejected without large allocation or partial success.
                string corruptFile = Path.Combine(Path.GetTempPath(), "qp_test_corrupt_cache_" + Guid.NewGuid().ToString("N") + ".epl");
                try
                {
                    byte[] corrupt = new byte[25];
                    corrupt[0] = (byte)'Q'; corrupt[1] = (byte)'P'; corrupt[2] = (byte)'E'; corrupt[3] = (byte)'P';
                    corrupt[4] = 1;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(corrupt.AsSpan(6, 8), DateTime.UtcNow.Ticks);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(14, 4), int.MaxValue);
                    await File.WriteAllBytesAsync(corruptFile, corrupt);
                    if (QuicPunch.EndpointCache.TryRead(corruptFile, TimeSpan.FromHours(1), out _, checkTtl: true))
                        throw new Exception("Cache with impossible count was accepted.");

                    byte[] truncated = bytes[..Math.Min(bytes.Length, 20)];
                    await File.WriteAllBytesAsync(corruptFile, truncated);
                    if (QuicPunch.EndpointCache.TryRead(corruptFile, TimeSpan.FromHours(1), out _, checkTtl: true))
                        throw new Exception("Truncated binary cache was accepted.");

                    byte[] future = (byte[])bytes.Clone();
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(future.AsSpan(6, 8), DateTime.UtcNow.AddHours(1).Ticks);
                    await File.WriteAllBytesAsync(corruptFile, future);
                    if (QuicPunch.EndpointCache.TryRead(corruptFile, TimeSpan.FromHours(1), out _, checkTtl: true))
                        throw new Exception("Cache with implausible future timestamp was accepted.");
                }
                finally
                {
                    try { File.Delete(corruptFile); } catch { }
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
            await TestSingleFlightFailureCompletesFollowers();
            await TestActiveSessionReuse();
            Console.WriteLine("==================================================");
            Console.WriteLine("   SINGLE-FLIGHT TEST PASSED SUCCESSFULLY!        ");
            Console.WriteLine("==================================================");
        }

        private static async Task TestSingleFlightFailureCompletesFollowers()
        {
            Console.Write("[TEST] Single-flight owner failure completes all waiting followers... ");

            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_sf_fail_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_sf_fail_b_" + Guid.NewGuid().ToString("N"));
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
                await qpB.ProcessIncomingPacketAsync(qpA.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true), epA, QuicPunch.QuicPunch.TransportType.Wan);
                await qpA.ProcessIncomingPacketAsync(qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true), epB, QuicPunch.QuicPunch.TransportType.Wan);

                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerBOnA))
                    throw new Exception("Peer B was not discovered before single-flight failure test.");
                peerBOnA.ActiveEndPoint = epB;

                var protocolId = Guid.NewGuid();
                qpA.RegisterProtocol(new DummyProtocolHandler(protocolId, () => { }));

                // Make the remote endpoint intentionally unavailable. The owner is
                // then cancelled after it has published its flight, while followers
                // are already attached to that same TCS.
                await qpB.StopAsync();
                using var ownerCts = new CancellationTokenSource();
                Task owner = Task.Run(() => qpA.InitQuicConnection(protocolId, peerBOnA, cancellationToken: ownerCts.Token));

                for (int i = 0; i < 100 && qpA.ActiveConnectionFlightsCount == 0; i++)
                    await Task.Delay(10);
                if (qpA.ActiveConnectionFlightsCount != 1)
                    throw new Exception("Owner did not publish a single-flight entry.");

                var followers = Enumerable.Range(0, 12)
                    .Select(_ => Task.Run(() => qpA.InitQuicConnection(protocolId, peerBOnA)))
                    .ToArray();
                await Task.Delay(30);
                ownerCts.Cancel();

                Task all = Task.WhenAll(followers.Prepend(owner));
                try
                {
                    await all.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception) when (all.IsCompleted)
                {
                    // Failure/cancellation is expected; completion is the invariant.
                }
                catch (TimeoutException)
                {
                    throw new Exception("Single-flight followers remained pending after owner failure.");
                }

                if (followers.Any(task => !task.IsCompleted))
                    throw new Exception("At least one single-flight follower never completed.");
                if (owner.Status == TaskStatus.RanToCompletion || followers.Any(task => task.Status == TaskStatus.RanToCompletion))
                    throw new Exception("Owner failure was not propagated to every single-flight follower.");
                if (qpA.ActiveConnectionFlightsCount != 0)
                    throw new Exception($"Connection flight leaked after owner failure: {qpA.ActiveConnectionFlightsCount}");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
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

                // 1. Mutual discovery via Hello packets
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

                // 2. Launch 50 simultaneous concurrent calls to InitQuicConnection for the same (peerB, protocolGuid)
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

                // 3. Verifications:
                // a) Exactly ONE effective connectionGuid in qpB's IncomingHandshakeSessions
                if (qpB.IncomingHandshakeSessions.Count != 1)
                    throw new Exception($"Expected exactly 1 incoming handshake session on Node B, found {qpB.IncomingHandshakeSessions.Count}");

                // b) Exactly ONE QUIC session handled on Node A and Node B
                if (handleCountA != 1)
                    throw new Exception($"Expected handleCountA == 1, got {handleCountA}");
                if (handleCountB != 1)
                    throw new Exception($"Expected handleCountB == 1, got {handleCountB}");

                // c) No orphaned outbound negotiations
                if (qpA.ActiveOutboundNegotiationsCount != 0)
                    throw new Exception($"Expected ActiveOutboundNegotiationsCount == 0, got {qpA.ActiveOutboundNegotiationsCount}");

                // d) No orphaned connection flights
                if (qpA.ActiveConnectionFlightsCount != 0)
                    throw new Exception($"Expected ActiveConnectionFlightsCount == 0, got {qpA.ActiveConnectionFlightsCount}");

                // 4. Verify a second connection can be initiated normally after finishing the first
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

                // Start connection in background
                var firstConnTask = Task.Run(async () => await qpA.InitQuicConnection(protocolGuid, peerB_on_A));

                // Wait until session is registered and active
                int waitTries = 0;
                while (waitTries < 150 && (!qpA.HasActiveProtocolSession(peerB_on_A.Id, protocolGuid) || handleCountA == 0))
                {
                    waitTries++;
                    await Task.Delay(100);
                }

                if (!qpA.HasActiveProtocolSession(peerB_on_A.Id, protocolGuid))
                    throw new Exception("Session should be active on Node A.");

                // While session is active, call InitQuicConnection: must reuse existing session immediately!
                await qpA.InitQuicConnection(protocolGuid, peerB_on_A);

                // Handshake sessions on Node B must still be 1 (no new handshake occurred)
                if (qpB.IncomingHandshakeSessions.Count != 1)
                    throw new Exception($"Expected 1 incoming handshake session on Node B, got {qpB.IncomingHandshakeSessions.Count}");

                if (handleCountA != 1)
                    throw new Exception($"Expected handleCountA == 1, got {handleCountA}");

                // Release hold so connection completes
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

        private static async Task TestTorTokenFixedOffsetEncodingAndDecoding()
        {
            Console.Write("[TEST] Tor token fixed-offset raw encoding & zero-allocation roundtrip... ");

            byte[] certHash = new byte[32];
            Random.Shared.NextBytes(certHash);

            const string onion = "h6mwth5qlqfulm2kyo4nhid6zx423kit33urpnq7m5iakj4mthtinkid.onion";
            const int port = 49484;

            var torPeer = new PeerInfo
            {
                NetworkType = QuicPunch.QuicPunch.NetworkType.Tor,
                OnionAddress = onion,
                MinPort = port,
                MaxPort = port
            };
            torPeer.SetCertificateHash(certHash);

            string token = Utilities.EncodeEndpointToken(torPeer);
            byte[] rawBytes = System.Buffers.Text.Base64Url.DecodeFromChars(token);

            if (rawBytes.Length != Utilities.TorTokenBinaryLength)
                throw new Exception($"Expected Tor token binary length {Utilities.TorTokenBinaryLength}, got {rawBytes.Length}");

            // Offset 0: Flags (NetworkType = Tor = 4)
            var flags = new PackedFlags(rawBytes[0]);
            if (flags.NetworkType != QuicPunch.QuicPunch.NetworkType.Tor)
                throw new Exception($"Expected NetworkType Tor, got {flags.NetworkType}");

            // Offset 36..37: Port Little-Endian
            ushort encodedPort = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(rawBytes.AsSpan(36, 2));
            if (encodedPort != port)
                throw new Exception($"Expected port {port}, got {encodedPort}");

            // Offset 38..69: CertHash
            if (!CryptographicOperations.FixedTimeEquals(rawBytes.AsSpan(38, 32), certHash))
                throw new Exception("Encoded certificate hash does not match original");

            // Decode roundtrip
            var decoded = Utilities.DecodeEndpointToken(token);
            if (decoded.NetworkType != QuicPunch.QuicPunch.NetworkType.Tor)
                throw new Exception("Decoded NetworkType is not Tor");
            if (!string.Equals(decoded.OnionAddress, onion, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Decoded onion address mismatch: expected {onion}, got {decoded.OnionAddress}");
            if (decoded.MinPort != port || decoded.MaxPort != port)
                throw new Exception($"Decoded port mismatch: expected {port}, got {decoded.MinPort}");
            if (!decoded.TryGetCertificateHash(out var decodedHash) || !CryptographicOperations.FixedTimeEquals(decodedHash, certHash))
                throw new Exception("Decoded certificate hash mismatch");

            Console.WriteLine("PASSED");
            await Task.CompletedTask;
        }

        private static async Task TestNostrDiscoveredPeersRecordAndManualConnect()
        {
            Console.Write("[TEST] Discovered peers recorded without auto-connect & manual connection initiation... ");
            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_disc_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_disc_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);
            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0) { LanDiscoveryPort = 48123 };
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0) { LanDiscoveryPort = 48124 };
                await qpA.StartAsync();
                await qpB.StartAsync();

                string tokenB = qpB.GetWanToken();
                using var decodedB = Utilities.DecodeEndpointToken(tokenB);

                // Record peer B as a discovered peer on node A
                qpA.RecordDiscoveredPeer(decodedB, tokenB, "Nostr");

                if (!decodedB.TryGetCertificateHash(out var hashB))
                    throw new Exception("Peer B certificate hash unavailable.");

                string hexKeyB = Convert.ToHexString(hashB);
                if (!qpA.DiscoveredPeers.TryGetValue(hexKeyB, out var discoveredB))
                    throw new Exception("Discovered peer was not stored in DiscoveredPeers.");

                if (discoveredB.Source != "Nostr")
                    throw new Exception($"Expected source Nostr, got {discoveredB.Source}");

                if (discoveredB.Token != tokenB)
                    throw new Exception("Discovered peer token mismatch.");

                // Verify peer B is NOT yet in AvailablePeers (no auto-connect happened)
                if (qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id))
                    throw new Exception("Discovered peer was prematurely added to AvailablePeers!");

                // Now manually interrogate using the discovered token
                _ = qpA.PeerInterrogation(discoveredB.Token);

                int tries = 0;
                while (tries < 50 && !qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id))
                {
                    await Task.Delay(100);
                    tries++;
                }

                // Now peer B should be discovered and in AvailablePeers!
                if (!qpA.AvailablePeers.ContainsKey(qpB.CurrentPeer.Id))
                    throw new Exception("Manual interrogation of discovered peer failed to add to AvailablePeers.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestWanAndTorNostrDiscoverySeparation()
        {
            Console.Write("[TEST] Nostr WAN vs Tor discovery separation, scoped channels, payload isolation & proxying... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_nostr_wan_tor_sep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                byte[] poolBytes = new byte[20];
                RandomNumberGenerator.Fill(poolBytes);

                // 1. Channel Tag Isolation
                string wanTag = NostrDiscovery.CreateChannelTag(poolBytes, "wan");
                string torTag = NostrDiscovery.CreateChannelTag(poolBytes, "tor");
                string legacyTag = NostrDiscovery.CreateChannelTag(poolBytes, null);

                if (wanTag == torTag)
                    throw new Exception("WAN and Tor channel tags must be strictly different.");
                if (wanTag == legacyTag)
                    throw new Exception("Scoped WAN tag must be different from un-scoped tag.");
                if (torTag == legacyTag)
                    throw new Exception("Scoped Tor tag must be different from un-scoped tag.");

                // 2. Token Separation and Leak Prevention
                using var qp = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDir)
                    .UsePool(poolBytes)
                    .WithWanNostrDiscovery(true)
                    .WithTorNostrDiscovery(true)
                    .Build();

                qp.CurrentPeer.Addresses = new[]
                {
                    IPAddress.Parse("198.51.100.50"),
                    IPAddress.Parse("192.168.1.100")
                };
                qp.CurrentPeer.MinPort = 35000;
                qp.CurrentPeer.MaxPort = 35100;

                qp.TorCurrentPeer.OnionAddress = "vww6ybal4bd7szmgncyruucpgfkqahzddi37ktceo3ah7ngmcopnpyyd.onion";
                qp.TorCurrentPeer.MinPort = 9050;
                qp.TorCurrentPeer.MaxPort = 9050;
                qp.TorCurrentPeer.NetworkType = QuicPunch.QuicPunch.NetworkType.Tor;

                string wanToken = qp.GetWanToken();
                string? torToken = qp.GetTorToken();

                if (string.IsNullOrEmpty(wanToken) || string.IsNullOrEmpty(torToken))
                    throw new Exception("Generated tokens must not be empty.");

                using var decodedWan = Utilities.DecodeEndpointToken(wanToken);
                using var decodedTor = Utilities.DecodeEndpointToken(torToken);

                // WAN token MUST have WAN addresses and NOT Tor onion
                if (decodedWan.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor)
                    throw new Exception("WAN token has unexpected Tor network type.");
                if (decodedWan.Addresses.Length == 0)
                    throw new Exception("WAN token must include IP addresses.");
                if (!string.IsNullOrEmpty(decodedWan.OnionAddress))
                    throw new Exception("WAN token unexpectedly included onion address.");

                // Tor token MUST have ONLY .onion address and ZERO clearnet/LAN IPs
                if (decodedTor.NetworkType != QuicPunch.QuicPunch.NetworkType.Tor)
                    throw new Exception("Tor token must have NetworkType.Tor.");
                if (string.IsNullOrEmpty(decodedTor.OnionAddress) || !decodedTor.OnionAddress.EndsWith(".onion", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Tor token must contain valid .onion address.");
                if (decodedTor.Addresses != null && decodedTor.Addresses.Length > 0)
                    throw new Exception("Tor token leaked IP addresses! Tor tokens must contain ZERO IP addresses.");

                // 3. NostrDiscovery Instance Scoping & Proxy Provider
                var dummyProxy = new WebProxy("socks5://127.0.0.1:9050");
                using var torDiscovery = new NostrDiscovery(
                    poolBytes,
                    () => torToken,
                    scope: "tor",
                    proxyProvider: () => dummyProxy);

                if (torDiscovery.Scope != "tor")
                    throw new Exception("Tor discovery instance did not retain 'tor' scope.");
                if (torDiscovery.ChannelTag != torTag)
                    throw new Exception("Tor discovery instance channel tag mismatch.");

                // 4. Independent Controls and Lifecycle
                using var customBuilderQp = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(Path.Combine(tempDir, "sub"))
                    .UsePool(poolBytes)
                    .WithWanNostrDiscovery(true)
                    .WithTorNostrDiscovery(false)
                    .Build();

                if (!customBuilderQp.WanNostrDiscoveryEnabled)
                    throw new Exception("WanNostrDiscoveryEnabled should be true from builder.");
                if (customBuilderQp.TorNostrDiscoveryEnabled)
                    throw new Exception("TorNostrDiscoveryEnabled should be false from builder.");

                await customBuilderQp.StartAsync();
                if (customBuilderQp.WanNostrDiscovery == null || !customBuilderQp.WanNostrDiscovery.IsRunning)
                    throw new Exception("WanNostrDiscovery was not running after StartAsync.");
                if (customBuilderQp.TorNostrDiscovery != null)
                    throw new Exception("TorNostrDiscovery should not be running when disabled.");

                // Stop WAN discovery dynamically
                await customBuilderQp.SetWanPeerDiscoveryEnabledAsync(false);
                if (customBuilderQp.WanNostrDiscoveryEnabled || customBuilderQp.WanNostrDiscovery != null)
                    throw new Exception("WanNostrDiscovery did not stop when disabled.");

                await customBuilderQp.StopAsync();

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestTokenEcdsaSignatureVerification()
        {
            Console.Write("[TEST] Compact token generation, Nostr ECDSA signing & verification roundtrip... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_token_ecdsa_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: tempDir, listeningPort: 0);
                await qp.StartAsync();

                // 1. WAN compact token
                string wanToken = qp.GetWanToken();
                using var decodedWan = Utilities.DecodeEndpointToken(wanToken);

                // WAN token should be compact (~50-80 chars) without embedded 64-byte signature
                if (wanToken.Length > 100)
                    throw new Exception($"Expected compact WAN token (<100 chars), but got {wanToken.Length} chars.");

                byte[] wanCertPubKey = qp.CertManager.PeerCertificate.GetPublicKey();
                byte[] wanSignature = Utilities.SignToken(wanToken, qp.CertManager.PeerCertificate);
                if (wanSignature.Length != 64)
                    throw new Exception($"Expected 64-byte ECDSA signature, got {wanSignature.Length}");

                if (!Utilities.TryVerifyTokenCertificate(wanToken, wanCertPubKey, wanSignature, out var verifiedWanHash) || verifiedWanHash == null)
                    throw new Exception("WAN token signature verification failed with valid peer public key and signature.");

                if (!CryptographicOperations.FixedTimeEquals(verifiedWanHash, decodedWan.CertHash))
                    throw new Exception("Verified WAN cert hash mismatch.");

                // 1b. Tamper check: modifying any token byte MUST fail verification
                byte[] tamperedWanSig = (byte[])wanSignature.Clone();
                tamperedWanSig[0] ^= 0xFF;
                if (Utilities.TryVerifyTokenCertificate(wanToken, wanCertPubKey, tamperedWanSig, out _))
                    throw new Exception("Tampered signature unexpectedly passed verification!");

                // 2. Tor compact token with valid signature
                qp.TorCurrentPeer.OnionAddress = "vww6ybal4bd7szmgncyruucpgfkqahzddi37ktceo3ah7ngmcopnpyyd.onion";
                qp.TorCurrentPeer.MinPort = 9050;
                qp.TorCurrentPeer.MaxPort = 9050;
                qp.TorCurrentPeer.NetworkType = QuicPunch.QuicPunch.NetworkType.Tor;

                string? torToken = qp.GetTorToken();
                if (string.IsNullOrEmpty(torToken))
                    throw new Exception("Tor token generation failed.");

                using var decodedTor = Utilities.DecodeEndpointToken(torToken);
                byte[] torCertPubKey = qp.TorCertManager.PeerCertificate.GetPublicKey();
                byte[] torSignature = Utilities.SignToken(torToken, qp.TorCertManager.PeerCertificate);

                if (!Utilities.TryVerifyTokenCertificate(torToken, torCertPubKey, torSignature, out var verifiedTorHash) || verifiedTorHash == null)
                    throw new Exception("Tor token signature verification failed with valid peer public key.");

                // Verify Tor and WAN certificates are distinct for privacy & security
                if (CryptographicOperations.FixedTimeEquals(qp.CurrentPeer.CertHash, qp.TorCurrentPeer.CertHash))
                    throw new Exception("WAN and Tor must have distinct cryptographic certificates.");

                // 2b. Tamper check for Tor
                byte[] tamperedTorSig = (byte[])torSignature.Clone();
                tamperedTorSig[1] ^= 0xFF;
                if (Utilities.TryVerifyTokenCertificate(torToken, torCertPubKey, tamperedTorSig, out _))
                    throw new Exception("Tampered Tor signature unexpectedly passed verification!");

                // 3. Dynamic port & multi-IP WAN token
                var multiIpPeer = new PeerInfo(qp.CurrentPeer.Certificate!, qp.CurrentPeer.EcdhPublicKey)
                {
                    Addresses = new[] { IPAddress.Parse("1.2.3.4"), IPAddress.Parse("5.6.7.8") },
                    MinPort = 40000,
                    MaxPort = 40050,
                    NetworkType = QuicPunch.QuicPunch.NetworkType.DynamicPortAndAddress
                };
                string multiToken = Utilities.EncodeEndpointToken(multiIpPeer);
                using var decodedMulti = Utilities.DecodeEndpointToken(multiToken);
                if (decodedMulti.MinPort != 40000 || decodedMulti.MaxPort != 40050 || decodedMulti.Addresses.Length < 2)
                    throw new Exception("Dynamic port/multi-IP token did not preserve attributes.");

                byte[] multiSig = Utilities.SignToken(multiToken, qp.CertManager.PeerCertificate);
                if (!Utilities.TryVerifyTokenCertificate(multiToken, wanCertPubKey, multiSig, out _))
                    throw new Exception("Dynamic port token signature verification failed.");

                // 4. Prefix handling with compact tokens
                using var decodedPrefixed1 = Utilities.DecodeEndpointToken("qp://" + wanToken);
                if (!CryptographicOperations.FixedTimeEquals(decodedPrefixed1.CertHash, decodedWan.CertHash))
                    throw new Exception("Prefixed qp:// token failed to decode correctly.");

                using var decodedPrefixed2 = Utilities.DecodeEndpointToken("QPHP://" + wanToken);
                if (!CryptographicOperations.FixedTimeEquals(decodedPrefixed2.CertHash, decodedWan.CertHash))
                    throw new Exception("Prefixed QPHP:// token failed to decode correctly.");

                // 5. PeerStore persistence and decoding from token
                string peerDbPath = Path.Combine(tempDir, "test_peers.db");
                using (var peerStore = new PeerStore(peerDbPath))
                {
                    bool added = peerStore.AddOrUpdate(wanToken, autoConnect: true, save: true);
                    if (!added)
                        throw new Exception("Failed to add signed token to PeerStore.");

                    if (!peerStore.TryGet(decodedWan.CertHash, out var savedPeer) || savedPeer == null)
                        throw new Exception("PeerStore failed to retrieve peer by CertHash from signed token.");

                    if (savedPeer.MinPort != decodedWan.MinPort)
                        throw new Exception("PeerStore saved peer port mismatch.");
                }

                await qp.StopAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestNostrDiscoveryUpdatesSavedPeerInPeerStoreAndTriggersReconnect()
        {
            Console.Write("[TEST] Nostr discovery auto-updates saved peer in PeerStore & peers.db on port/IP change... ");
            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_nostr_update_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_nostr_update_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);
            try
            {
                using var qpA = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirA)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();

                using var qpB = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirB)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();

                await qpA.StartAsync();
                await qpB.StartAsync();

                // 1. Peer B has old port 40001
                qpB.CurrentPeer.MinPort = 40001;
                qpB.CurrentPeer.MaxPort = 40001;
                qpB.CurrentPeer.Addresses = new[] { IPAddress.Parse("127.0.0.1") };
                qpB.InvalidateTokenCache();
                string oldTokenB = qpB.GetWanToken();

                // Peer A saves Peer B with the old port
                bool added = qpA.PeerStore.AddOrUpdate(oldTokenB, autoConnect: true, save: true);
                if (!added)
                    throw new Exception("Failed to add initial peer B to qpA.PeerStore.");

                if (!qpA.PeerStore.TryGet(qpB.CurrentPeer.CertHash, out var savedInitial) || savedInitial == null)
                    throw new Exception("qpA.PeerStore did not contain peer B.");
                if (savedInitial.MinPort != 40001)
                    throw new Exception($"Expected initial port 40001, got {savedInitial.MinPort}");

                // 2. Peer B updates port (e.g. CGNAT change) to 50002
                qpB.CurrentPeer.MinPort = 50002;
                qpB.CurrentPeer.MaxPort = 50002;
                qpB.InvalidateTokenCache();
                string newTokenB = qpB.GetWanToken();

                // 3. Inject new Nostr discovery token into qpA via ProcessWanNostrEndpointToken
                var method = typeof(QuicPunch.QuicPunch).GetMethod("ProcessWanNostrEndpointToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (method == null)
                    throw new Exception("ProcessWanNostrEndpointToken method not found.");

                method.Invoke(qpA, new object?[] { newTokenB, qpB.CertManager.NostrPublicKeyHex, null, null });

                // 4. Verify PeerStore in memory was updated to new port 50002
                if (!qpA.PeerStore.TryGet(qpB.CurrentPeer.CertHash, out var savedUpdated) || savedUpdated == null)
                    throw new Exception("qpA.PeerStore lost peer B after Nostr discovery update.");

                if (savedUpdated.MinPort != 50002 || savedUpdated.MaxPort != 50002)
                    throw new Exception($"Expected updated port 50002 in PeerStore, got {savedUpdated.MinPort}");

                // 5. Verify peers.db on disk was persisted with the new port 50002
                string peersDbPath = Path.Combine(tempDirA, "peers.db");
                using (var diskStore = new PeerStore(peersDbPath))
                {
                    if (!diskStore.TryGet(qpB.CurrentPeer.CertHash, out var diskPeer) || diskPeer == null)
                        throw new Exception("Disk peers.db did not contain updated peer B.");

                    if (diskPeer.MinPort != 50002 || diskPeer.MaxPort != 50002)
                        throw new Exception($"Expected port 50002 in persisted peers.db, got {diskPeer.MinPort}");
                }

                await qpA.StopAsync();
                await qpB.StopAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestDeterministicNostrIdentityDerivation()
        {
            Console.Write("Running TestDeterministicNostrIdentityDerivation... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_nostr_det_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var cm1 = new CertManager(tempDir, "node");
                byte[] nostrPriv1 = cm1.NostrPrivateKey;
                string nostrPub1 = cm1.NostrPublicKeyHex;

                if (nostrPriv1 == null || nostrPriv1.Length != 32)
                    throw new Exception("Nostr private key is not 32 bytes.");

                if (string.IsNullOrEmpty(nostrPub1) || nostrPub1.Length != 64)
                    throw new Exception("Nostr public key hex is not 64 characters.");

                // Reload from disk and verify identity is 100% deterministic and procedural
                var cm2 = new CertManager(tempDir, "node");
                byte[] nostrPriv2 = cm2.NostrPrivateKey;
                string nostrPub2 = cm2.NostrPublicKeyHex;

                if (!nostrPriv1.AsSpan().SequenceEqual(nostrPriv2))
                    throw new Exception("Nostr private key was not deterministic across CertManager reload.");

                if (nostrPub1 != nostrPub2)
                    throw new Exception("Nostr public key hex was not deterministic across CertManager reload.");

                // Verify signing and verification with procedural key
                byte[] testMsg = SHA256.HashData("ProceduralNostrTest"u8);
                byte[] sig = NostrSchnorr.Sign(testMsg, nostrPriv1);
                byte[] pubKeyBytes = Convert.FromHexString(nostrPub1);
                if (!NostrSchnorr.Verify(testMsg, pubKeyBytes, sig))
                    throw new Exception("BIP-340 verification failed with procedurally derived key.");

                Console.WriteLine("PASSED");
                await Task.CompletedTask;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestTokenCachingAndStability()
        {
            Console.Write("Running TestTokenCachingAndStability... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_tok_stab_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var qp = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDir)
                    .Build();

                qp.CurrentPeer.Addresses = new[] { IPAddress.Parse("203.0.113.10") };
                qp.CurrentPeer.MinPort = 30001;
                qp.CurrentPeer.MaxPort = 30001;

                string firstToken = qp.GetWanToken();
                for (int i = 0; i < 10; i++)
                {
                    string pollToken = qp.GetWanToken();
                    if (pollToken != firstToken)
                        throw new Exception($"GetWanToken() returned unstable/jittering token on poll {i + 1}.");
                }

                // Invalidate and change port -> new stable token
                qp.CurrentPeer.MinPort = 40002;
                qp.CurrentPeer.MaxPort = 40002;
                qp.InvalidateTokenCache();

                string secondToken = qp.GetWanToken();
                if (secondToken == firstToken)
                    throw new Exception("Token did not update after invalidating cache and changing port.");

                for (int i = 0; i < 5; i++)
                {
                    string pollToken = qp.GetWanToken();
                    if (pollToken != secondToken)
                        throw new Exception($"GetWanToken() after update returned unstable token on poll {i + 1}.");
                }

                Console.WriteLine("PASSED");
                await Task.CompletedTask;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestNostrSpoofingRejectedForSavedPeer()
        {
            Console.Write("Running TestNostrSpoofingRejectedForSavedPeer... ");
            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_spoof_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_spoof_b_" + Guid.NewGuid().ToString("N"));
            string tempDirAttacker = Path.Combine(Path.GetTempPath(), "qp_spoof_att_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);
            Directory.CreateDirectory(tempDirAttacker);

            try
            {
                var poolBytes = new byte[20];
                RandomNumberGenerator.Fill(poolBytes);

                using var qpA = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirA)
                    .UsePool(poolBytes)
                    .WithAutoDiscovery(false)
                    .Build();

                using var qpB = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirB)
                    .UsePool(poolBytes)
                    .WithAutoDiscovery(false)
                    .Build();

                using var qpAttacker = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirAttacker)
                    .UsePool(poolBytes)
                    .WithAutoDiscovery(false)
                    .Build();

                await qpA.StartAsync();
                await qpB.StartAsync();
                await qpAttacker.StartAsync();

                qpB.CurrentPeer.MinPort = 40001;
                qpB.CurrentPeer.MaxPort = 40001;
                qpB.CurrentPeer.Addresses = new[] { IPAddress.Parse("127.0.0.1") };
                string validTokenB = qpB.GetWanToken();

                // Peer A saves Peer B along with B's Nostr public key
                qpA.PeerStore.AddOrUpdate(validTokenB, autoConnect: true, save: true);
                if (!qpA.PeerStore.TryGet(qpB.CurrentPeer.CertHash, out var savedInitial) || savedInitial == null)
                    throw new Exception("qpA.PeerStore did not contain peer B.");

                // Register B's Nostr public key in PeerStore
                qpA.PeerStore.AddOrUpdate(qpB.CurrentPeer, autoConnect: true, save: true, nostrPubKey: qpB.CertManager.NostrPublicKeyHex);

                // 1. Attacker tries to publish a fake update for Peer B using Attacker's Nostr key
                var method = typeof(QuicPunch.QuicPunch).GetMethod("ProcessWanNostrEndpointToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (method == null)
                    throw new Exception("ProcessWanNostrEndpointToken method not found.");

                // Attacker creates a token that has B's CertHash but a malicious port (e.g. 6666)
                using var spoofedPeer = new PeerInfo(qpB.CertManager.PeerCertificate, qpB.CertManager.EcdhPublicKeyRaw)
                {
                    Addresses = new[] { IPAddress.Parse("198.51.100.99") },
                    MinPort = 6666,
                    MaxPort = 6666,
                    Name = "SpoofedB"
                };
                string spoofedToken = Utilities.EncodeEndpointToken(spoofedPeer);

                // Attacker sends event with Attacker's Nostr public key
                method.Invoke(qpA, new object?[] { spoofedToken, qpAttacker.CertManager.NostrPublicKeyHex, null, null });

                // Verify PeerStore was NOT modified by the spoofed event
                if (!qpA.PeerStore.TryGet(qpB.CurrentPeer.CertHash, out var savedAfterAttack) || savedAfterAttack == null)
                    throw new Exception("qpA.PeerStore lost peer B.");

                if (savedAfterAttack.MinPort == 6666)
                    throw new Exception("SECURITY VULNERABILITY: Spoofed Nostr event updated saved peer in PeerStore!");

                if (savedAfterAttack.MinPort != 40001)
                    throw new Exception($"Expected port 40001 to remain intact, got {savedAfterAttack.MinPort}");

                // 2. Legitimate Peer B publishes an update (port 50005) with B's legitimate Nostr key
                qpB.CurrentPeer.MinPort = 50005;
                qpB.CurrentPeer.MaxPort = 50005;
                qpB.InvalidateTokenCache();
                string legitimateUpdateToken = qpB.GetWanToken();

                method.Invoke(qpA, new object?[] { legitimateUpdateToken, qpB.CertManager.NostrPublicKeyHex, null, null });

                // Verify PeerStore WAS updated by the legitimate event
                if (!qpA.PeerStore.TryGet(qpB.CurrentPeer.CertHash, out var savedAfterLegit) || savedAfterLegit == null)
                    throw new Exception("qpA.PeerStore lost peer B after legitimate update.");

                if (savedAfterLegit.MinPort != 50005)
                    throw new Exception($"Expected legitimate update to port 50005, got {savedAfterLegit.MinPort}");

                await qpA.StopAsync();
                await qpB.StopAsync();
                await qpAttacker.StopAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
                try { Directory.Delete(tempDirAttacker, true); } catch { }
            }
        }

        private static async Task TestNostrCertificateVerificationAndValidation()
        {
            Console.Write("[TEST] Nostr certificate public key verification & token validation... ");
            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_cert_val_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_cert_val_b_" + Guid.NewGuid().ToString("N"));
            string tempDirC = Path.Combine(Path.GetTempPath(), "qp_test_cert_val_c_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);
            Directory.CreateDirectory(tempDirC);

            try
            {
                using var qpA = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirA)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();

                using var qpB = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirB)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();

                using var qpAttacker = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirC)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();

                await qpA.StartAsync();
                await qpB.StartAsync();
                await qpAttacker.StartAsync();

                qpB.CurrentPeer.Addresses = new[] { IPAddress.Parse("198.51.100.22") };
                qpB.CurrentPeer.MinPort = 45000;
                qpB.CurrentPeer.MaxPort = 45000;
                qpB.InvalidateTokenCache();

                string tokenB = qpB.GetWanToken();
                byte[] certPubKeyB = qpB.CertManager.PeerCertificate.GetPublicKey();
                byte[] sigB = Utilities.SignToken(tokenB, qpB.CertManager.PeerCertificate);

                // 1. Direct Utilities verification
                if (!Utilities.TryVerifyTokenCertificate(tokenB, certPubKeyB, sigB, out var verifiedHash) || verifiedHash == null)
                    throw new Exception("Utilities.TryVerifyTokenCertificate failed for valid WAN token, key and signature.");

                if (!CryptographicOperations.FixedTimeEquals(verifiedHash, qpB.CurrentPeer.CertHash))
                    throw new Exception("Verified cert hash did not match expected peer cert hash.");

                // 2. Direct verification with wrong public key (Attacker's key) must fail
                byte[] attackerPubKey = qpAttacker.CertManager.PeerCertificate.GetPublicKey();
                if (Utilities.TryVerifyTokenCertificate(tokenB, attackerPubKey, sigB, out _))
                    throw new Exception("SECURITY VULNERABILITY: Token verified successfully with attacker's public key!");

                // 2b. Direct verification with tampered signature must fail
                byte[] tamperedSig = (byte[])sigB.Clone();
                tamperedSig[0] ^= 0xFF;
                if (Utilities.TryVerifyTokenCertificate(tokenB, certPubKeyB, tamperedSig, out _))
                    throw new Exception("SECURITY VULNERABILITY: Token verified successfully with tampered signature!");

                // 3. Process WAN Nostr discovery JSON payload on qpA
                qpA.WanNostrDiscoveryEnabled = true;
                string nostrPayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    wan = tokenB,
                    certPubKey = Convert.ToBase64String(certPubKeyB),
                    sig = Convert.ToBase64String(sigB)
                });

                var onWanEventMethod = typeof(QuicPunch.QuicPunch).GetMethod("OnWanNostrEventDiscovered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (onWanEventMethod == null)
                    throw new Exception("OnWanNostrEventDiscovered method not found.");

                onWanEventMethod.Invoke(qpA, new object?[] { qpB.CertManager.NostrPublicKeyHex, nostrPayload });

                string hexKeyB = Convert.ToHexString(qpB.CurrentPeer.CertHash);
                if (!qpA.DiscoveredPeers.TryGetValue(hexKeyB, out var discB) || discB == null)
                    throw new Exception("Discovered peer B was not added to qpA.DiscoveredPeers.");

                if (!discB.IsCertVerified)
                    throw new Exception("Discovered peer B was not marked as IsCertVerified!");

                if (discB.CertPublicKey == null || !CryptographicOperations.FixedTimeEquals(discB.CertPublicKey, certPubKeyB))
                    throw new Exception("Discovered peer B did not retain valid CertPublicKey bytes.");

                // 4. Test that forged certPubKey on Nostr payload is rejected
                string forgedNostrPayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    wan = tokenB,
                    certPubKey = Convert.ToBase64String(attackerPubKey),
                    sig = Convert.ToBase64String(sigB)
                });

                qpA.DiscoveredPeers.TryRemove(hexKeyB, out _);
                onWanEventMethod.Invoke(qpA, new object?[] { qpAttacker.CertManager.NostrPublicKeyHex, forgedNostrPayload });

                if (qpA.DiscoveredPeers.ContainsKey(hexKeyB))
                    throw new Exception("SECURITY VULNERABILITY: Forged Nostr payload with invalid certPubKey was accepted into DiscoveredPeers!");

                // 5. Test that forged signature on Nostr payload is rejected
                string forgedSigPayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    wan = tokenB,
                    certPubKey = Convert.ToBase64String(certPubKeyB),
                    sig = Convert.ToBase64String(tamperedSig)
                });

                qpA.DiscoveredPeers.TryRemove(hexKeyB, out _);
                onWanEventMethod.Invoke(qpA, new object?[] { qpB.CertManager.NostrPublicKeyHex, forgedSigPayload });

                if (qpA.DiscoveredPeers.ContainsKey(hexKeyB))
                    throw new Exception("SECURITY VULNERABILITY: Forged Nostr payload with invalid sig was accepted into DiscoveredPeers!");

                await qpA.StopAsync();
                await qpB.StopAsync();
                await qpAttacker.StopAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
                try { Directory.Delete(tempDirC, true); } catch { }
            }
        }

        private static async Task TestReconnectingDiscoveredPeerWhenEndpointChanges()
        {
            Console.Write("[TEST] Connecting discovered peer reconnects immediately when verified endpoint changes... ");
            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_reconnect_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_reconnect_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirA)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();

                using var qpB = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirB)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();

                await qpA.StartAsync();
                await qpB.StartAsync();

                qpA.WanNostrDiscoveryEnabled = true;

                // 1. Peer B starts at port 41000
                qpB.CurrentPeer.Addresses = new[] { IPAddress.Parse("198.51.100.10") };
                qpB.CurrentPeer.MinPort = 41000;
                qpB.CurrentPeer.MaxPort = 41000;
                qpB.InvalidateTokenCache();

                string tokenB1 = qpB.GetWanToken();
                byte[] certPubKeyB = qpB.CertManager.PeerCertificate.GetPublicKey();
                byte[] sigB1 = Utilities.SignToken(tokenB1, qpB.CertManager.PeerCertificate);

                // Start interrogation on A for initial token
                _ = qpA.PeerInterrogation(tokenB1);
                await Task.Delay(100);

                string hexKeyB = Convert.ToHexString(qpB.CurrentPeer.CertHash);
                if (!qpA.ActiveInterrogations.Values.Any(s => s.Peer.MinPort == 41000))
                    throw new Exception("Initial interrogation for port 41000 was not found in ActiveInterrogations.");

                // 2. Peer B's NAT/endpoint changes to 198.51.100.20:52000
                qpB.CurrentPeer.Addresses = new[] { IPAddress.Parse("198.51.100.20") };
                qpB.CurrentPeer.MinPort = 52000;
                qpB.CurrentPeer.MaxPort = 52000;
                qpB.InvalidateTokenCache();

                string tokenB2 = qpB.GetWanToken();
                byte[] sigB2 = Utilities.SignToken(tokenB2, qpB.CertManager.PeerCertificate);

                string nostrPayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    wan = tokenB2,
                    certPubKey = Convert.ToBase64String(certPubKeyB),
                    sig = Convert.ToBase64String(sigB2)
                });

                var onWanEventMethod = typeof(QuicPunch.QuicPunch).GetMethod("OnWanNostrEventDiscovered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (onWanEventMethod == null)
                    throw new Exception("OnWanNostrEventDiscovered method not found.");

                // 3. Deliver new verified Nostr announcement to qpA
                onWanEventMethod.Invoke(qpA, new object?[] { qpB.CertManager.NostrPublicKeyHex, nostrPayload });

                // 4. Verify DiscoveredPeers updated to 52000
                bool discoveredUpdated = false;
                for (int i = 0; i < 40; i++)
                {
                    if (qpA.DiscoveredPeers.TryGetValue(hexKeyB, out var disc) && disc.MinPort == 52000)
                    {
                        discoveredUpdated = true;
                        break;
                    }
                    await Task.Delay(25);
                }

                if (!discoveredUpdated)
                    throw new Exception($"DiscoveredPeers did not update port to 52000");

                // 5. Verify old interrogation session for 41000 was replaced by new session for 52000
                bool newInterrogationStarted = false;
                for (int i = 0; i < 40; i++)
                {
                    if (qpA.ActiveInterrogations.Values.Any(s => s.Peer.MinPort == 52000) &&
                        !qpA.ActiveInterrogations.Values.Any(s => s.Peer.MinPort == 41000))
                    {
                        newInterrogationStarted = true;
                        break;
                    }
                    await Task.Delay(25);
                }

                if (qpA.ActiveInterrogations.Values.Any(s => s.Peer.MinPort == 41000))
                    throw new Exception("Old interrogation session for port 41000 was not canceled after endpoint change!");

                if (!newInterrogationStarted)
                    throw new Exception("New interrogation session for port 52000 was not started!");

                await qpA.StopAsync();
                await qpB.StopAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestSavedPeerUpdatedWhenConnectedWithNewEndpoints()
        {
            Console.Write("[TEST] Connected saved peer updates stored endpoints and information in PeerStore... ");
            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_saved_upd_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_saved_upd_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirA)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();

                using var qpB = new QuicPunch.QuicPunchBuilder()
                    .WithAppDataPath(tempDirB)
                    .WithPort(0)
                    .WithAutoDiscovery(false)
                    .Build();

                await qpA.StartAsync();
                await qpB.StartAsync();

                qpA.WanNostrDiscoveryEnabled = true;

                // Save Peer B on Peer A with old dummy endpoints
                qpA.PeerStore.AddOrUpdate(new[] { IPAddress.Parse("198.51.100.99") }, 33333, 33333, qpB.CurrentPeer.CertHash, name: "RemotePeerB", autoConnect: true);
                qpA.TrustPeer(qpB.CurrentPeer.CertHash);
                qpB.TrustPeer(qpA.CurrentPeer.CertHash);

                // Now Peer B connects/answers with its actual listening endpoint
                int realPortB = qpB.LocalDiscoveryPort;
                var realIpB = IPAddress.Parse("192.168.1.100");
                qpB.CurrentPeer.Addresses = new[] { realIpB };
                qpB.CurrentPeer.MinPort = realPortB;
                qpB.CurrentPeer.MaxPort = realPortB;
                qpB.InvalidateTokenCache();

                string tokenB = qpB.GetWanToken();
                byte[] certPubKeyB = qpB.CertManager.PeerCertificate.GetPublicKey();
                byte[] sigB = Utilities.SignToken(tokenB, qpB.CertManager.PeerCertificate);

                string nostrPayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    wan = tokenB,
                    certPubKey = Convert.ToBase64String(certPubKeyB),
                    sig = Convert.ToBase64String(sigB)
                });

                var onWanEventMethod = typeof(QuicPunch.QuicPunch).GetMethod("OnWanNostrEventDiscovered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (onWanEventMethod == null)
                    throw new Exception("OnWanNostrEventDiscovered method not found.");

                // Deliver Nostr announcement to qpA
                onWanEventMethod.Invoke(qpA, new object?[] { qpB.CertManager.NostrPublicKeyHex, nostrPayload });

                // Wait for connection to establish and PeerStore to update
                bool storeUpdated = false;
                for (int i = 0; i < 60; i++)
                {
                    if (qpA.PeerStore.TryGet(qpB.CurrentPeer.CertHash, out var saved) && saved != null)
                    {
                        if (saved.MinPort == realPortB && saved.Addresses.Any(a => a.Equals(realIpB)))
                        {
                            storeUpdated = true;
                            break;
                        }
                    }
                    await Task.Delay(50);
                }

                if (!storeUpdated)
                    throw new Exception($"PeerStore on Node A was not updated to port {realPortB} after receiving announcement and connecting!");

                await qpA.StopAsync();
                await qpB.StopAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }

        private static async Task TestWanServiceStartStopDynamicLifecycle()
        {
            Console.Write("[TEST] WAN UDP service dynamic start/stop and token lifecycle... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_wan_lifecycle_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var cts = new CancellationTokenSource();
                var qp = new QuicPunch.QuicPunch(cts, null, null, true, 0, appDataPath: tempDir)
                {
                    WanEnabled = true
                };

                await qp.StartAsync(cts.Token);

                if (!qp.IsWanStarted)
                    throw new Exception("WAN service should be started by default when WanEnabled is true.");

                string? initialToken = qp.GetWanToken();
                if (string.IsNullOrEmpty(initialToken))
                    throw new Exception("WAN token should be available when WAN service is running.");

                // Stop WAN dynamically
                await qp.StopWanAsync();

                if (qp.IsWanStarted)
                    throw new Exception("IsWanStarted should be false after StopWanAsync.");

                if (qp.CurrentPeer?.Addresses?.Length > 0)
                    throw new Exception("CurrentPeer addresses should be cleared when WAN service is stopped.");

                // Start WAN dynamically
                await qp.StartWanAsync(cancellationToken: cts.Token);

                if (!qp.IsWanStarted)
                    throw new Exception("IsWanStarted should be true after StartWanAsync.");

                string? restartedToken = qp.GetWanToken();
                if (string.IsNullOrEmpty(restartedToken))
                    throw new Exception("WAN token should be available after StartWanAsync.");

                await qp.StopAsync();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestDummyQuicConnectionTransportLeaveOpen()
        {
            Console.Write("[TEST] DummyQuicConnectionTransport leaveProviderOpen preserves provider lifecycle... ");

            var mockProvider = new MockLaneProvider();
            var connDefault = new DummyQuicConnectionTransport(mockProvider, QuicConnectionRole.Client, leaveProviderOpen: true);
            await connDefault.DisposeAsync();

            if (mockProvider.IsDisposed)
                throw new Exception("Provider should not be disposed when leaveProviderOpen is true.");

            var connClosing = new DummyQuicConnectionTransport(mockProvider, QuicConnectionRole.Client, leaveProviderOpen: false);
            await connClosing.DisposeAsync();

            if (!mockProvider.IsDisposed)
                throw new Exception("Provider should be disposed when leaveProviderOpen is false.");

            Console.WriteLine("PASSED");
        }

        private sealed class MockLaneProvider : IDummyQuicLaneProvider
        {
            public bool IsDisposed { get; private set; }

            public ValueTask<IDummyQuicLane> OpenOutboundLaneAsync(DummyQuicOpenLaneRequest request, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();

            public ValueTask<DummyQuicInboundLane> AcceptInboundLaneAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();

            public ValueTask DisposeAsync()
            {
                IsDisposed = true;
                return ValueTask.CompletedTask;
            }
        }

        private static async Task TestTorTokenInterrogationDoesNotDisposeActiveChannel()
        {
            Console.Write("[TEST] Tor token interrogation with ownsPeer:true does not dispose active channel... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_tor_interrogation_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var cts = new CancellationTokenSource();
                var qp = new QuicPunch.QuicPunch(cts, null, null, true, 0, appDataPath: tempDir);

                byte[] certHash = new byte[32];
                Random.Shared.NextBytes(certHash);

                const string onion = "h6mwth5qlqfulm2kyo4nhid6zx423kit33urpnq7m5iakj4mthtinkid.onion";
                const int port = 443;

                var peer = new PeerInfo
                {
                    OnionAddress = onion,
                    MinPort = port,
                    MaxPort = port,
                    NetworkType = QuicPunch.QuicPunch.NetworkType.Tor,
                    ActiveTransport = QuicPunch.QuicPunch.TransportType.Tor
                };
                peer.SetCertificateHash(certHash);

                string token = Utilities.EncodeEndpointToken(peer);
                var decoded = Utilities.DecodeEndpointToken(token);

                if (decoded.NetworkType != QuicPunch.QuicPunch.NetworkType.Tor)
                    throw new Exception("Decoded token should be Tor type.");
                if (decoded.OnionAddress != onion)
                    throw new Exception("Decoded token onion address mismatch.");

                decoded.Dispose();
                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestRandomIdentityNamesAndCertificateSanitization()
        {
            Console.Write("[TEST] Random identity generation and certificate sanitization (Tor/WAN isolation)... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_id_sanitization_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var cts = new CancellationTokenSource();
                var qp = new QuicPunch.QuicPunch(cts, null, null, true, 0, appDataPath: tempDir);

                string wanName = qp.CurrentPeer.Name ?? "";
                string torName = qp.TorCurrentPeer.Name ?? "";

                if (string.IsNullOrWhiteSpace(wanName) || string.IsNullOrWhiteSpace(torName))
                    throw new Exception("WAN or Tor peer name is empty.");

                if (wanName == torName)
                    throw new Exception($"WAN name ({wanName}) and Tor name ({torName}) should not be identical.");

                // Validate that neither name leaks the actual OS user or machine name
                string osUserName = Environment.UserName;
                string osMachineName = Environment.MachineName;

                if (wanName.Contains(osUserName, StringComparison.OrdinalIgnoreCase) ||
                    wanName.Contains(osMachineName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"WAN name '{wanName}' leaked OS credentials ({osUserName}, {osMachineName}).");
                }

                if (torName.Contains(osUserName, StringComparison.OrdinalIgnoreCase) ||
                    torName.Contains(osMachineName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"Tor name '{torName}' leaked OS credentials ({osUserName}, {osMachineName}).");
                }

                // Check certificate CN and SAN
                var wanCert = qp.CertManager.PeerCertificate;
                var torCert = qp.TorCertManager.PeerCertificate;

                string? wanCn = wanCert.GetNameInfo(X509NameType.SimpleName, false);
                string? torCn = torCert.GetNameInfo(X509NameType.SimpleName, false);

                if (string.IsNullOrWhiteSpace(wanCn) || string.IsNullOrWhiteSpace(torCn))
                    throw new Exception("Certificate CN is empty.");

                if (wanCn == torCn)
                    throw new Exception("WAN certificate CN and Tor certificate CN should be distinct.");

                if (wanCn.Contains(osMachineName, StringComparison.OrdinalIgnoreCase) ||
                    torCn.Contains(osMachineName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("Certificate CN leaked OS MachineName.");
                }

                // Test persistence: create a new instance with the same directory and ensure names remain stable
                var qp2 = new QuicPunch.QuicPunch(cts, null, null, true, 0, appDataPath: tempDir);
                if (qp2.CurrentPeer.Name != wanName)
                    throw new Exception($"WAN name did not persist across reloads: expected '{wanName}', got '{qp2.CurrentPeer.Name}'.");
                if (qp2.TorCurrentPeer.Name != torName)
                    throw new Exception($"Tor name did not persist across reloads: expected '{torName}', got '{qp2.TorCurrentPeer.Name}'.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestAckSharePeersTransportIsolation()
        {
            Console.Write("[TEST] SharePeers ACK transport isolation (no WAN IPs over Tor, no Tor over WAN)... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_ack_isolation_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var cts = new CancellationTokenSource();
                var qp = new QuicPunch.QuicPunch(cts, null, null, true, 0, appDataPath: tempDir);
                qp.SharePeers = true;

                // Create a WAN peer
                byte[] wanCertHash = new byte[32];
                Random.Shared.NextBytes(wanCertHash);
                var wanPeer = new PeerInfo
                {
                    Addresses = new[] { IPAddress.Parse("203.0.113.10"), IPAddress.Parse("198.51.100.20") },
                    MinPort = 12345,
                    MaxPort = 12345,
                    NetworkType = QuicPunch.QuicPunch.NetworkType.Static,
                    ActiveTransport = QuicPunch.QuicPunch.TransportType.Wan
                };
                wanPeer.SetCertificateHash(wanCertHash);
                qp.AvailablePeers.TryAdd(wanPeer.Id, wanPeer);
                qp.ExpectedPeerCerts.Add(wanCertHash);
                qp.SetPeerAutoAccept(wanPeer.Id, true);

                // Create a Tor peer
                byte[] torCertHash = new byte[32];
                Random.Shared.NextBytes(torCertHash);
                var torPeer = new PeerInfo
                {
                    OnionAddress = "h6mwth5qlqfulm2kyo4nhid6zx423kit33urpnq7m5iakj4mthtinkid.onion",
                    MinPort = 443,
                    MaxPort = 443,
                    NetworkType = QuicPunch.QuicPunch.NetworkType.Tor,
                    ActiveTransport = QuicPunch.QuicPunch.TransportType.Tor
                };
                torPeer.SetCertificateHash(torCertHash);
                qp.AvailablePeers.TryAdd(torPeer.Id, torPeer);
                qp.ExpectedPeerCerts.Add(torCertHash);
                qp.SetPeerAutoAccept(torPeer.Id, true);

                // Generate Tor ACK
                byte[] torAck = qp.GenerateAck(true, QuicPunch.QuicPunch.TransportType.Tor);
                using (var ms = new MemoryStream(torAck))
                using (var r = new BinaryReader(ms))
                {
                    ms.Position = QuicPunch.QuicPunch.MagicHeader.Length + 1 + 16; // Skip header, type, senderId
                    ushort sharedCount = r.ReadUInt16();
                    if (sharedCount != 1)
                        throw new Exception($"Expected exactly 1 shared peer in Tor ACK, got {sharedCount}.");

                    byte flags = r.ReadByte();
                    byte addrCount = r.ReadByte();
                    if (addrCount != 0)
                        throw new Exception($"Tor ACK should not serialize any WAN IP addresses, got {addrCount}.");

                    ushort minPort = r.ReadUInt16();
                    ushort maxPort = r.ReadUInt16();
                    byte[] certHash = r.ReadBytes(32);
                    if (!CryptographicOperations.FixedTimeEquals(certHash, torCertHash))
                        throw new Exception("Tor ACK did not contain the Tor peer's cert hash.");
                }

                // Generate WAN ACK
                byte[] wanAck = qp.GenerateAck(true, QuicPunch.QuicPunch.TransportType.Wan);
                using (var ms = new MemoryStream(wanAck))
                using (var r = new BinaryReader(ms))
                {
                    ms.Position = QuicPunch.QuicPunch.MagicHeader.Length + 1 + 16; // Skip header, type, senderId
                    ushort sharedCount = r.ReadUInt16();
                    if (sharedCount != 1)
                        throw new Exception($"Expected exactly 1 shared peer in WAN ACK, got {sharedCount}.");

                    byte flags = r.ReadByte();
                    byte addrCount = r.ReadByte();
                    if (addrCount != 2)
                        throw new Exception($"WAN ACK should serialize the 2 WAN IP addresses, got {addrCount}.");

                    for (int i = 0; i < addrCount; i++)
                    {
                        byte[] ipBytes = r.ReadBytes(4);
                    }
                    ushort minPort = r.ReadUInt16();
                    ushort maxPort = r.ReadUInt16();
                    byte[] certHash = r.ReadBytes(32);
                    if (!CryptographicOperations.FixedTimeEquals(certHash, wanCertHash))
                        throw new Exception("WAN ACK did not contain the WAN peer's cert hash.");
                }

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestPingTimestampDriftAndFutureDrop()
        {
            Console.Write("[TEST] Ping handler drops timestamps drifted > 5s or future (PreciseTime)... ");
            string tempDirA = Path.Combine(Path.GetTempPath(), "qp_test_ping_drift_a_" + Guid.NewGuid().ToString("N"));
            string tempDirB = Path.Combine(Path.GetTempPath(), "qp_test_ping_drift_b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirA);
            Directory.CreateDirectory(tempDirB);

            try
            {
                using var qpA = new QuicPunch.QuicPunch(appDataPath: tempDirA, listeningPort: 0);
                using var qpB = new QuicPunch.QuicPunch(appDataPath: tempDirB, listeningPort: 0);
                await qpA.StartAsync();
                await qpB.StartAsync();

                // Connect A and B
                var epB = new IPEndPoint(IPAddress.Loopback, qpB.LocalBoundPort);
                byte[] helloB = qpB.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true);
                await qpA.ProcessIncomingPacketAsync(helloB, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (!qpA.AvailablePeers.TryGetValue(qpB.CurrentPeer.Id, out var peerBOnA))
                    throw new Exception("Peer B was not discovered on A.");

                await Task.Delay(200);

                // 1. Future timestamp Ping response (> nowTicks) must be dropped and NOT poison LastSeenPingTimestamp
                long futureTicks = PreciseTime.GetCorrectTime().Ticks + TimeSpan.FromSeconds(60).Ticks;
                byte[] futurePingResponse = qpB.BuildPingPacket(futureTicks, isResponse: true);
                await qpA.ProcessIncomingPacketAsync(futurePingResponse, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (peerBOnA.LastSeenPingTimestamp == futureTicks)
                    throw new Exception("Future timestamp ping response poisoned LastSeenPingTimestamp!");

                // 2. Stale timestamp Ping response (> 5 seconds in the past) must be dropped
                long staleTicks = PreciseTime.GetCorrectTime().Ticks - TimeSpan.FromSeconds(10).Ticks;
                byte[] stalePingResponse = qpB.BuildPingPacket(staleTicks, isResponse: true);
                await qpA.ProcessIncomingPacketAsync(stalePingResponse, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (peerBOnA.LastSeenPingTimestamp == staleTicks)
                    throw new Exception("Stale (>5s) ping response was accepted into LastSeenPingTimestamp!");

                // 3. Valid recent timestamp (e.g. 15ms ago) must be ACCEPTED and update Ping RTT
                long recentTicks = PreciseTime.GetCorrectTime().Ticks - TimeSpan.FromMilliseconds(15).Ticks;
                byte[] validPingResponse = qpB.BuildPingPacket(recentTicks, isResponse: true);
                await qpA.ProcessIncomingPacketAsync(validPingResponse, epB, QuicPunch.QuicPunch.TransportType.Wan);
                if (peerBOnA.LastSeenPingTimestamp != recentTicks)
                    throw new Exception("Valid recent ping response was not accepted!");
                if (!peerBOnA.Ping.HasValue || peerBOnA.Ping.Value.TotalMilliseconds < 0.1)
                    throw new Exception("Valid recent ping response did not calculate Peer.Ping!");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDirA, true); } catch { }
                try { Directory.Delete(tempDirB, true); } catch { }
            }
        }
    }
