using System;
using System.Security.Cryptography;
using System.Text;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

internal static class Sha3Tests
{
    public static void Run()
    {
        Console.WriteLine("[TEST] Verifying Sha3 pure C# fallback against native SHA3-256 and SHA3-512...");

        string[] testStrings =
        [
            "",
            "a",
            "abc",
            "The quick brown fox jumps over the lazy dog",
            new string('x', 135), // Just below block size
            new string('y', 136), // Exactly block size
            new string('z', 137), // Just above block size
            new string('w', 1000) // Multi-block
        ];

        foreach (var s in testStrings)
        {
            byte[] input = Encoding.UTF8.GetBytes(s);
            byte[] native256 = SHA3_256.HashData(input);
            byte[] pure256 = Sha3Crypto.Keccak(input, rateBytes: 136, outputBytes: 32, domainSuffix: 0x06);

            if (!native256.AsSpan().SequenceEqual(pure256))
            {
                throw new InvalidOperationException($"SHA3-256 mismatch for input '{s}'! Native: {Convert.ToHexString(native256)}, Pure: {Convert.ToHexString(pure256)}");
            }

            byte[] native512 = SHA3_512.HashData(input);
            byte[] pure512 = Sha3Crypto.Keccak(input, rateBytes: 72, outputBytes: 64, domainSuffix: 0x06);

            if (!native512.AsSpan().SequenceEqual(pure512))
            {
                throw new InvalidOperationException($"SHA3-512 mismatch for input '{s}'! Native: {Convert.ToHexString(native512)}, Pure: {Convert.ToHexString(pure512)}");
            }
        }

        Console.WriteLine("[PASS] All SHA3-256 and SHA3-512 pure C# test vectors matched byte-for-byte with native .NET!");
    }
}
