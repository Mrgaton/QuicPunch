#nullable enable

using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using QuicPunch.Helpers;

namespace QuicPunch;

/// <summary>
/// QUIC-datagram compatibility channel for the Tor-backed dummy QUIC transport.
/// Tor is stream-only, so datagrams are carried as independently framed messages on
/// the persistent Tor message lane.  Delivery is therefore reliable/ordered at the
/// Tor/TCP layer, but the synchronous QUIC datagram API keeps bounded/drop-on-pressure
/// semantics so real-time callers are never forced to block.
/// </summary>
internal sealed class TorEmulatedDatagramChannel : IQuicDatagramChannel
{
    private const int QueueCapacity = 1024;

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendAsync;
    private readonly Channel<byte[]> _outgoing;
    private readonly Channel<byte[]> _incoming;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _sendPump;

    private int _sendFailed;
    private int _disposed;

    public TorEmulatedDatagramChannel(
        ushort maxSendDatagramLength,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendAsync)
    {
        if (maxSendDatagramLength == 0)
            throw new ArgumentOutOfRangeException(nameof(maxSendDatagramLength));

        _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        MaxSendDatagramLength = maxSendDatagramLength;

        _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        _incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });

        _sendPump = Task.Run(SendPumpAsync);
    }

    public bool IsSendEnabled =>
        Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _sendFailed) == 0;

    public bool IsReceiveEnabled => Volatile.Read(ref _disposed) == 0;

    public ushort MaxSendDatagramLength { get; }

    public Channel<byte[]> IncomingDatagrams => _incoming;

    public event Action<byte[]>? OnDatagramReceived;

    // A Tor onion connection has no meaningful IP path migration event.
    public event Action<IPEndPoint>? OnPeerAddressChanged
    {
        add { }
        remove { }
    }

    public bool Send(ReadOnlySpan<byte> datagram)
    {
        if (!IsSendEnabled || datagram.Length > MaxSendDatagramLength)
            return false;

        // IQuicDatagramChannel.Send is synchronous.  Queue the immutable copy and
        // report backpressure as a dropped datagram instead of blocking the caller.
        return _outgoing.Writer.TryWrite(datagram.ToArray());
    }

    internal void Dispatch(byte[] datagram)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (datagram.Length > MaxSendDatagramLength)
            return;

        _incoming.Writer.TryWrite(datagram);

        try
        {
            OnDatagramReceived?.Invoke(datagram);
        }
        catch (Exception ex)
        {
            // Application callbacks must not terminate the shared Tor message pump.
            QuicPunchLog.Error("[TorDatagram] Datagram receive callback failed.", ex);
        }
    }

    private async Task SendPumpAsync()
    {
        try
        {
            await foreach (byte[] datagram in _outgoing.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                await _sendAsync(datagram, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _sendFailed, 1);
            _outgoing.Writer.TryComplete(ex);
            QuicPunchLog.Error("[TorDatagram] Datagram send pump failed.", ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _outgoing.Writer.TryComplete();
        _incoming.Writer.TryComplete();
        _shutdown.Cancel();

        _ = _sendPump.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _shutdown,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
