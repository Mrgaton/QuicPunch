using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using QuicPunch.Helpers;

namespace QuicPunch;

/// <summary>
/// Small BIP-340 implementation for ephemeral Nostr discovery identities.
/// It deliberately does not reuse the QuicPunch certificate identity.
/// </summary>
internal static class NostrSchnorr
{
    private static readonly BigInteger P = BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger N = BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", System.Globalization.NumberStyles.HexNumber);
    private static readonly Point G = new(
        BigInteger.Parse("079BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798", System.Globalization.NumberStyles.HexNumber),
        BigInteger.Parse("0483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8", System.Globalization.NumberStyles.HexNumber));

    public static byte[] CreatePrivateKey()
    {
        while (true)
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            BigInteger d = FromBytes(bytes);
            if (d > 0 && d < N)
                return bytes;
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static byte[] DerivePrivateKey(byte[] masterKey, byte[] info)
    {
        byte[] derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt: null, info: info);
        try
        {
            BigInteger d = Mod(FromBytes(derived), N - 1) + 1;
            return ToBytes32(d);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    public static byte[] GetPublicKeyX(byte[] privateKey)
    {
        BigInteger d = FromBytes(privateKey);
        ValidateSecret(d);
        Point p = Multiply(G, d);
        return ToBytes32(p.X);
    }

    public static byte[] Sign(ReadOnlySpan<byte> message32, byte[] privateKey)
    {
        if (message32.Length != 32)
            throw new ArgumentException("BIP-340 messages must be exactly 32 bytes.", nameof(message32));

        BigInteger d0 = FromBytes(privateKey);
        ValidateSecret(d0);

        Point p = Multiply(G, d0);
        BigInteger d = IsEven(p.Y) ? d0 : N - d0;
        byte[] px = ToBytes32(p.X);
        byte[] dBytes = ToBytes32(d);
        byte[] aux = RandomNumberGenerator.GetBytes(32);

        try
        {
            byte[] auxHash = TaggedHash("BIP0340/aux", aux);
            byte[] t = new byte[32];
            for (int i = 0; i < 32; i++)
                t[i] = (byte)(dBytes[i] ^ auxHash[i]);

            byte[] nonceInput = Utilities.Combine(t, px, message32.ToArray());
            BigInteger k0 = Mod(FromBytes(TaggedHash("BIP0340/nonce", nonceInput)), N);
            if (k0.IsZero)
                throw new CryptographicException("BIP-340 generated an invalid zero nonce.");

            Point rPoint = Multiply(G, k0);
            BigInteger k = IsEven(rPoint.Y) ? k0 : N - k0;
            byte[] rx = ToBytes32(rPoint.X);

            BigInteger e = Mod(FromBytes(TaggedHash("BIP0340/challenge", Utilities.Combine(rx, px, message32.ToArray()))), N);
            BigInteger s = Mod(k + e * d, N);

            byte[] signature = new byte[64];
            rx.CopyTo(signature, 0);
            ToBytes32(s).CopyTo(signature, 32);
            return signature;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dBytes);
            CryptographicOperations.ZeroMemory(aux);
        }
    }

    public static bool Verify(ReadOnlySpan<byte> message32, ReadOnlySpan<byte> publicKeyX32, ReadOnlySpan<byte> signature64)
    {
        if (message32.Length != 32 || publicKeyX32.Length != 32 || signature64.Length != 64)
            return false;

        BigInteger px = FromBytes(publicKeyX32);
        Point? p = LiftX(px);
        if (p == null)
            return false;

        BigInteger r = FromBytes(signature64[..32]);
        BigInteger s = FromBytes(signature64[32..]);
        if (r >= P || s >= N)
            return false;

        BigInteger e = Mod(FromBytes(TaggedHash(
            "BIP0340/challenge",
            Utilities.Combine(signature64[..32].ToArray(), publicKeyX32.ToArray(), message32.ToArray()))), N);

        Point sG = Multiply(G, s);
        Point eP = Multiply(p.Value, Mod(N - e, N));
        Point rPoint = Add(sG, eP);

        return !rPoint.Infinity && IsEven(rPoint.Y) && rPoint.X == r;
    }

    private static Point? LiftX(BigInteger x)
    {
        if (x < 0 || x >= P)
            return null;

        BigInteger c = Mod(BigInteger.ModPow(x, 3, P) + 7, P);
        BigInteger y = BigInteger.ModPow(c, (P + 1) >> 2, P);
        if (Mod(y * y - c, P) != 0)
            return null;
        if (!IsEven(y))
            y = P - y;
        return new Point(x, y);
    }

    private static Point Multiply(Point point, BigInteger scalar)
    {
        scalar = Mod(scalar, N);
        Jacobian result = Jacobian.InfinityPoint;
        Jacobian addend = Jacobian.FromAffine(point);

        int bits = BitLength(scalar);
        for (int i = bits - 1; i >= 0; i--)
        {
            result = Double(result);
            if (!((scalar >> i) & BigInteger.One).IsZero)
                result = Add(result, addend);
        }

        return result.ToAffine();
    }

    private static Point Add(Point a, Point b)
    {
        if (a.Infinity) return b;
        if (b.Infinity) return a;
        return Add(Jacobian.FromAffine(a), Jacobian.FromAffine(b)).ToAffine();
    }

    private static Jacobian Double(Jacobian p)
    {
        if (p.IsInfinity || p.Y.IsZero)
            return Jacobian.InfinityPoint;

        BigInteger xx = Mod(p.X * p.X, P);
        BigInteger yy = Mod(p.Y * p.Y, P);
        BigInteger yyyy = Mod(yy * yy, P);
        BigInteger s = Mod(2 * (Mod((p.X + yy) * (p.X + yy), P) - xx - yyyy), P);
        BigInteger m = Mod(3 * xx, P);
        BigInteger t = Mod(m * m - 2 * s, P);
        BigInteger x3 = t;
        BigInteger y3 = Mod(m * (s - t) - 8 * yyyy, P);
        BigInteger z3 = Mod(2 * p.Y * p.Z, P);
        return new Jacobian(x3, y3, z3);
    }

    private static Jacobian Add(Jacobian p, Jacobian q)
    {
        if (p.IsInfinity) return q;
        if (q.IsInfinity) return p;

        BigInteger z1z1 = Mod(p.Z * p.Z, P);
        BigInteger z2z2 = Mod(q.Z * q.Z, P);
        BigInteger u1 = Mod(p.X * z2z2, P);
        BigInteger u2 = Mod(q.X * z1z1, P);
        BigInteger s1 = Mod(p.Y * q.Z * z2z2, P);
        BigInteger s2 = Mod(q.Y * p.Z * z1z1, P);

        if (u1 == u2)
            return s1 == s2 ? Double(p) : Jacobian.InfinityPoint;

        BigInteger h = Mod(u2 - u1, P);
        BigInteger i = Mod((2 * h) * (2 * h), P);
        BigInteger j = Mod(h * i, P);
        BigInteger r = Mod(2 * (s2 - s1), P);
        BigInteger v = Mod(u1 * i, P);
        BigInteger x3 = Mod(r * r - j - 2 * v, P);
        BigInteger y3 = Mod(r * (v - x3) - 2 * s1 * j, P);
        BigInteger z3 = Mod((Mod((p.Z + q.Z) * (p.Z + q.Z), P) - z1z1 - z2z2) * h, P);
        return new Jacobian(x3, y3, z3);
    }

    private static byte[] TaggedHash(string tag, byte[] data)
    {
        byte[] tagBytes = Encoding.ASCII.GetBytes(tag);
        byte[] tagHash = SHA256.HashData(tagBytes);
        return SHA256.HashData(Utilities.Combine(tagHash, tagHash, data));
    }

    private static BigInteger FromBytes(ReadOnlySpan<byte> bytes) =>
        new(bytes, isUnsigned: true, isBigEndian: true);

    private static byte[] ToBytes32(BigInteger value)
    {
        byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length > 32)
            throw new CryptographicException("Integer does not fit in 32 bytes.");
        byte[] result = new byte[32];
        raw.CopyTo(result, 32 - raw.Length);
        return result;
    }

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        BigInteger result = value % modulus;
        return result.Sign < 0 ? result + modulus : result;
    }

    private static bool IsEven(BigInteger value) => value.IsEven;

    private static int BitLength(BigInteger value)
    {
        if (value.Sign <= 0)
            return 0;
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        int leading = BitOperations.LeadingZeroCount((uint)bytes[0]) - 24;
        return bytes.Length * 8 - leading;
    }

    private static void ValidateSecret(BigInteger d)
    {
        if (d <= 0 || d >= N)
            throw new CryptographicException("Invalid secp256k1 private key.");
    }

    private readonly record struct Point(BigInteger X, BigInteger Y, bool Infinity = false);

    private readonly record struct Jacobian(BigInteger X, BigInteger Y, BigInteger Z)
    {
        public static Jacobian InfinityPoint => new(BigInteger.Zero, BigInteger.One, BigInteger.Zero);
        public bool IsInfinity => Z.IsZero;
        public static Jacobian FromAffine(Point p) =>
            p.Infinity ? InfinityPoint : new Jacobian(p.X, p.Y, BigInteger.One);

        public Point ToAffine()
        {
            if (IsInfinity)
                return new Point(BigInteger.Zero, BigInteger.Zero, true);

            BigInteger zInv = BigInteger.ModPow(Z, P - 2, P);
            BigInteger zInv2 = Mod(zInv * zInv, P);
            BigInteger x = Mod(X * zInv2, P);
            BigInteger y = Mod(Y * zInv2 * zInv, P);
            return new Point(x, y);
        }
    }
}