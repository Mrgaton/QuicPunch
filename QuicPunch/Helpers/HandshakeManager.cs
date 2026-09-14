using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using QuicPunch;

namespace QuicPunch.Helpers
{
    public sealed class HandshakeManager
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<HandshakeDecision>> _pending = new();
        private readonly ConcurrentDictionary<Guid, byte> _activeRaises = new();

        public event Func<HandshakeRequest, CancellationToken, Task<HandshakeDecision>>? HandshakeRequested;

        public Task<HandshakeDecision> WaitForDecisionAsync(
            HandshakeRequest request,
            TimeSpan timeout,
            bool localRaise,
            CancellationToken ct)
        {
            var tcs = _pending.GetOrAdd(request.Id, _ =>
                new TaskCompletionSource<HandshakeDecision>(TaskCreationOptions.RunContinuationsAsynchronously));

            if (localRaise)
            {
                if (_activeRaises.TryAdd(request.Id, 0))
                {
                    _ = RaiseAsync(request, timeout, ct);
                }
            }
            else
            {
                var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);

                CancellationTokenRegistration registration = default;
                registration = timeoutCts.Token.Register(() =>
                {
                    Complete(request.Id, new HandshakeDecision(false, null, null));
                });

                tcs.Task.ContinueWith(_ =>
                {
                    registration.Dispose();
                    timeoutCts.Dispose();
                }, TaskContinuationOptions.ExecuteSynchronously);
            }

            return tcs.Task;
        }

        private async Task RaiseAsync(HandshakeRequest request, TimeSpan timeout, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                var handler = HandshakeRequested;
                if (handler is null)
                {
                    Complete(request.Id, new HandshakeDecision(false, null, null));
                    return;
                }

                var decision = await handler(request, timeoutCts.Token).WaitAsync(timeoutCts.Token);
                Complete(request.Id, decision);
            }
            catch (OperationCanceledException)
            {
                Complete(request.Id, new HandshakeDecision(false, null, null));
            }
            catch (Exception ex)
            {
                if (_pending.TryRemove(request.Id, out var tcs))
                    tcs.TrySetException(ex);
            }
        }
        public bool Reject(Guid id) => Complete(id, new HandshakeDecision(false, null, null));
        public bool Approve(Guid id, ushort port, IReadOnlyList<CandidateEndpoint>? candidates = null, CancellationTokenSource? cts = null) => Complete(id, new HandshakeDecision(true, port, cts?.Token, candidates));

        public int PendingDecisionsCount => _pending.Count;

        public void CancelAll()
        {
            _activeRaises.Clear();
            foreach (var kvp in _pending)
            {
                if (_pending.TryRemove(kvp.Key, out var tcs))
                {
                    tcs.TrySetResult(new HandshakeDecision(false, null, null));
                }
            }
        }

        private bool Complete(Guid id, HandshakeDecision decision)
        {
            _activeRaises.TryRemove(id, out _);
            if (!_pending.TryRemove(id, out var tcs))
                return false;

            return tcs.TrySetResult(decision);
        }
    }

    public sealed record HandshakeRequest(
        Guid Id,
        Guid ProtocolId,
        System.Net.IPEndPoint RemoteEndPoint,
        Guid PeerId = default,
        byte[]? CertHash = null);

    public sealed record HandshakeDecision(
        bool Accepted,
        ushort? Port,
        CancellationToken? Ct,
        IReadOnlyList<CandidateEndpoint>? Candidates = null);
}
