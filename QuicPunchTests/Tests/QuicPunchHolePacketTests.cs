#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using QuicPunch.Helpers;
using QuicPunch.Security;

namespace QuicPunchTests.Tests
{
    public static class QuicPunchHolePacketTests
    {
        public static Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("    QUIC PUNCH HOLE PACKET SECURITY TESTS        ");
            Console.WriteLine("==================================================");

            Test_SuccessfulRoundTrip_WithMatchingPassword();
            Test_SuccessfulRoundTrip_WithoutPassword();
            Test_Rejected_WhenPasswordsDiffer();
            Test_Rejected_WhenSecurityPostureMismatched();
            Test_Rejected_WhenRecipientIdSpoofed();
            Test_Rejected_WhenTamperedHeader();
            Test_Rejected_WhenTimestampDrifted();
            Test_Rejected_WhenNonceReplayed();
            Test_ZeroAlloc_TryWritePacket();

            Console.WriteLine("==================================================");
            Console.WriteLine("    ALL HOLE PUNCH PACKET TESTS PASSED!           ");
            Console.WriteLine("==================================================");
            return Task.CompletedTask;
        }

        private static void Test_SuccessfulRoundTrip_WithMatchingPassword()
        {
            Console.Write("[TEST] Successful roundtrip with matching password... ");

            using var senderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var recipientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var senderId = Guid.NewGuid();
            var recipientId = Guid.NewGuid();

            byte[] senderCertHash = SHA256.HashData(senderKey.ExportSubjectPublicKeyInfo());
            byte[] recipientCertHash = SHA256.HashData(recipientKey.ExportSubjectPublicKeyInfo());

            byte[] passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes("SuperSecretP2PPassphrase"));

            var packet = QuicPunchHolePacket.Create(
                senderId,
                recipientId,
                senderCertHash,
                recipientCertHash,
                senderKey,
                passwordHash);

            byte[] encoded = packet.Encode();
            if (encoded.Length != QuicPunchHolePacket.PacketSize)
                throw new Exception($"Expected packet size {QuicPunchHolePacket.PacketSize}, got {encoded.Length}");

            bool verified = QuicPunchHolePacket.TryVerify(
                encoded,
                expectedRecipientId: recipientId,
                ourCertHash: recipientCertHash,
                senderPublicKey: senderKey,
                localPasswordHash: passwordHash,
                out var parsedPacket);

            if (!verified || parsedPacket == null)
                throw new Exception("Verification failed for valid packet with matching password.");

            if (parsedPacket.SenderId != senderId || parsedPacket.RecipientId != recipientId)
                throw new Exception("Parsed IDs do not match original.");

            if (!parsedPacket.HasPasswordProof)
                throw new Exception("HasPasswordProof should be true.");

