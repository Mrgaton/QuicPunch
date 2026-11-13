namespace QuicPunch.Discovery.PortMapping;

/// <summary>Owns one router lease, including renewal and bounded deletion.</summary>
internal sealed class ManagedPortMapping : IAsyncDisposable
{
    private readonly PortMappingCoordinator _mapper = new();
    private readonly CancellationTokenSource _stop;
    private readonly int _port;
    private readonly Action<PortMappingResult?> _changed;
    private Task _worker = Task.CompletedTask;
    private int _disposeStarted;
    private int _stopDisposed;
    public PortMappingCoordinator Mapper => _mapper;
    public PortMappingResult? Result { get; private set; }
    public bool IsDisposed => Volatile.Read(ref _disposeStarted) != 0;

    private ManagedPortMapping(int port, Action<PortMappingResult?> changed, CancellationToken token)
    {
        _port = port;
        _changed = changed;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    public static async Task<ManagedPortMapping> StartAsync(int port, Action<PortMappingResult?> changed, CancellationToken token, CancellationToken initialAttempt = default)
    {
        var lease = new ManagedPortMapping(port, changed, token);
        try
        {
            await lease.RenewAsync(initialAttempt).ConfigureAwait(false);
            lease._worker = lease.RunAsync();
            return lease;
        }
        catch
        {
            await lease.DeleteAsync().ConfigureAwait(false);
            lease._mapper.Dispose();
            lease._stop.Dispose();
            throw;
        }
    }

    private async Task RenewAsync(CancellationToken attempt = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, attempt);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        PortMappingResult? result = null;
        try { result = await _mapper.TryMapPortAsync(_port, Result?.ExternalPort ?? _port, lifetime: TimeSpan.FromMinutes(2), ct: timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested && !attempt.IsCancellationRequested) { }
        if (result is { Success: false })
            global::QuicPunch.QuicPunchLog.Info($"[PORT-MAP] {result.ProtocolName}: {result.ErrorMessage}");
        Result = result is { Success: true } ? result : null;
        NotifyChanged(Result);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                double seconds = Result is { Lifetime.TotalSeconds: > 0 } result
                    ? Math.Clamp(result.Lifetime.TotalSeconds / 2, 0.1, 3600) : 15;
                await Task.Delay(TimeSpan.FromSeconds(seconds), _stop.Token).ConfigureAwait(false);
                await RenewAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            NotifyChanged(null);
            try { await DeleteAsync().ConfigureAwait(false); }
            finally { _mapper.Dispose(); }
        }
    }

    private async Task DeleteAsync()
    {
        if (_mapper.LastResult is not { Success: true } last) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _mapper.TryUnmapPortAsync(last.ExternalPort, ct: timeout.Token).ConfigureAwait(false); } catch { }
    }

    private void NotifyChanged(PortMappingResult? result)
    {
        try { _changed(result); }
        catch (Exception ex)
        {
            global::QuicPunch.QuicPunchLog.Info($"[PORT-MAP] Mapping change callback notice: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        try { await _worker.ConfigureAwait(false); }
        finally
        {
            if (Interlocked.Exchange(ref _stopDisposed, 1) == 0)
                _stop.Dispose();
        }
    }
}
