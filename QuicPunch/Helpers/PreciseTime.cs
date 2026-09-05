using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch.Helpers;

public static class PreciseTime
{
    private static TimeSpan _offset;
    private static bool _syncTriggered;
    private static readonly object _syncLock = new();

    private static readonly string[] HttpsTimeServers = new[]
    {
        "https://1.1.1.1",
        "https://www.google.com",
        "https://www.cloudflare.com",
        "https://www.microsoft.com"
    };

    public static async Task<bool> SyncWithNtpAsync()
    {
        try
        {
            var ntpData = new byte[48];
            ntpData[0] = 0x1B;

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.ReceiveTimeout = 3000;
            await socket.ConnectAsync("pool.ntp.org", 123).ConfigureAwait(false);

            var sw = Stopwatch.StartNew();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await socket.SendAsync(ntpData, SocketFlags.None, cts.Token).ConfigureAwait(false);
            await socket.ReceiveAsync(ntpData, SocketFlags.None, cts.Token).ConfigureAwait(false);
            sw.Stop();

            ulong intPart = (ulong)ntpData[40] << 24 | (ulong)ntpData[41] << 16 | (ulong)ntpData[42] << 8 | (ulong)ntpData[43];
            ulong fractPart = (ulong)ntpData[44] << 24 | (ulong)ntpData[45] << 16 | (ulong)ntpData[46] << 8 | (ulong)ntpData[47];
            long ms = (long)(intPart * 1000 + (fractPart * 1000) / 0x100000000L);

            DateTime ntpTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddMilliseconds(ms)
                .AddMilliseconds(sw.ElapsedMilliseconds / 2.0);

            _offset = ntpTime - DateTime.UtcNow;
            QuicPunchLog.Info($"[PreciseTime] Synchronized via NTP (pool.ntp.org) with offset {_offset.TotalMilliseconds:F1}ms");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> SyncWithHttpsAsync()
    {
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        foreach (var url in HttpsTimeServers)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
                using var response = await client.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                sw.Stop();

                if (response.Headers.Date.HasValue)
                {
                    DateTime serverTime = response.Headers.Date.Value.UtcDateTime.AddMilliseconds(sw.ElapsedMilliseconds / 2.0);
                    _offset = serverTime - DateTime.UtcNow;
                    QuicPunchLog.Info($"[PreciseTime] Synchronized via HTTPS fallback ({url}) with offset {_offset.TotalMilliseconds:F1}ms");
                    return true;
                }
            }
            catch
            {
                // Graceful fallback to next server
            }
        }
        return false;
    }

    public static async Task SyncTimeAsync()
    {
        bool ok = await SyncWithNtpAsync().ConfigureAwait(false);
        if (!ok)
        {
            await SyncWithHttpsAsync().ConfigureAwait(false);
        }
    }

    public static DateTime GetCorrectTime()
    {
        if (!_syncTriggered)
        {
            lock (_syncLock)
            {
                if (!_syncTriggered)
                {
                    _syncTriggered = true;
                    _ = Task.Run(SyncTimeAsync);
                }
            }
        }

        return DateTime.UtcNow.Add(_offset);
    }

    public static async Task WaitNextTrigger(int multipleInMilliseconds, CancellationToken cancellationToken = default)
    {
        if (multipleInMilliseconds <= 0)
            return;

        long intervalTicks = TimeSpan.FromMilliseconds(multipleInMilliseconds).Ticks;
        DateTime now = GetCorrectTime();
        long nextTicks = now.Ticks - (now.Ticks % intervalTicks) + intervalTicks;
        DateTime nextBoundary = new DateTime(nextTicks, DateTimeKind.Utc);
        TimeSpan delay = nextBoundary - GetCorrectTime();
        int delayMs = (int)Math.Clamp(delay.TotalMilliseconds, 0, (double)int.MaxValue);

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }
}
