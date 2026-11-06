using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace QuicPunch.Helpers;

/// <summary>
/// Provides SHA-3 (FIPS 202) hashing, HMAC, and PBKDF2 with automatic fallback
/// to pure C# Keccak-f[1600] on platforms where System.Security.Cryptography.SHA3
/// is not supported natively (e.g. Android / Conscrypt).
/// </summary>
public static class Sha3Crypto
{
    private static readonly bool HasNativeSha3 = CheckNativeSha3Support();

    private static bool CheckNativeSha3Support()
    {
        try
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return false;
            if (OperatingSystem.IsAndroid()) return false;
            return SHA3_256.IsSupported && SHA3_512.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    public static byte[] Hash256(byte[] data) => Hash256(data.AsSpan());

    public static byte[] Hash256(ReadOnlySpan<byte> data)
    {
        if (HasNativeSha3)
        {
            try { return SHA3_256.HashData(data); } catch { }
        }
        return Keccak(data, rateBytes: 136, outputBytes: 32, domainSuffix: 0x06);
    }

    public static void Hash256(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (HasNativeSha3)
        {
            try
            {
                if (SHA3_256.TryHashData(source, destination, out _)) return;
            }
            catch { }
        }
        byte[] hash = Keccak(source, rateBytes: 136, outputBytes: 32, domainSuffix: 0x06);
        hash.CopyTo(destination);
    }

    public static byte[] Hash512(byte[] data) => Hash512(data.AsSpan());

    public static byte[] Hash512(ReadOnlySpan<byte> data)
    {
        if (HasNativeSha3)
        {
            try { return SHA3_512.HashData(data); } catch { }
        }
        return Keccak(data, rateBytes: 72, outputBytes: 64, domainSuffix: 0x06);
    }

    public static byte[] Hmac256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        if (HasNativeSha3)
        {
            try { return HMACSHA3_256.HashData(key, data); } catch { }
        }
        return ComputeHmacSha3_256(key, data);
    }

