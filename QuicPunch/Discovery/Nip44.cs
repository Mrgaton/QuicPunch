using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace QuicPunch;

/// <summary>NIP-44 version 2 encryption used by NIP-59 gift wraps.</summary>
internal static class Nip44
{
    private static readonly byte[] ConversationSalt = "nip44-v2"u8.ToArray();
    private const int MaxPlaintextBytes = ushort.MaxValue;

    internal static byte[] GetConversationKey(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        byte[] sharedX = NostrSchnorr.ComputeSharedSecret(privateKey.ToArray(), publicKey.ToArray());
        try
        {
            using var hmac = new HMACSHA256(ConversationSalt);
            return hmac.ComputeHash(sharedX);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedX);
        }
    }

    internal static string Encrypt(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey, string plaintext, byte[]? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        if (plain.Length is < 1 or > MaxPlaintextBytes)
            throw new ArgumentOutOfRangeException(nameof(plaintext), $"NIP-44 plaintext must contain 1 to {MaxPlaintextBytes} UTF-8 bytes.");

        byte[] conversationKey = GetConversationKey(privateKey, publicKey);
        byte[] messageNonce = nonce is null ? RandomNumberGenerator.GetBytes(32) : (byte[])nonce.Clone();
        if (messageNonce.Length != 32) throw new ArgumentException("NIP-44 nonce must be 32 bytes.", nameof(nonce));

        byte[] keys = HkdfExpand(conversationKey, messageNonce, 76);
        byte[] padded = Pad(plain);
        byte[] cipher = new byte[padded.Length];
        ChaCha20Xor(keys.AsSpan(0, 32), keys.AsSpan(32, 12), padded, cipher);

        byte[] authenticated = new byte[messageNonce.Length + cipher.Length];
        messageNonce.CopyTo(authenticated, 0);
        cipher.CopyTo(authenticated, messageNonce.Length);
        byte[] mac;
        using (var hmac = new HMACSHA256(keys.AsSpan(44, 32).ToArray()))
            mac = hmac.ComputeHash(authenticated);

        byte[] result = new byte[1 + 32 + cipher.Length + 32];
        result[0] = 2;
        messageNonce.CopyTo(result, 1);
        cipher.CopyTo(result, 33);
        mac.CopyTo(result, 33 + cipher.Length);

        CryptographicOperations.ZeroMemory(conversationKey);
        CryptographicOperations.ZeroMemory(keys);
        CryptographicOperations.ZeroMemory(padded);
        return Convert.ToBase64String(result);
    }

    internal static string Decrypt(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey, string payload)
    {
        if (string.IsNullOrEmpty(payload) || payload[0] == '#' || payload.Length < 132)
            throw new CryptographicException("Invalid or unsupported NIP-44 payload.");

        byte[] raw = Convert.FromBase64String(payload);
        if (raw.Length < 99 || raw[0] != 2 || raw.Length > MaxPlaintextBytes * 2)
            throw new CryptographicException("Invalid or unsupported NIP-44 payload.");

        ReadOnlySpan<byte> nonce = raw.AsSpan(1, 32);
        ReadOnlySpan<byte> cipher = raw.AsSpan(33, raw.Length - 65);
        ReadOnlySpan<byte> suppliedMac = raw.AsSpan(raw.Length - 32, 32);
        byte[] conversationKey = GetConversationKey(privateKey, publicKey);
        byte[] keys = HkdfExpand(conversationKey, nonce, 76);

        byte[] authenticated = new byte[32 + cipher.Length];
        nonce.CopyTo(authenticated);
        cipher.CopyTo(authenticated.AsSpan(32));
        byte[] calculatedMac;
        using (var hmac = new HMACSHA256(keys.AsSpan(44, 32).ToArray()))
            calculatedMac = hmac.ComputeHash(authenticated);
        if (!CryptographicOperations.FixedTimeEquals(calculatedMac, suppliedMac))
            throw new CryptographicException("Invalid NIP-44 MAC.");

        byte[] padded = new byte[cipher.Length];
        ChaCha20Xor(keys.AsSpan(0, 32), keys.AsSpan(32, 12), cipher, padded);
        byte[] plain = Unpad(padded);

        CryptographicOperations.ZeroMemory(conversationKey);
        CryptographicOperations.ZeroMemory(keys);
        CryptographicOperations.ZeroMemory(padded);
        return new UTF8Encoding(false, true).GetString(plain);
    }

    private static byte[] Pad(ReadOnlySpan<byte> plaintext)
    {
        int paddedLength = CalculatePaddedLength(plaintext.Length);
        byte[] result = new byte[2 + paddedLength];
        BinaryPrimitives.WriteUInt16BigEndian(result, checked((ushort)plaintext.Length));
        plaintext.CopyTo(result.AsSpan(2));
        return result;
    }

    private static byte[] Unpad(ReadOnlySpan<byte> padded)
    {
        if (padded.Length < 2) throw new CryptographicException("Invalid NIP-44 padding.");
        int length = BinaryPrimitives.ReadUInt16BigEndian(padded);
        if (length is 0 or > MaxPlaintextBytes || padded.Length != 2 + CalculatePaddedLength(length))
            throw new CryptographicException("Invalid NIP-44 padding length.");
        return padded.Slice(2, length).ToArray();
    }

    private static int CalculatePaddedLength(int length)
    {
        if (length <= 32) return 32;
        int nextPower = 1 << (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(length - 1)));
        int chunk = nextPower <= 256 ? 32 : nextPower / 8;
        return chunk * (((length - 1) / chunk) + 1);
    }

    private static byte[] HkdfExpand(ReadOnlySpan<byte> prk, ReadOnlySpan<byte> info, int length)
    {
        byte[] output = new byte[length];
        byte[] previous = Array.Empty<byte>();
        int offset = 0;
        byte counter = 1;
        while (offset < length)
        {
            byte[] input = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(input, 0);
            info.CopyTo(input.AsSpan(previous.Length));
            input[^1] = counter++;
            using var hmac = new HMACSHA256(prk.ToArray());
            previous = hmac.ComputeHash(input);
            int take = Math.Min(previous.Length, length - offset);
            previous.AsSpan(0, take).CopyTo(output.AsSpan(offset));
            offset += take;
        }
        return output;
    }

    private static void ChaCha20Xor(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (key.Length != 32 || nonce.Length != 12 || output.Length < input.Length)
            throw new ArgumentException("Invalid ChaCha20 parameters.");
        ReadOnlySpan<byte> constants = "expand 32-byte k"u8;
        Span<uint> initial = stackalloc uint[16];
        for (int i = 0; i < 4; i++) initial[i] = BinaryPrimitives.ReadUInt32LittleEndian(constants.Slice(i * 4, 4));
        for (int i = 0; i < 8; i++) initial[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
        initial[12] = 0;
        for (int i = 0; i < 3; i++) initial[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(i * 4, 4));

        Span<byte> block = stackalloc byte[64];
        Span<uint> state = stackalloc uint[16];
        int offset = 0;
        while (offset < input.Length)
        {
            initial.CopyTo(state);
            for (int i = 0; i < 10; i++)
            {
                QuarterRound(state, 0, 4, 8, 12); QuarterRound(state, 1, 5, 9, 13);
                QuarterRound(state, 2, 6, 10, 14); QuarterRound(state, 3, 7, 11, 15);
                QuarterRound(state, 0, 5, 10, 15); QuarterRound(state, 1, 6, 11, 12);
                QuarterRound(state, 2, 7, 8, 13); QuarterRound(state, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i * 4, 4), state[i] + initial[i]);
            int count = Math.Min(64, input.Length - offset);
            for (int i = 0; i < count; i++) output[offset + i] = (byte)(input[offset + i] ^ block[i]);
            initial[12]++;
            offset += count;
        }
    }

    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = System.Numerics.BitOperations.RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = System.Numerics.BitOperations.RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = System.Numerics.BitOperations.RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = System.Numerics.BitOperations.RotateLeft(x[b] ^ x[c], 7);
    }
}
