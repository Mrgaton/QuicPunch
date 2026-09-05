using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunch.Security;

namespace QuicPunchTests.Tests;
    public static class PeerAccessControllerTests
    {
        public static async Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   PEER ACCESS CONTROLLER (ACL & TRUST) TESTS     ");
            Console.WriteLine("==================================================");

            // Test 1: Trust and Untrust Lifecycle
            Console.Write("[TEST 1] Trust and Untrust lifecycle in memory... ");
            var controller = new PeerAccessController(autoAcceptConnections: false);
            byte[] certHash1 = RandomNumberGenerator.GetBytes(32);
            byte[] certHash2 = RandomNumberGenerator.GetBytes(32);

            if (controller.IsTrusted(certHash1) || controller.IsTrusted(certHash2))
                throw new Exception("Unknown hashes should not be trusted initially.");

            controller.TrustPeer(certHash1);
            if (!controller.IsTrusted(certHash1))
                throw new Exception("certHash1 was not marked as trusted after TrustPeer.");
            if (controller.IsTrusted(certHash2))
                throw new Exception("certHash2 became trusted unexpectedly.");

            controller.UntrustPeer(certHash1);
            if (controller.IsTrusted(certHash1))
                throw new Exception("certHash1 remained trusted after UntrustPeer.");

            Console.WriteLine("PASSED");

            // Test 2: Auto-Accept Policies and Individual Flags
            Console.Write("[TEST 2] Auto-accept policies, trust prerequisites and revocation... ");
            Guid peerId = Guid.NewGuid();
            var peer = new PeerInfo();
            peer.SetCertificateHash(certHash1);

            var controllerWithResolver = new PeerAccessController(
                peerStoreProvider: () => null,
                peerByIdResolver: id => id == peerId ? peer : null,
                peerByCertHashResolver: (byte[] hash, out PeerInfo? p) =>
                {
                    if (CryptographicOperations.FixedTimeEquals(hash, certHash1))
                    {
                        p = peer;
                        return true;
                    }
                    p = null;
                    return false;
                },
                autoAcceptConnections: false,
                autoAcceptUntrusted: false);

            // Cannot auto-accept an untrusted peer
            controllerWithResolver.SetPeerAutoAccept(peerId, true);
            if (controllerWithResolver.IsAutoAccepted(peerId))
                throw new Exception("Untrusted peer was accepted for auto-accept.");

            // Trust the peer, then enable auto-accept
            controllerWithResolver.TrustPeer(certHash1);
            controllerWithResolver.SetPeerAutoAccept(peerId, true);
            if (!controllerWithResolver.IsAutoAccepted(peerId))
                throw new Exception("Trusted peer was not auto-accepted.");
            if (!controllerWithResolver.IsAutoAccepted(certHash1))
                throw new Exception("Auto-accept by certHash was not propagated.");

            // Revoke auto-accept
            controllerWithResolver.SetPeerAutoAccept(peerId, false);
            if (controllerWithResolver.IsAutoAccepted(peerId))
                throw new Exception("Auto-accept was not revoked.");

            Console.WriteLine("PASSED");

            // Test 3: PeerStore Persistence Integration
            Console.Write("[TEST 3] PeerStore saved peers are implicitly trusted... ");
            string tempDir = Path.Combine(Path.GetTempPath(), "qp_acl_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var store = new PeerStore(Path.Combine(tempDir, "peers.db"));
                var savedPeer = new PeerInfo 
                { 
                    Name = "SavedNode",
                    Addresses = new[] { System.Net.IPAddress.Parse("192.168.1.100") },
                    MinPort = 5000,
                    MaxPort = 5000
                };
                savedPeer.SetCertificateHash(certHash2);
                if (!store.AddOrUpdate(savedPeer, autoConnect: false))
                    throw new Exception("Failed to add savedPeer to PeerStore.");

                var controllerWithStore = new PeerAccessController(
                    peerStoreProvider: () => store,
                    peerByIdResolver: _ => null,
                    autoAcceptConnections: false);

                if (!controllerWithStore.IsTrusted(certHash2))
                    throw new Exception("Peer saved in PeerStore was not recognized as trusted.");
                if (controllerWithStore.IsTrusted(certHash1))
                    throw new Exception("Non-saved peer was recognized as trusted without TrustPeer.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }

            // Test 4: Application Guard (EnsureAllowedForApplication)
            Console.Write("[TEST 4] Application guard rejects untrusted and allows trusted/bypassed... ");
            var guardController = new PeerAccessController(autoAcceptUntrusted: false);
            var testPeer = new PeerInfo();
            testPeer.SetCertificateHash(certHash1);

            bool caught = false;
            try
            {
                guardController.EnsureAllowedForApplication(testPeer);
            }
            catch (InvalidOperationException)
            {
                caught = true;
            }
            if (!caught)
                throw new Exception("Application guard failed to throw for untrusted peer.");

            // Explicitly trust
            guardController.TrustPeer(certHash1);
            guardController.EnsureAllowedForApplication(testPeer); // Should not throw

            // With legacy bypass
            var bypassController = new PeerAccessController(autoAcceptUntrusted: true);
            var untrustedPeer = new PeerInfo();
            untrustedPeer.SetCertificateHash(certHash2);
            bypassController.EnsureAllowedForApplication(untrustedPeer); // Should not throw

            Console.WriteLine("PASSED");

            Console.WriteLine("==================================================");
            Console.WriteLine("   ALL PEER ACCESS CONTROLLER TESTS PASSED!       ");
            Console.WriteLine("==================================================");
            await Task.CompletedTask;
        }
    }
