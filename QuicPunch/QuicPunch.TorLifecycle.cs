using QuicPunch.Discovery;
using QuicPunch.Discovery.PortMapping;
using QuicPunch.Helpers;
using QuicPunch.PacketHandler;
using QuicPunch.Security;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunch
{
    // Tor startup and shutdown.
    public partial class QuicPunch
    {
        private readonly SemaphoreSlim _torLifecycleLock = new(1, 1);
        private CancellationTokenSource? _torLoopCts;
        private Task? _torAcceptLoopTask;
        private readonly ConcurrentDictionary<Guid, Task> _torReceiveLoopTasks = new();
        public async Task StartTorAsync(int virtualPort = 0, TorRuntimeOptions? options = null, TorManager? existingTorManager = null, CancellationToken cancellationToken = default)
        {
            EnsureStarted();
            await _torLifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureStarted();
                if (IsTorStarted)
                    return;
                TorLastError = null;
                await StartTorCoreAsync(virtualPort, options, existingTorManager, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await StopTorCoreAsync().ConfigureAwait(false);
                TorLastError = ex.Message;
                TorBootstrapStatus = $"Failed: {ex.Message}";
                throw;
            }
            finally
            {
                _torLifecycleLock.Release();
            }
        }

        public TorRuntimeOptions? DefaultTorRuntimeOptions { get; set; }

        private async Task StartTorCoreAsync(int virtualPort, TorRuntimeOptions? options, TorManager? existingTorManager, CancellationToken cancellationToken)
        {
            options ??= DefaultTorRuntimeOptions;
            QuicPunchLog.Info("[TOR SERVER] Initializing Tor runtime...");

            try { _torLoopCts?.Cancel(); _torLoopCts?.Dispose(); } catch { }
            _torLoopCts = CancellationTokenSource.CreateLinkedTokenSource(LifecycleToken, cancellationToken);
            CancellationToken torToken = _torLoopCts.Token;

            Directory.CreateDirectory(NodeAppDataPath);
            string identityPath = Path.Combine(NodeAppDataPath, "tor_identity.key");

            if (File.Exists(identityPath))
            {
                try
                {
                    string keyBase64 = await File.ReadAllTextAsync(identityPath, torToken).ConfigureAwait(false);
                    TorIdentity = TorIdentity.FromPrivateKeyBase64(keyBase64);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[TOR SERVER] Warning: Failed to load saved identity: {ex.Message}. Generating new one.");
                    TorIdentity = TorIdentity.CreateRandom();
                    await File.WriteAllTextAsync(identityPath, TorIdentity.PrivateKeyBase64, torToken).ConfigureAwait(false);
                }
            }
            else
            {
                TorIdentity = TorIdentity.CreateRandom();
                await File.WriteAllTextAsync(identityPath, TorIdentity.PrivateKeyBase64, torToken).ConfigureAwait(false);
            }

            int resolvedPort = virtualPort > 0
                ? virtualPort
                : Utilities.GetDeterministicPortFromCertHash(TorCertManager.CertPublicHash);

            if (existingTorManager != null)
            {
                TorManager = existingTorManager;
                _ownsTorManager = false;
            }
            else
            {
                var runtimeOptions = options != null
                    ? new TorRuntimeOptions
                    {
                        ExecutablePath = options.ExecutablePath,
                        DataDirectory = options.DataDirectory ?? Path.Combine(NodeAppDataPath, "TorData"),
                        InstallDirectory = options.InstallDirectory,
                        SocksPort = options.SocksPort,
                        ControlPort = options.ControlPort,
                        TransportMode = options.TransportMode,
                        StartupTimeout = options.StartupTimeout,
                        BootstrapTimeout = options.BootstrapTimeout,
                        TierBootstrapTimeout = options.TierBootstrapTimeout,
                        ShutdownTimeout = options.ShutdownTimeout,
                        AdditionalTorrcLines = options.AdditionalTorrcLines,
                        CustomBridges = options.CustomBridges
                    }
                    : new TorRuntimeOptions
                    {
                        DataDirectory = Path.Combine(NodeAppDataPath, "TorData")
                    };

                var trm = new TorRuntimeManager(runtimeOptions);
                TorBootstrapStatus = "Bootstrapping...";
                TorBootstrapProgress = 0;
                trm.LogLine += (line) =>
                {
                    if (line.Contains("Bootstrapped ", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = line.IndexOf("Bootstrapped ", StringComparison.OrdinalIgnoreCase);
                        string progress = line[idx..];
                        TorBootstrapStatus = $"{trm.ActiveTransport}: {progress}";
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"\b(\d{1,3})%");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int pVal))
                        {
                            TorBootstrapProgress = pVal;
                        }
                        WriteLine($"[TOR SERVER] [{trm.ActiveTransport}] {progress}");
                    }
                    else if (line.Contains("Cascading to ", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("Attempting connection via ", StringComparison.OrdinalIgnoreCase))
                    {
                        TorBootstrapStatus = line.Trim();
                        TorBootstrapProgress = 0;
                        WriteLine($"[TOR SERVER] {line.Trim()}");
                    }
                };

                try { await trm.StartAsync(torToken).ConfigureAwait(false); }
                catch { await trm.DisposeAsync().ConfigureAwait(false); throw; }
                TorManager = new TorManager(trm, ownsRuntime: true);
                _ownsTorManager = true;
            }

            TorHub = await TorPeerTransportHub.CreateAsync(TorManager, TorIdentity, resolvedPort, torToken).ConfigureAwait(false);

            TorOnionAddress = TorHub.OnionAddress;
            TorCurrentPeer.OnionAddress = TorHub.OnionAddress;
            TorCurrentPeer.PortArray = [(ushort)resolvedPort];
            TorCurrentPeer.ConnectionFlags.PortMode = PortMode.Single;
            TorCurrentPeer.ConnectionFlags.IsTor = true;
            InvalidateTokenCache();
            TorBootstrapProgress = 100;
            TorBootstrapStatus = $"Active (100%) [{TorManager.ActiveTransport}]";
            TorLastError = null;
            WriteLine($"[TOR SERVER] Hidden Service active at: {TorOnionAddress}:{resolvedPort} (via {TorManager.ActiveTransport})");

            _torAcceptLoopTask = Task.Run(() => AcceptTorLoopAsync(torToken), CancellationToken.None);

            if (Discovery.TorNostrDiscoveryEnabled && IsStarted)
            {
                try
                {
                    await Discovery.StartTorNostrDiscoveryAsync(torToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[NOSTR-TOR] Discovery start warning: {ex.Message}");
                }
            }
        }

        public async Task StopTorAsync()
        {
            await _torLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopTorCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _torLifecycleLock.Release();
            }
        }

        private async Task StopTorCoreAsync()
        {
            TorBootstrapStatus = "Stopping...";
            try { await Discovery.StopTorNostrDiscoveryAsync().ConfigureAwait(false); } catch { }
            try { _torLoopCts?.Cancel(); } catch { }
            await CloseTransportSessionsAsync(TransportType.Tor).ConfigureAwait(false);
            if (TorHub != null)
            {
                try { await TorHub.DisposeAsync().ConfigureAwait(false); } catch { }
                TorHub = null;
            }
            if (TorManager != null)
            {
                if (_ownsTorManager) { try { await TorManager.DisposeAsync().ConfigureAwait(false); } catch { } }
                _ownsTorManager = false;
                TorManager = null;
            }
            await AwaitTorWorkersAsync().ConfigureAwait(false);
            TorOnionAddress = null;
            if (CurrentPeer != null)
            {
                CurrentPeer.OnionAddress = null;
            }
            if (TorCurrentPeer != null)
            {
                TorCurrentPeer.OnionAddress = null;
            }
            InvalidateTokenCache();
            TorBootstrapStatus = "Stopped";
            TorBootstrapProgress = 0;
            TorLastError = null;
        }

    }
}
