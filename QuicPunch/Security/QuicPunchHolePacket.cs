#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuicPunch.Helpers;

namespace QuicPunch.Security
{
    /// <summary>
    /// Represents the fixed-size 192-byte high-security UDP hole-punch packet.
    /// Provides mutual password authentication (PoP via HMAC-SHA256), identity binding to TLS ECDSA certificates,
    /// zero-heap-allocation cryptography, and strict anti-replay protection with monotonic timestamps and sliding-window nonces.
    /// </summary>
    public sealed record QuicPunchHolePacket
    {
        public const uint MagicHeader = 0x51504850; // "QPHP" in ASCII (QuicPunch Hole Punch)
        public const byte CurrentVersion = 0x01;
        public const byte MessageTypePunch = 0x01;
        public const int PacketSize = 192;
        public const int HeaderSize = 128; // Data covered by ECDSA signature
        public const int SignatureSize = 64; // IEEE P1363 (32 bytes r + 32 bytes s for P-256)

        [Flags]
        public enum PacketFlags : byte
        {
            None = 0,
            HasPasswordProof = 1 << 0,
            IsEcho = 1 << 1
        }

        // Thread-safe sliding window nonce cache for anti-replay
        private static readonly ConcurrentDictionary<Guid, long> s_seenNonces = new();
        private static long s_lastPurgeTicks = 0;

        public byte Version { get; init; } = CurrentVersion;
        public byte Type { get; init; } = MessageTypePunch;
        public PacketFlags Flags { get; init; } = PacketFlags.None;
        public Guid SenderId { get; init; }
        public Guid RecipientId { get; init; }
        public long TimestampUtcTicks { get; init; }
        public byte[] Nonce { get; init; } = new byte[16];
        public byte[] SenderCertHash { get; init; } = new byte[32];
        public byte[] ProofOfPassword { get; init; } = new byte[32];
        public byte[] Signature { get; init; } = new byte[SignatureSize];

        public bool HasPasswordProof => (Flags & PacketFlags.HasPasswordProof) != 0;
        public bool IsEcho => (Flags & PacketFlags.IsEcho) != 0;

        /// <summary>
        /// Clears the static nonce cache. Useful for test suites.
        /// </summary>
        public static void ClearNonceCache()
        {
            s_seenNonces.Clear();
        }

        /// <summary>
        /// Creates and signs an outgoing single-packet hole punch message (zero-allocation helper).
        /// </summary>
        public static bool TryWritePacket(
            Span<byte> destination,
            Guid senderId,
            Guid recipientId,
            ReadOnlySpan<byte> senderCertHash,
            ReadOnlySpan<byte> recipientCertHash,
            ECDsa senderPrivateKey,
            ReadOnlySpan<byte> passwordHash = default,
            bool isEcho = false,
            long? overrideTicks = null)
        {
            if (destination.Length < PacketSize)
                return false;

            if (senderCertHash.Length != 32 || recipientCertHash.Length != 32)
                return false;

            ArgumentNullException.ThrowIfNull(senderPrivateKey);

            long ticks = overrideTicks ?? PreciseTime.GetCorrectTime().Ticks;
            var flags = PacketFlags.None;
            if (isEcho) flags |= PacketFlags.IsEcho;

            Span<byte> header = destination[..HeaderSize];

            BinaryPrimitives.WriteUInt32LittleEndian(header[..4], MagicHeader);
            header[4] = CurrentVersion;
            header[5] = MessageTypePunch;
            header[6] = (byte)flags;
            header[7] = 0; // Reserved

            senderId.TryWriteBytes(header.Slice(8, 16));
            recipientId.TryWriteBytes(header.Slice(24, 16));
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(40, 8), ticks);

            RandomNumberGenerator.Fill(header.Slice(48, 16));

            senderCertHash.CopyTo(header.Slice(64, 32));

            // Compute Proof-of-Password HMAC in-place if password configured
            Span<byte> popSpan = header.Slice(96, 32);
            if (!passwordHash.IsEmpty)
            {
                flags |= PacketFlags.HasPasswordProof;
                header[6] = (byte)flags; // Update flags byte

                ComputeProofOfPassword(header[..96], recipientCertHash, passwordHash, popSpan);
            }
            else
            {
                popSpan.Clear();
            }

            // Sign the 128-byte header using IEEE P1363 (fixed 64 bytes) directly into destination[128..192]
            Span<byte> sigSpan = destination.Slice(HeaderSize, SignatureSize);
            if (!senderPrivateKey.TrySignData(header, sigSpan, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation, out int written) || written != SignatureSize)
            {
                throw new CryptographicException($"Failed to write {SignatureSize} byte ECDSA signature.");
            }

            return true;
        }

