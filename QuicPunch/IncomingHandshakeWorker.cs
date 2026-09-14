namespace QuicPunch;

internal sealed class IncomingHandshakeWorker
{
    public Guid ConnectionGuid { get; }
    public long Generation { get; }
    public CancellationTokenSource Cts { get; }
    public Task Task { get; set; }

    public IncomingHandshakeWorker(Guid connectionGuid, long generation, CancellationTokenSource cts)
    {
        ConnectionGuid = connectionGuid;
        Generation = generation;
        Cts = cts;
        Task = Task.CompletedTask;
    }
}