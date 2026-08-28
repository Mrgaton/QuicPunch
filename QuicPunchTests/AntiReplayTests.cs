using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using QuicPunch.Helpers;

namespace QuicPunchTests
{
    public static class AntiReplayTests
    {
        public static async Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   ANTI-REPLAY SLIDING WINDOW (RFC 6479) TESTS   ");
            Console.WriteLine("==================================================");

            TestSequentialPackets();
            TestDuplicateReplay();
            TestOutOfOrderArrival();
            TestWindowBoundariesAndOldPackets();
            TestZeroSequence();
            TestWindowReset();
            await TestConcurrencyAsync();
            TestCryptographicAadBinding();

            Console.WriteLine("==================================================");
            Console.WriteLine("   ALL ANTI-REPLAY TESTS PASSED SUCCESSFULLY!   ");
            Console.WriteLine("==================================================");
        }

        private static void TestSequentialPackets()
        {
            Console.Write("[TEST] In-order sequential packets (1..500)... ");
            var window = new AntiReplayWindow();

            for (ulong i = 1; i <= 500; i++)
            {
                if (!window.Check(i))
                    throw new Exception($"Check failed unexpectedly for sequence {i}");

                if (!window.CheckAndAdd(i))
                    throw new Exception($"CheckAndAdd failed unexpectedly for sequence {i}");

                if (window.LastSequence != i)
                    throw new Exception($"LastSequence mismatch: expected {i}, got {window.LastSequence}");
            }
            Console.WriteLine("PASSED");
        }

        private static void TestDuplicateReplay()
        {
            Console.Write("[TEST] Duplicate & replay rejection... ");
            var window = new AntiReplayWindow();

            if (!window.CheckAndAdd(1)) throw new Exception("Failed to add 1");
            if (!window.CheckAndAdd(2)) throw new Exception("Failed to add 2");
            if (!window.CheckAndAdd(3)) throw new Exception("Failed to add 3");

            if (window.Check(2)) throw new Exception("Check allowed replayed packet 2");
            if (window.CheckAndAdd(2)) throw new Exception("CheckAndAdd allowed replayed packet 2");

            if (window.Check(3)) throw new Exception("Check allowed replayed packet 3");
            if (window.CheckAndAdd(3)) throw new Exception("CheckAndAdd allowed replayed packet 3");

            if (window.Check(1)) throw new Exception("Check allowed replayed packet 1");
            if (window.CheckAndAdd(1)) throw new Exception("CheckAndAdd allowed replayed packet 1");

            Console.WriteLine("PASSED");
        }

        private static void TestOutOfOrderArrival()
        {
            Console.Write("[TEST] Out-of-order packets within 128-bit window... ");
            var window = new AntiReplayWindow();

            ulong[] order = { 1, 5, 2, 4, 3 };

            foreach (var seq in order)
            {
                if (!window.Check(seq))
                    throw new Exception($"Check rejected valid out-of-order sequence {seq}");

                if (!window.CheckAndAdd(seq))
                    throw new Exception($"CheckAndAdd rejected valid out-of-order sequence {seq}");
            }

            if (window.LastSequence != 5)
                throw new Exception($"Expected LastSequence=5, got {window.LastSequence}");

            foreach (var seq in order)
            {
                if (window.CheckAndAdd(seq))
                    throw new Exception($"Replay of sequence {seq} was erroneously accepted!");
            }

            Console.WriteLine("PASSED");
        }

        private static void TestWindowBoundariesAndOldPackets()
        {
            Console.Write("[TEST] Window sliding boundaries and stale packet drop... ");
            var window = new AntiReplayWindow();

            if (!window.CheckAndAdd(200)) throw new Exception("Failed to advance window to 200");
            if (window.LastSequence != 200) throw new Exception("LastSequence != 200");

            if (window.Check(72) || window.CheckAndAdd(72))
                throw new Exception("Packet 72 should have been rejected as outside window (diff=128)");

            if (window.Check(50) || window.CheckAndAdd(50))
                throw new Exception("Packet 50 should have been rejected as outside window");

            if (!window.Check(73) || !window.CheckAndAdd(73))
                throw new Exception("Packet 73 should have been accepted at window boundary");

            if (window.CheckAndAdd(73))
                throw new Exception("Packet 73 duplicate should have been rejected");

            if (!window.CheckAndAdd(500)) throw new Exception("Failed to advance to 500");

            if (window.CheckAndAdd(200)) throw new Exception("Packet 200 should be rejected now");

            if (!window.CheckAndAdd(400)) throw new Exception("Packet 400 should be accepted within window");

            Console.WriteLine("PASSED");
        }

        private static void TestZeroSequence()
        {
            Console.Write("[TEST] Zero sequence rejection... ");
            var window = new AntiReplayWindow();
            if (window.Check(0) || window.CheckAndAdd(0))
                throw new Exception("Sequence number 0 must be rejected");
            Console.WriteLine("PASSED");
        }

