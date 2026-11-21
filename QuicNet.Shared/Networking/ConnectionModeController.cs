namespace QuicNet.Networking;

internal sealed record ConnectionModeTransition(long id, string target, string phase, string? error = null);

// Acceptance can supersede a request immediately. Only one worker may touch the transports at a time.
internal sealed class ConnectionModeController(
    Func<(bool Wan, bool Tor)> desired,
    Action<bool, bool> persist,
    Func<bool, int, CancellationToken, Task> setWan,
    Func<bool, int, CancellationToken, Task> setTor,
    Action changed)
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private Operation? _current;
    private ConnectionModeTransition? _status;
    private long _nextId;

    internal ConnectionModeTransition? Status
    {
        get { lock (_stateGate) return _status; }
    }

    internal (ConnectionModeTransition Accepted, Task<ConnectionModeTransition> Completion) RequestMode(string mode, CancellationToken ct) => mode switch
    {
        "wan" => Enqueue(true, false, null, 0, ct),
        "tor" => Enqueue(false, true, null, 0, ct),
        "both" => Enqueue(true, true, null, 0, ct),
        _ => throw new InvalidDataException("Elige WAN, Solo Tor o Ambas.")
    };

    internal async Task SetServiceAsync(string service, bool enabled, int port, CancellationToken ct)
    {
        if (service is not ("wan" or "tor")) throw new InvalidDataException("Servicio desconocido.");
        CheckResult(await Enqueue(enabled, enabled, service, port, ct).Completion.ConfigureAwait(false), ct);
    }

    internal async Task RestoreTorAsync(CancellationToken ct)
    {
        Task<ConnectionModeTransition> completion;
        lock (_stateGate)
        {
            // A user request already reconciles both transports with the saved preferences.
            if (_status != null || !desired().Tor) return;
            completion = Enqueue(false, true, "restore", 0, ct).Completion;
        }
        var result = await completion.ConfigureAwait(false);
        if (result.phase != "superseded") CheckResult(result, ct);
    }

    private (ConnectionModeTransition Accepted, Task<ConnectionModeTransition> Completion) Enqueue(
        bool wan, bool tor, string? service, int port, CancellationToken ct)
    {
        ConnectionModeTransition accepted;
        Task<ConnectionModeTransition> completion;
        lock (_stateGate)
        {
            ct.ThrowIfCancellationRequested();
            var previous = desired();
            if (service is "tor" or "restore") wan = previous.Wan;
            if (service == "wan") tor = previous.Tor;
            var target = new Target(wan, tor, service == "wan" ? port : 0, service == "tor" ? port : 0);
            if (_current is { } current && current.Target == target) return (_status!, current.Completion);

            // Save the explicit choice before applying it. Failed Tor startup never enables WAN.
            if ((wan, tor) != previous) persist(wan, tor);
            bool cancelingTor = _current?.Target.Tor == true && !tor;
            if (_current != null) _current.Cancellation = _current.Stop.CancelAsync();
            accepted = new(++_nextId, target.Mode, cancelingTor ? "canceling-tor" : "applying");
            var operation = new Operation(target, accepted, service == "restore", CancellationTokenSource.CreateLinkedTokenSource(ct));
            _current = operation;
            _status = accepted;
            completion = operation.Completion = Task.Run(() => ApplyAsync(operation));
        }
        changed();
        return (accepted, completion);
    }

    private async Task<ConnectionModeTransition> ApplyAsync(Operation operation)
    {
        var token = operation.Stop.Token;
        var result = operation.Accepted with { phase = "ready" };
        bool entered = false;
        try
        {
            await _applyGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            token.ThrowIfCancellationRequested();
            if (operation.RestoreOnly)
                await setTor(true, 0, token).ConfigureAwait(false);
            else
                await ApplyTargetAsync(operation, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { result = result with { phase = "failed", error = ex.Message }; }
        finally
        {
            bool publish;
            lock (_stateGate)
            {
                publish = _current == operation;
                if (!publish || token.IsCancellationRequested) result = result with { phase = "superseded", error = null };
                if (publish)
                {
                    _current = null;
                    _status = result;
                }
            }
            if (entered) _applyGate.Release();
            if (publish) changed();
            try { await operation.Cancellation.ConfigureAwait(false); }
            catch (Exception ex) { Diagnostics.CoreErrorReporter.Report("Cancelar cambio de red", ex); }
            operation.Stop.Dispose();
        }
        return result;
    }

    private async Task ApplyTargetAsync(Operation operation, CancellationToken token)
    {
        var target = operation.Target;
        if (!target.Wan) await setWan(false, 0, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!target.Tor) await setTor(false, 0, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (operation.Accepted.phase == "canceling-tor")
        {
            lock (_stateGate)
            {
                if (_current == operation) _status = operation.Accepted with { phase = "applying" };
            }
            changed();
        }
        if (target.Wan) await setWan(true, target.WanPort, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (target.Tor) await setTor(true, target.TorPort, token).ConfigureAwait(false);
    }

    private static void CheckResult(ConnectionModeTransition result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (result.phase == "failed") throw new InvalidOperationException(result.error);
        if (result.phase == "superseded") throw new InvalidOperationException("Otra selección de red sustituyó este cambio.");
    }

    private sealed record Target(bool Wan, bool Tor, int WanPort, int TorPort)
    {
        internal string Mode => Wan ? Tor ? "both" : "wan" : Tor ? "tor" : "off";
    }

    private sealed class Operation(Target target, ConnectionModeTransition accepted, bool restoreOnly, CancellationTokenSource stop)
    {
        internal Target Target { get; } = target;
        internal ConnectionModeTransition Accepted { get; } = accepted;
        internal bool RestoreOnly { get; } = restoreOnly;
        internal CancellationTokenSource Stop { get; } = stop;
        internal Task<ConnectionModeTransition> Completion { get; set; } = null!;
        internal Task Cancellation { get; set; } = Task.CompletedTask;
    }
}
