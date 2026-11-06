using System.Collections.Concurrent;
using QuicPunch.Helpers;

namespace QuicPunch.Datagrams;

/// <summary>
/// Framing state for one native connection or virtual session. The channel's MTU
/// already excludes any outer transport headers, including the session ID.
/// </summary>
internal sealed class CategorizedDatagramProtocol
{
    private readonly DatagramFrameReassembler _reassembler = new();
    private readonly ConcurrentDictionary<byte, Action<PeerInfo, QuicDatagramMessage>> _categoryHandlers = new();
    private uint _nextSequenceNumber;
    private int _nextFrameId;

    public event Action<PeerInfo, QuicDatagramMessage>? OnDatagramReceived;
    public event Action<PeerInfo, byte, ReadOnlyMemory<byte>, bool>? OnFrameReceived;

    public bool HasReceivers => !_categoryHandlers.IsEmpty || OnDatagramReceived != null || OnFrameReceived != null;

    public void RegisterCategory(byte category, Action<PeerInfo, QuicDatagramMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _categoryHandlers[category] = handler;
    }

    public bool SendDatagram(IQuicDatagramChannel? channel, byte category, ReadOnlySpan<byte> payload,
        QuicDatagramFlags flags = QuicDatagramFlags.None, uint sequenceNumber = 0)
    {
        if (channel == null || payload.Length > channel.MaxSendDatagramLength - QuicDatagramEnvelope.UnfragmentedHeaderSize)
            return false;

        if (sequenceNumber == 0)
            sequenceNumber = Interlocked.Increment(ref _nextSequenceNumber);

        int required = QuicDatagramEnvelope.UnfragmentedHeaderSize + payload.Length;
        Span<byte> buffer = required <= 2048 ? stackalloc byte[required] : new byte[required];
        QuicDatagramEnvelope.WriteEnvelope(buffer, category, flags, sequenceNumber, payload);
        return channel.Send(buffer);
    }

    public bool SendFrame(IQuicDatagramChannel? channel, byte category, ReadOnlySpan<byte> frameData,
        bool isKeyFrame = false, int maxFragmentSize = 1200)
    {
        if (channel == null) return false;
        if (maxFragmentSize <= 0) maxFragmentSize = 1200;
        maxFragmentSize = Math.Min(maxFragmentSize, channel.MaxSendDatagramLength - QuicDatagramEnvelope.FragmentedHeaderSize);
        if (maxFragmentSize <= 0) return false;

        if (frameData.Length <= maxFragmentSize)
            return SendDatagram(channel, category, frameData, isKeyFrame ? QuicDatagramFlags.IsKeyFrame : QuicDatagramFlags.None);

        int totalFragments = 1 + (frameData.Length - 1) / maxFragmentSize;
        if (totalFragments > 1024)
            throw new ArgumentException("Frame exceeds max 1024 fragments", nameof(frameData));

        ushort frameId = (ushort)(Interlocked.Increment(ref _nextFrameId) & 0x7FFF);
        Span<byte> buffer = new byte[QuicDatagramEnvelope.FragmentedHeaderSize + maxFragmentSize];
        bool allSent = true;
        for (int i = 0; i < totalFragments; i++)
        {
            int offset = i * maxFragmentSize;
            int length = Math.Min(maxFragmentSize, frameData.Length - offset);
            var flags = QuicDatagramFlags.IsFragmented;
            if (isKeyFrame) flags |= QuicDatagramFlags.IsKeyFrame;
            if (i == totalFragments - 1) flags |= QuicDatagramFlags.IsLastFragment;

            int written = QuicDatagramEnvelope.WriteFragmentEnvelope(buffer, category, flags,
                Interlocked.Increment(ref _nextSequenceNumber), frameId, (ushort)i, (ushort)totalFragments,
                frameData.Slice(offset, length));
            if (!channel.Send(buffer[..written])) allSent = false;
        }
        return allSent;
    }

    public void Receive(byte[] data, PeerInfo peer)
    {
        if (!QuicDatagramEnvelope.TryParse(data, out var envelope)) return;
        if (envelope.IsFragmented)
        {
            if (OnFrameReceived != null &&
                _reassembler.TryProcessFragment(envelope, out byte category, out var frame, out bool isKeyFrame))
                OnFrameReceived?.Invoke(peer, category, frame, isKeyFrame);
            return;
        }

        var message = new QuicDatagramMessage(peer, envelope.Category, envelope.Flags, envelope.SequenceNumber,
            data.AsMemory(envelope.IsFramed ? QuicDatagramEnvelope.UnfragmentedHeaderSize : 0), envelope.IsFramed);
        if (_categoryHandlers.TryGetValue(envelope.Category, out var handler))
            handler(peer, message);
        OnDatagramReceived?.Invoke(peer, message);
    }

    public void Clear() => _reassembler.Clear();
}
