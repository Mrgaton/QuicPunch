namespace QuicNet.Networking;

// All UI service changes share this gate, including the individual controls in Settings.
internal sealed class ConnectionModeController(
    Func<(bool Wan, bool Tor)> desired,
    Action<bool, bool> persist,
    Func<bool, int, CancellationToken, Task> setWan,
    Func<bool, int, CancellationToken, Task> setTor,
    Action changed)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal Task SetModeAsync(string mode, CancellationToken ct) => mode switch
    {
        "wan" => ChangeAsync(true, false, null, 0, ct),
        "tor" => ChangeAsync(false, true, null, 0, ct),
        "both" => ChangeAsync(true, true, null, 0, ct),
        _ => throw new InvalidDataException("Elige WAN, Solo Tor o Ambas.")
    };

    internal Task SetServiceAsync(string service, bool enabled, int port, CancellationToken ct) =>
        service is "wan" or "tor" ? ChangeAsync(enabled, enabled, service, port, ct)
        : throw new InvalidDataException("Servicio desconocido.");

    private async Task ChangeAsync(bool wan, bool tor, string? service, int port, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = desired();
            if (service == "wan") tor = previous.Tor;
            if (service == "tor") wan = previous.Wan;
            // Persist the requested state before touching transports. Never restore WAN after a Tor failure.
            persist(wan, tor);
            changed();
            if (!wan) await setWan(false, 0, ct).ConfigureAwait(false);
            if (!tor) await setTor(false, 0, ct).ConfigureAwait(false);
            if (wan) await setWan(true, service == "wan" ? port : 0, ct).ConfigureAwait(false);
            if (tor) await setTor(true, service == "tor" ? port : 0, ct).ConfigureAwait(false);
        }
        finally { changed(); _gate.Release(); }
    }
}