        /// <summary>
        /// Creates and signs an outgoing single-packet hole punch message.
        /// </summary>
        public static QuicPunchHolePacket Create(
            Guid senderId,
            Guid recipientId,
            byte[] senderCertHash,
            byte[] recipientCertHash,
            ECDsa senderPrivateKey,
            byte[]? passwordHash = null,
            bool isEcho = false,
            long? overrideTicks = null)
        {
            ArgumentNullException.ThrowIfNull(senderCertHash);
            ArgumentNullException.ThrowIfNull(recipientCertHash);
            ArgumentNullException.ThrowIfNull(senderPrivateKey);

            byte[] buffer = new byte[PacketSize];
            if (!TryWritePacket(buffer, senderId, recipientId, senderCertHash, recipientCertHash, senderPrivateKey, passwordHash ?? ReadOnlySpan<byte>.Empty, isEcho, overrideTicks))
            {
                throw new InvalidOperationException("Failed to construct QuicPunchHolePacket.");
            }

            return Decode(buffer);
        }

        /// <summary>
        /// Decodes a 192-byte buffer into a <see cref="QuicPunchHolePacket"/> object.
        /// </summary>
        public static QuicPunchHolePacket Decode(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < PacketSize)
                throw new ArgumentException($"Buffer too small for QuicPunchHolePacket ({buffer.Length} < {PacketSize})", nameof(buffer));

