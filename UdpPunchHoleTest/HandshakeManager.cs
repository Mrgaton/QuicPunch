using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace QuicPunch
{
    public sealed record HandshakeRequest(
       Guid Id,
       Guid ConnectionType,
       IPEndPoint RemoteEndPoint);

    public sealed record HandshakeDecision(
        bool Accepted,
        ushort? Port = null);

    public sealed class HandshakeManager
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<HandshakeDecision>> _pending = new();

        public event Func<HandshakeRequest, CancellationToken, Task<HandshakeDecision>>? HandshakeRequested;

        public Task<HandshakeDecision> WaitForDecisionAsync(
            HandshakeRequest request,
            TimeSpan timeout,
            bool localRaise,
            CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<HandshakeDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_pending.TryAdd(request.Id, tcs))
                throw new InvalidOperationException("Duplicate handshake request.");

            if (localRaise)
                _ = RaiseAsync(request, timeout, ct);
            
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
                    Complete(request.Id, new HandshakeDecision(false, null));
                    return;
                }

                var decision = await handler(request, timeoutCts.Token).WaitAsync(timeoutCts.Token);
                Complete(request.Id, decision);
            }
            catch (OperationCanceledException)
            {
                Complete(request.Id, new HandshakeDecision(false, null));
            }
            catch (Exception ex)
            {
                if (_pending.TryRemove(request.Id, out var tcs))
                    tcs.TrySetException(ex);
            }
        }

        public bool Approve(Guid id, ushort port) => Complete(id, new HandshakeDecision(true, port));
        public bool Reject(Guid id) => Complete(id, new HandshakeDecision(false, 0));

        private bool Complete(Guid id, HandshakeDecision decision)
        {
            if (!_pending.TryRemove(id, out var tcs))
                return false;

            return tcs.TrySetResult(decision);
        }
    }
}
