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
    // WAN startup, listener rebinding and shutdown.
    public partial class QuicPunch
    {
        public const int SioUdpConnReset = unchecked((int)0x9800000C);
        public const int SioUdpNetReset = unchecked((int)0x9800000F);

        public static void ConfigureUdpSocket(UdpClient client)
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (!OperatingSystem.IsWindows())
            {
                try { client.Client.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)15, true); } catch { }
            }
            if (OperatingSystem.IsWindows())
            {
                byte[] optionInValue = new byte[] { 0 };
                client.Client.IOControl(SioUdpConnReset, optionInValue, null);
                client.Client.IOControl(SioUdpNetReset, optionInValue, null);
            }
        }

        public UdpClient? udp = null;
        public int LocalDiscoveryPort { get; private set; } //Random.Shared.Next(1, 1024);
        public int LocalBoundPort => udp != null && udp.Client.LocalEndPoint is IPEndPoint ip ? ip.Port : LocalPort;

        public bool RebindListenerPort(ushort newPort)
        {
            EnsureStarted();
            _lifecycleLock.Wait();
            try
            {
                if (LifecycleState != QuicPunchLifecycleState.Started || CancellationSource.IsCancellationRequested)
                    return false;

                if (!IsWanStarted)
                {
                    LocalPort = newPort;
                    LocalDiscoveryPort = newPort;
                    return true;
                }

                long generation = _lifecycleGeneration;
                CancellationToken token = _wanLoopCts?.Token ?? CancellationSource.Token;
                var oldUdp = udp;
                var newUdp = EndpointAddressCodec.CreateUdpClient();
                ConfigureUdpSocket(newUdp);
                newUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                newUdp.Client.Bind(new IPEndPoint(EndpointAddressCodec.BindAddress(newUdp), newPort));
                if (newUdp.Client.AddressFamily == AddressFamily.InterNetwork) newUdp.Client.DontFragment = true;

                udp = newUdp;
                LocalDiscoveryPort = ((IPEndPoint)newUdp.Client.LocalEndPoint!).Port;
                LocalPort = LocalDiscoveryPort;

                _advertisedMapping = null;
                IsUpnpMapped = false;
                ActivePortMappingProtocol = null;
                ResetNatMapping();
                NatCoordinator.SetUdpClient(newUdp);
                _upnpMappingTask = StartControlMappingAsync(newUdp, token);

                try { oldUdp?.Close(); oldUdp?.Dispose(); } catch { }

                var receiveTask = Task.Run(() => ReceiveUdpLoopAsync(newUdp, token), token);
                _receiveLoopTask = _receiveLoopTask == null ? receiveTask : Task.WhenAll(_receiveLoopTask, receiveTask);

                var refreshTask = Task.Run(() => RefreshAfterRebindAsync(newUdp, generation, token), token);
                _rebindRefreshTask = _rebindRefreshTask == null ? refreshTask : Task.WhenAll(_rebindRefreshTask, refreshTask);
                return true;
            }
            catch (Exception ex)
            {
                QuicPunchLog.Error($"[QuicPunch] Failed to rebind listener port to {newPort}", ex);
                return false;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private async Task RefreshAfterRebindAsync(UdpClient expectedUdp, long generation, CancellationToken token)
        {
            try
            {
                await StunRequest(resetOnFailure: true, cancellationToken: token, generation: generation, expectedUdp: expectedUdp).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    QuicPunchLog.Info($"[QuicPunch] STUN request after rebind warning: {ex.Message}");
            }

            if (!IsLifecycleWorkerCurrent(generation, token, expectedUdp))
                return;

            await Discovery.PublishNostrDiscoveryAsync(token).ConfigureAwait(false);
        }

        private Task? _receiveLoopTask;
        private Task? _stunLoopTask;
        private Task? _stunCatalogTask;
        private Task? _rebindRefreshTask;
        private readonly SemaphoreSlim _wanLifecycleLock = new(1, 1);
        private CancellationTokenSource? _wanLoopCts;
        public bool IsWanStarted => udp != null && LifecycleState == QuicPunchLifecycleState.Started;
        public string? WanLastError { get; private set; }
        public bool WanEnabled { get; set; } = true;

        public async Task StartWanAsync(ushort port = 0, CancellationToken cancellationToken = default)
        {
            EnsureStarted();
            await _wanLifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureStarted();
                if (IsWanStarted)
                    return;
                WanLastError = null;
                await StartWanCoreAsync(port > 0 ? port : LocalPort, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WanLastError = ex.Message;
                throw;
            }
            finally
            {
                _wanLifecycleLock.Release();
            }
        }

        private async Task StartWanCoreAsync(int port, CancellationToken cancellationToken)
        {
            QuicPunchLog.Info("[WAN] Starting WAN UDP service...");
            Helpers.MsQuicTuner.EnsurePathMtuDiscovery();
            try { _wanLoopCts?.Cancel(); _wanLoopCts?.Dispose(); } catch { }
            _wanLoopCts = CancellationTokenSource.CreateLinkedTokenSource(LifecycleToken, cancellationToken);
            CancellationToken wanToken = _wanLoopCts.Token;

            var newUdp = EndpointAddressCodec.CreateUdpClient();
            ConfigureUdpSocket(newUdp);
            int bindPort = port > 0 ? port : (LocalPort > 0 ? LocalPort : 0);
            if (bindPort > 0)
            {
                try { newUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
            }

            try
            {
                newUdp.Client.Bind(new IPEndPoint(EndpointAddressCodec.BindAddress(newUdp), bindPort));
            }
            catch (SocketException) when (bindPort > 0)
            {
                newUdp.Dispose();
                newUdp = EndpointAddressCodec.CreateUdpClient();
                ConfigureUdpSocket(newUdp);
                newUdp.Client.Bind(new IPEndPoint(EndpointAddressCodec.BindAddress(newUdp), 0));
            }
            if (newUdp.Client.AddressFamily == AddressFamily.InterNetwork) newUdp.Client.DontFragment = true;

            udp = newUdp;
            LocalDiscoveryPort = ((IPEndPoint)newUdp.Client.LocalEndPoint!).Port;
            LocalPort = LocalDiscoveryPort;
            ResetNatMapping();
            CurrentPeer.Addresses = Utilities.GetValidLocalIPAddresses().OrderBy(a => a.AddressFamily).ThenBy(a => a.ToString(), StringComparer.Ordinal).ToArray();

            _upnpMappingTask = StartControlMappingAsync(newUdp, wanToken);

            UdpClient listenerUdp = udp;
            _receiveLoopTask = Task.Run(() => ReceiveUdpLoopAsync(listenerUdp, wanToken), wanToken);
            if (_autoConnectTask == null || _autoConnectTask.IsCompleted)
                _autoConnectTask = Task.Run(() => AutoConnectSavedPeersAsync(wanToken), wanToken);

            await Discovery.StartLanDiscoveryAsync(wanToken).ConfigureAwait(false);
            _stunLoopTask = Task.Run(async () =>
            {
                while (!wanToken.IsCancellationRequested)
                {
                    try { await Discovery.SendLocalLanDiscoveryAsync().ConfigureAwait(false); }
                    catch (OperationCanceledException) when (wanToken.IsCancellationRequested) { break; }
                    catch (Exception ex) { QuicPunchLog.Info($"[LAN] Beacon failed: {ex.Message}"); }
                    // Retry discovery promptly when a mobile misses the first beacon while waking.
                    await Task.Delay(TimeSpan.FromSeconds(2), wanToken).ConfigureAwait(false);
                }
            }, wanToken);

            if (NatCoordinator.Servers.Count == 0)
            {
                var stunEndpoints = await StunGatherer.GatherStunEndpoints(ct: wanToken).ConfigureAwait(false);
                NatCoordinator.SetServerEndpoints(stunEndpoints);
            }

            NatCoordinator.Start(udp, wanToken);
            _stunCatalogTask = StunGatherer.StartBackgroundCatalogFetchAsync(batch =>
            {
                if (wanToken.IsCancellationRequested || !ReferenceEquals(udp, listenerUdp))
                    return;

                int previousCount = NatCoordinator.Servers.Count;
                NatCoordinator.AddServerEndpoints(batch);
                int addedCount = NatCoordinator.Servers.Count - previousCount;
                if (addedCount > 0)
                {
                    QuicPunchLog.Info(
                        $"[STUN] Added {addedCount} GitHub catalog endpoints; " +
                        $"{NatCoordinator.Servers.Count} STUN servers are now active.");
                }
            }, wanToken);

            try
            {
                await NatCoordinator.ExecuteBurstAsync(wanToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[QuicPunch] Initial STUN burst warning: {ex.Message}");
            }

            if (CurrentPeer.Addresses == null || CurrentPeer.Addresses.Length == 0)
            {
                var localIps = Utilities.GetValidLocalIPAddresses();
                if (localIps.Count > 0)
                {
                    CurrentPeer.Addresses = localIps.OrderBy(a => a.AddressFamily).ThenBy(a => a.ToString(), StringComparer.Ordinal).ToArray();
                }
            }

            InvalidateTokenCache();
            WanLastError = null;

            if (Discovery.WanNostrDiscoveryEnabled && IsStarted)
            {
                try
                {
                    await Discovery.StartWanNostrDiscoveryAsync(wanToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[NOSTR-WAN] Discovery start warning: {ex.Message}");
                }
            }
        }

        public async Task StopWanAsync()
        {
            await _wanLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopWanCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _wanLifecycleLock.Release();
            }
        }

        private async Task StopWanCoreAsync()
        {
            var oldUdp = udp;
            udp = null;
            try { _wanLoopCts?.Cancel(); } catch { }
            try { oldUdp?.Close(); } catch { }
            await CloseTransportSessionsAsync(TransportType.Wan).ConfigureAwait(false);
            try { await Discovery.StopWanNostrDiscoveryAsync().ConfigureAwait(false); } catch { }
            try { await Discovery.StopLanDiscoveryAsync().ConfigureAwait(false); } catch { }
            try { _wanLoopCts?.Cancel(); } catch { }

            Task? catalogTask = _stunCatalogTask;
            _stunCatalogTask = null;
            if (catalogTask != null)
            {
                try { await catalogTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            if (_upnpMappingTask != null) { try { await _upnpMappingTask.ConfigureAwait(false); } catch { } }
            await StopPortMappingsAsync().ConfigureAwait(false);
            try { oldUdp?.Close(); oldUdp?.Dispose(); } catch { }

            await NatCoordinator.StopAsync().ConfigureAwait(false);
            ResetNatMapping();
            if (CurrentPeer != null)
            {
                CurrentPeer.Addresses = Array.Empty<IPAddress>();
            }

            InvalidateTokenCache();
            WanLastError = null;
        }

    }
}
