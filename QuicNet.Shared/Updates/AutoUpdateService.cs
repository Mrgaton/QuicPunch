using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuicNet.Updates;

[JsonSerializable(typeof(UpdateStatus))]
internal partial class UpdateJsonContext : JsonSerializerContext { }

public sealed class AutoUpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _directory, _settings, _channelSettings, _publicKey;
    private readonly Func<string, UpdateArtifact, bool, Task<bool>> _installer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private UpdateArtifact? _available;
    private string? _readyPath;
    private readonly object _stateGate = new();
    private UpdateStatus _status;
    public UpdateStatus Status { get { lock (_stateGate) return _status; } }
    public event Action? StatusChanged;
    private void Set(Func<UpdateStatus, UpdateStatus> update)
    {
        lock (_stateGate)
        {
            var next = update(_status);
            if (next == _status) return;
            _status = next with { Revision = _status.Revision + 1 };
        }
        foreach (Action observer in StatusChanged?.GetInvocationList() ?? [])
            try { observer(); } catch { /* A UI observer cannot interrupt an update. */ }
    }
    public string? ReadyPath => _readyPath;
    public void ResetInstallAttempt()
    {
        Set(s => s.Stage == "installing" && _readyPath != null
            ? s with { Stage = "ready", Message = "Actualización lista. Puedes volver a abrir el instalador." } : s);
    }

    public AutoUpdateService(string directory, string platform,
        Func<string, UpdateArtifact, bool, Task<bool>> installer, HttpMessageHandler? handler = null, string? publicKey = null)
    {
        _directory = Path.Combine(directory, "updates");
        Directory.CreateDirectory(_directory);
        _settings = Path.Combine(_directory, "automatic.txt");
        _channelSettings = Path.Combine(_directory, "channel.txt");
        bool automatic = !File.Exists(_settings) || File.ReadAllText(_settings).Trim() != "false";
        string channel = File.Exists(_channelSettings) ? File.ReadAllText(_channelSettings).Trim() : ReleaseIdentity.Channel;
        if (!ReleaseIdentity.IsValidChannel(channel)) channel = "stable";
        _status = new(ReleaseIdentity.Version, ReleaseIdentity.Build, platform, automatic,
            Channel: channel, CurrentChannel: ReleaseIdentity.Channel);
        // Enrollment must survive installing a stable binary on a beta device,
        // including when the user has never touched the channel selector.
        if (!File.Exists(_channelSettings) || File.ReadAllText(_channelSettings).Trim() != channel) PersistChannel(channel);
        _installer = installer;
        _publicKey = publicKey ?? UpdateManifest.ReadPublicKey();
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuicNet/" + ReleaseIdentity.Version);
    }

    public void SetAutomatic(bool enabled)
    {
        string temporary = _settings + ".tmp";
        File.WriteAllText(temporary, enabled ? "true" : "false");
        File.Move(temporary, _settings, true);
        Set(s => s with { Automatic = enabled });
    }

    public async Task SetChannelAsync(string channel, CancellationToken token = default)
    {
        if (!ReleaseIdentity.IsValidChannel(channel)) throw new ArgumentException("Elige el canal estable o beta.");
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("Espera a que termine la actualización para cambiar de canal.");
        try
        {
            if (Status.Stage == "installing") throw new InvalidOperationException("La instalación ya está en curso.");
            if (Status.Channel == channel) return;
            PersistChannel(channel);
            // A previously downloaded package must never be installed after switching feeds.
            _available = null;
            _readyPath = null;
            Set(s => s with { Channel = channel, Stage = "idle", LatestVersion = null, LatestBuild = null, LatestChannel = null,
                Downloaded = 0, Total = 0, LastCheck = null, Message = "Canal cambiado. Buscando nuevas versiones…" });
        }
        finally { _gate.Release(); }
    }

    private void PersistChannel(string channel)
    {
        string temporary = _channelSettings + ".tmp";
        File.WriteAllText(temporary, channel);
        File.Move(temporary, _channelSettings, true);
    }

    private async Task<(UpdateArtifact? Artifact, Exception? Error)> ReadFeedAsync(string channel, CancellationToken token)
    {
        try
        {
            string feedUrl = ReleaseIdentity.GetFeedUrl(channel);
            using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl + "?check=" + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60).ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Avoid the CDN's immutable cache without sending its challenge-triggering no-cache header.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
            response.EnsureSuccessStatusCode();
            using var input = await response.Content.ReadAsStreamAsync(token);
            byte[] bytes = await ReadLimitedAsync(input, UpdateManifest.MaximumManifestBytes, token);
            // Each feed retains its own signed channel identity, even for beta subscribers.
            return (UpdateManifest.Parse(bytes, new Uri(feedUrl), Status.Platform, _publicKey, channel), null);
        }
        catch (Exception ex) { return (null, ex); }
    }

    public void Start(CancellationToken lifetime)
    {
        _ = RunAsync(lifetime);
    }

    private async Task RunAsync(CancellationToken lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _stop.Token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), linked.Token);
            while (!linked.IsCancellationRequested)
            {
                if (Status.Automatic) await CheckAsync(true, linked.Token);
                await Task.Delay(Status.Stage == "error" ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(15), linked.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task CheckAsync(bool installAutomatically = false, CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        try
        {
            if (Status.Stage == "installing") return;
            Set(s => s with { Stage = "checking", Message = "Buscando actualizaciones…", LastCheck = DateTimeOffset.UtcNow });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            string[] channels = Status.Channel == "beta" ? ["stable", "beta"] : ["stable"];
            var results = await Task.WhenAll(channels.Select(channel => ReadFeedAsync(channel, timeout.Token)));
            linked.Token.ThrowIfCancellationRequested();
            var candidate = results.Where(result => result.Artifact != null).Select(result => result.Artifact!)
                .OrderByDescending(artifact => artifact.Build).ThenBy(artifact => artifact.Channel == "stable" ? 0 : 1).FirstOrDefault();
            // A newer release replaces a pending beta. An offline/older feed must
            // still preserve a package whose download has already been verified.
            if (_readyPath != null && _available != null && (candidate == null || candidate.Build < _available.Build || candidate == _available))
            {
                Set(s => s with { Stage = "ready", Message = "Actualización verificada y lista para instalar." });
                if (installAutomatically && Status.Automatic) await InstallCoreAsync(true, linked.Token);
                return;
            }
            if (candidate == null)
            {
                _available = null;
                if (results.Any(result => result.Error != null)) throw results.First(result => result.Error != null).Error!;
                Set(s => s with { Stage = "current", LatestVersion = null, LatestBuild = null, LatestChannel = null, Downloaded = 0, Total = 0,
                    Message = "Todavía no hay versiones publicadas para este canal." });
                return;
            }
            if (candidate != _available) _readyPath = null;
            _available = candidate;
            Set(s => s with { LatestVersion = _available.Version, LatestBuild = _available.Build, LatestChannel = _available.Channel });
            // Build is monotonically increasing across PC and Android; CDN rollback cannot downgrade clients.
            if (_available.Build <= ReleaseIdentity.Build)
            {
                _available = null;
                Set(s => s with { Stage = "current", Message = s.CurrentChannel != s.Channel
                    ? "No hay una compilación más reciente en este canal. Se conservará la versión instalada hasta que se publique una superior."
                    : "Tienes la última versión.", Downloaded = 0, Total = 0 });
                return;
            }
            Set(s => s with { Stage = "available", Message = "Hay una actualización disponible." });
            if (installAutomatically && Status.Automatic)
            {
                string failed = Path.Combine(_directory, "release-" + _available.Build + (UpdateManifest.PlatformExtension(Status.Platform) + ".failed"));
                if (File.Exists(failed))
                {
                    Set(s => s with { Stage = "error", Message = "La instalación anterior falló. Se conserva la versión actual; espera otra publicación o pulsa Instalar para reintentar." });
                    return;
                }
                await DownloadCoreAsync(linked.Token);
                if (Status.Automatic) await InstallCoreAsync(true, linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex) { Set(s => s with { Stage = "error", Message = ex is OperationCanceledException ? "El CDN no respondió a tiempo. Se reintentará automáticamente." : ex.Message }); }
        finally { _gate.Release(); }
    }

    public async Task InstallAsync(CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        try
        {
            if (_available == null) throw new InvalidOperationException("Comprueba primero si hay una actualización disponible.");
            if (_readyPath == null) await DownloadCoreAsync(linked.Token);
            await InstallCoreAsync(false, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex) { Set(s => s with { Stage = "error", Message = ex.Message }); }
        finally { _gate.Release(); }
    }

    private async Task DownloadCoreAsync(CancellationToken token)
    {
        var artifact = _available!;
        string destination = Path.Combine(_directory, "release-" + artifact.Build + UpdateManifest.PlatformExtension(Status.Platform));
        string temporary = destination + ".partial";
        string wirePath = artifact.Compressed == null ? temporary : temporary + ".zst";
        var wire = artifact.Compressed is { } compressed ? artifact with { Url = compressed.Url, Sha256 = compressed.Sha256, Size = compressed.Size, Compressed = null } : artifact;
        Set(s => s with { Stage = "downloading", Downloaded = 0, Total = wire.Size, Message = artifact.Compressed == null ? "Descargando actualización…" : "Descargando actualización Zstandard…" });
        try
        {
            if (File.Exists(destination))
            {
                try { await UpdateManifest.VerifyFileAsync(destination, artifact, token); _readyPath = destination; return; }
                catch (InvalidDataException) { File.Delete(destination); }
            }
            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            downloadTimeout.CancelAfter(TimeSpan.FromMinutes(10));
            token = downloadTimeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, wire.Url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long size && size != wire.Size)
                throw new InvalidDataException("El tamaño publicado no coincide con la descarga.");
            using var input = await response.Content.ReadAsStreamAsync(token);
            await using (var output = new FileStream(wirePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > wire.Size) throw new InvalidDataException("La descarga supera el tamaño firmado.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    Set(s => s with { Downloaded = total });
                }
                await output.FlushAsync(token);
            }
            await UpdateManifest.VerifyFileAsync(wirePath, wire, token);
            if (artifact.Compressed != null)
            {
                Set(s => s with { Message = "Descomprimiendo y verificando en este dispositivo…" });
                await UpdateCompression.DecompressAsync(wirePath, temporary, artifact.Size, token);
            }
            await UpdateManifest.VerifyFileAsync(temporary, artifact, token);
            File.Move(temporary, destination, true);
            _readyPath = destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); if (wirePath != temporary && File.Exists(wirePath)) File.Delete(wirePath); }
        Set(s => s with { Stage = "ready", Message = "Actualización verificada y lista para instalar." });
    }

    private async Task InstallCoreAsync(bool automatic, CancellationToken token)
    {
        if (_readyPath == null || _available == null) return;
        await UpdateManifest.VerifyFileAsync(_readyPath, _available, token);
        Set(s => s with { Stage = "ready", Message = "Actualización verificada. Preparando instalación…" });
        bool started = await _installer(_readyPath, _available, automatic);
        Set(s => s with { Stage = started ? "installing" : "ready", Message = started
            ? (Status.Platform != "android-arm64" ? "Reiniciando para actualizar…" : "Confirma la instalación en Android.")
            : (Status.Platform == "android-arm64" ? "Lista para instalar. Pulsa Instalar para permitir esta fuente y confirmar en Android." : "Lista para instalar. Se espera a que terminen las llamadas.") });
    }

    internal static async Task<byte[]> ReadLimitedAsync(Stream input, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Respuesta demasiado grande.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public void Dispose() { _stop.Cancel(); /* In-flight requests observe cancellation before the loop exits. */ }
}
