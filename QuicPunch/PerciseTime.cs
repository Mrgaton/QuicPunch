using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public static class PreciseTime
{
    private static TimeSpan _offset; 
    public static async Task SyncWithNtpAsync()
    {
        var ntpData = new byte[48];
        ntpData[0] = 0x1B; 

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = 3000;
        await socket.ConnectAsync("pool.ntp.org", 123);

        var sw = Stopwatch.StartNew();
        await socket.SendAsync(ntpData, SocketFlags.None);
        await socket.ReceiveAsync(ntpData, SocketFlags.None);
        sw.Stop();

        ulong intPart = (ulong)ntpData[40] << 24 | (ulong)ntpData[41] << 16 | (ulong)ntpData[42] << 8 | (ulong)ntpData[43];
        ulong fractPart = (ulong)ntpData[44] << 24 | (ulong)ntpData[45] << 16 | (ulong)ntpData[46] << 8 | (ulong)ntpData[47];
        long ms = (long)(intPart * 1000 + (fractPart * 1000) / 0x100000000L);

        DateTime ntpTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMilliseconds(ms)
            .AddMilliseconds(sw.ElapsedMilliseconds / 2.0);

        _offset = ntpTime - DateTime.UtcNow;
    }

    public static DateTime GetCorrectTime()
    {
        if (_offset == TimeSpan.Zero)
            SyncWithNtpAsync().GetAwaiter().GetResult();

        return DateTime.UtcNow.Add(_offset);
    }

    public static async Task StartSyncedLoggerAsync(int multipleInMilliseconds)
    {
        long intervalTicks = TimeSpan.FromMilliseconds(multipleInMilliseconds).Ticks;

        while (true)
        {
            DateTime now = GetCorrectTime();
            long nextTicks = now.Ticks - (now.Ticks % intervalTicks) + intervalTicks;
            DateTime nextBoundary = new DateTime(nextTicks, DateTimeKind.Utc);
            TimeSpan delay = nextBoundary - GetCorrectTime();

            if (delay.TotalMilliseconds > 5)
            {
                await Task.Delay((int)delay.TotalMilliseconds - 5);
            }

            while (GetCorrectTime() < nextBoundary)
            {
                Thread.SpinWait(10);
            }

            Console.WriteLine($"[Triggered every {multipleInMilliseconds}s] Exact Time: {GetCorrectTime():HH:mm:ss.fff}");
        }
    }
}