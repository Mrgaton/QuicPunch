
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SharedCodeTests;

public static class TimeHook
{
    private static long offsetTicks;
    private static int installed;
    private static IntPtr offsetPtr;
    private static IntPtr detourPtr;
    private static readonly object installLock = new();

    public static TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));
    public static DateTime RealUtcNow => DateTime.FromFileTimeUtc(GetRealSystemTime());
    public static DateTime CorrectedUtcNow => DateTime.FromFileTimeUtc(GetRealSystemTime() + Interlocked.Read(ref offsetTicks));
    public static DateTime CorrectedLocalNow => CorrectedUtcNow.ToLocalTime();
    public static DateTimeOffset CorrectedUtcNowOffset => new(CorrectedUtcNow, TimeSpan.Zero);

    public static void SetOffset(TimeSpan offset)
    {
        long ticks = offset.Ticks;
        Interlocked.Exchange(ref offsetTicks, ticks);

        if (offsetPtr != IntPtr.Zero)
        {
            Marshal.WriteInt64(offsetPtr, ticks);
        }
    }

    public static unsafe void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1)
        {
            return;
        }

        lock (installLock)
        {
            EnsureOffsetStorage();
            detourPtr = IntPtr.Size == 8 ? BuildX64Detour() : BuildX86Detour();
            if (detourPtr == IntPtr.Zero)
            {
                Interlocked.Exchange(ref installed, 0);
                return;
            }

            // Locate kernelbase.dll (kernel32 is fallback)
            IntPtr hModule = GetModuleHandle("kernelbase.dll");
            if (hModule == IntPtr.Zero) hModule = GetModuleHandle("kernel32.dll");
            if (hModule == IntPtr.Zero)
            {
                Interlocked.Exchange(ref installed, 0);
                return;
            }

            // Install hooks in the two APIs used by .NET for current time
            IntPtr addrAsFileTime = GetProcAddress(hModule, "GetSystemTimeAsFileTime");
            bool firstHooked = addrAsFileTime != IntPtr.Zero && InstallTrampolineHook(addrAsFileTime, detourPtr);

            IntPtr addrPrecise = GetProcAddress(hModule, "GetSystemTimePreciseAsFileTime");
            bool secondHooked = addrPrecise != IntPtr.Zero && InstallTrampolineHook(addrPrecise, detourPtr);

            if (!firstHooked && !secondHooked)
            {
                Interlocked.Exchange(ref installed, 0);
            }
        }
    }

    private static void EnsureOffsetStorage()
    {
        if (offsetPtr != IntPtr.Zero)
        {
            return;
        }

        offsetPtr = Marshal.AllocHGlobal(sizeof(long));
        Marshal.WriteInt64(offsetPtr, Interlocked.Read(ref offsetTicks));
    }

    private static unsafe IntPtr BuildX86Detour()
    {
        IntPtr mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)64, 0x3000, 0x40);
        if (mem == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        byte[] code =
        {
            0x8B, 0x0D, 0x1C, 0x00, 0xFE, 0x7F,
            0xA1, 0x14, 0x00, 0xFE, 0x7F,
            0x8B, 0x15, 0x18, 0x00, 0xFE, 0x7F,
            0x3B, 0xCA,
            0x75, 0xEB,
            0xB9, 0x00, 0x00, 0x00, 0x00,
            0x03, 0x01,
            0x13, 0x51, 0x04,
            0x8B, 0x4C, 0x24, 0x04,
            0x89, 0x01,
            0x89, 0x51, 0x04,
            0xC2, 0x04, 0x00
        };

        uint addr = (uint)offsetPtr.ToInt32();
        code[22] = (byte)(addr & 0xFF);
        code[23] = (byte)((addr >> 8) & 0xFF);
        code[24] = (byte)((addr >> 16) & 0xFF);
        code[25] = (byte)((addr >> 24) & 0xFF);

        Marshal.Copy(code, 0, mem, code.Length);
        VirtualProtect(mem, (UIntPtr)code.Length, 0x20, out _);
        FlushInstructionCache(GetCurrentProcess(), mem, (UIntPtr)code.Length);
        return mem;
    }

    private static unsafe IntPtr BuildX64Detour()
    {
        IntPtr mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)64, 0x3000, 0x40);
        if (mem == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        byte[] code =
        {
            0x49, 0xC7, 0xC0, 0x00, 0x00, 0xFE, 0x7F,
            0x41, 0x8B, 0x50, 0x1C,
            0x41, 0x8B, 0x40, 0x14,
            0x45, 0x8B, 0x48, 0x18,
            0x44, 0x39, 0xCA,
            0x75, 0xEF,
            0x49, 0xC1, 0xE1, 0x20,
            0x4C, 0x09, 0xC8,
            0x49, 0xBA, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x49, 0x03, 0x02,
            0x48, 0x89, 0x01,
            0xC3
        };

        ulong addr = (ulong)offsetPtr.ToInt64();
        for (int i = 0; i < 8; i++)
        {
            code[33 + i] = (byte)((addr >> (i * 8)) & 0xFF);
        }

        Marshal.Copy(code, 0, mem, code.Length);
        VirtualProtect(mem, (UIntPtr)code.Length, 0x20, out _);
        FlushInstructionCache(GetCurrentProcess(), mem, (UIntPtr)code.Length);
        return mem;
    }

    // Uses a near relay on x64 so rel32 jump can always reach.
    private static unsafe bool InstallTrampolineHook(IntPtr target, IntPtr dest)
    {
        // --- x86 (32-bit) Simple Hook ---
        if (IntPtr.Size == 4)
        {
            if (!VirtualProtect(target, (UIntPtr)7, 0x40, out uint oldProtect))
            {
                return false;
            }

            byte* p = (byte*)target;
            p[0] = 0xB8; // MOV EAX, ...
            *(uint*)(p + 1) = (uint)dest;
            p[5] = 0xFF; // JMP EAX
            p[6] = 0xE0;
            VirtualProtect(target, (UIntPtr)7, oldProtect, out _);
            FlushInstructionCache(GetCurrentProcess(), target, (UIntPtr)7);
            return true;
        }

        // --- x64 (64-bit) Relay Hook ---
        IntPtr relay = AllocateMemoryWithin2GB(target);
        if (relay == IntPtr.Zero)
        {
            return false;
        }

        if (!VirtualProtect(relay, (UIntPtr)12, 0x40, out uint oldRelayProtect))
        {
            return false;
        }

        byte* pRelay = (byte*)relay;
        pRelay[0] = 0x48;
        pRelay[1] = 0xB8;
        *(ulong*)(pRelay + 2) = (ulong)dest; // The address of our C# Hooked method
        pRelay[10] = 0xFF;
        pRelay[11] = 0xE0;
        VirtualProtect(relay, (UIntPtr)12, oldRelayProtect, out _);
        FlushInstructionCache(GetCurrentProcess(), relay, (UIntPtr)12);

        if (!VirtualProtect(target, (UIntPtr)5, 0x40, out uint oldTargetProtect))
        {
            return false;
        }

        long relativeOffset = (long)relay - ((long)target + 5);
        if (relativeOffset < int.MinValue || relativeOffset > int.MaxValue)
        {
            VirtualProtect(target, (UIntPtr)5, oldTargetProtect, out _);
            return false;
        }

        byte* pTarget = (byte*)target;
        pTarget[0] = 0xE9; // JMP rel32
        *(int*)(pTarget + 1) = (int)relativeOffset;

        VirtualProtect(target, (UIntPtr)5, oldTargetProtect, out _);
        FlushInstructionCache(GetCurrentProcess(), target, (UIntPtr)5);
        return true;
    }

    /// <summary>
    /// Scans memory regions to find a free gap within +/- 2GB of the target address.
    /// This is required for x64 relative jumps.
    /// </summary>
    private static IntPtr AllocateMemoryWithin2GB(IntPtr target)
    {
        long allocationGranularity = GetAllocationGranularity();

        long minAddr = (long)target - 0x7FF00000; // slightly less than 2GB to be safe
        long maxAddr = (long)target + 0x7FF00000;
        if (minAddr < 0) minAddr = allocationGranularity;
        // Don't go into kernel space (user mode limit)
        long userModeMax = 0x7FFFFFFF0000;
        if (maxAddr > userModeMax) maxAddr = userModeMax;

        // Start looking from the target address upwards
        long current = (long)target;

        // Align current to granularity
        current = (current + allocationGranularity - 1) & ~(allocationGranularity - 1);

        MEMORY_BASIC_INFORMATION mbi;

        // Scan Upwards
        while (current < maxAddr)
        {
            if (VirtualQuery((IntPtr)current, out mbi, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == 0)
                break;

            // We need a free block
            if (mbi.State == 0x10000) // MEM_FREE
            {
                IntPtr res = VirtualAlloc((IntPtr)current, (UIntPtr)1024, 0x3000, 0x40); // COMMIT|RESERVE, EXECUTE_READWRITE
                if (res != IntPtr.Zero) return res;
            }

            // Move to end of this region
            current = (long)mbi.BaseAddress + (long)mbi.RegionSize;
            // Re-align
            current = (current + allocationGranularity - 1) & ~(allocationGranularity - 1);
        }

        // If Upwards failed, Scan Downwards
        current = (long)target;
        current = (current - 1) & ~(allocationGranularity - 1); // Align down

        while (current > minAddr)
        {
            if (VirtualQuery((IntPtr)current, out mbi, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == 0)
                break;

            // We are looking for the end of a free block or the start of one, 
            // but VirtualQuery gives info about the base. 
            // Simplification: Just jump over occupied blocks and check free ones.

            if (mbi.State == 0x10000) // MEM_FREE
            {
                // Try to alloc at the end of this free block (closest to target)
                long allocPos = (long)mbi.BaseAddress + (long)mbi.RegionSize - allocationGranularity;
                if (allocPos < current) allocPos = current;

                IntPtr res = VirtualAlloc((IntPtr)allocPos, (UIntPtr)1024, 0x3000, 0x40);
                if (res != IntPtr.Zero) return res;
            }

            current = (long)mbi.BaseAddress - allocationGranularity;
        }

        return IntPtr.Zero;
    }

    private static long GetAllocationGranularity()
    {
        SYSTEM_INFO si;
        GetSystemInfo(out si);
        return si.dwAllocationGranularity;
    }

    private static unsafe long GetRealSystemTime()
    {
        // 0x7FFE0000 is the KUSER_SHARED_DATA structure, mapped read-only in every process.
        // It allows reading system time without a kernel transition (fast).
        uint* pLow = (uint*)0x7FFE0014;
        uint* pHigh1 = (uint*)0x7FFE0018;
        uint* pHigh2 = (uint*)0x7FFE001C;

        uint high1, high2, low;

        do
        {
            high1 = System.Threading.Volatile.Read(ref *pHigh1);
            low = System.Threading.Volatile.Read(ref *pLow);
            high2 = System.Threading.Volatile.Read(ref *pHigh2);
        } while (high1 != high2);

        return ((long)high1 << 32) | low;
    }

    // --- P/INVOKE DEFINITIONS ---

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_INFO
    {
        public uint dwOemId;
        public uint dwPageSize;
        public IntPtr lpMinimumApplicationAddress;
        public IntPtr lpMaximumApplicationAddress;
        public IntPtr dwActiveProcessorMask;
        public uint dwNumberOfProcessors;
        public uint dwProcessorType;
        public uint dwAllocationGranularity;
        public ushort wProcessorLevel;
        public ushort wProcessorRevision;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);
}


internal static class CertificateTransparencyLogAndDnsOverHttpHandlerClass
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:148.0) Gecko/20100101 Firefox/148.0";
    public static HttpClientHandler CTDohHandler { get => new DOHHttpHandler(
        new HttpClientHandler()
        {
            MaxAutomaticRedirections = 4,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls13 | System.Security.Authentication.SslProtocols.Tls12,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = CertificateTransparencyLog.Verify,
            Proxy = null,
            UseProxy = false,
            PreAuthenticate = false,
            CheckCertificateRevocationList = true,
            UseDefaultCredentials = false,
        }
    ); private set; }

    private static readonly Lazy<HttpClient> _ctDohClientLazy = new(() =>
        new HttpClient(CTDohHandler)
        {
            Timeout = TimeSpan.FromSeconds(90),
            DefaultRequestHeaders = {
                {"accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"},
                {"accept-encoding", "gzip, deflate, br"},
                {"accept-language", "es,en;q=0.5"},
                //{"cache-control", "max-age=0"},
                //{"Cookie", "countryCode=US"},
                {"Dnt", "1"},
                {"Sec-Fetch-Dest", "document"},
                {"Sec-Fetch-Mode", "navigate"},
                {"sec-fetch-site", "none"},
                //{"sec-fetch-user", "?1"},
                {"Priority", "u=0, i"},
                //{"Service-Worker-Navigation-Preload", "true"},
                {"Sec-GPC", "1"},
                {"referer", "/"},
                //{"upgrade-insecure-requests", "1"},
                
                {"User-Agent", UserAgent }
                },

            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        });

    public static HttpClient CTDohClient => _ctDohClientLazy.Value;

    public static async Task<TimeSpan> SetNetworkTimeAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "https://www.google.com");
        using var response = await CTDohClient.SendAsync(request);

        DateTimeOffset? networkTime = response.Headers.Date;

        if (networkTime.HasValue)
        {
            var time =  networkTime.Value.UtcDateTime;

            var offset = time - DateTime.UtcNow;

            TimeHook.SetOffset(offset);
            TimeHook.Install();
            return offset;
        }

        throw new Exception("The server did not return a Date header.");
    }
    public class DOHHttpHandler : HttpClientHandler
    {
        public DOHHttpHandler(HttpClientHandler handler)
        {
            CopyHandlerProperties(handler);
        }

        private void CopyHandlerProperties(HttpClientHandler baseHandler)
        {
            var properties = typeof(HttpClientHandler).GetProperties();

            foreach (var property in properties)
            {
                if (property.CanWrite && property.CanRead)
                {
                    var value = property.GetValue(baseHandler);

                    property.SetValue(this, value);
                }
            }
        }

        private const string Protocol = "https";
        private const string DnsHostname = "cloudflare-dns.com";
        private const string IPV6Dns = "2606:4700:4700::1001";
        private const string IPV4Dns = "1.0.0.1";
        public static NetworkInterfaceComponent LastInterface { get; set; }

        private DateTime LastCheched { get; set; }

        public async Task<NetworkInterfaceComponent> CheckIPvSupport()
        {
            double lastTime = (DateTime.Now - LastCheched).TotalSeconds;

            if (lastTime < 240)
                return LastInterface;

            if (Socket.OSSupportsIPv6)
            {
                bool replied = false;

                try
                {
                    using (Ping ping = new Ping())
                    {
                        PingReply reply = ping.Send(IPV6Dns, 1500);
                        replied = reply.Status == IPStatus.Success;
                    }
                }
                catch { }

                if (replied && !string.IsNullOrEmpty(await ResolveHostName(IPV6Dns, DnsHostname, NetworkInterfaceComponent.IPv6)))
                {
                    LastCheched = DateTime.Now;

                    if (LastInterface != NetworkInterfaceComponent.IPv6)
                        ClearCache();

                    LastInterface = NetworkInterfaceComponent.IPv6;

                    return LastInterface;
                }
            }

            try
            {
                if (!string.IsNullOrEmpty(await ResolveHostName(IPV4Dns, DnsHostname, NetworkInterfaceComponent.IPv4)))
                {
                    LastCheched = DateTime.Now;

                    if (LastInterface != NetworkInterfaceComponent.IPv4)
                        ClearCache();

                    LastInterface = NetworkInterfaceComponent.IPv4;

                    return LastInterface;
                }
            }
            catch { }

            throw new Exception("Failed to connect the remote dns server");
        }

        private async Task<string> ResolveHostName(string dnsHost, string host, NetworkInterfaceComponent type)
        {
            if (string.Equals(host, CertificateTransparencyLog.GstaticDomain, StringComparison.InvariantCultureIgnoreCase))
                return CertificateTransparencyLog.GstaticDomain;

            if (type == NetworkInterfaceComponent.IPv6)
                dnsHost = '[' + dnsHost + ']';

            using (HttpRequestMessage dohRequest = new HttpRequestMessage(HttpMethod.Get, Protocol + "://" + dnsHost + "/dns-query?name=" + host + "&type=" + (type == NetworkInterfaceComponent.IPv4 ? "A" : "AAAA")))
            {
                dohRequest.Headers.TryAddWithoutValidation("accept", "application/dns-json");
                dohRequest.Headers.TryAddWithoutValidation("Host", DnsHostname);

                using (HttpResponseMessage dohResponse = await base.SendAsync(dohRequest, new CancellationTokenSource(TimeSpan.FromSeconds(12)).Token))
                {
                    string dohJson = await dohResponse.Content.ReadAsStringAsync();

                    JsonNode obj = JsonNode.Parse(dohJson);

                    if (obj["Answer"] is null)
                    {
                        //if (obj["Authority"] is not null) return await ResolveHostName(dnsHost, (string)obj["Authority"][0]["name"], type);

                        if (type == NetworkInterfaceComponent.IPv6)
                        {
                            return await ResolveHostName(dnsHost, host, NetworkInterfaceComponent.IPv4);
                        }
                        else
                        {
                            throw new HttpRequestException("Name or service not known (" + dnsHost + ")");
                        }
                    }

#if DEBUG
                    Console.WriteLine("Resolved domain " + host + " : " + obj["Answer"][0]["data"].ToString());
#endif

                    return obj["Answer"][0]["data"].ToString();
                }
            }
        }

        private static ConcurrentDictionary<string, string> DnsCache = new ConcurrentDictionary<string, string>();

        public static void ClearCache() => DnsCache.Clear();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (Uri.CheckHostName(request.RequestUri.Host) is UriHostNameType.IPv4 or UriHostNameType.IPv6)
                {
                    return await base.SendAsync(request, cancellationToken);
                }

                UriBuilder requestUri = new UriBuilder(request.RequestUri);

                requestUri.Scheme = Protocol;
                //requestUri.Port = 443;

                var hostname = requestUri.Host;

                request.Headers.TryAddWithoutValidation("Host", hostname);

                if (DnsCache.TryGetValue(hostname, out string cachedRemoteIP))
                {
                    requestUri.Host = cachedRemoteIP;

                    try
                    {
                        return await base.SendAsync(request, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        ClearCache();
                        LastCheched = DateTime.MinValue;
                        throw;
                    }
                }

                //NetworkInterfaceComponent type = await CheckIPvSupport();
                NetworkInterfaceComponent type = NetworkInterfaceComponent.IPv4;

                string remoteIp = await ResolveHostName(type == NetworkInterfaceComponent.IPv6 ? IPV6Dns : IPV4Dns, requestUri.Host, type);

                DnsCache[hostname] = remoteIp;

                requestUri.Host = remoteIp;
                request.RequestUri = requestUri.Uri;

                return await base.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;

                Console.Error.WriteLine($"Fatal error in request URI: {request.RequestUri.ToString().Replace(request.RequestUri.Host, "127.0.0.1")}");
                Console.Error.WriteLine(ex);
                throw;
            }
        }
    }
}

