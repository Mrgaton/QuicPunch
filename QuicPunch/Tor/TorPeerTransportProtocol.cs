#nullable enable

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch;

internal enum TorLaneKind : byte
{
    Message = 1,
    QuicStream = 2,
    RawTcp = 3
}

internal readonly record struct TorLanePreface(
    TorLaneKind Kind,
    Guid ConnectionId,
    long StreamId,
    QuicStreamType StreamType,
    byte[] ConnectionToken,
    string SenderServiceId,
    int SenderVirtualPort);

internal static class TorPeerTransportProtocol
{
    private static readonly byte[] Magic = "QPT1"u8.ToArray();
    private const byte Version = 1;
    private const int TokenLength = 32;
    private const int ServiceIdLength = 56;

    // magic(4) version(1) kind(1) streamType(1) flags(1)
    // guid(16) streamId(8) token(32) senderServiceId(56) senderPort(2)
    public const int PrefaceLength = 122;

    public static async ValueTask WritePrefaceAsync(
        Stream stream,
        TorLanePreface preface,
        CancellationToken cancellationToken)
    {
        Validate(preface);

        byte[] buffer = new byte[PrefaceLength];
        int p = 0;
        Magic.CopyTo(buffer, p); p += 4;
        buffer[p++] = Version;
        buffer[p++] = (byte)preface.Kind;
        buffer[p++] = (byte)preface.StreamType;
        buffer[p++] = 0;

        preface.ConnectionId.TryWriteBytes(buffer.AsSpan(p, 16));
        p += 16;

        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(p, 8), preface.StreamId);
        p += 8;

        preface.ConnectionToken.CopyTo(buffer, p);
        p += TokenLength;

        Encoding.ASCII.GetBytes(preface.SenderServiceId, buffer.AsSpan(p, ServiceIdLength));
        p += ServiceIdLength;

        BinaryPrimitives.WriteUInt16BigEndian(
            buffer.AsSpan(p, 2),
            checked((ushort)preface.SenderVirtualPort));

        await TorManager.WriteAllAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<TorLanePreface> ReadPrefaceAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[PrefaceLength];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (!buffer.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Unknown Tor peer transport magic.");
        if (buffer[4] != Version)
            throw new InvalidDataException($"Unsupported Tor peer transport version {buffer[4]}.");

        TorLaneKind kind = buffer[5] switch
        {
            (byte)TorLaneKind.Message => TorLaneKind.Message,
            (byte)TorLaneKind.QuicStream => TorLaneKind.QuicStream,
            (byte)TorLaneKind.RawTcp => TorLaneKind.RawTcp,
            _ => throw new InvalidDataException("Unknown Tor lane kind.")
        };

        QuicStreamType streamType = buffer[6] switch
        {
            (byte)QuicStreamType.Unidirectional => QuicStreamType.Unidirectional,
            (byte)QuicStreamType.Bidirectional => QuicStreamType.Bidirectional,
            _ => throw new InvalidDataException("Invalid QUIC-like stream type in Tor preface.")
        };

        int p = 8;
        Guid connectionId = new(buffer.AsSpan(p, 16)); p += 16;
        long streamId = BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(p, 8)); p += 8;
        byte[] token = buffer.AsSpan(p, TokenLength).ToArray(); p += TokenLength;
        string sender = Encoding.ASCII.GetString(buffer, p, ServiceIdLength).ToLowerInvariant(); p += ServiceIdLength;
        int senderPort = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(p, 2));

        var preface = new TorLanePreface(
            kind,
            connectionId,
            streamId,
            streamType,
            token,
            sender,
            senderPort);

        Validate(preface);
        return preface;
    }

    public static TorLanePreface Create(
        TorLaneKind kind,
        Guid connectionId,
        long streamId,
        QuicStreamType streamType,
        byte[] token,
        TorOnionService localService) =>
        new(
            kind,
            connectionId,
            streamId,
            streamType,
            (byte[])token.Clone(),
            localService.ServiceId,
            localService.VirtualPort);

    private static void Validate(TorLanePreface preface)
    {
        if (preface.ConnectionId == Guid.Empty)
            throw new InvalidDataException("ConnectionId cannot be empty.");
        if (preface.ConnectionToken is null || preface.ConnectionToken.Length != TokenLength)
            throw new InvalidDataException("Connection token must be exactly 32 bytes.");
        if (preface.SenderServiceId is null || preface.SenderServiceId.Length != ServiceIdLength)
            throw new InvalidDataException("Sender service id must be a Tor v3 56-character id.");
        foreach (char c in preface.SenderServiceId)
        {
            if (!(c is >= 'a' and <= 'z' or >= '2' and <= '7'))
                throw new InvalidDataException("Sender service id contains invalid Base32 characters.");
        }
        if (preface.SenderVirtualPort is < 1 or > 65535)
            throw new InvalidDataException("Sender virtual port is invalid.");
        if (preface.Kind == TorLaneKind.QuicStream && preface.StreamId < 0)
            throw new InvalidDataException("QUIC-like stream id cannot be negative.");
    }
}
