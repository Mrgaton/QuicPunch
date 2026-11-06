using System.Security.Cryptography;
using NBitcoin.Secp256k1;

namespace QuicPunch;

/// <summary>Nostr's BIP-340 and x-only ECDH backed by the maintained secp256k1 implementation.</summary>
internal static class NostrSchnorr
{
    private static readonly byte[] OrderMinusOne = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364140");

    public static byte[] CreatePrivateKey()
    {
        while (true)
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            if (ECPrivKey.TryCreate(bytes, out var key)) { key.Dispose(); return bytes; }
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static byte[] DerivePrivateKey(byte[] masterKey, byte[] info)
    {
        byte[] derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt: null, info: info);
        // Preserve the existing identity mapping (d mod (n-1)) + 1 with fixed-width arithmetic.
        // The input is 256 bits, so at most one subtraction is necessary.
        Span<byte> difference = stackalloc byte[32];
        int borrow = 0;
        for (int i = 31; i >= 0; i--)
        {
            int value = derived[i] - OrderMinusOne[i] - borrow;
            difference[i] = (byte)value;
            borrow = (value >> 31) & 1;
        }
        int mask = -borrow;
        for (int i = 0; i < 32; i++) derived[i] = (byte)((derived[i] & mask) | (difference[i] & ~mask));
        int carry = 1;
        for (int i = 31; i >= 0; i--)
        {
            int value = derived[i] + carry;
            derived[i] = (byte)value;
            carry = value >> 8;
        }
        CryptographicOperations.ZeroMemory(difference);
        return derived;
    }

    public static byte[] GetPublicKeyX(byte[] privateKey)
    {
        using var key = ReadPrivateKey(privateKey);
        return key.CreatePubKey().ToXOnlyPubKey().ToBytes();
    }

    public static byte[] Sign(ReadOnlySpan<byte> message32, byte[] privateKey)
    {
        byte[] auxiliaryRandom = RandomNumberGenerator.GetBytes(32);
        try { return Sign(message32, privateKey, auxiliaryRandom); }
        finally { CryptographicOperations.ZeroMemory(auxiliaryRandom); }
    }

    internal static byte[] Sign(ReadOnlySpan<byte> message32, byte[] privateKey, ReadOnlyMemory<byte> auxiliaryRandom)
    {
        if (message32.Length != 32 || auxiliaryRandom.Length != 32)
            throw new ArgumentException("BIP-340 requires a 32-byte message and 32 bytes of auxiliary randomness.");
        using var key = ReadPrivateKey(privateKey);
        return key.SignBIP340(message32, auxiliaryRandom).ToBytes();
    }

    public static byte[] ComputeSharedSecret(ReadOnlySpan<byte> privateKey32, ReadOnlySpan<byte> otherPublicKeyX32)
    {
        if (otherPublicKeyX32.Length != 32) throw new ArgumentException("Public key must contain 32 bytes.");
        using var key = ReadPrivateKey(privateKey32);
        Span<byte> compressed = stackalloc byte[33];
        compressed[0] = 2; // Nostr x-only public keys use the even-Y lift.
        otherPublicKeyX32.CopyTo(compressed[1..]);
        if (!ECPubKey.TryCreate(compressed, null, out _, out var otherKey)) throw new ArgumentException("Invalid secp256k1 public key.");
        byte[] sharedPoint = otherKey.GetSharedPubkey(key).ToBytes();
        try { return sharedPoint.AsSpan(1, 32).ToArray(); }
        finally { CryptographicOperations.ZeroMemory(sharedPoint); }
    }

    public static bool Verify(ReadOnlySpan<byte> message32, ReadOnlySpan<byte> publicKeyX32, ReadOnlySpan<byte> signature64)
    {
        if (message32.Length != 32 || publicKeyX32.Length != 32 || signature64.Length != 64) return false;
        return ECXOnlyPubKey.TryCreate(publicKeyX32, out var key) && SecpSchnorrSignature.TryCreate(signature64, out var signature) &&
            key.SigVerifyBIP340(signature, message32);
    }

    private static ECPrivKey ReadPrivateKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32 || !ECPrivKey.TryCreate(bytes, out var key)) throw new ArgumentException("Invalid 32-byte secp256k1 secret key.");
        return key;
    }
}
