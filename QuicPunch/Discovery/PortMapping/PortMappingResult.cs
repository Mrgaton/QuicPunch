using System;
using System.Net;

namespace QuicPunch.Discovery.PortMapping;

/// <summary>
/// Encapsulates the outcome of an external port mapping attempt.
/// </summary>
public sealed record PortMappingResult
{
    public bool Success { get; init; }
    public string ProtocolName { get; init; } = string.Empty;
    public IPAddress? ExternalIp { get; init; }
    public int ExternalPort { get; init; }
    public int InternalPort { get; init; }
    public TimeSpan Lifetime { get; init; }
    public string? ErrorMessage { get; init; }

    public static PortMappingResult Failed(string protocolName, string error) => new()
    {
        Success = false,
        ProtocolName = protocolName,
        ErrorMessage = error
    };

    public static PortMappingResult Succeeded(
        string protocolName,
        IPAddress? externalIp,
        int externalPort,
        int internalPort,
        TimeSpan lifetime) => new()
    {
        Success = true,
        ProtocolName = protocolName,
        ExternalIp = externalIp,
        ExternalPort = externalPort,
        InternalPort = internalPort,
        Lifetime = lifetime
    };
}
