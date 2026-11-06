using System.Buffers.Binary;
using System.Security.Cryptography;

namespace QuicPunch.Security;

/// <summary>
/// Versioned envelope binding the password/identity proof to one connection attempt
/// and an endpoint-specific return-routability challenge. Legacy punches cannot nominate paths.
/// </summary>
internal static class ConnectivityProbe
{
    private const uint Magic = 0x32504351; // QCP2
    private const int ProofOffset = 52;
    private const int SignedSize = ProofOffset + QuicPunchHolePacket.PacketSize;
    internal const int PacketSize = SignedSize + QuicPunchHolePacket.SignatureSize;

    internal static byte[] Create(Guid connectionId, Guid challenge, Guid echo,
        PeerInfo sender, PeerInfo recipient, ECDsa key, byte[]? passwordHash)
    {
        byte[] packet = new byte[PacketSize];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic);
        connectionId.TryWriteBytes(packet.AsSpan(4, 16));
        challenge.TryWriteBytes(packet.AsSpan(20, 16));
        echo.TryWriteBytes(packet.AsSpan(36, 16));
        if (!QuicPunchHolePacket.TryWritePacket(packet.AsSpan(ProofOffset), sender.Id, recipient.Id,
            sender.CertHash, recipient.CertHash, key, passwordHash ?? []))
            throw new CryptographicException("Cannot create connectivity identity proof.");
        key.SignData(packet.AsSpan(0, SignedSize), packet.AsSpan(SignedSize), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return packet;
    }

    internal static bool TryVerify(ReadOnlySpan<byte> packet, Guid connectionId,
        PeerInfo recipient, PeerInfo sender, byte[]? passwordHash, out Guid challenge, out Guid echo)
    {
        challenge = echo = Guid.Empty;
        if (packet.Length != PacketSize || BinaryPrimitives.ReadUInt32LittleEndian(packet) != Magic
            || new Guid(packet.Slice(4, 16)) != connectionId)
            return false;
        var proof = packet.Slice(ProofOffset, QuicPunchHolePacket.PacketSize);
        if (new Guid(proof.Slice(8, 16)) != sender.Id
            || !CryptographicOperations.FixedTimeEquals(proof.Slice(64, 32), sender.CertHash)
            || !sender.Curve.VerifyData(packet[..SignedSize], packet[SignedSize..], HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            || !QuicPunchHolePacket.TryVerifyFast(proof, recipient.Id, recipient.CertHash,
                sender.Curve, passwordHash ?? [], checkNonceCache: false))
            return false;
        // Retransmissions are allowed. A fresh challenge per endpoint and receive loop
        // prevents a captured proof from selecting a path, including in a later attempt.
        challenge = new Guid(packet.Slice(20, 16));
        echo = new Guid(packet.Slice(36, 16));
        return challenge != Guid.Empty;
    }
}