/// <summary>
/// Validates Certificate Transparency (CT) Signed Certificate Timestamps (SCTs) 
/// to prevent Man-In-The-Middle (MITM) attacks.
/// </summary>
internal static class CertificateTransparencyLog
{
    public const string GstaticDomain = "gstatic.com";
    private const string EmbeddedSctListOid = "1.3.6.1.4.1.11129.2.4.2";

    private const string CtLogListUrl = "https://www.gstatic.com/ct/log_list/v3/log_list.json";
    private const string CtLogListSignatureUrl = "https://www.gstatic.com/ct/log_list/v3/log_list.sig";

    private const int MinimumValidScts = 2;
    private const byte SctVersionV1 = 0;
    private const byte SctSignatureTypeCertificateTimestamp = 0;
    private const ushort CtLogEntryTypePrecertificate = 1;

    private static readonly TimeSpan MaximumFutureSctSkew = TimeSpan.FromHours(24);
    private static readonly Asn1Tag ContextSpecific3 = new(TagClass.ContextSpecific, 3);

    // Google key for the detached signature that accompanies log_list.json.
    private static readonly byte[] GoogleCtLogListPublicKeySpki = Convert.FromBase64String(
        "MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAsu0BHGnQ++W2CTdyZyxvHHRALOZPlnu/VMVgo2m+JZ8MNbAOH2cgXb8mvOj8flsX/qPMuKIaauO+PwROMjiqfUpcFm80Kl7i97ZQyBDYKm3MkEYYpGN+skAR2OebX9G2DfDqFY8+jUpOOWtBNr3LrmVcwx+FcFdMjGDlrZ5JRmoJ/SeGKiORkbbu9eY1Wd0uVhz/xI5bQb0OgII7hEj+i/IPbJqOHgB8xQ5zWAJJ0DmG+FM6o7gk403v6W3S8qRYiR84c50KppGwe4YqSMkFbLDleGQWLoaDSpEWtESisb4JiLaY4H+Kk0EyAhPSb+49JfUozYl+lf7iFN3qRq/SIXXTh6z0S7Qa8EYDhKGCrpI03/+qprwy+my6fpWHi6aUIk4holUCmWvFxZDfixoxK0RlqbFDl2JXMBquwlQpm8u5wrsic1ksIv9z8x9zh4PJqNpCah0ciemI3YGRQqSe/mRRXBiSn9YQBUPcaeqCYan+snGADFwHuXCd9xIAdFBolw9R9HTedHGUfVXPJDiF4VusfX6BRR/qaadB+bqEArF/TzuDUr6FvOR4o8lUUxgLuZ/7HO+bHnaPFKYHHSm++z1lVDhhYuSZ8ax3T0C3FZpb7HMjZtpEorSV5ElKJEJwrhrBCMOD8L01EoSPrGlS1w22i9uGHMn/uGQKo28u7AsCAwEAAQ==");

