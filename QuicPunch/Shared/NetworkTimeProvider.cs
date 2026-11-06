using Rage;
using System.Net;
using System.Net.Sockets;
public static class NetworkTimeProvider
{
    private static readonly object _timeLock = new object();
    private static DateTime _lastOnlineTime = DateTime.MinValue;
    private static DateTime _lastLocalTime = DateTime.MinValue;

    private static readonly string[] NtpServers = {
        "time.google.com",
        "pool.ntp.org",
        "time.windows.com"
    };

    public static bool ErrorGettingDate { get; private set; } = false;
    public static bool CheckNet()
    {
        return InternetGetConnectedState(out int flags, 0);
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll")]
    private extern static bool InternetGetConnectedState(out int lpdwFlags, int dwReserved);

    //public static DateTime TryGetNetworkTime(bool forceUpdate = false) => TryGetNetworkTimeUtc(forceUpdate).ToLocalTime();
    public static DateTime TryGetNetworkTimeUtc(bool forceUpdate = false)
    {
        lock (_timeLock)
        {
            if (!forceUpdate && _lastOnlineTime != DateTime.MinValue)
            {
                var offset = DateTime.UtcNow - _lastLocalTime;
                //Console.WriteLine("Giving time: " + _lastOnlineTime.Add(offset));
                return _lastOnlineTime.Add(offset);
            }

            ErrorGettingDate = false;

            foreach (var server in NtpServers)
            {
                try
                {
                    _lastOnlineTime = FetchNetworkTimeUtc(server);
                    _lastLocalTime = DateTime.UtcNow;
                    //Console.WriteLine("Giving time: " + _lastOnlineTime);

                    return _lastOnlineTime;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to get time from {server}: {ex.Message}");
                    Program.HandleException(ex);
                    continue;
                }
            }

            ErrorGettingDate = true;
            return DateTime.MinValue;
        }
    }

    private static DateTime FetchNetworkTimeUtc(string ntpServer)
    {
        const int timeout = 4000;
        const int ntpDataLength = 48;

        byte[] ntpData = new byte[ntpDataLength];
        ntpData[0] = 0x1B; // LI = 0, VN = 3, Mode = 3 (client)

        var addresses = Dns.GetHostAddresses(ntpServer);
        if (addresses == null || addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        Exception lastEx = null;
        foreach (var addr in addresses)
        {
            var ipEndPoint = new IPEndPoint(addr, 123);
            try
            {
                using (var socket = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.ReceiveTimeout = timeout;
                    socket.SendTimeout = timeout;
                    if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        try { socket.DualMode = true; } catch { /* ignore if not supported */ }
                    }

                    // write client's transmit timestamp (originate) into request at offset 40
                    WriteTimestamp(ntpData, 40, DateTime.UtcNow);

                    socket.Connect(ipEndPoint);
                    socket.Send(ntpData);

                    int received = socket.Receive(ntpData);
                    if (received < ntpDataLength)
                        throw new SocketException((int)SocketError.ConnectionReset);

                    // capture destination timestamp immediately after receive
                    DateTime destinationTimestamp = DateTime.UtcNow;

                    // Read timestamps from response:
                    DateTime originate = ReadTimestamp(ntpData, 24); // T1 (originate) - copied from our transmit
                    DateTime receive = ReadTimestamp(ntpData, 32); // T2 server receive
                    DateTime transmit = ReadTimestamp(ntpData, 40); // T3 server transmit

                    // Standard SNTP formulas:
                    // roundTripDelay = (T4 - T1) - (T3 - T2)
                    // localClockOffset = ((T2 - T1) + (T3 - T4)) / 2
                    TimeSpan t1t4 = destinationTimestamp - originate;      // (T4 - T1)
                    TimeSpan t3t2 = transmit - receive;                    // (T3 - T2)
                    TimeSpan roundTripDelay = t1t4 - t3t2;

                    TimeSpan offset = new TimeSpan(
                        ((receive - originate).Ticks + (transmit - destinationTimestamp).Ticks) / 2
                    );

                    // You can log roundTripDelay/offset for diagnostics
                    //Console.WriteLine($"RTT={roundTripDelay.TotalMilliseconds}ms Offset={offset.TotalMilliseconds}ms");

                    // Return corrected UTC: local receive time + offset
                    return destinationTimestamp + offset;
                }
            }
            catch (SocketException ex)
            {
                lastEx = ex;
                Console.WriteLine($"[WARN] Failed to contact {ipEndPoint}: {ex.Message}");
                continue;
            }
        }

        throw lastEx ?? new SocketException((int)SocketError.HostNotFound);
    }

    // Read NTP 64-bit timestamp at given offset into DateTime (UTC)
    private static DateTime ReadTimestamp(byte[] data, int offset)
    {
        uint seconds = SwapEndianness(BitConverter.ToUInt32(data, offset));
        uint fraction = SwapEndianness(BitConverter.ToUInt32(data, offset + 4));

        // seconds since 1900-01-01
        const long ticksPerSecond = TimeSpan.TicksPerSecond;
        long secondsTicks = (long)seconds * ticksPerSecond;

        // fractional part -> ticks (fraction / 2^32 * ticksPerSecond)
        double fractionFraction = fraction / (double)0x100000000UL;
        long fractionTicks = (long)(fractionFraction * ticksPerSecond);

        long totalTicks = secondsTicks + fractionTicks;
        DateTime ntpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return ntpEpoch.AddTicks(totalTicks);
    }

    // Write client's transmit time at offset into packet as NTP 64-bit timestamp
    private static void WriteTimestamp(byte[] data, int offset, DateTime time)
    {
        // convert to NTP seconds/fraction
        DateTime ntpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        TimeSpan ts = time.ToUniversalTime() - ntpEpoch;
        uint seconds = (uint)ts.TotalSeconds;
        // fraction = fractional part of second * 2^32
        double fractional = (ts.TotalSeconds - Math.Floor(ts.TotalSeconds));
        uint fraction = (uint)(fractional * 0x100000000UL);

        // write in big-endian (network order)
        Array.Copy(BitConverter.GetBytes(SwapEndianness(seconds)), 0, data, offset, 4);
        Array.Copy(BitConverter.GetBytes(SwapEndianness(fraction)), 0, data, offset + 4, 4);
    }

    // your existing endian helper
    private static uint SwapEndianness(uint x)
    {
        return ((x & 0x000000FF) << 24) |
               ((x & 0x0000FF00) << 8) |
               ((x & 0x00FF0000) >> 8) |
               ((x & 0xFF000000) >> 24);
    }

}
