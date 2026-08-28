#nullable enable

using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace QuicPunch;

public sealed class TorIdentity
{
    private const byte OnionVersion = 3;
    private const string OnionChecksumPrefix = ".onion checksum";

    private readonly byte[] _privateKeyBlob;
    private readonly byte[] _publicKey;

    private TorIdentity(byte[] privateKeyBlob, byte[] publicKey)
    {
        if (privateKeyBlob.Length != 64)
            throw new ArgumentException("Tor ED25519-V3 private key blob must be 64 bytes.", nameof(privateKeyBlob));
        if (publicKey.Length != 32)
            throw new ArgumentException("Tor v3 public key must be 32 bytes.", nameof(publicKey));

        _privateKeyBlob = (byte[])privateKeyBlob.Clone();
        _publicKey = (byte[])publicKey.Clone();

        ServiceId = EncodeServiceId(_publicKey);
        OnionAddress = ServiceId + ".onion";
    }

    public string ServiceId { get; }

    public string OnionAddress { get; }

    public string PrivateKeyBase64 => Convert.ToBase64String(_privateKeyBlob);

    public ReadOnlyMemory<byte> PublicKey => new((byte[])_publicKey.Clone());

    public ReadOnlyMemory<byte> PrivateKeyBlob => new((byte[])_privateKeyBlob.Clone());

    public static TorIdentity CreateRandom()
    {
        byte[] seed = RandomNumberGenerator.GetBytes(32);
        try
        {
            return FromEd25519Seed(seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public static TorIdentity CreateDeterministic(
        string secret,
        string context = "QuicPunch/default",
        int iterations = 600_000)
    {
        if (string.IsNullOrEmpty(secret))
            throw new ArgumentException("Secret cannot be empty.", nameof(secret));
        if (string.IsNullOrWhiteSpace(context))
            throw new ArgumentException("Context cannot be empty.", nameof(context));
        if (iterations < 10_000)
            throw new ArgumentOutOfRangeException(nameof(iterations), "Use at least 10,000 iterations.");

        byte[] contextBytes = Encoding.UTF8.GetBytes("QuicPunch/TorIdentity/v1\0" + context.Normalize(NormalizationForm.FormC));
        byte[] salt = SHA256.HashData(contextBytes);
        byte[] password = Encoding.UTF8.GetBytes(secret.Normalize(NormalizationForm.FormC));
        byte[] seed = new byte[32];

        try
        {
            Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                seed,
                iterations,
                HashAlgorithmName.SHA512);

            return FromEd25519Seed(seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static TorIdentity FromPrivateKeyBase64(string keyBlobBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyBlobBase64);

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(keyBlobBase64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Invalid Base64 Tor private key blob.", nameof(keyBlobBase64), ex);
        }

        return FromPrivateKeyBlob(blob);
    }

    public static TorIdentity FromPrivateKeyBlob(ReadOnlySpan<byte> keyBlob)
    {
        if (keyBlob.Length != 64)
            throw new ArgumentException("Tor ED25519-V3 KeyBlob must contain exactly 64 bytes.", nameof(keyBlob));

        byte[] blob = keyBlob.ToArray();
        byte[] scalar = blob.AsSpan(0, 32).ToArray();

        try
        {
            byte[] publicKey = Ed25519PublicKeyFromScalar(scalar);
            return new TorIdentity(blob, publicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    public static TorIdentity ParseTorPrivateKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        const string prefix = "ED25519-V3:";
        return FromPrivateKeyBase64(
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? value[prefix.Length..]
                : value);
    }

    private static TorIdentity FromEd25519Seed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
            throw new ArgumentException("Ed25519 seed must be 32 bytes.", nameof(seed));

        byte[] expanded = SHA512.HashData(seed);
        try
        {
            // RFC 8032 pruning/clamping. Tor's ED25519-V3 control KeyBlob stores
            // this secret scalar plus the second 32-byte PRF secret.
            expanded[0] &= 248;
            expanded[31] &= 63;
            expanded[31] |= 64;

            byte[] blob = (byte[])expanded.Clone();
            byte[] publicKey = Ed25519PublicKeyFromScalar(blob.AsSpan(0, 32));
            return new TorIdentity(blob, publicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expanded);
        }
    }

    private static string EncodeServiceId(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != 32)
            throw new ArgumentException("Public key must be 32 bytes.", nameof(publicKey));

        byte[] prefix = Encoding.ASCII.GetBytes(OnionChecksumPrefix);
        byte[] checksumInput = new byte[prefix.Length + 32 + 1];
        prefix.CopyTo(checksumInput, 0);
        publicKey.CopyTo(checksumInput.AsSpan(prefix.Length, 32));
        checksumInput[^1] = OnionVersion;

        byte[] checksum = SHA3_256.HashData(checksumInput);
        byte[] address = new byte[35];
        publicKey.CopyTo(address.AsSpan(0, 32));
        address[32] = checksum[0];
        address[33] = checksum[1];
        address[34] = OnionVersion;

        return Base32Encode(address).ToLowerInvariant();
    }

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((data.Length * 8 + 4) / 5);

        int buffer = 0;
        int bitsLeft = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                output.Append(alphabet[(buffer >> bitsLeft) & 31]);
            }
        }

        if (bitsLeft > 0)
            output.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);

        return output.ToString();
    }

