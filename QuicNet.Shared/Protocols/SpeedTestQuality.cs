using System.Collections.Concurrent;
using System.Diagnostics;
using QuicPunch;
using QuicPunch.Datagrams;

namespace QuicNet.Protocols;

public sealed partial class SpeedTestHandler
{
    public sealed record QualityMetrics(string Method, int Sent, int Received, int SendFailures,
        double? LossPercent, double? RttMinMs, double? RttAverageMs, double? RttMaxMs, double? JitterMs);

    // Jitter is the mean absolute difference between successive measured RTTs.
    internal static QualityMetrics SummarizeQuality(string method, int sent, int failures, IReadOnlyList<double> samples)
    {
        double? jitter = samples.Count < 2 ? null : samples.Zip(samples.Skip(1), (a, b) => Math.Abs(a - b)).Average();
        return new QualityMetrics(method, sent, samples.Count, failures,
            method == "QUIC DATAGRAM" && sent > 0 ? Math.Round(100.0 * (sent - samples.Count) / sent, 2) : null,
            samples.Count == 0 ? null : Math.Round(samples.Min(), 2),
            samples.Count == 0 ? null : Math.Round(samples.Average(), 2),
            samples.Count == 0 ? null : Math.Round(samples.Max(), 2),
            jitter.HasValue ? Math.Round(jitter.Value, 2) : null);
    }

    internal sealed class QualityProbe : IDisposable
    {
        private const byte Category = 240;
        private readonly SpeedTestSession _session;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _pending = new();
        private bool? _supported;
        private async Task<bool> CheckSupportAsync(CancellationToken ct)
        {
            if (_supported.HasValue) return _supported.Value;
            var id = Guid.NewGuid();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                await _session.SendFrameAsync(FrameType.Probe, id.ToByteArray(), ct).ConfigureAwait(false);
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                _supported = true;
            }
            catch (TimeoutException) { _supported = false; }
            finally { _pending.TryRemove(id, out _); }
            return _supported == true;
        }

        public bool UsesDatagrams => !_session.Connection.IsTor &&
            _session.Connection.DatagramChannel is { IsSendEnabled: true, IsReceiveEnabled: true };

        public QualityProbe(SpeedTestSession session)
        {
            _session = session;
            session.Connection.OnCategorizedDatagramReceived += OnDatagram;
        }

        private void OnDatagram(PeerInfo peer, QuicDatagramMessage message)
        {
            if (message.Category != Category || message.Payload.Length != 17) return;
            var payload = message.Payload.ToArray();
            if (payload[0] == 0)
            {
                payload[0] = 1;
                _session.Connection.SendCategorizedDatagram(Category, payload);
            }
            else if (payload[0] == 1)
                Complete(payload.AsMemory(1));
        }

        public void Complete(ReadOnlyMemory<byte> payload)
        {
            if (payload.Length == 16 && _pending.TryRemove(new Guid(payload.Span), out var completion))
                completion.TrySetResult(true);
        }

        public async Task<QualityMetrics> MeasureAsync(int count, CancellationToken ct)
        {
            // Verify protocol support over a reliable stream first. An older peer
            // ignoring probe frames must never be mistaken for 100% datagram loss.
            if (!await CheckSupportAsync(ct).ConfigureAwait(false))
                return SummarizeQuality("Sondas no compatibles con este peer", 0, 0, []);
            bool datagrams = UsesDatagrams;
            var samples = new List<double>();
            int sent = 0, failures = 0;
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                var id = Guid.NewGuid();
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[id] = completion;
                long start = Stopwatch.GetTimestamp();
                try
                {
                    bool accepted = true;
                    if (datagrams)
                    {
                        byte[] payload = new byte[17];
                        id.TryWriteBytes(payload.AsSpan(1));
                        accepted = _session.Connection.SendCategorizedDatagram(Category, payload);
                    }
                    else
                    {
                        using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        sendTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                        await _session.SendFrameAsync(FrameType.Probe, id.ToByteArray(), sendTimeout.Token).ConfigureAwait(false);
                    }
                    if (!accepted) failures++;
                    else
                    {
                        sent++;
                        try
                        {
                            await completion.Task.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                            samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        }
                        catch (TimeoutException) { }
                        catch (OperationCanceledException) { sent--; break; }
                    }
                }
                catch (OperationCanceledException) { break; }
                finally { _pending.TryRemove(id, out _); }
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            return SummarizeQuality(datagrams ? "QUIC DATAGRAM" : "Stream fiable", sent, failures, samples);
        }

        public void Dispose()
        {
            _session.Connection.OnCategorizedDatagramReceived -= OnDatagram;
            foreach (var completion in _pending.Values) completion.TrySetCanceled();
            _pending.Clear();
        }
    }
}