    public static byte[] Pbkdf2Sha3_512(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int outputBytes)
    {
        if (HasNativeSha3)
        {
            try
            {
                return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA3_512, outputBytes);
            }
            catch { }
        }
        return ComputePbkdf2Sha3_512(password, salt, iterations, outputBytes);
    }

    public static byte[] SignData(ECDsa ecdsa, ReadOnlySpan<byte> data)
    {
        if (HasNativeSha3)
        {
            try
            {
                return ecdsa.SignData(data, HashAlgorithmName.SHA3_256);
            }
            catch { }
        }
        byte[] hash = Hash256(data);
        return ecdsa.SignHash(hash);
    }

    public static bool VerifyData(ECDsa ecdsa, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (HasNativeSha3)
        {
            try
            {
                return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA3_256);
            }
            catch { }
        }
        byte[] hash = Hash256(data);
        return ecdsa.VerifyHash(hash, signature);
    }

    #region HMAC-SHA3-256
    private static byte[] ComputeHmacSha3_256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        const int blockSize = 136; // Rate of SHA3-256 in bytes
        byte[] k = new byte[blockSize];
        if (key.Length > blockSize)
        {
            byte[] keyHash = Hash256(key);
            keyHash.CopyTo(k.AsSpan());
        }
        else
        {
            key.CopyTo(k.AsSpan());
        }

        byte[] iPad = new byte[blockSize + data.Length];
        byte[] oPad = new byte[blockSize + 32];

        for (int i = 0; i < blockSize; i++)
        {
            iPad[i] = (byte)(k[i] ^ 0x36);
            oPad[i] = (byte)(k[i] ^ 0x5c);
        }
        data.CopyTo(iPad.AsSpan(blockSize));

        byte[] innerHash = Hash256(iPad);
        innerHash.CopyTo(oPad.AsSpan(blockSize));

        return Hash256(oPad);
    }
    #endregion

    #region HMAC-SHA3-512 & PBKDF2
    private static byte[] ComputeHmacSha3_512(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        const int blockSize = 72; // Rate of SHA3-512 in bytes
        byte[] k = new byte[blockSize];
        if (key.Length > blockSize)
        {
            byte[] keyHash = Hash512(key);
            keyHash.CopyTo(k.AsSpan());
        }
        else
        {
            key.CopyTo(k.AsSpan());
        }

        byte[] iPad = new byte[blockSize + data.Length];
        byte[] oPad = new byte[blockSize + 64];

        for (int i = 0; i < blockSize; i++)
        {
            iPad[i] = (byte)(k[i] ^ 0x36);
            oPad[i] = (byte)(k[i] ^ 0x5c);
        }
        data.CopyTo(iPad.AsSpan(blockSize));

        byte[] innerHash = Hash512(iPad);
        innerHash.CopyTo(oPad.AsSpan(blockSize));

        return Hash512(oPad);
    }

    private static byte[] ComputePbkdf2Sha3_512(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int outputBytes)
    {
        int hLen = 64; // SHA3-512 output size
        int l = (outputBytes + hLen - 1) / hLen;
        byte[] result = new byte[outputBytes];
        byte[] saltPlusI = new byte[salt.Length + 4];
        salt.CopyTo(saltPlusI.AsSpan());

        byte[] u = new byte[hLen];
        byte[] t = new byte[hLen];

        int bytesRemaining = outputBytes;
        int outOffset = 0;

        for (int i = 1; i <= l; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(saltPlusI.AsSpan(salt.Length), i);
            u = ComputeHmacSha3_512(password, saltPlusI);
            u.CopyTo(t.AsSpan());

            for (int round = 2; round <= iterations; round++)
            {
                u = ComputeHmacSha3_512(password, u);
                for (int j = 0; j < hLen; j++)
                {
                    t[j] ^= u[j];
                }
            }

            int toCopy = Math.Min(bytesRemaining, hLen);
            Buffer.BlockCopy(t, 0, result, outOffset, toCopy);
            outOffset += toCopy;
            bytesRemaining -= toCopy;
        }

        return result;
    }
    #endregion

    #region Pure C# Keccak-f[1600]
    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL,
        0x8000000080008000UL, 0x000000000000808bUL, 0x0000000080000001UL,
        0x8000000080008081UL, 0x8000000000008009UL, 0x000000000000008aUL,
        0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL,
        0x8000000000008003UL, 0x8000000000008002UL, 0x8000000000000080UL,
        0x000000000000800aUL, 0x800000008000000aUL, 0x8000000080008081UL,
        0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
    ];

    private static readonly int[] RotationOffsets =
    [
         0,  1, 62, 28, 27,
        36, 44,  6, 55, 20,
         3, 10, 43, 25, 39,
        41, 45, 15, 21,  8,
        18,  2, 61, 56, 14
    ];

    internal static byte[] Keccak(ReadOnlySpan<byte> message, int rateBytes, int outputBytes, byte domainSuffix)
    {
        ulong[] state = new ulong[25];
        int rateWords = rateBytes / 8;
        int offset = 0;

        while (offset + rateBytes <= message.Length)
        {
            for (int i = 0; i < rateWords; i++)
            {
                state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(offset + i * 8, 8));
            }
            KeccakPermute(state);
            offset += rateBytes;
        }

        // Final block with padding
        Span<byte> block = stackalloc byte[rateBytes];
        int rem = message.Length - offset;
        if (rem > 0)
        {
            message.Slice(offset, rem).CopyTo(block);
        }
        block[rem] = domainSuffix;
        block[rateBytes - 1] |= 0x80;

        for (int i = 0; i < rateWords; i++)
        {
            state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));
        }
        KeccakPermute(state);

        byte[] output = new byte[outputBytes];
        int outOffset = 0;
        Span<byte> remainderTemp = stackalloc byte[8];
        while (outOffset < outputBytes)
        {
            int toCopy = Math.Min(outputBytes - outOffset, rateBytes);
            for (int i = 0; i < toCopy / 8; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(outOffset + i * 8, 8), state[i]);
            }
            int remainderBytes = toCopy % 8;
            if (remainderBytes > 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(remainderTemp, state[toCopy / 8]);
                remainderTemp.Slice(0, remainderBytes).CopyTo(output.AsSpan(outOffset + (toCopy / 8) * 8));
            }
            outOffset += toCopy;
            if (outOffset < outputBytes)
            {
                KeccakPermute(state);
            }
        }

        return output;
    }

    private static void KeccakPermute(ulong[] state)
    {
        Span<ulong> b = stackalloc ulong[25];

        for (int round = 0; round < 24; round++)
        {
            // Theta
            ulong c0 = state[0] ^ state[5] ^ state[10] ^ state[15] ^ state[20];
            ulong c1 = state[1] ^ state[6] ^ state[11] ^ state[16] ^ state[21];
            ulong c2 = state[2] ^ state[7] ^ state[12] ^ state[17] ^ state[22];
            ulong c3 = state[3] ^ state[8] ^ state[13] ^ state[18] ^ state[23];
            ulong c4 = state[4] ^ state[9] ^ state[14] ^ state[19] ^ state[24];

            ulong d0 = BitOperations.RotateLeft(c1, 1) ^ c4;
            ulong d1 = BitOperations.RotateLeft(c2, 1) ^ c0;
            ulong d2 = BitOperations.RotateLeft(c3, 1) ^ c1;
            ulong d3 = BitOperations.RotateLeft(c4, 1) ^ c2;
            ulong d4 = BitOperations.RotateLeft(c0, 1) ^ c3;

            state[0] ^= d0; state[5] ^= d0; state[10] ^= d0; state[15] ^= d0; state[20] ^= d0;
            state[1] ^= d1; state[6] ^= d1; state[11] ^= d1; state[16] ^= d1; state[21] ^= d1;
            state[2] ^= d2; state[7] ^= d2; state[12] ^= d2; state[17] ^= d2; state[22] ^= d2;
            state[3] ^= d3; state[8] ^= d3; state[13] ^= d3; state[18] ^= d3; state[23] ^= d3;
            state[4] ^= d4; state[9] ^= d4; state[14] ^= d4; state[19] ^= d4; state[24] ^= d4;

            // Rho and Pi
            for (int i = 0; i < 25; i++)
            {
                int x = i % 5;
                int y = i / 5;
                int newIndex = y + 5 * ((2 * x + 3 * y) % 5);
                b[newIndex] = BitOperations.RotateLeft(state[i], RotationOffsets[i]);
            }

            // Chi
            for (int y = 0; y < 5; y++)
            {
                int y5 = y * 5;
                state[y5 + 0] = b[y5 + 0] ^ ((~b[y5 + 1]) & b[y5 + 2]);
                state[y5 + 1] = b[y5 + 1] ^ ((~b[y5 + 2]) & b[y5 + 3]);
                state[y5 + 2] = b[y5 + 2] ^ ((~b[y5 + 3]) & b[y5 + 4]);
                state[y5 + 3] = b[y5 + 3] ^ ((~b[y5 + 4]) & b[y5 + 0]);
                state[y5 + 4] = b[y5 + 4] ^ ((~b[y5 + 0]) & b[y5 + 1]);
            }

            // Iota
            state[0] ^= RoundConstants[round];
        }
    }
    #endregion
}