            return new QuicPunchHolePacket
            {
                Version = buffer[4],
                Type = buffer[5],
                Flags = (PacketFlags)buffer[6],
                SenderId = new Guid(buffer.Slice(8, 16)),
                RecipientId = new Guid(buffer.Slice(24, 16)),
                TimestampUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(40, 8)),
                Nonce = buffer.Slice(48, 16).ToArray(),
                SenderCertHash = buffer.Slice(64, 32).ToArray(),
                ProofOfPassword = buffer.Slice(96, 32).ToArray(),
                Signature = buffer.Slice(HeaderSize, SignatureSize).ToArray()
            };
        }

        /// <summary>
        /// Serializes the hole punch packet to a byte buffer of length 192.
        /// </summary>
        public void Encode(Span<byte> destination)
        {
            if (destination.Length < PacketSize)
                throw new ArgumentException($"Destination span too small. Required: {PacketSize}, Actual: {destination.Length}", nameof(destination));

            WriteUnsignedHeader(destination[..HeaderSize]);
            Signature.AsSpan(0, SignatureSize).CopyTo(destination.Slice(HeaderSize, SignatureSize));
        }

        public byte[] Encode()
        {
            byte[] buffer = new byte[PacketSize];
            Encode(buffer);
            return buffer;
        }

        private void WriteUnsignedHeader(Span<byte> headerSpan)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(headerSpan[..4], MagicHeader);
            headerSpan[4] = Version;
            headerSpan[5] = Type;
            headerSpan[6] = (byte)Flags;
            headerSpan[7] = 0; // Reserved

            SenderId.TryWriteBytes(headerSpan.Slice(8, 16));
            RecipientId.TryWriteBytes(headerSpan.Slice(24, 16));
            BinaryPrimitives.WriteInt64LittleEndian(headerSpan.Slice(40, 8), TimestampUtcTicks);

            Nonce.AsSpan(0, 16).CopyTo(headerSpan.Slice(48, 16));
            SenderCertHash.AsSpan(0, 32).CopyTo(headerSpan.Slice(64, 32));
            ProofOfPassword.AsSpan(0, 32).CopyTo(headerSpan.Slice(96, 32));
        }

        /// <summary>
        /// Computes the Proof-of-Possession HMAC for password validation with zero allocations.
        /// </summary>
        public static void ComputeProofOfPassword(
            ReadOnlySpan<byte> headerPrefix96,
            ReadOnlySpan<byte> recipientCertHash32,
            ReadOnlySpan<byte> passwordHash,
            Span<byte> destinationPop32)
        {
            // Preimage covers Header bytes [0..96] (excluding PoP itself) + RecipientCertHash (32 bytes) = 128 bytes
            Span<byte> preimage = stackalloc byte[128];
            headerPrefix96.CopyTo(preimage[..96]);
            recipientCertHash32.CopyTo(preimage.Slice(96, 32));

            HMACSHA256.HashData(passwordHash, preimage, destinationPop32);
        }

        public static byte[] ComputeProofOfPassword(
            Guid senderId,
            Guid recipientId,
            long ticks,
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> senderCertHash,
            ReadOnlySpan<byte> recipientCertHash,
            byte[] passwordHash,
            PacketFlags flags)
        {
            Span<byte> headerPrefix = stackalloc byte[96];
            BinaryPrimitives.WriteUInt32LittleEndian(headerPrefix[..4], MagicHeader);
            headerPrefix[4] = CurrentVersion;
            headerPrefix[5] = MessageTypePunch;
            headerPrefix[6] = (byte)flags;
            headerPrefix[7] = 0;

            senderId.TryWriteBytes(headerPrefix.Slice(8, 16));
            recipientId.TryWriteBytes(headerPrefix.Slice(24, 16));
            BinaryPrimitives.WriteInt64LittleEndian(headerPrefix.Slice(40, 8), ticks);

            nonce.Slice(0, 16).CopyTo(headerPrefix.Slice(48, 16));
            senderCertHash.Slice(0, 32).CopyTo(headerPrefix.Slice(64, 32));

            byte[] result = new byte[32];
            ComputeProofOfPassword(headerPrefix, recipientCertHash, passwordHash, result);
            return result;
        }

        /// <summary>
        /// Validates anti-replay using monotonic timestamps and the sliding nonce cache.
        /// </summary>
        private static bool ValidateAntiReplay(ReadOnlySpan<byte> nonceSpan, long ticks, TimeSpan maxDrift, bool checkNonceCache)
        {
            long nowTicks = PreciseTime.GetCorrectTime().Ticks;
            long driftTicks = Math.Abs(nowTicks - ticks);
            if (driftTicks > maxDrift.Ticks)
                return false;

            if (!checkNonceCache)
                return true;

            var nonceGuid = MemoryMarshal.Read<Guid>(nonceSpan);

            // Periodically purge expired entries every 10 seconds
            if (nowTicks - Volatile.Read(ref s_lastPurgeTicks) > TimeSpan.FromSeconds(10).Ticks)
            {
                PurgeExpiredNonces(nowTicks);
                Volatile.Write(ref s_lastPurgeTicks, nowTicks);
            }

            // Expiry is 2x maxDrift into the future
            long expiryTicks = nowTicks + (maxDrift.Ticks * 2);

            // If already seen, this is a replay attack!
            if (!s_seenNonces.TryAdd(nonceGuid, expiryTicks))
            {
                return false;
            }

            return true;
        }

        private static void PurgeExpiredNonces(long nowTicks)
        {
            foreach (var kvp in s_seenNonces)
            {
                if (kvp.Value < nowTicks)
                {
                    s_seenNonces.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// Fast verification returning boolean with zero allocations on failure or success.
        /// </summary>
        public static bool TryVerifyFast(
            ReadOnlySpan<byte> data,
            Guid expectedRecipientId,
            ReadOnlySpan<byte> ourCertHash,
            ECDsa senderPublicKey,
            ReadOnlySpan<byte> localPasswordHash,
            TimeSpan? maxTimestampDrift = null,
            bool checkNonceCache = true)
        {
            if (data.Length < PacketSize)
                return false;

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
            if (magic != MagicHeader)
                return false;

            byte version = data[4];
            if (version != CurrentVersion)
                return false;

            byte type = data[5];
            if (type != MessageTypePunch)
                return false;

            var flags = (PacketFlags)data[6];

            var recipientId = new Guid(data.Slice(24, 16));
            if (recipientId != expectedRecipientId)
                return false;

            long ticks = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(40, 8));
            TimeSpan maxDrift = maxTimestampDrift ?? TimeSpan.FromSeconds(10);

            if (!ValidateAntiReplay(data.Slice(48, 16), ticks, maxDrift, checkNonceCache))
                return false;

            // Verify ECDSA signature of header [0..128]
            bool signatureValid = senderPublicKey.VerifyData(
                data[..HeaderSize],
                data.Slice(HeaderSize, SignatureSize),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            if (!signatureValid)
                return false;

            bool hasPasswordProof = (flags & PacketFlags.HasPasswordProof) != 0;
            bool localHasPassword = !localPasswordHash.IsEmpty;

            if (localHasPassword != hasPasswordProof)
                return false;

            if (localHasPassword)
            {
                Span<byte> expectedPop = stackalloc byte[32];
                ComputeProofOfPassword(data[..96], ourCertHash, localPasswordHash, expectedPop);

                if (!CryptographicOperations.FixedTimeEquals(data.Slice(96, 32), expectedPop))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Parses and cryptographically verifies an incoming single-packet hole punch.
        /// </summary>
        public static bool TryVerify(
            ReadOnlySpan<byte> data,
            Guid expectedRecipientId,
            byte[] ourCertHash,
            ECDsa senderPublicKey,
            byte[]? localPasswordHash,
            out QuicPunchHolePacket? packet,
            TimeSpan? maxTimestampDrift = null,
            bool checkNonceCache = true)
        {
            packet = null;

            if (!TryVerifyFast(
                data,
                expectedRecipientId,
                ourCertHash,
                senderPublicKey,
                localPasswordHash ?? ReadOnlySpan<byte>.Empty,
                maxTimestampDrift,
                checkNonceCache))
            {
                return false;
            }

            packet = Decode(data);
            return true;
        }
    }
}