    // Minimal Ed25519 public-key derivation.
    // .NET 10 does not expose a first-party Ed25519 signing/key-generation API.
    // We only need scalar*basepoint to convert Tor's 32-byte secret scalar into
    // the corresponding public key. Extended Edwards coordinates avoid an
    // expensive modular inverse on every bit; there is one inverse at encoding.

    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger BaseX = BigInteger.Parse(
        "15112221349535400772501151409588531511454012693041857206046113283949847762202",
        CultureInfo.InvariantCulture);
    private static readonly BigInteger BaseY = BigInteger.Parse(
        "46316835694926478169428394003475163141307993866256225615783033603165251855960",
        CultureInfo.InvariantCulture);

    private readonly record struct EdwardsPoint(
        BigInteger X,
        BigInteger Y,
        BigInteger Z,
        BigInteger T);

    private static readonly EdwardsPoint IdentityPoint =
        new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);

    private static readonly EdwardsPoint BasePoint =
        new(BaseX, BaseY, BigInteger.One, Mod(BaseX * BaseY));

    private static byte[] Ed25519PublicKeyFromScalar(ReadOnlySpan<byte> scalarLittleEndian)
    {
        if (scalarLittleEndian.Length != 32)
            throw new ArgumentException("Scalar must be 32 bytes.", nameof(scalarLittleEndian));

        BigInteger scalar = new(scalarLittleEndian, isUnsigned: true, isBigEndian: false);
        EdwardsPoint point = ScalarMultiply(BasePoint, scalar);

        BigInteger zInv = Inverse(point.Z);
        BigInteger x = Mod(point.X * zInv);
        BigInteger y = Mod(point.Y * zInv);

        byte[] encoded = y.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Resize(ref encoded, 32);

        if (!x.IsEven)
            encoded[31] |= 0x80;

        return encoded;
    }

    private static EdwardsPoint ScalarMultiply(EdwardsPoint point, BigInteger scalar)
    {
        EdwardsPoint result = IdentityPoint;
        EdwardsPoint addend = point;

        while (scalar > BigInteger.Zero)
        {
            if (!scalar.IsEven)
                result = Add(result, addend);

            addend = Double(addend);
            scalar >>= 1;
        }

        return result;
    }

    private static EdwardsPoint Add(EdwardsPoint p, EdwardsPoint q)
    {
        BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X));
        BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X));
        BigInteger c = Mod(2 * D * p.T * q.T);
        BigInteger d = Mod(2 * p.Z * q.Z);
        BigInteger e = Mod(b - a);
        BigInteger f = Mod(d - c);
        BigInteger g = Mod(d + c);
        BigInteger h = Mod(b + a);

        return new EdwardsPoint(
            Mod(e * f),
            Mod(g * h),
            Mod(f * g),
            Mod(e * h));
    }

    private static EdwardsPoint Double(EdwardsPoint p)
    {
        BigInteger a = Mod(p.X * p.X);
        BigInteger b = Mod(p.Y * p.Y);
        BigInteger c = Mod(2 * p.Z * p.Z);
        BigInteger d = Mod(-a);
        BigInteger e = Mod((p.X + p.Y) * (p.X + p.Y) - a - b);
        BigInteger g = Mod(d + b);
        BigInteger f = Mod(g - c);
        BigInteger h = Mod(d - b);

        return new EdwardsPoint(
            Mod(e * f),
            Mod(g * h),
            Mod(f * g),
            Mod(e * h));
    }

    private static BigInteger Inverse(BigInteger value) =>
        BigInteger.ModPow(Mod(value), P - 2, P);

    private static BigInteger Mod(BigInteger value)
    {
        value %= P;
        return value.Sign < 0 ? value + P : value;
    }
}