    private static readonly HttpClient Client = new();
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);

    // Thread-safe cached state
    private static ReadOnlyDictionary<string, TrustedLogInfo>? _trustedLogs;
    private static DateTimeOffset _lastUpdate = DateTimeOffset.MinValue;

    public static async Task InitializeAsync()
    {
        // Skip if loaded and less than 24 hours old
        if (_trustedLogs is not null && (DateTimeOffset.UtcNow - _lastUpdate).TotalHours < 24)
            return;

        await InitializationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Double-check locking pattern
            if (_trustedLogs is null || (DateTimeOffset.UtcNow - _lastUpdate).TotalHours >= 24)
            {
                await LoadAndVerifyCTLogListAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    private static async Task LoadAndVerifyCTLogListAsync()
    {
        Task<byte[]> logListTask = Client.GetByteArrayAsync(CtLogListUrl);
        Task<byte[]> signatureTask = Client.GetByteArrayAsync(CtLogListSignatureUrl);

        await Task.WhenAll(logListTask, signatureTask).ConfigureAwait(false);

        byte[] logListBytes = await logListTask;
        byte[] signatureBytes = await signatureTask;

        if (!VerifyGoogleLogListSignature(logListBytes, signatureBytes))
            throw new CryptographicException("Security alert: Failed to verify Google CT log list signature.");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,

            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        var logList = JsonSerializer.Deserialize<LogList>(logListBytes, options);
        if (logList?.Operators is null)
            throw new InvalidDataException("Unable to deserialize Google CT log list.");

        var parsedLogs = new Dictionary<string, TrustedLogInfo>(StringComparer.Ordinal);

        foreach (var ctOperator in logList.Operators)
        {
            AddOperatorLogs(ctOperator?.Logs, parsedLogs);
            AddOperatorLogs(ctOperator?.TiledLogs, parsedLogs);
        }

        if (parsedLogs.Count == 0)
            throw new InvalidDataException("The verified CT log list did not contain usable log keys.");

        // Atomic swap and update timestamp
        Interlocked.Exchange(ref _trustedLogs, new ReadOnlyDictionary<string, TrustedLogInfo>(parsedLogs));
        _lastUpdate = DateTimeOffset.UtcNow;
    }

    private static bool VerifyGoogleLogListSignature(byte[] logListBytes, byte[] signatureBytes)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(GoogleCtLogListPublicKeySpki, out _);
        return rsa.VerifyData(logListBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static void AddOperatorLogs(IEnumerable<Log>? logs, IDictionary<string, TrustedLogInfo> destination)
    {
        if (logs is null) return;

        foreach (var log in logs)
        {
            if (string.IsNullOrWhiteSpace(log.LogId) || string.IsNullOrWhiteSpace(log.Key))
                continue;

            byte[] logIdBytes;
            byte[] keyBytes;

            try
            {
                logIdBytes = Convert.FromBase64String(log.LogId);
                keyBytes = Convert.FromBase64String(log.Key);
            }
            catch (FormatException)
            {
                continue;
            }

            if (logIdBytes.Length != 32) continue;

            ECDsa? ecdsaKey = null;
            RSA? rsaKey = null;

            try
            {
                ecdsaKey = ECDsa.Create();
                ecdsaKey.ImportSubjectPublicKeyInfo(keyBytes, out _);
            }
            catch (CryptographicException)
            {
                ecdsaKey?.Dispose();
                ecdsaKey = null;

                try
                {
                    rsaKey = RSA.Create();
                    rsaKey.ImportSubjectPublicKeyInfo(keyBytes, out _);
                }
                catch (CryptographicException)
                {
                    rsaKey?.Dispose();
                    continue; // Key is neither valid ECDSA nor RSA, skip log
                }
            }

            destination[log.LogId] = new TrustedLogInfo(log.State, ecdsaKey, rsaKey);
        }
    }

    public static bool Verify(HttpRequestMessage req, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        DebugLog($"verify start source={req.RequestUri.Host} sslPolicyErrors={sslPolicyErrors}");

        if (sslPolicyErrors != SslPolicyErrors.None || cert is null || chain is null)
        {
            DebugLog("verify failed: pre-validation SSL policy/cert/chain check failed.");
            return false;
        }

        if (!EnsureTrustedLogsLoadedForVerification())
        {
            DebugLog("verify failed: trusted CT log cache unavailable.");
            return false;
        }

        var trustedLogs = _trustedLogs; // Capture reference safely
        if (trustedLogs is null || trustedLogs.Count == 0)
        {
            DebugLog("verify failed: trusted CT log cache empty.");
            return false;
        }

        using var leaf = cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        DebugLogCertificate(req, leaf);
        DebugLogChain(chain);

        if (!IsCertificateTimeValid(leaf, utcNow))
        {
            DebugLog("verify failed: leaf certificate is outside validity period.");
            return false;
        }

        if (!TryGetIssuerKeyHashCandidates(chain, leaf, out var issuerKeyHashCandidates))
        {
            DebugLog("verify failed: no issuer SPKI hash candidates.");
            return false;
        }

        var sctExtension = leaf.Extensions[EmbeddedSctListOid];
        if (sctExtension is null)
        {
            DebugLog("verify failed: embedded SCT extension missing.");
            return false;
        }

        if (!TryExtractScts(sctExtension.RawData, out var scts))
        {
            DebugLog("verify failed: SCT extension parse failed.");
            return false;
        }

        if (!TryGetTbsCertificateWithoutSct(leaf, out byte[]? tbsWithoutSct))
        {
            DebugLog("verify failed: unable to build TBS certificate without SCT extension.");
            return false;
        }

        int validScts = 0;
        var validLogIds = new HashSet<string>(StringComparer.Ordinal);
        DebugLog($"verify info: extractedScts={scts.Count} issuerHashCandidates={issuerKeyHashCandidates.Count}");

        foreach (var sct in scts)
        {
            if (sct.Version != SctVersionV1) 
                continue;

            if (!IsSctTimestampPlausible(sct.Timestamp, leaf, utcNow)) 
                continue;

            string logIdBase64 = Convert.ToBase64String(sct.LogId);

            if (!trustedLogs.TryGetValue(logIdBase64, out var logInfo))
                continue;

            if (!IsAcceptedLogState(logInfo.State, sct.Timestamp))
                continue;

            // Ensure we don't double-count SCTs from the same Log ID
            if (!validLogIds.Add(logIdBase64))
                continue;

            if (VerifySctSignatureAgainstAnyIssuerHash(sct, logInfo, issuerKeyHashCandidates, tbsWithoutSct))
                validScts++;
        }

        bool isValid = validScts >= MinimumValidScts;
        DebugLog($"verify result source={req.RequestUri.Host} valid={isValid} validScts={validScts} minimumRequired={MinimumValidScts}");
        return isValid;
    }

    private static bool IsCertificateTimeValid(X509Certificate2 certificate, DateTimeOffset utcNow)
    {
        var now = utcNow.UtcDateTime;
        return now >= certificate.NotBefore.ToUniversalTime() && now <= certificate.NotAfter.ToUniversalTime();
    }

    private static bool EnsureTrustedLogsLoadedForVerification()
    {
        if (_trustedLogs is not null && (DateTimeOffset.UtcNow - _lastUpdate).TotalHours < 24)
            return true;

        try
        {
            InitializeAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }

        return _trustedLogs is not null && _trustedLogs.Count > 0;
    }

    [Conditional("CT_DEBUG")]
    private static void DebugLog(string message)
    {
        Console.WriteLine($"[CT_DEBUG] {DateTimeOffset.UtcNow:O} {message}");
    }

    [Conditional("CT_DEBUG")]
    private static void DebugLogCertificate(HttpRequestMessage source, X509Certificate2 leaf)
    {
        DebugLog(
            $"cert source={source} subject='{leaf.Subject}' issuer='{leaf.Issuer}' " +
            $"thumbprint={leaf.Thumbprint} notBefore={leaf.NotBefore:O} notAfter={leaf.NotAfter:O}");
    }

    [Conditional("CT_DEBUG")]
    private static void DebugLogChain(X509Chain chain)
    {
        DebugLog($"chain elements={chain.ChainElements.Count} statusCount={chain.ChainStatus.Length}");

        for (int i = 0; i < chain.ChainStatus.Length; i++)
        {
            DebugLog($"chain status[{i}]={chain.ChainStatus[i].Status} info='{chain.ChainStatus[i].StatusInformation?.Trim()}'");
        }
    }

    private static bool TryGetIssuerKeyHashCandidates(X509Chain chain, X509Certificate2 leaf, [NotNullWhen(true)] out List<byte[]>? issuerKeyHashes)
    {
        issuerKeyHashes = null;
        if (chain.ChainElements.Count < 2) return false;

        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<byte[]>();

        // Try to locate the specific issuer by matching Subject/Issuer string
        for (int i = 1; i < chain.ChainElements.Count; i++)
        {
            var candidate = chain.ChainElements[i].Certificate;
            if (string.Equals(candidate.Subject, leaf.Issuer, StringComparison.OrdinalIgnoreCase))
                AddIssuerKeyHashCandidate(candidate, candidates, seenHashes);
        }

        // Include remaining issuers to support delegated precertificate-signing certs.
        for (int i = 1; i < chain.ChainElements.Count; i++)
            AddIssuerKeyHashCandidate(chain.ChainElements[i].Certificate, candidates, seenHashes);

        if (candidates.Count == 0)
            return false;

        issuerKeyHashes = candidates;
        return true;
    }

    private static void AddIssuerKeyHashCandidate(X509Certificate2 certificate, ICollection<byte[]> destination, ISet<string> seenHashes)
    {
        if (!TryComputeSubjectPublicKeyHash(certificate, out var hash))
            return;

        string hashId = Convert.ToHexString(hash);
        if (!seenHashes.Add(hashId))
            return;

        destination.Add(hash);
    }

    private static bool TryComputeSubjectPublicKeyHash(X509Certificate2 certificate, [NotNullWhen(true)] out byte[]? hash)
    {
        hash = null;

        try
        {
            byte[] subjectPublicKeyInfo = certificate.PublicKey.ExportSubjectPublicKeyInfo();
            hash = SHA256.HashData(subjectPublicKeyInfo);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool IsAcceptedLogState(State? state, ulong sctTimestamp)
    {
        if (state is null) return false;

        if (state.Usable is not null)
            return IsOnOrAfterStateTimestamp(state.Usable, sctTimestamp);
        if (state.Readonly is not null)
            return IsOnOrAfterStateTimestamp(state.Readonly, sctTimestamp);
        if (state.Qualified is not null)
            return IsOnOrAfterStateTimestamp(state.Qualified, sctTimestamp);
        if (state.Retired is not null && TryGetStateTimestampMilliseconds(state.Retired, out ulong retiredTimestamp))
            return sctTimestamp <= retiredTimestamp;

        return false;
    }

    private static bool IsOnOrAfterStateTimestamp(StateInfo state, ulong sctTimestamp)
    {
        if (!TryGetStateTimestampMilliseconds(state, out ulong stateTimestamp))
            return true;

        return sctTimestamp >= stateTimestamp;
    }

    private static bool TryGetStateTimestampMilliseconds(StateInfo state, out ulong timestampMs)
    {
        timestampMs = 0;
        if (string.IsNullOrWhiteSpace(state.Timestamp)) return false;

        if (!DateTimeOffset.TryParse(state.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            return false;

        long unixMs = timestamp.ToUnixTimeMilliseconds();
        if (unixMs < 0) return false;

        timestampMs = (ulong)unixMs;
        return true;
    }

    private static bool VerifySctSignatureAgainstAnyIssuerHash(in SctData sct, TrustedLogInfo logInfo, IReadOnlyList<byte[]> issuerKeyHashes, byte[] tbsWithoutSct)
    {
        foreach (var issuerKeyHash in issuerKeyHashes)
        {
            if (VerifySctSignature(sct, logInfo, issuerKeyHash, tbsWithoutSct))
                return true;
        }

        return false;
    }

    private static bool VerifySctSignature(in SctData sct, TrustedLogInfo logInfo, byte[] issuerKeyHash, byte[] tbsWithoutSct)
    {
        HashAlgorithmName? hashAlgorithm = sct.HashAlgorithm switch
        {
            2 => HashAlgorithmName.SHA1,
            4 => HashAlgorithmName.SHA256,
            5 => HashAlgorithmName.SHA384,
            6 => HashAlgorithmName.SHA512,
            _ => null
        };

        if (hashAlgorithm is null) 
            return false;

        byte[] signedData = BuildSignedDataForEmbeddedSct(sct, issuerKeyHash, tbsWithoutSct);

        try
        {
            if (logInfo.EcdsaKey is not null && sct.SignatureAlgorithm == 3)
            {
                return logInfo.EcdsaKey.VerifyData(signedData, sct.Signature, hashAlgorithm.Value, DSASignatureFormat.Rfc3279DerSequence);
            }

            if (logInfo.RsaKey is not null && sct.SignatureAlgorithm == 1)
            {
                return logInfo.RsaKey.VerifyData(signedData, sct.Signature, hashAlgorithm.Value, RSASignaturePadding.Pkcs1);
            }
        }
        catch (CryptographicException)
        {
            // Invalid signature format
        }

        return false;
    }

    private static bool IsSctTimestampPlausible(ulong sctTimestamp, X509Certificate2 leaf, DateTimeOffset utcNow)
    {
        if (!TryConvertMillisecondsToDateTimeOffset(sctTimestamp, out var sctDateTime))
            return false;

        if (sctDateTime > utcNow + MaximumFutureSctSkew)
            return false;

        if (sctDateTime.UtcDateTime > leaf.NotAfter.ToUniversalTime())
            return false;

        return true;
    }

    private static bool TryConvertMillisecondsToDateTimeOffset(ulong unixMilliseconds, out DateTimeOffset value)
    {
        value = default;
        if (unixMilliseconds > long.MaxValue)
            return false;

        try
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds((long)unixMilliseconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static byte[] BuildSignedDataForEmbeddedSct(in SctData sct, byte[] issuerKeyHash, byte[] tbsWithoutSct)
    {
        int totalLength = 12 + issuerKeyHash.Length + 3 + tbsWithoutSct.Length + 2 + sct.Extensions.Length;
        byte[] signedData = new byte[totalLength];

        var span = signedData.AsSpan();

        span[0] = sct.Version;
        span[1] = SctSignatureTypeCertificateTimestamp;

        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(2, 8), sct.Timestamp);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(10, 2), CtLogEntryTypePrecertificate);

        issuerKeyHash.CopyTo(span.Slice(12));
        int offset = 12 + issuerKeyHash.Length;

        // Write 24-bit length for TBS
        span[offset++] = (byte)((tbsWithoutSct.Length >> 16) & 0xFF);
        span[offset++] = (byte)((tbsWithoutSct.Length >> 8) & 0xFF);
        span[offset++] = (byte)(tbsWithoutSct.Length & 0xFF);

        tbsWithoutSct.CopyTo(span.Slice(offset));
        offset += tbsWithoutSct.Length;

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(offset, 2), (ushort)sct.Extensions.Length);
        offset += 2;

        if (sct.Extensions.Length > 0)
            sct.Extensions.CopyTo(span.Slice(offset));

        return signedData;
    }

    private static bool TryGetTbsCertificateWithoutSct(X509Certificate2 certificate, [NotNullWhen(true)] out byte[]? tbsWithoutSct)
    {
        tbsWithoutSct = null;
        try
        {
            var certReader = new AsnReader(certificate.RawData, AsnEncodingRules.DER);
            var certSequence = certReader.ReadSequence();
            var tbsEncoded = certSequence.ReadEncodedValue();

            var tbsReader = new AsnReader(tbsEncoded, AsnEncodingRules.DER);
            var tbsSequence = tbsReader.ReadSequence();

            var writer = new AsnWriter(AsnEncodingRules.DER);
            writer.PushSequence();

            bool removedSct = false;

            while (tbsSequence.HasData)
            {
                Asn1Tag tag = tbsSequence.PeekTag();

                if (tag.HasSameClassAndValue(ContextSpecific3))
                {
                    var extensionsWrapper = tbsSequence.ReadSequence(ContextSpecific3);
                    var extensionsSequence = extensionsWrapper.ReadSequence();
                    var retainedExtensions = new List<ReadOnlyMemory<byte>>();

                    while (extensionsSequence.HasData)
                    {
                        var extensionEncoded = extensionsSequence.ReadEncodedValue();
                        var extensionReader = new AsnReader(extensionEncoded, AsnEncodingRules.DER);
                        var extensionSequence = extensionReader.ReadSequence();
                        string extensionOid = extensionSequence.ReadObjectIdentifier();

                        if (string.Equals(extensionOid, EmbeddedSctListOid, StringComparison.Ordinal))
                            removedSct = true;
                        else
                            retainedExtensions.Add(extensionEncoded);
                    }

                    // Only write the extensions block [3] if there are remaining extensions
                    if (retainedExtensions.Count > 0)
                    {
                        writer.PushSequence(ContextSpecific3);
                        writer.PushSequence();
                        foreach (var ext in retainedExtensions)
                            writer.WriteEncodedValue(ext.Span);
                        writer.PopSequence();
                        writer.PopSequence(ContextSpecific3);
                    }
                }
                else
                {
                    writer.WriteEncodedValue(tbsSequence.ReadEncodedValue().Span);
                }
            }

            writer.PopSequence();

            if (!removedSct) return false;

            tbsWithoutSct = writer.Encode();
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryExtractScts(byte[] extensionRawData, [NotNullWhen(true)] out List<SctData>? scts)
    {
        scts = null;
        try
        {
            var reader = new AsnReader(extensionRawData, AsnEncodingRules.DER);
            byte[] serializedSctList = reader.ReadOctetString();

            if (reader.HasData || serializedSctList.Length < 2) return false;

            ushort sctListLength = BinaryPrimitives.ReadUInt16BigEndian(serializedSctList.AsSpan(0, 2));
            if (sctListLength != serializedSctList.Length - 2) return false;

            scts = new List<SctData>();
            int offset = 2;

            while (offset < serializedSctList.Length)
            {
                if (serializedSctList.Length - offset < 2) return false;

                ushort sctLength = BinaryPrimitives.ReadUInt16BigEndian(serializedSctList.AsSpan(offset, 2));
                offset += 2;

                if (sctLength == 0 || serializedSctList.Length - offset < sctLength) return false;

                if (!TryParseSct(serializedSctList.AsSpan(offset, sctLength), out var parsedSct))
                    return false;

                scts.Add(parsedSct);
                offset += sctLength;
            }

            return scts.Count > 0;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static bool TryParseSct(ReadOnlySpan<byte> rawSct, out SctData sct)
    {
        sct = default;
        if (rawSct.Length < 47) return false;

        int p = 0;
        byte version = rawSct[p++];
        byte[] logId = rawSct.Slice(p, 32).ToArray();
        p += 32;

        ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(rawSct.Slice(p, 8));
        p += 8;

        ushort extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(rawSct.Slice(p, 2));
        p += 2;

        if (rawSct.Length - p < extensionsLength + 4) return false;

        byte[] extensions = rawSct.Slice(p, extensionsLength).ToArray();
        p += extensionsLength;

        byte hashAlgorithm = rawSct[p++];
        byte signatureAlgorithm = rawSct[p++];

        ushort signatureLength = BinaryPrimitives.ReadUInt16BigEndian(rawSct.Slice(p, 2));
        p += 2;

        if (signatureLength == 0 || rawSct.Length - p != signatureLength) return false;

        byte[] signature = rawSct.Slice(p, signatureLength).ToArray();

        sct = new SctData(version, logId, timestamp, extensions, hashAlgorithm, signatureAlgorithm, signature);
        return true;
    }

    // --- Modern Models ---

    private sealed class TrustedLogInfo : IDisposable
    {
        public State? State { get; }
        public ECDsa? EcdsaKey { get; }
        public RSA? RsaKey { get; }

        public TrustedLogInfo(State? state, ECDsa? ecdsaKey, RSA? rsaKey)
        {
            State = state;
            EcdsaKey = ecdsaKey;
            RsaKey = rsaKey;
        }

        public void Dispose()
        {
            EcdsaKey?.Dispose();
            RsaKey?.Dispose();
        }
    }

    // Switched to readonly struct to reduce GC allocations on heavy parsing
    private readonly struct SctData
    {
        public byte Version { get; }
        public byte[] LogId { get; }
        public ulong Timestamp { get; }
        public byte[] Extensions { get; }
        public byte HashAlgorithm { get; }
        public byte SignatureAlgorithm { get; }
        public byte[] Signature { get; }

        public SctData(byte version, byte[] logId, ulong timestamp, byte[] extensions, byte hashAlgorithm, byte signatureAlgorithm, byte[] signature)
        {
            Version = version;
            LogId = logId;
            Timestamp = timestamp;
            Extensions = extensions;
            HashAlgorithm = hashAlgorithm;
            SignatureAlgorithm = signatureAlgorithm;
            Signature = signature;
        }
    }

    // Using records for clean, lightweight JSON deserialization targets
    public record LogList(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("log_list_timestamp")] string? LogListTimestamp,
        [property: JsonPropertyName("operators")] List<Operator>? Operators
    );

    public record Operator(
        [property: JsonPropertyName("logs")] List<Log>? Logs,
        [property: JsonPropertyName("tiled_logs")] List<Log>? TiledLogs
    );

    public record Log(
        [property: JsonPropertyName("log_id")] string? LogId,
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("state")] State? State
    );

    public record State(
        [property: JsonPropertyName("usable")] StateInfo? Usable,
        [property: JsonPropertyName("readonly")] StateInfo? Readonly,
        [property: JsonPropertyName("retired")] StateInfo? Retired,
        [property: JsonPropertyName("qualified")] StateInfo? Qualified,
        [property: JsonPropertyName("pending")] StateInfo? Pending,
        [property: JsonPropertyName("rejected")] StateInfo? Rejected
    );

    public record StateInfo(
        [property: JsonPropertyName("timestamp")] string? Timestamp
    );
}