        private static void TestWindowReset()
        {
            Console.Write("[TEST] Window reset functionality... ");
            var window = new AntiReplayWindow();
            window.CheckAndAdd(100);
            window.CheckAndAdd(101);

            window.Reset();

            if (window.LastSequence != 0)
                throw new Exception("LastSequence not reset to 0");

            if (!window.CheckAndAdd(100))
                throw new Exception("Failed to add 100 after reset");

            Console.WriteLine("PASSED");
        }

        private static async Task TestConcurrencyAsync()
        {
            Console.Write("[TEST] High concurrency multi-threaded jitter & replay stress test... ");
            var window = new AntiReplayWindow();
            const int BatchSize = 64;
            const int BatchesCount = 50;
            int totalAccepted = 0;
            int totalReplayRejections = 0;

            for (int batch = 0; batch < BatchesCount; batch++)
            {
                int start = batch * BatchSize + 1;
                var batchIndices = Enumerable.Range(start, BatchSize).OrderBy(_ => Random.Shared.Next()).ToArray();

                var acceptedInBatch = new ConcurrentBag<ulong>();

                await Parallel.ForEachAsync(batchIndices, new ParallelOptions { MaxDegreeOfParallelism = 8 }, (seq, ct) =>
                {
                    ulong sequenceNumber = (ulong)seq;
                    if (window.CheckAndAdd(sequenceNumber))
                    {
                        acceptedInBatch.Add(sequenceNumber);
                    }
                    return ValueTask.CompletedTask;
                });

                if (acceptedInBatch.Count != BatchSize)
                    throw new Exception($"Batch {batch}: Expected {BatchSize} accepted packets, got {acceptedInBatch.Count}");

                totalAccepted += acceptedInBatch.Count;

                var replayedRejectionsInBatch = new ConcurrentBag<ulong>();
                await Parallel.ForEachAsync(batchIndices, new ParallelOptions { MaxDegreeOfParallelism = 8 }, (seq, ct) =>
                {
                    ulong sequenceNumber = (ulong)seq;
                    if (!window.CheckAndAdd(sequenceNumber))
                    {
                        replayedRejectionsInBatch.Add(sequenceNumber);
                    }
                    return ValueTask.CompletedTask;
                });

                if (replayedRejectionsInBatch.Count != BatchSize)
                    throw new Exception($"Batch {batch}: Expected {BatchSize} replayed packets rejected, got {replayedRejectionsInBatch.Count}");

                totalReplayRejections += replayedRejectionsInBatch.Count;
            }

            if (totalAccepted != BatchesCount * BatchSize)
                throw new Exception($"Expected {BatchesCount * BatchSize} total accepted packets, got {totalAccepted}");

            if (totalReplayRejections != BatchesCount * BatchSize)
                throw new Exception($"Expected {BatchesCount * BatchSize} total replayed rejections, got {totalReplayRejections}");

            Console.WriteLine("PASSED");
        }

        private static void TestCryptographicAadBinding()
        {
            Console.Write("[TEST] AEAD Associated Data (AAD) sequence integrity... ");
            byte[] key = new byte[16];
            RandomNumberGenerator.Fill(key);
            using var cipher = new AesGcm(key, tagSizeInBytes: 16);

            var window = new AntiReplayWindow();

            byte[] originalData = Encoding.UTF8.GetBytes("SuperSecretPayloadData");
            ulong seq = 1;

            byte[] aad = new byte[sizeof(ulong)];
            BitConverter.TryWriteBytes(aad, seq);

            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[originalData.Length];

            cipher.Encrypt(nonce, originalData, ciphertext, tag, aad);

            if (!window.Check(seq)) throw new Exception("Pre-check failed for seq 1");

            byte[] decrypted = new byte[ciphertext.Length];
            cipher.Decrypt(nonce, ciphertext, tag, decrypted, aad);

            if (!window.CheckAndAdd(seq)) throw new Exception("Window commit failed for seq 1");
            if (!decrypted.SequenceEqual(originalData)) throw new Exception("Decrypted data mismatch");

            if (window.Check(seq)) throw new Exception("AntiReplayWindow failed to detect replay before decryption!");
            if (window.CheckAndAdd(seq)) throw new Exception("AntiReplayWindow accepted duplicate packet!");

            ulong forgedSeq = 2;
            byte[] forgedAad = new byte[sizeof(ulong)];
            BitConverter.TryWriteBytes(forgedAad, forgedSeq);

            bool tamperDetected = false;
            try
            {
                byte[] tamperedDecrypted = new byte[ciphertext.Length];
                cipher.Decrypt(nonce, ciphertext, tag, tamperedDecrypted, forgedAad);
            }
            catch (CryptographicException)
            {
                tamperDetected = true;
            }

            if (!tamperDetected)
                throw new Exception("AES-GCM failed to detect tampered sequence number in AAD!");

            Console.WriteLine("PASSED");
        }
    }
}
