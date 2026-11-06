using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch.Security
{
    /// <summary>
    /// Contract for IP-level filtering and threat reputation checking.
    /// </summary>
    public interface IIpFilter
    {
        bool IsBlocked(IPAddress address);
        bool IsBlocked(IPEndPoint endPoint);
        bool IsAllowed(IPAddress address);
        bool IsAllowed(IPEndPoint endPoint);
    }

    /// <summary>
    /// High-performance, thread-safe IP reputation and threat filter.
    /// Uses IPsum feeds (https://github.com/stamparm/ipsum) to automatically download,
    /// cache, and block malicious IPs with zero-lock O(1) lookups.
    /// </summary>
    public sealed class IpReputationFilter : IIpFilter, IDisposable, IAsyncDisposable
    {
        private static readonly HttpClient SharedHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private readonly SemaphoreSlim _updateLock = new(1, 1);
        private readonly ConcurrentDictionary<uint, byte> _manualWhitelist = new();
        private readonly ConcurrentDictionary<uint, byte> _manualBlacklist = new();

        private volatile FrozenSet<uint> _blockedIps = FrozenSet<uint>.Empty;
        private long _totalDroppedPackets;
        private CancellationTokenSource? _autoUpdateCts;
        private Task? _autoUpdateTask;
        private readonly object _autoUpdateGate = new();
        private bool _disposed;

        public int Level { get; set; }
        public TimeSpan? AutoUpdateInterval { get; }
        public string? CacheFilePath { get; }
        public DateTimeOffset? LastUpdated { get; private set; }

        public int BlockedCount => _blockedIps.Count + _manualBlacklist.Count;
        public long TotalDroppedPackets => Interlocked.Read(ref _totalDroppedPackets);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordDrop() => Interlocked.Increment(ref _totalDroppedPackets);

        /// <summary>
        /// Creates a new IpReputationFilter.
        /// </summary>
        /// <param name="level">IPsum blacklist confidence level (1-8). Defaults to 2.</param>
        /// <param name="cacheFilePath">Optional local file path to cache IPs across reboots.</param>
        /// <param name="autoUpdateInterval">Frequency to re-download the feed. Defaults to 24 hours. Null to disable auto-update.</param>
        /// <param name="autoLoadCache">If true, immediately warms the filter from the local cache file if it exists.</param>
        /// <param name="autoStart">If true, starts the background auto-updater immediately.</param>
        public IpReputationFilter(
            int level = 2,
            string? cacheFilePath = null,
            TimeSpan? autoUpdateInterval = null,
            bool autoLoadCache = true,
            bool autoStart = true)
        {
            if (level < 1 || level > 8)
                throw new ArgumentOutOfRangeException(nameof(level), "IPsum level must be between 1 and 8.");

            Level = level;
            CacheFilePath = cacheFilePath;
            AutoUpdateInterval = autoUpdateInterval ?? TimeSpan.FromHours(24);

            if (autoLoadCache && !string.IsNullOrWhiteSpace(CacheFilePath) && File.Exists(CacheFilePath))
            {
                TryLoadFromCacheSync();
            }

            if (autoStart && AutoUpdateInterval.HasValue && AutoUpdateInterval.Value > TimeSpan.Zero)
            {
                StartAutoUpdate();
            }
        }

        #region Fast O(1) IP Check

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsBlocked(IPAddress address)
        {
            if (address == null) return false;

            if (!TryGetIPv4AsUInt32(address, out uint ip))
            {
                // Pure IPv6 addresses are not covered by standard IPsum feeds
                return false;
            }

            // Explicit whitelist always bypasses any block
            if (!_manualWhitelist.IsEmpty && _manualWhitelist.ContainsKey(ip))
                return false;

            // Explicit manual blacklist overrides feed
            if (!_manualBlacklist.IsEmpty && _manualBlacklist.ContainsKey(ip))
                return true;

            // Never block loopback or private LAN addresses via threat feed
            if (IsPrivateOrLoopback(address))
                return false;

            return _blockedIps.Contains(ip);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPrivateOrLoopback(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                Span<byte> bytes = stackalloc byte[4];
                if (address.TryWriteBytes(bytes, out _))
                {
                    if (bytes[0] == 10) return true; // 10.0.0.0/8
                    if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true; // 172.16.0.0/12
                    if (bytes[0] == 192 && bytes[1] == 168) return true; // 192.168.0.0/16
                    if (bytes[0] == 169 && bytes[1] == 254) return true; // 169.254.0.0/16 (link-local)
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsBlocked(IPEndPoint endPoint) => endPoint != null && IsBlocked(endPoint.Address);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAllowed(IPAddress address) => !IsBlocked(address);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAllowed(IPEndPoint endPoint) => endPoint != null && IsAllowed(endPoint.Address);

        #endregion

        #region Whitelist / Blacklist Custom Overrides

        public void AddToWhitelist(IPAddress address)
        {
            if (TryGetIPv4AsUInt32(address, out uint ip))
            {
                _manualWhitelist[ip] = 0;
                _manualBlacklist.TryRemove(ip, out _);
            }
        }

        public void RemoveFromWhitelist(IPAddress address)
        {
            if (TryGetIPv4AsUInt32(address, out uint ip))
            {
                _manualWhitelist.TryRemove(ip, out _);
            }
        }

        public void AddToBlacklist(IPAddress address)
        {
            if (TryGetIPv4AsUInt32(address, out uint ip))
            {
                _manualBlacklist[ip] = 0;
                _manualWhitelist.TryRemove(ip, out _);
            }
        }

        public void RemoveFromBlacklist(IPAddress address)
        {
            if (TryGetIPv4AsUInt32(address, out uint ip))
            {
                _manualBlacklist.TryRemove(ip, out _);
            }
        }

        #endregion

        #region Feed Updates & Cache

        /// <summary>
        /// Downloads the latest IPsum list for the configured level and swaps the active filter set atomically.
        /// </summary>
        /// <returns>The number of blocked IPs loaded.</returns>
        public async Task<int> UpdateAsync(CancellationToken cancellationToken = default)
        {
            await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string url = $"https://raw.githubusercontent.com/stamparm/ipsum/master/levels/{Level}.txt";
                HashSet<uint> newSet = new(capacity: 35_000);

                try
                {
                    using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var reader = new StreamReader(stream);

                    string? line;
                    while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                    {
                        ParseAndAddLine(line, newSet);
                    }

                    if (newSet.Count > 0)
                    {
                        _blockedIps = newSet.ToFrozenSet();
                        LastUpdated = DateTimeOffset.UtcNow;

                        // Persist to cache file if specified
                        if (!string.IsNullOrWhiteSpace(CacheFilePath))
                        {
                            try
                            {
                                string? dir = Path.GetDirectoryName(CacheFilePath);
                                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                    Directory.CreateDirectory(dir);

                                await SaveToCacheAsync(newSet, CacheFilePath, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                QuicPunchLog.Info($"[IpReputationFilter] Warning: Failed to write cache to {CacheFilePath}: {ex.Message}");
                            }
                        }

                        QuicPunchLog.Info($"[IpReputationFilter] Successfully updated IP reputation list (Level {Level}): {_blockedIps.Count:N0} IPs blocked.");
                        return _blockedIps.Count;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    QuicPunchLog.Info($"[IpReputationFilter] Warning: Network update failed ({ex.Message}). Trying local cache fallback...");
                    if (TryLoadFromCacheSync())
                    {
                        return _blockedIps.Count;
                    }
                    throw;
                }

                return _blockedIps.Count;
            }
            finally
            {
                _updateLock.Release();
            }
        }

        private bool TryLoadFromCacheSync()
        {
            if (string.IsNullOrWhiteSpace(CacheFilePath) || !File.Exists(CacheFilePath))
                return false;

            try
            {
                var newSet = new HashSet<uint>(capacity: 35_000);
                using var reader = new StreamReader(CacheFilePath);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    ParseAndAddLine(line, newSet);
                }

                if (newSet.Count > 0)
                {
                    _blockedIps = newSet.ToFrozenSet();
                    LastUpdated = File.GetLastWriteTimeUtc(CacheFilePath);
                    QuicPunchLog.Info($"[IpReputationFilter] Loaded {_blockedIps.Count:N0} blocked IPs from cache: {CacheFilePath}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[IpReputationFilter] Failed to read cache: {ex.Message}");
            }

            return false;
        }

        private static async Task SaveToCacheAsync(HashSet<uint> ipSet, string path, CancellationToken ct)
        {
            string tempPath = path + ".tmp";
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            await using (var writer = new StreamWriter(fs, Encoding.ASCII))
            {
                foreach (uint ip in ipSet)
                {
                    uint b0 = (ip >> 24) & 0xFF;
                    uint b1 = (ip >> 16) & 0xFF;
                    uint b2 = (ip >> 8) & 0xFF;
                    uint b3 = ip & 0xFF;
                    writer.WriteLine($"{b0}.{b1}.{b2}.{b3}");
                }
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tempPath, path, overwrite: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ParseAndAddLine(string line, HashSet<uint> set)
        {
            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#') return;

            // Optional tab-separated level (like raw ipsum.txt)
            int tabIndex = span.IndexOf('\t');
            if (tabIndex > 0)
                span = span.Slice(0, tabIndex).Trim();

            // High performance zero-allocation IPv4 parsing
            if (TryParseIPv4Fast(span, out uint ipUint))
            {
                set.Add(ipUint);
            }
            else if (IPAddress.TryParse(span, out var address) && TryGetIPv4AsUInt32(address, out uint parsedUint))
            {
                set.Add(parsedUint);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryParseIPv4Fast(ReadOnlySpan<char> span, out uint ipUint)
        {
            ipUint = 0;
            int partCount = 0;
            uint currentPart = 0;
            bool hasDigit = false;

            for (int i = 0; i < span.Length; i++)
            {
                char c = span[i];
                if (c >= '0' && c <= '9')
                {
                    currentPart = currentPart * 10 + (uint)(c - '0');
                    if (currentPart > 255) return false;
                    hasDigit = true;
                }
                else if (c == '.')
                {
                    if (!hasDigit || partCount >= 3) return false;
                    ipUint = (ipUint << 8) | currentPart;
                    currentPart = 0;
                    hasDigit = false;
                    partCount++;
                }
                else
                {
                    return false;
                }
            }

            if (!hasDigit || partCount != 3) return false;
            ipUint = (ipUint << 8) | currentPart;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetIPv4AsUInt32(IPAddress address, out uint result)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                Span<byte> bytes = stackalloc byte[4];
                if (address.TryWriteBytes(bytes, out _))
                {
                    result = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                    return true;
                }
            }

            result = 0;
            return false;
        }

        #endregion

        #region Background Periodic Updater

        public void StartAutoUpdate()
        {
            lock (_autoUpdateGate)
            {
                if (_disposed || _autoUpdateTask != null || !AutoUpdateInterval.HasValue)
                    return;
                var cts = new CancellationTokenSource();
                _autoUpdateCts = cts;
                _autoUpdateTask = Task.Run(() => RunAutoUpdateLoopAsync(cts.Token));
            }
        }

        public void StopAutoUpdate()
        {
            CancellationTokenSource? cts;
            Task? task;
            lock (_autoUpdateGate)
            {
                cts = _autoUpdateCts;
                task = _autoUpdateTask;
                _autoUpdateCts = null;
                _autoUpdateTask = null;
            }
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                if (task != null)
                    _ = task.ContinueWith(t => { _ = t.Exception; cts.Dispose(); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                else
                    cts.Dispose();
            }
        }

        private async Task RunAutoUpdateLoopAsync(CancellationToken ct)
        {
            // Initial update if empty
            if (_blockedIps.Count == 0)
            {
                try
                {
                    await UpdateAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    QuicPunchLog.Info($"[IpReputationFilter] Initial update attempt warning: {ex.Message}");
                }
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(AutoUpdateInterval!.Value, ct).ConfigureAwait(false);
                    await UpdateAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[IpReputationFilter] Periodic update error: {ex.Message}");
                }
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Task? task;
            lock (_autoUpdateGate) task = _autoUpdateTask;
            StopAutoUpdate();
            try { task?.GetAwaiter().GetResult(); } catch { }
            _updateLock.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            var task = _autoUpdateTask;
            StopAutoUpdate();
            if (task != null)
            {
                try { await task.ConfigureAwait(false); } catch { }
            }
            _updateLock.Dispose();
        }

        #endregion
    }
}
