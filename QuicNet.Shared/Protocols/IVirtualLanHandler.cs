using System.Net;
using QuicPunch;

namespace QuicNet.Protocols;

public interface IVirtualLanHandler : QuicPunch.QuicPunch.IProtocolHandler, IDisposable
{
    public const int DefaultDatagramMtu = 1420;
    string AdapterName { get; }
    string AdapterStatus { get; }
    string? LastError { get; }
    IPAddress LocalIp { get; }
    string SubnetMask { get; }
    int Mtu { get; }
    bool AutoAssignEnabled { get; }
    bool IsActive { get; }
    string AddressMode { get; }
    QuicNet.Vpn.IVpnControl? Control => null;
    bool CanStartWithoutElevation => IsActive;
    event Action? StateChanged;
    long TotalRxPackets { get; }
    long TotalTxPackets { get; }
    long TotalRxBytes { get; }
    long TotalTxBytes { get; }
    bool HasActivePeer(Guid peerId);
    int ActivePeerCount { get; }
    IEnumerable<object> GetActivePeerSummaries();
    void SetupTun(string? initialIp = null, string? subnetMask = null, int? mtu = null);
    void StopTun();
    void Stop();
    void ConfigureAutoAssignment(byte[] identityHash, byte[]? groupId = null);
    bool ConfigureManualAddress(string ip, string mask);
    bool SetMtu(int mtu);
    bool SetVirtualIp(string ip, string mask);
    bool DisconnectPeer(Guid peerId);
    Task<(bool Success, double RttMs, string? Error)> PingIpAsync(IPAddress targetIp, int timeoutMs = 1500);
    void NotifyPrivilegeRequired();
}
