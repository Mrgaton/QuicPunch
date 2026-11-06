using System.Diagnostics;
using System.Threading.Channels;

namespace QuicNet.Video;

// Never block the encoder on a slow network or preview. After dropping a packet,
// dependent frames must also be dropped until a fresh random-access frame.
internal sealed class RealtimeScreenFrameQueue
{
    private readonly Channel<(EncodedScreenFrame Frame, long Enqueued)> _frames = Channel.CreateBounded<(EncodedScreenFrame, long)>(new BoundedChannelOptions(2)
    { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false });
    private bool _awaitKey;

    public void Enqueue(EncodedScreenFrame frame)
    {
        if (_awaitKey && !frame.KeyFrame) return;
        if (!_frames.Writer.TryWrite((frame, Stopwatch.GetTimestamp())))
        {
            while (_frames.Reader.TryRead(out _)) { }
            _awaitKey = true;
            if (!frame.KeyFrame) return;
            _frames.Writer.TryWrite((frame, Stopwatch.GetTimestamp()));
        }
        _awaitKey = false;
    }

    public void Complete(Exception? error = null) => _frames.Writer.TryComplete(error);

    public async Task DrainAsync(Func<EncodedScreenFrame, Task> output, CancellationToken ct)
    {
        bool needsKey = true;
        await foreach (var item in _frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (Stopwatch.GetElapsedTime(item.Enqueued).TotalMilliseconds > 150) { needsKey = true; continue; }
            if (needsKey && !item.Frame.KeyFrame) continue;
            needsKey = false;
            await output(item.Frame).ConfigureAwait(false);
        }
    }
}
