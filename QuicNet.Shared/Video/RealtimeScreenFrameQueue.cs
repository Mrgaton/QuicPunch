using System.Diagnostics;
using System.Threading.Channels;

namespace QuicNet.Video;

// Never block the encoder on a slow network or preview. After dropping a packet,
// dependent frames must also be dropped until a fresh random-access frame.
internal sealed class RealtimeScreenFrameQueue(int capacity = 2, ScreenCaptureMetrics? statistics = null, Action? requestKeyFrame = null)
{
    private readonly Channel<(EncodedScreenFrame Frame, long Enqueued)> _frames = Channel.CreateBounded<(EncodedScreenFrame, long)>(new BoundedChannelOptions(capacity)
    { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false });
    private bool _awaitKey;

    public void Enqueue(EncodedScreenFrame frame)
    {
        if (_awaitKey && !frame.KeyFrame) { statistics?.AwaitingKey(); return; }
        if (!_frames.Writer.TryWrite((frame, Stopwatch.GetTimestamp())))
        {
            int discarded = frame.KeyFrame ? 0 : 1;
            while (_frames.Reader.TryRead(out _)) ++discarded;
            statistics?.Overflow(discarded);
            _awaitKey = true;
            if (!frame.KeyFrame) { requestKeyFrame?.Invoke(); return; }
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
            var age = Stopwatch.GetElapsedTime(item.Enqueued);
            if (age.TotalMilliseconds > 150)
            {
                if (!needsKey || item.Frame.KeyFrame) requestKeyFrame?.Invoke();
                statistics?.Expired(); needsKey = true; continue;
            }
            if (needsKey && !item.Frame.KeyFrame) { statistics?.AwaitingKey(); continue; }
            needsKey = false;
            statistics?.Submit(age);
            await output(item.Frame).ConfigureAwait(false);
        }
    }
}
