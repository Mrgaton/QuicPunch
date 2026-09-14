using System.Buffers.Binary;

namespace QuicPunch.Datagrams;

/// <summary>
/// Stack-allocated, zero-copy envelope for categorized RFC 9221 QUIC datagrams.
/// </summary>
public readonly ref struct QuicDatagramEnvelope
{
    public const byte MagicMarker = 0xFD;
    public const int UnfragmentedHeaderSize = 7; // Magic (1) + Category (1) + Flags (1) + SeqNum (4)
    public const int FragmentedHeaderSize = 13;  // Unfragmented (7) + FrameId (2) + FragIndex (2) + TotalFrags (2)

    public readonly byte Category;
    public readonly QuicDatagramFlags Flags;
    public readonly uint SequenceNumber;
    public readonly ushort FrameId;
    public readonly ushort FragmentIndex;
    public readonly ushort TotalFragments;
    public readonly ReadOnlySpan<byte> Payload;
    public readonly bool IsFramed;

    public bool IsFragmented => Flags.HasFlag(QuicDatagramFlags.IsFragmented);
    public bool IsKeyFrame => Flags.HasFlag(QuicDatagramFlags.IsKeyFrame);
    public bool IsLastFragment => Flags.HasFlag(QuicDatagramFlags.IsLastFragment);

    public QuicDatagramEnvelope(
        byte category,
        QuicDatagramFlags flags,
        uint sequenceNumber,
        ushort frameId,
        ushort fragmentIndex,
        ushort totalFragments,
        ReadOnlySpan<byte> payload,
        bool isFramed = true)
    {
        Category = category;
        Flags = flags;
        SequenceNumber = sequenceNumber;
        FrameId = frameId;
        FragmentIndex = fragmentIndex;
        TotalFragments = totalFragments;
        Payload = payload;
        IsFramed = isFramed;
    }

    /// <summary>
    /// Attempts to parse a datagram payload into a <see cref="QuicDatagramEnvelope"/>.
    /// If the datagram does not start with <see cref="MagicMarker"/> or is shorter than 7 bytes,
    /// it is treated transparently as a raw unfragmented datagram with Category = Generic (0).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out QuicDatagramEnvelope envelope)
    {
        if (data.Length >= UnfragmentedHeaderSize && data[0] == MagicMarker)
        {
            byte category = data[1];
            var flags = (QuicDatagramFlags)data[2];
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(3, 4));

            if (flags.HasFlag(QuicDatagramFlags.IsFragmented))
            {
                if (data.Length < FragmentedHeaderSize)
                {
                    envelope = default;
                    return false;
                }

                ushort frameId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(7, 2));
                ushort fragIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(9, 2));
                ushort totalFrags = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(11, 2));
                ReadOnlySpan<byte> payload = data.Slice(FragmentedHeaderSize);

                envelope = new QuicDatagramEnvelope(category, flags, seq, frameId, fragIndex, totalFrags, payload, isFramed: true);
                return true;
            }
            else
            {
                ReadOnlySpan<byte> payload = data.Slice(UnfragmentedHeaderSize);
                envelope = new QuicDatagramEnvelope(category, flags, seq, 0, 0, 1, payload, isFramed: true);
                return true;
            }
        }

        // Raw datagram without 0xFD magic header -> categorized as Generic (0)
        envelope = new QuicDatagramEnvelope(QuicDatagramCategory.Generic, QuicDatagramFlags.None, 0, 0, 0, 1, data, isFramed: false);
        return true;
    }

    /// <summary>
    /// Writes an unfragmented categorized datagram header and payload into the destination buffer.
    /// </summary>
    public static int WriteEnvelope(Span<byte> destination, byte category, QuicDatagramFlags flags, uint sequenceNumber, ReadOnlySpan<byte> payload)
    {
        int required = UnfragmentedHeaderSize + payload.Length;
        if (destination.Length < required)
            throw new ArgumentException("Destination buffer too small", nameof(destination));

        destination[0] = MagicMarker;
        destination[1] = category;
        destination[2] = (byte)(flags & ~QuicDatagramFlags.IsFragmented);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(3, 4), sequenceNumber);
        payload.CopyTo(destination.Slice(UnfragmentedHeaderSize));
        return required;
    }

    /// <summary>
    /// Writes a fragmented categorized datagram header and chunk payload into the destination buffer.
    /// </summary>
    public static int WriteFragmentEnvelope(
        Span<byte> destination,
        byte category,
        QuicDatagramFlags flags,
        uint sequenceNumber,
        ushort frameId,
        ushort fragmentIndex,
        ushort totalFragments,
        ReadOnlySpan<byte> chunkPayload)
    {
        int required = FragmentedHeaderSize + chunkPayload.Length;
        if (destination.Length < required)
            throw new ArgumentException("Destination buffer too small", nameof(destination));

        destination[0] = MagicMarker;
        destination[1] = category;
        destination[2] = (byte)(flags | QuicDatagramFlags.IsFragmented);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(3, 4), sequenceNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(7, 2), frameId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(9, 2), fragmentIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(11, 2), totalFragments);
        chunkPayload.CopyTo(destination.Slice(FragmentedHeaderSize));
        return required;
    }
}

/// <summary>
/// Well-known categories for RFC 9221 QUIC datagrams.
/// Applications can use these constants or any custom byte value (0-255).
/// </summary>
public static class QuicDatagramCategory
{
    public const byte Generic = 0;
    public const byte VideoKeyFrame = 1;
    public const byte VideoDeltaFrame = 2;
    public const byte Audio = 3;
    public const byte ScreenSlice = 4;
    public const byte CursorInput = 5;
    public const byte Control = 6;
    public const byte Telemetry = 7;
    public const byte Custom = 8;
}

/// <summary>
/// Flags for categorized QUIC datagram framing.
/// </summary>
[Flags]
public enum QuicDatagramFlags : byte
{
    None = 0,
    IsFragmented = 1 << 0,
    IsKeyFrame = 1 << 1,
    IsLastFragment = 1 << 2
}

/// <summary>
/// Managed, heap-safe categorized datagram message received from a verified peer.
/// </summary>
public sealed class QuicDatagramMessage
{
    public PeerInfo Peer { get; }
    public byte Category { get; }
    public QuicDatagramFlags Flags { get; }
    public uint SequenceNumber { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public bool IsFramed { get; }

    public bool IsKeyFrame => Flags.HasFlag(QuicDatagramFlags.IsKeyFrame);

    public QuicDatagramMessage(PeerInfo peer, byte category, QuicDatagramFlags flags, uint sequenceNumber, ReadOnlyMemory<byte> payload, bool isFramed = true)
    {
        Peer = peer;
        Category = category;
        Flags = flags;
        SequenceNumber = sequenceNumber;
        Payload = payload;
        IsFramed = isFramed;
    }
}