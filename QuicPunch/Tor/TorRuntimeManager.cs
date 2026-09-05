#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch;

public enum TorTransportMode
{
    AutoCascade = 0,
    Direct = 1,
    Snowflake = 2,
    Obfs4 = 3
}

public enum TorTransportTier
{
    Direct = 1,
    Snowflake = 2,
    Obfs4 = 3
}

public sealed class TorRuntimeManager : IAsyncDisposable
{
    public const string PinnedBundleVersion = "15.0.19";
    public const string PinnedTorVersion = "0.4.9.11";

    public const string WindowsX64Url =
        "https://archive.torproject.org/tor-package-archive/torbrowser/15.0.19/tor-expert-bundle-windows-x86_64-15.0.19.tar.gz";
    public const string WindowsX64Sha256 =
        "6ac067402c7b4a3dc37887ed3754b3914b67fdc220c966190683e9ccf91abf0f";

    public const string LinuxX64Url =
        "https://archive.torproject.org/tor-package-archive/torbrowser/15.0.19/tor-expert-bundle-linux-x86_64-15.0.19.tar.gz";
    public const string LinuxX64Sha256 =
        "5a8f19f5f119b5fa2a8fd799a3a532e3236ad36164241800d6302e32f0e1c2a9";

    private const long MaxBundleBytes = 128L * 1024 * 1024;
    private const int MaxLogLines = 300;

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    private readonly TorRuntimeOptions _options;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentQueue<string> _logs = new();

    private Process? _process;
    private TorControlClient? _control;
    private FileStream? _dataDirectoryLock;
    private string? _torExecutable;
    private string? _lyrebirdExecutable;
    private int _disposed;

    public TorRuntimeManager(TorRuntimeOptions? options = null)
    {
        _options = options ?? new TorRuntimeOptions();
        if (_options.SocksPort > 0)
            SocksPort = ValidatePort(_options.SocksPort, nameof(_options.SocksPort));
        if (_options.ControlPort > 0)
            ControlPort = ValidatePort(_options.ControlPort, nameof(_options.ControlPort));
    }

    public bool IsRunning =>
        _process is { HasExited: false };

    public int SocksPort { get; private set; }
    public int ControlPort { get; private set; }
    public TorTransportTier ActiveTransport { get; private set; } = TorTransportTier.Direct;

    public string InstallDirectory =>
        Path.GetFullPath(_options.InstallDirectory ?? GetDefaultInstallDirectory());

    public string DataDirectory =>
        Path.GetFullPath(_options.DataDirectory ?? GetDefaultDataDirectory());

    public string CookieFile =>
        Path.Combine(DataDirectory, "control_auth_cookie");

    public string? TorExecutablePath => _torExecutable;
    public string? LyrebirdExecutablePath => _lyrebirdExecutable;

    public IReadOnlyCollection<string> RecentLogs => _logs.ToArray();

    public event Action<string>? LogLine;

    internal TorControlClient ControlClient =>
        _control ?? throw new InvalidOperationException("Tor runtime has not been started.");