            Console.WriteLine("PASSED");
        }

        private static void Test_SuccessfulRoundTrip_WithoutPassword()
        {
            Console.Write("[TEST] Successful roundtrip without password (open mode)... ");

            using var senderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var recipientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var senderId = Guid.NewGuid();
            var recipientId = Guid.NewGuid();

            byte[] senderCertHash = SHA256.HashData(senderKey.ExportSubjectPublicKeyInfo());
            byte[] recipientCertHash = SHA256.HashData(recipientKey.ExportSubjectPublicKeyInfo());

            var packet = QuicPunchHolePacket.Create(
                senderId,
                recipientId,
                senderCertHash,
                recipientCertHash,
                senderKey,
                passwordHash: null);

            byte[] encoded = packet.Encode();

            bool verified = QuicPunchHolePacket.TryVerify(
                encoded,
                expectedRecipientId: recipientId,
                ourCertHash: recipientCertHash,
                senderPublicKey: senderKey,
                localPasswordHash: null,
                out var parsedPacket);

            if (!verified || parsedPacket == null)
                throw new Exception("Verification failed for valid unauthenticated packet.");

            if (parsedPacket.HasPasswordProof)
                throw new Exception("HasPasswordProof should be false.");

            Console.WriteLine("PASSED");
        }

        private static void Test_Rejected_WhenPasswordsDiffer()
        {
            Console.Write("[TEST] Rejection when peer passwords do not match... ");

            using var senderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var recipientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var senderId = Guid.NewGuid();
            var recipientId = Guid.NewGuid();

            byte[] senderCertHash = SHA256.HashData(senderKey.ExportSubjectPublicKeyInfo());
            byte[] recipientCertHash = SHA256.HashData(recipientKey.ExportSubjectPublicKeyInfo());

            byte[] senderPassword = SHA256.HashData(Encoding.UTF8.GetBytes("Password123"));
            byte[] recipientPassword = SHA256.HashData(Encoding.UTF8.GetBytes("WrongPassword999"));

            var packet = QuicPunchHolePacket.Create(
                senderId,
                recipientId,
                senderCertHash,
                recipientCertHash,
                senderKey,
                senderPassword);

            byte[] encoded = packet.Encode();

            bool verified = QuicPunchHolePacket.TryVerify(
                encoded,
                expectedRecipientId: recipientId,
                ourCertHash: recipientCertHash,
                senderPublicKey: senderKey,
                localPasswordHash: recipientPassword,
                out _);

            if (verified)
                throw new Exception("Expected verification to FAIL for differing passwords, but it SUCCEEDED!");

            Console.WriteLine("PASSED");
        }

        private static void Test_Rejected_WhenSecurityPostureMismatched()
        {
            Console.Write("[TEST] Rejection when password requirement differs (one expects, one doesn't)... ");

            using var senderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var recipientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var senderId = Guid.NewGuid();
            var recipientId = Guid.NewGuid();

            byte[] senderCertHash = SHA256.HashData(senderKey.ExportSubjectPublicKeyInfo());
            byte[] recipientCertHash = SHA256.HashData(recipientKey.ExportSubjectPublicKeyInfo());

            byte[] password = SHA256.HashData(Encoding.UTF8.GetBytes("SharedPassword"));

            // Case A: Sender has password, recipient does not expect one
            var packetWithPassword = QuicPunchHolePacket.Create(
                senderId, recipientId, senderCertHash, recipientCertHash, senderKey, password);

            if (QuicPunchHolePacket.TryVerify(packetWithPassword.Encode(), recipientId, recipientCertHash, senderKey, null, out _))
                throw new Exception("Case A should have failed.");

            // Case B: Sender has no password, recipient expects one
            var packetWithoutPassword = QuicPunchHolePacket.Create(
                senderId, recipientId, senderCertHash, recipientCertHash, senderKey, null);

            if (QuicPunchHolePacket.TryVerify(packetWithoutPassword.Encode(), recipientId, recipientCertHash, senderKey, password, out _))
                throw new Exception("Case B should have failed.");

            Console.WriteLine("PASSED");
        }

        private static void Test_Rejected_WhenRecipientIdSpoofed()
        {
            Console.Write("[TEST] Rejection when packet was intended for a different recipient GUID... ");

            using var senderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var recipientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var senderId = Guid.NewGuid();
            var intendedRecipientId = Guid.NewGuid();
            var thirdPartyRecipientId = Guid.NewGuid();

            byte[] senderCertHash = SHA256.HashData(senderKey.ExportSubjectPublicKeyInfo());
            byte[] recipientCertHash = SHA256.HashData(recipientKey.ExportSubjectPublicKeyInfo());

            var packet = QuicPunchHolePacket.Create(
                senderId, intendedRecipientId, senderCertHash, recipientCertHash, senderKey, null);

            bool verified = QuicPunchHolePacket.TryVerify(
                packet.Encode(),
                expectedRecipientId: thirdPartyRecipientId,
                ourCertHash: recipientCertHash,
                senderPublicKey: senderKey,
                localPasswordHash: null,
                out _);

            if (verified)
                throw new Exception("Packet should be rejected when recipient GUID does not match local ID.");

            Console.WriteLine("PASSED");
        }

        private static void Test_Rejected_WhenTamperedHeader()
        {
            Console.Write("[TEST] Rejection when header byte is tampered (invalidating ECDSA signature)... ");

            using var senderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var recipientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var senderId = Guid.NewGuid();
            var recipientId = Guid.NewGuid();

            byte[] senderCertHash = SHA256.HashData(senderKey.ExportSubjectPublicKeyInfo());
            byte[] recipientCertHash = SHA256.HashData(recipientKey.ExportSubjectPublicKeyInfo());

            var packet = QuicPunchHolePacket.Create(
                senderId, recipientId, senderCertHash, recipientCertHash, senderKey, null);

            byte[] encoded = packet.Encode();
            encoded[10] ^= 0xFF; // Flip bits in SenderId

            bool verified = QuicPunchHolePacket.TryVerify(
                encoded,
                expectedRecipientId: recipientId,
                ourCertHash: recipientCertHash,
                senderPublicKey: senderKey,
                localPasswordHash: null,
                out _);

            if (verified)
                throw new Exception("Packet should be rejected when header is tampered.");

            Console.WriteLine("PASSED");
        }

        private static void Test_Rejected_WhenTimestampDrifted()
        {
            Console.Write("[TEST] Rejection when timestamp drifts beyond allowed window (replay defense)... ");

            using var senderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var recipientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var senderId = Guid.NewGuid();
            var recipientId = Guid.NewGuid();

            byte[] senderCertHash = SHA256.HashData(senderKey.ExportSubjectPublicKeyInfo());
            byte[] recipientCertHash = SHA256.HashData(recipientKey.ExportSubjectPublicKeyInfo());

            long staleTime = PreciseTime.GetCorrectTime().Ticks - TimeSpan.FromSeconds(15).Ticks;

            var packet = QuicPunchHolePacket.Create(
                senderId,
                recipientId,
                senderCertHash,
                recipientCertHash,
                senderKey,
                passwordHash: null,
                overrideTicks: staleTime);

            bool verified = QuicPunchHolePacket.TryVerify(
                packet.Encode(),
                expectedRecipientId: recipientId,
                ourCertHash: recipientCertHash,
                senderPublicKey: senderKey,
                localPasswordHash: null,
                out _,
                maxTimestampDrift: TimeSpan.FromSeconds(5));

            if (verified)
                throw new Exception("Stale packet should be rejected.");

            Console.WriteLine("PASSED");
        }

        private static void Test_Rejected_WhenNonceReplayed()
        {
            Console.Write("[TEST] Rejection when nonce is replayed within valid window... ");

            QuicPunchHolePacket.ClearNonceCache();

            using var senderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var recipientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var senderId = Guid.NewGuid();
            var recipientId = Guid.NewGuid();

            byte[] senderCertHash = SHA256.HashData(senderKey.ExportSubjectPublicKeyInfo());
            byte[] recipientCertHash = SHA256.HashData(recipientKey.ExportSubjectPublicKeyInfo());

            var packet = QuicPunchHolePacket.Create(
                senderId,
                recipientId,
                senderCertHash,
                recipientCertHash,
                senderKey,
                passwordHash: null);

            byte[] encoded = packet.Encode();

            // First attempt must succeed
            bool firstAttempt = QuicPunchHolePacket.TryVerify(
                encoded,
                expectedRecipientId: recipientId,
                ourCertHash: recipientCertHash,
                senderPublicKey: senderKey,
                localPasswordHash: null,
                out var parsed1,
                checkNonceCache: true);

            if (!firstAttempt || parsed1 == null)
                throw new Exception("First attempt with new nonce should succeed.");

            // Immediate replay of exact same packet must be rejected!
            bool replayedAttempt = QuicPunchHolePacket.TryVerify(
                encoded,
                expectedRecipientId: recipientId,
                ourCertHash: recipientCertHash,
                senderPublicKey: senderKey,
                localPasswordHash: null,
                out _,
                checkNonceCache: true);

            if (replayedAttempt)
                throw new Exception("Replayed packet with duplicate nonce MUST be rejected by NonceCache!");

            Console.WriteLine("PASSED");
        }

        private static void Test_ZeroAlloc_TryWritePacket()
        {
            Console.Write("[TEST] Zero-allocation TryWritePacket and TryVerifyFast... ");

            QuicPunchHolePacket.ClearNonceCache();

            using var senderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var recipientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var senderId = Guid.NewGuid();
            var recipientId = Guid.NewGuid();

            byte[] senderCertHash = SHA256.HashData(senderKey.ExportSubjectPublicKeyInfo());
            byte[] recipientCertHash = SHA256.HashData(recipientKey.ExportSubjectPublicKeyInfo());
            byte[] passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes("SecretToken1234"));

            Span<byte> buffer = stackalloc byte[QuicPunchHolePacket.PacketSize];
            bool written = QuicPunchHolePacket.TryWritePacket(
                buffer,
                senderId,
                recipientId,
                senderCertHash,
                recipientCertHash,
                senderKey,
                passwordHash);

            if (!written)
                throw new Exception("TryWritePacket failed.");

            // Verify with TryVerifyFast (zero heap allocations)
            bool fastVerified = QuicPunchHolePacket.TryVerifyFast(
                buffer,
                expectedRecipientId: recipientId,
                ourCertHash: recipientCertHash,
                senderPublicKey: senderKey,
                localPasswordHash: passwordHash);

            if (!fastVerified)
                throw new Exception("TryVerifyFast failed to verify valid packet.");

            Console.WriteLine("PASSED");
        }
    }
}
