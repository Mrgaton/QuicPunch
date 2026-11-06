using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using QuicPunch;

namespace QuicPunch.Helpers
{
    public sealed class HandshakeManager
    {
        private sealed record PendingDecision(HandshakeRequest Request, bool LocalRaise)
        {
            public TaskCompletionSource<HandshakeDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Started;
        }
        private readonly ConcurrentDictionary<Guid, PendingDecision> _pending = new();

        public event Func<HandshakeRequest, CancellationToken, Task<HandshakeDecision>>? HandshakeRequested;

        public Task<HandshakeDecision> WaitForDecisionAsync(
            HandshakeRequest request,
            TimeSpan timeout,
            bool localRaise,
            CancellationToken ct)
        {
            // Snapshot identity bytes so callers cannot change the expected signer later.
            request = request with
            {
                CertHash = request.CertHash?.ToArray(),
                RemoteEndPoint = new System.Net.IPEndPoint(request.RemoteEndPoint.Address, request.RemoteEndPoint.Port)
            };
            var pending = _pending.GetOrAdd(request.Id, _ => new PendingDecision(request, localRaise));
            if (pending.LocalRaise != localRaise || pending.Request.PeerId != request.PeerId
                || pending.Request.ProtocolId != request.ProtocolId || pending.Request.Transport != request.Transport
                || !Equals(pending.Request.RemoteEndPoint, request.RemoteEndPoint)
                || !(pending.Request.CertHash ?? []).AsSpan().SequenceEqual(request.CertHash ?? []))
                throw new InvalidOperationException("Handshake ID already belongs to another negotiation.");
            var tcs = pending.Completion;

            if (Interlocked.Exchange(ref pending.Started, 1) != 0)
                return tcs.Task;

            if (localRaise)
            {
                _ = RaiseAsync(pending, timeout, ct);
            }
            else
            {
                var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);

                CancellationTokenRegistration registration = default;
                registration = timeoutCts.Token.Register(() =>
                {
                    Complete(pending, new HandshakeDecision(false, null, null, FailureReason: ct.IsCancellationRequested ? "cancelled" : "timeout"));
                });

                tcs.Task.ContinueWith(_ =>
                {
                    registration.Dispose();
                    timeoutCts.Dispose();
                }, TaskContinuationOptions.ExecuteSynchronously);
            }

            return tcs.Task;
        }

        private async Task RaiseAsync(PendingDecision pending, TimeSpan timeout, CancellationToken ct)
        {
            var request = pending.Request;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                var handler = HandshakeRequested;
                if (handler is null)
                {
                    Complete(pending, new HandshakeDecision(false, null, null));
                    return;
                }

                var decision = await handler(request, timeoutCts.Token).WaitAsync(timeoutCts.Token);
                Complete(pending, decision);
            }
            catch (OperationCanceledException)
            {
                Complete(pending, new HandshakeDecision(false, null, null));
            }
            catch (Exception ex)
            {
                if (_pending.TryRemove(new KeyValuePair<Guid, PendingDecision>(request.Id, pending)))
                    pending.Completion.TrySetException(ex);
            }
        }
        public bool Reject(Guid id) => Complete(id, new HandshakeDecision(false, null, null));
        public bool Approve(Guid id, ushort port, IReadOnlyList<CandidateEndpoint>? candidates = null, CancellationTokenSource? cts = null) => Complete(id, new HandshakeDecision(true, port, cts?.Token, candidates));

        public int PendingDecisionsCount => _pending.Count;

        internal bool CompleteRemote(Guid id, Guid peerId, ReadOnlySpan<byte> certHash, Guid protocolId,
            QuicPunch.TransportType transport, System.Net.EndPoint remoteEndPoint, HandshakeDecision decision)
        {
            if (!_pending.TryGetValue(id, out var pending) || pending.LocalRaise)
                return false;
            var request = pending.Request;
            if (request.PeerId == Guid.Empty || request.PeerId != peerId || request.ProtocolId != protocolId
                || request.Transport != transport || request.CertHash is not { Length: > 0 }
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(request.CertHash, certHash)
                || (transport == QuicPunch.TransportType.Wan && !request.RemoteEndPoint.Equals(remoteEndPoint)))
                return false;

            if (!_pending.TryRemove(new KeyValuePair<Guid, PendingDecision>(id, pending)))
                return false;
            return pending.Completion.TrySetResult(decision);
        }

        public void CancelAll()
        {
            foreach (var kvp in _pending)
            {
                Complete(kvp.Value, new HandshakeDecision(false, null, null));
            }
        }

        private bool Complete(Guid id, HandshakeDecision decision)
        {
            return _pending.TryGetValue(id, out var pending) && Complete(pending, decision);
        }

        private bool Complete(PendingDecision pending, HandshakeDecision decision)
        {
            if (!_pending.TryRemove(new KeyValuePair<Guid, PendingDecision>(pending.Request.Id, pending)))
                return false;
            return pending.Completion.TrySetResult(decision);
        }
    }

    public sealed record HandshakeRequest(
        Guid Id,
        Guid ProtocolId,
        System.Net.IPEndPoint RemoteEndPoint,
        Guid PeerId = default,
        byte[]? CertHash = null,
        QuicPunch.TransportType Transport = QuicPunch.TransportType.Wan);

    public sealed record HandshakeDecision(
        bool Accepted,
        ushort? Port,
        CancellationToken? Ct,
        IReadOnlyList<CandidateEndpoint>? Candidates = null,
        string? FailureReason = null);
}