    public static async ValueTask<TorRuntimeManager> StartPortableAsync(
        TorRuntimeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var manager = new TorRuntimeManager(options);
        try
        {
            await manager.StartAsync(cancellationToken).ConfigureAwait(false);
            return manager;
        }
        catch
        {
            await manager.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static ValueTask StopPortableAsync(
        TorRuntimeManager? manager,
        CancellationToken cancellationToken = default) =>
        manager is null
            ? ValueTask.CompletedTask
            : manager.StopAsync(cancellationToken);

    public async ValueTask<string> EnsureInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        TorBundleDescriptor bundle = GetBundleForCurrentPlatform();
        string installDirectory = InstallDirectory;
        string markerPath = Path.Combine(installDirectory, ".quicpunch-tor-runtime.json");
        string parent = Path.GetDirectoryName(installDirectory)
            ?? throw new InvalidOperationException("Tor install directory must have a parent directory.");

        // Fast path: if tor already exists in the application directory or Tor folder, use it immediately
        if (TryFindPreExistingTorExecutable(installDirectory, out string? preExistingTor))
        {
            _torExecutable = preExistingTor;
            AddLog($"[TOR] Found existing Tor executable at {_torExecutable}");
            return preExistingTor!;
        }

        await InstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(parent);
            await using FileStream installLock = await AcquireExclusiveFileLockAsync(
                Path.Combine(parent, ".quicpunch-tor-install.lock"),
                cancellationToken).ConfigureAwait(false);

            if (TryFindPreExistingTorExecutable(installDirectory, out preExistingTor))
            {
                _torExecutable = preExistingTor;
                AddLog($"[TOR] Found existing Tor executable at {_torExecutable}");
                return preExistingTor!;
            }

            if (TryValidateExistingInstall(installDirectory, markerPath, bundle, out string? existingTor))
            {
                _torExecutable = existingTor;
                return existingTor;
            }

            string staging = Path.Combine(parent, ".tor-staging-" + Guid.NewGuid().ToString("N"));
            string archive = Path.Combine(parent, ".tor-download-" + Guid.NewGuid().ToString("N") + ".tar.gz");

            try
            {
                await DownloadVerifiedAsync(bundle, archive, cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(staging);
                await ExtractTarGzSafelyAsync(archive, staging, cancellationToken).ConfigureAwait(false);

                string torExecutable = FindTorExecutable(staging);
                EnsureExecutablePermission(torExecutable);

                string? stagedLyrebird = FindLyrebirdExecutable(staging);
                if (stagedLyrebird is not null)
                    EnsureExecutablePermission(stagedLyrebird);

                var marker = new TorInstallMarker(
                    PinnedBundleVersion,
                    PinnedTorVersion,
                    bundle.Platform,
                    bundle.Sha256.ToLowerInvariant(),
                    Path.GetRelativePath(staging, torExecutable));

                await File.WriteAllTextAsync(
                    Path.Combine(staging, ".quicpunch-tor-runtime.json"),
                    JsonSerializer.Serialize(marker),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);

                if (Directory.Exists(installDirectory))
                    Directory.Delete(installDirectory, recursive: true);

                Directory.Move(staging, installDirectory);

                _torExecutable = Path.Combine(installDirectory, marker.ExecutableRelativePath);
                EnsureExecutablePermission(_torExecutable);

                string? installedLyrebird = FindLyrebirdExecutable(installDirectory);
                if (installedLyrebird is not null)
                {
                    EnsureExecutablePermission(installedLyrebird);
                    _lyrebirdExecutable = installedLyrebird;
                }

                return _torExecutable;
            }
            finally
            {
                TryDeleteFile(archive);
                TryDeleteDirectory(staging);
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    public async ValueTask StartAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                return;

            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);

            string torExecutable = await EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(DataDirectory);
            _dataDirectoryLock = await AcquireExclusiveFileLockAsync(
                Path.Combine(DataDirectory, ".quicpunch-runtime.lock"),
                cancellationToken).ConfigureAwait(false);

            SocksPort = _options.SocksPort == 0
                ? ReserveEphemeralPort()
                : ValidatePort(_options.SocksPort, nameof(_options.SocksPort));

            ControlPort = _options.ControlPort == 0
                ? ReserveEphemeralPort(except: SocksPort)
                : ValidatePort(_options.ControlPort, nameof(_options.ControlPort));

            if (SocksPort == ControlPort)
                throw new InvalidOperationException("Tor SOCKS and Control ports must be different.");

            string torrc = Path.Combine(DataDirectory, "quicpunch.torrc");

            string? lyrebirdExecutable = _lyrebirdExecutable
                ?? FindLyrebirdExecutable(InstallDirectory)
                ?? FindLyrebirdExecutable(AppContext.BaseDirectory);
            if (lyrebirdExecutable is not null)
            {
                EnsureExecutablePermission(lyrebirdExecutable);
                _lyrebirdExecutable = lyrebirdExecutable;
            }

            IReadOnlyList<TorTransportTier> tiers = _options.TransportMode switch
            {
                TorTransportMode.Direct => new[] { TorTransportTier.Direct },
                TorTransportMode.Snowflake => new[] { TorTransportTier.Snowflake },
                TorTransportMode.Obfs4 => new[] { TorTransportTier.Obfs4 },
                _ => new[] { TorTransportTier.Direct, TorTransportTier.Snowflake, TorTransportTier.Obfs4 }
            };

            Exception? lastException = null;

            for (int i = 0; i < tiers.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tier = tiers[i];
                ActiveTransport = tier;
                bool isLastTier = (i == tiers.Count - 1);

                if (tier != TorTransportTier.Direct && string.IsNullOrEmpty(lyrebirdExecutable))
                {
                    AddLog($"[TOR] [Tier {i + 1}/{tiers.Count}] Skipping {tier}: Pluggable transport 'lyrebird' executable not found.");
                    if (isLastTier)
                    {
                        throw new FileNotFoundException(
                            $"Pluggable transport 'lyrebird' not found in '{InstallDirectory}'. Cannot bootstrap {tier}.",
                            lastException);
                    }
                    continue;
                }

                TimeSpan tierTimeout = CalculateTierTimeout(_options, tier, isLastTier);
                AddLog($"[TOR] [Tier {i + 1}/{tiers.Count}] Attempting connection via {tier} (timeout: {tierTimeout.TotalSeconds:F0}s)...");

                try
                {
                    await TryStartProcessAndBootstrapAsync(
                        torExecutable,
                        tier,
                        lyrebirdExecutable,
                        torrc,
                        tierTimeout,
                        cancellationToken).ConfigureAwait(false);

                    AddLog($"[TOR] Successfully connected and bootstrapped via {tier} (Tier {i + 1}/{tiers.Count})!");
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (!isLastTier)
                    {
                        AddLog($"[TOR] [Tier {i + 1}/{tiers.Count}] Connection via {tier} failed or timed out: {ex.Message}");
                        AddLog($"[TOR] Cascading to next tier...");
                        await StopChildProcessOnlyAsync().ConfigureAwait(false);
                    }
                }
            }

            throw new InvalidOperationException(
                $"Failed to bootstrap Tor across all configured tiers ({string.Join(" -> ", tiers)}). Last error: {lastException?.Message}",
                lastException);
        }
        catch
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private async ValueTask TryStartProcessAndBootstrapAsync(
        string torExecutable,
        TorTransportTier tier,
        string? lyrebirdExecutable,
        string torrc,
        TimeSpan bootstrapTimeout,
        CancellationToken cancellationToken)
    {
        TryDeleteFile(CookieFile);
        await WriteTorrcAsync(torrc, torExecutable, tier, lyrebirdExecutable, cancellationToken).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = torExecutable,
            WorkingDirectory = Path.GetDirectoryName(torExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(torrc);

        if (OperatingSystem.IsLinux())
        {
            string? ldLibraryPath = BuildLinuxLibraryPath(InstallDirectory);
            if (!string.IsNullOrWhiteSpace(ldLibraryPath))
            {
                if (startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out string? existing) &&
                    !string.IsNullOrWhiteSpace(existing))
                {
                    ldLibraryPath += Path.PathSeparator + existing;
                }
                startInfo.Environment["LD_LIBRARY_PATH"] = ldLibraryPath;
            }
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) AddLog("OUT " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AddLog("ERR " + e.Data); };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Unable to start the Tor process.");
        }
        catch
        {
            process.Dispose();
            throw;
        }

        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await WaitForControlSocketAndCookieAsync(process, cancellationToken).ConfigureAwait(false);

            _control = new TorControlClient(ControlPort, CookieFile);
            await _control.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await WaitForBootstrapAsync(process, bootstrapTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopChildProcessOnlyAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask StopChildProcessOnlyAsync()
    {
        TorControlClient? control = Interlocked.Exchange(ref _control, null);
        Process? process = Interlocked.Exchange(ref _process, null);

        if (control is not null)
        {
            try
            {
                using var signalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await control.SignalAsync("SHUTDOWN", signalTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddLog("Control shutdown warning: " + ex.Message);
            }
            finally
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                using var exitTimeout = new CancellationTokenSource(_options.ShutdownTimeout);
                try
                {
                    await process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            try { process.CancelOutputRead(); } catch { }
            try { process.CancelErrorRead(); } catch { }
            process.Dispose();
            TryDeleteFile(CookieFile);
        }
    }

    private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
    {
        await StopChildProcessOnlyAsync().ConfigureAwait(false);
        ReleaseDataDirectoryLock();
    }

    private void ReleaseDataDirectoryLock()
    {
        FileStream? dataLock = Interlocked.Exchange(ref _dataDirectoryLock, null);
        try { dataLock?.Dispose(); } catch { }
    }

    private async ValueTask WaitForControlSocketAndCookieAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + _options.StartupTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process);

            if (File.Exists(CookieFile) && new FileInfo(CookieFile).Length == 32 &&
                await IsPortOpenAsync(ControlPort, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Tor did not expose ControlPort {ControlPort} and SAFECOOKIE within {_options.StartupTimeout}.");
    }

    private async ValueTask WaitForBootstrapAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        int lastProgress = -1;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process);

            string? phase = null;
            try
            {
                phase = await ControlClient
                    .GetInfoAsync("status/bootstrap-phase", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                // Control port may still be completing handshake or warming up
            }

            if (!string.IsNullOrWhiteSpace(phase))
            {
                var match = Regex.Match(phase, @"PROGRESS=(\d{1,3})");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int progress))
                {
                    if (progress != lastProgress)
                    {
                        lastProgress = progress;
                        var summaryMatch = Regex.Match(phase, @"SUMMARY=""([^""]+)""");
                        string summary = summaryMatch.Success ? summaryMatch.Groups[1].Value : "Bootstrapping";
                        AddLog($"Bootstrapped {progress}%: {summary}");
                    }
                }

                if (phase.Contains("PROGRESS=100", StringComparison.OrdinalIgnoreCase) ||
                    phase.Contains("TAG=done", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog($"Tor bootstrap complete via {ActiveTransport}.");
                    return;
                }
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Tor did not finish bootstrapping via {ActiveTransport} within {timeout.TotalSeconds:F0}s.");
    }

    internal static TimeSpan CalculateTierTimeout(TorRuntimeOptions options, TorTransportTier tier, bool isLastTier)
    {
        if (options.TransportMode != TorTransportMode.AutoCascade)
            return options.BootstrapTimeout;

        TimeSpan baseTierTimeout = options.TierBootstrapTimeout > TimeSpan.Zero
            ? options.TierBootstrapTimeout
            : TimeSpan.FromSeconds(35);

        return tier switch
        {
            TorTransportTier.Direct => baseTierTimeout,
            TorTransportTier.Snowflake => baseTierTimeout + TimeSpan.FromSeconds(10),
            TorTransportTier.Obfs4 => isLastTier
                ? TimeSpan.FromSeconds(Math.Max(baseTierTimeout.TotalSeconds + 15, 50))
                : baseTierTimeout + TimeSpan.FromSeconds(15),
            _ => baseTierTimeout
        };
    }

    internal async ValueTask WriteTorrcAsync(
        string torrc,
        string torExecutable,
        TorTransportTier tier = TorTransportTier.Direct,
        string? lyrebirdExecutable = null,
        CancellationToken cancellationToken = default)
    {
        string? geoIp = FindFileRecursive(InstallDirectory, "geoip");
        string? geoIp6 = FindFileRecursive(InstallDirectory, "geoip6");

        int socksPort = SocksPort != 0 ? SocksPort : (_options.SocksPort != 0 ? _options.SocksPort : 9050);
        int controlPort = ControlPort != 0 ? ControlPort : (_options.ControlPort != 0 ? _options.ControlPort : 9051);

        var sb = new StringBuilder();
        sb.AppendLine("ClientOnly 1");
        sb.AppendLine($"DataDirectory {QuoteTorPath(DataDirectory)}");
        sb.AppendLine($"SocksPort 127.0.0.1:{socksPort} IsolateSOCKSAuth");
        sb.AppendLine($"ControlPort 127.0.0.1:{controlPort}");
        sb.AppendLine("CookieAuthentication 1");
        sb.AppendLine($"CookieAuthFile {QuoteTorPath(CookieFile)}");
        sb.AppendLine($"__OwningControllerProcess {Environment.ProcessId}");
        sb.AppendLine("Log notice stdout");

        if (geoIp is not null)
            sb.AppendLine($"GeoIPFile {QuoteTorPath(geoIp)}");
        if (geoIp6 is not null)
            sb.AppendLine($"GeoIPv6File {QuoteTorPath(geoIp6)}");

        if (tier == TorTransportTier.Snowflake)
        {
            if (string.IsNullOrWhiteSpace(lyrebirdExecutable))
                throw new InvalidOperationException("Snowflake transport requires 'lyrebird' executable.");

            sb.AppendLine("UseBridges 1");
            sb.AppendLine($"ClientTransportPlugin snowflake exec {QuoteTorPath(lyrebirdExecutable)}");

            var bridges = TorBridges.GetSnowflakeBridges(InstallDirectory, _options.CustomBridges);
            foreach (var bridge in bridges)
            {
                sb.AppendLine($"Bridge {bridge}");
            }
        }
        else if (tier == TorTransportTier.Obfs4)
        {
            if (string.IsNullOrWhiteSpace(lyrebirdExecutable))
                throw new InvalidOperationException("Obfs4 transport requires 'lyrebird' executable.");

            sb.AppendLine("UseBridges 1");
            sb.AppendLine($"ClientTransportPlugin obfs4 exec {QuoteTorPath(lyrebirdExecutable)}");

            var bridges = TorBridges.GetObfs4Bridges(InstallDirectory, _options.CustomBridges);
            foreach (var bridge in bridges)
            {
                sb.AppendLine($"Bridge {bridge}");
            }
        }

        foreach (string extra in _options.AdditionalTorrcLines)
        {
            if (string.IsNullOrWhiteSpace(extra))
                continue;
            if (extra.Contains('\r') || extra.Contains('\n'))
                throw new ArgumentException("AdditionalTorrcLines entries must contain one line each.");
            sb.AppendLine(extra);
        }

        await File.WriteAllTextAsync(torrc, sb.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask DownloadVerifiedAsync(
        TorBundleDescriptor bundle,
        string destination,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http
            .GetAsync(bundle.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long length && length > MaxBundleBytes)
            throw new InvalidDataException("Tor Expert Bundle is unexpectedly large.");

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;

        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
            if (total > MaxBundleBytes)
                throw new InvalidDataException("Tor Expert Bundle exceeded maximum permitted size.");

            hasher.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        string actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, bundle.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException(
                $"Tor Expert Bundle SHA-256 mismatch. Expected {bundle.Sha256}, got {actual}.");
        }
    }

    private static async ValueTask ExtractTarGzSafelyAsync(
        string archive,
        string destination,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);

        var deferredLinks = new System.Collections.Generic.List<(string Path, string LinkName, bool Symbolic)>();

        await using FileStream file = new(
            archive,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        using var tar = new TarReader(gzip, leaveOpen: false);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry(copyData: false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = NormalizeArchivePath(entry.Name);
            if (string.IsNullOrEmpty(relative))
                continue;

            string target = GetSafeExtractionPath(root, relative);

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (FileStream output = new(
                        target,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 128 * 1024,
                        useAsync: true))
                    {
                        if (entry.DataStream is not null)
                            await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
                    TrySetUnixMode(target, entry.Mode);
                    string targetName = Path.GetFileName(target);
                    if (string.Equals(targetName, "tor", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(targetName, "lyrebird", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(targetName, "conjure-client", StringComparison.OrdinalIgnoreCase))
                    {
                        EnsureExecutablePermission(target);
                    }
                    break;

                case TarEntryType.SymbolicLink:
                    deferredLinks.Add((target, entry.LinkName ?? string.Empty, true));
                    break;

                case TarEntryType.HardLink:
                    deferredLinks.Add((target, entry.LinkName ?? string.Empty, false));
                    break;

                default:
                    // Expert Bundles should not require devices/FIFOs. Ignore metadata-only entries.
                    break;
            }
        }

        foreach ((string path, string linkName, bool symbolic) in deferredLinks)
        {
            if (string.IsNullOrWhiteSpace(linkName))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (Path.IsPathRooted(linkName))
                throw new InvalidDataException("Tor archive contains an absolute link target.");

            string normalizedLink = NormalizeArchivePath(linkName);
            string baseDirectory = symbolic ? Path.GetDirectoryName(path)! : root;
            string resolved = Path.GetFullPath(Path.Combine(baseDirectory, normalizedLink));
            EnsureInsideRoot(root, resolved);

            if (symbolic)
            {
                if (OperatingSystem.IsWindows())
                {
                    // Creating symbolic links on Windows can require Developer Mode or
                    // elevated privileges. Materialize links from the trusted, verified
                    // bundle instead so the portable runtime never needs those privileges.
                    if (File.Exists(resolved))
                        File.Copy(resolved, path, overwrite: false);
                    else if (Directory.Exists(resolved))
                        CopyDirectory(resolved, path);
                    else
                        throw new InvalidDataException("Tor archive symlink target does not exist: " + linkName);
                }
                else
                {
                    string relativeTarget = Path.GetRelativePath(Path.GetDirectoryName(path)!, resolved);
                    File.CreateSymbolicLink(path, relativeTarget);
                }
            }
            else
            {
                if (!File.Exists(resolved))
                    throw new InvalidDataException("Tor archive hard-link target does not exist: " + linkName);
                // Copying a hard-link target avoids requiring the newer File.CreateHardLink API
                // and is fully sufficient for a small portable runtime bundle.
                File.Copy(resolved, path, overwrite: false);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static bool TryValidateExistingInstall(
        string installDirectory,
        string markerPath,
        TorBundleDescriptor bundle,
        out string? torExecutable)
    {
        torExecutable = null;
        try
        {
            if (!File.Exists(markerPath))
                return false;

            TorInstallMarker? marker = JsonSerializer.Deserialize<TorInstallMarker>(File.ReadAllText(markerPath));
            if (marker is null ||
                marker.BundleVersion != PinnedBundleVersion ||
                marker.TorVersion != PinnedTorVersion ||
                marker.Platform != bundle.Platform ||
                !string.Equals(marker.Sha256, bundle.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string executable = Path.GetFullPath(Path.Combine(installDirectory, marker.ExecutableRelativePath));
            EnsureInsideRoot(Path.GetFullPath(installDirectory), executable);
            if (!File.Exists(executable))
                return false;

            EnsureExecutablePermission(executable);
            torExecutable = executable;
            string? lyrebird = FindLyrebirdExecutable(installDirectory);
            if (lyrebird is not null)
                EnsureExecutablePermission(lyrebird);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindPreExistingTorExecutable(string installDirectory, out string? torExecutable)
    {
        torExecutable = null;
        string exeName = OperatingSystem.IsWindows() ? "tor.exe" : "tor";

        try
        {
            // 1. Look directly in AppContext.BaseDirectory (same directory as current binary)
            string directAppPath = Path.Combine(AppContext.BaseDirectory, exeName);
            if (File.Exists(directAppPath))
            {
                EnsureExecutablePermission(directAppPath);
                torExecutable = Path.GetFullPath(directAppPath);
                return true;
            }

            // 2. Look in AppContext.BaseDirectory/Tor/
            string appTorPath = Path.Combine(AppContext.BaseDirectory, "Tor", exeName);
            if (File.Exists(appTorPath))
            {
                EnsureExecutablePermission(appTorPath);
                torExecutable = Path.GetFullPath(appTorPath);
                return true;
            }

            // 3. Search in configured installDirectory if directory exists
            if (!string.IsNullOrWhiteSpace(installDirectory) && Directory.Exists(installDirectory))
            {
                string inInstallDir = Path.Combine(installDirectory, exeName);
                if (File.Exists(inInstallDir))
                {
                    EnsureExecutablePermission(inInstallDir);
                    torExecutable = Path.GetFullPath(inInstallDir);
                    return true;
                }

                try
                {
                    string found = FindTorExecutable(installDirectory);
                    if (File.Exists(found))
                    {
                        EnsureExecutablePermission(found);
                        torExecutable = Path.GetFullPath(found);
                        return true;
                    }
                }
                catch { }
            }

            // 4. Recursive search in AppContext.BaseDirectory/Tor if present
            string appTorDir = Path.Combine(AppContext.BaseDirectory, "Tor");
            if (Directory.Exists(appTorDir))
            {
                try
                {
                    string found = FindTorExecutable(appTorDir);
                    if (File.Exists(found))
                    {
                        EnsureExecutablePermission(found);
                        torExecutable = Path.GetFullPath(found);
                        return true;
                    }
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    private static TorBundleDescriptor GetBundleForCurrentPlatform()
    {
        Architecture architecture = RuntimeInformation.OSArchitecture;
        if (architecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                $"The pinned stable Tor Expert Bundle used by this class supports x64 here. Current architecture: {architecture}. " +
                "Do not silently fall back to an alpha Tor build; update the pinned descriptor explicitly when a stable build exists.");
        }

        if (OperatingSystem.IsWindows())
            return new("windows-x86_64", WindowsX64Url, WindowsX64Sha256);
        if (OperatingSystem.IsLinux())
            return new("linux-x86_64", LinuxX64Url, LinuxX64Sha256);

        throw new PlatformNotSupportedException("TorRuntimeManager currently supports Windows x64 and Linux x64.");
    }

    private static string FindTorExecutable(string root)
    {
        string expected = OperatingSystem.IsWindows() ? "tor.exe" : "tor";
        string[] candidates = Directory.GetFiles(root, expected, SearchOption.AllDirectories);

        if (candidates.Length == 0)
            throw new FileNotFoundException($"The Tor Expert Bundle did not contain {expected}.");

        // Prefer a path containing a directory literally named "tor".
        return candidates
            .OrderByDescending(p => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => string.Equals(part, "tor", StringComparison.OrdinalIgnoreCase)))
            .ThenBy(p => p.Length)
            .First();
    }

    public static string? FindLyrebirdExecutable(string root)
    {
        if (!Directory.Exists(root))
            return null;

        string expected = OperatingSystem.IsWindows() ? "lyrebird.exe" : "lyrebird";
        string[] candidates = Directory.GetFiles(root, expected, SearchOption.AllDirectories);

        if (candidates.Length == 0)
            return null;

        return candidates
            .OrderByDescending(p => p.Contains("pluggable_transports", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Length)
            .FirstOrDefault();
    }

    private static string? FindFileRecursive(string root, string exactName) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, exactName, SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault()
            : null;

    private static string? BuildLinuxLibraryPath(string root)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(root))
            return null;

        return string.Join(
            Path.PathSeparator,
            Directory.EnumerateFiles(root, "*.so*", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.Ordinal));
    }

    private static void EnsureExecutablePermission(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch (PlatformNotSupportedException)
        {
            // The subsequent Process.Start will give the meaningful failure.
        }
    }

    private static void TrySetUnixMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux())
            return;
        try { File.SetUnixFileMode(path, mode); }
        catch { }
    }

    private static string NormalizeArchivePath(string path) =>
        path.Replace('\\', '/').TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

    private static string GetSafeExtractionPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException("Archive entry is absolute.");

        string full = Path.GetFullPath(Path.Combine(root, relative));
        EnsureInsideRoot(root, full);
        return full;
    }

    private static void EnsureInsideRoot(string root, string candidate)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string normalizedCandidate = Path.GetFullPath(candidate);

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!normalizedCandidate.StartsWith(normalizedRoot, comparison) &&
            !string.Equals(Path.TrimEndingDirectorySeparator(normalizedCandidate), Path.TrimEndingDirectorySeparator(root), comparison))
        {
            throw new InvalidDataException("Path escapes the extraction root: " + candidate);
        }
    }

    private static async ValueTask<FileStream> AcquireExclusiveFileLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    useAsync: true);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ReserveEphemeralPort(int? except = null)
    {
        for (int i = 0; i < 16; i++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != except)
                return port;
        }
        throw new InvalidOperationException("Could not reserve a distinct local TCP port.");
    }

    private static int ValidatePort(int port, string name)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(name);
        return port;
    }

    private static string QuoteTorPath(string path) =>
        "\"" + Path.GetFullPath(path).Replace('\\', '/').Replace("\"", "\\\"") + "\"";

    private static string GetDefaultInstallDirectory() =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Tor",
            PinnedBundleVersion,
            OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64");

    private static string GetDefaultDataDirectory()
    {
        string basePath;
        if (OperatingSystem.IsWindows())
        {
            basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            basePath = !string.IsNullOrWhiteSpace(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(basePath, "QuicPunch", "TorData");
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QuicPunch-TorRuntime/1.0");
        return client;
    }

    private void AddLog(string line)
    {
        _logs.Enqueue(line);
        while (_logs.Count > MaxLogLines && _logs.TryDequeue(out _)) { }
        try { LogLine?.Invoke(line); } catch { }
    }

    private void ThrowIfExited(Process process)
    {
        if (!process.HasExited)
            return;

        string logDetails = string.Join("\n", _logs.ToArray());
        if (string.IsNullOrWhiteSpace(logDetails))
            logDetails = "(No log output recorded)";

        throw new InvalidOperationException(
            $"Tor exited unexpectedly with code {process.ExitCode}.\nTor Output Log:\n{logDetails}");
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record TorBundleDescriptor(
        string Platform,
        string Url,
        string Sha256);

    private sealed record TorInstallMarker(
        string BundleVersion,
        string TorVersion,
        string Platform,
        string Sha256,
        string ExecutableRelativePath);
}

public sealed class TorRuntimeOptions
{
    public string? InstallDirectory { get; init; }

    public string? DataDirectory { get; init; }

    public int SocksPort { get; init; }

    public int ControlPort { get; init; }

    public TorTransportMode TransportMode { get; init; } = TorTransportMode.AutoCascade;

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BootstrapTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan TierBootstrapTimeout { get; init; } = TimeSpan.FromSeconds(35);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public string[] AdditionalTorrcLines { get; init; } = Array.Empty<string>();
    public string[] CustomBridges { get; init; } = Array.Empty<string>();
}
