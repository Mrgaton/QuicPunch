using System.Buffers.Binary;
using System.Net;
using System.Security.Principal;
using System.Text.Json;

namespace QuicPunch.Vpn.Broker;

public sealed class VpnBrokerConfiguration
{
    public string Owner { get; set; } = "";
    public string AdapterName { get; set; } = VpnAdapterFactory.DefaultAdapterName;
    public string[] AllowedAddresses { get; set; } = ["10.0.0.0/24"];
    public string[] AllowedRoutes { get; set; } = ["10.0.0.0/24"];
    public string[] AllowedDestinations { get; set; } = ["10.0.0.0/24"];
    public bool AllowBroadcast { get; set; } = true;
    public int MinimumPrefixLength { get; set; } = 24;
    public bool AllowForwarding { get; set; }
    public static string DefaultPath => OperatingSystem.IsWindows()
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QuicNet", "vpn-broker.json")
        : "/etc/quicnet/vpn-broker.json";
    public void ValidateRequest(BrokerRequest request)
    {
        if (MinimumPrefixLength is < 0 or > 32 || request.Mtu is < 1200 or > 9000 || request.Routes == null || request.Routes.Length > 256 ||
            AllowedAddresses == null || AllowedRoutes == null)
            throw new InvalidDataException("Invalid adapter settings.");
        if (request.Forwarding && !AllowForwarding) throw new InvalidDataException("IP forwarding is not authorized by the administrator.");
        if (request.Broadcast && !AllowBroadcast) throw new InvalidDataException("Broadcast is not authorized by the administrator.");
        if (AllowedDestinations == null || request.InboundDestinations is { Length: > 256 }) throw new InvalidDataException("Invalid destination policy.");
        foreach (string prefix in request.InboundDestinations ?? [])
            if (!AllowedDestinations.Any(p => Contains(p, prefix))) throw new InvalidDataException($"Destination {prefix} is outside the administrator policy.");
        uint address = ParseAddress(request.Address);
        uint mask = ParseAddress(request.Mask);
        uint inv = ~mask;
        if ((inv & (inv + 1)) != 0 || System.Numerics.BitOperations.PopCount(mask) < MinimumPrefixLength)
            throw new InvalidDataException("Interface prefix is outside the administrator policy.");
        int length = System.Numerics.BitOperations.PopCount(mask);
        if (!AllowedAddresses.Any(p => Contains(p, $"{request.Address}/{length}")))
            throw new InvalidDataException("Interface address is outside the administrator policy.");
        foreach (string route in request.Routes)
            if (!AllowedRoutes.Any(p => Contains(p, route))) throw new InvalidDataException($"Route {route} is outside the administrator policy.");
        if (request.BypassAddresses is { Length: > 256 }) throw new InvalidDataException("Too many transport endpoints.");
        foreach (string endpoint in request.BypassAddresses ?? [])
        {
            uint ip = ParseAddress(endpoint);
            if (ip >> 24 is 0 or 127 || ip >= 0xE0000000) throw new InvalidDataException("Invalid transport endpoint.");
            if (!request.Routes.Append($"{request.Address}/{length}").Any(p => Contains(p, endpoint + "/32")))
                throw new InvalidDataException("A transport bypass must be covered by the VPN routes.");
        }
        if (address >> 24 is 0 or 127 || address >= 0xE0000000) throw new InvalidDataException("Invalid unicast address.");
    }
    private static uint ParseAddress(string value)
    {
        if (value.Split('.').Length != 4 || !IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("Invalid IPv4 address.");
        return BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
    }
    private static (uint Network, int Length) Prefix(string prefix)
    {
        string[] parts = prefix.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int length) || length is < 0 or > 32) throw new InvalidDataException("Invalid CIDR prefix.");
        uint mask = length == 0 ? 0 : uint.MaxValue << (32 - length);
        return (ParseAddress(parts[0]) & mask, length);
    }
    private static bool Contains(string allowed, string proposed)
    {
        var a = Prefix(allowed); var p = Prefix(proposed);
        uint mask = a.Length == 0 ? 0 : uint.MaxValue << (32 - a.Length);
        return p.Length >= a.Length && (p.Network & mask) == a.Network;
    }
    public static VpnBrokerConfiguration Load(string path) => JsonSerializer.Deserialize<VpnBrokerConfiguration>(File.ReadAllText(path), BrokerWire.Json)
        ?? throw new InvalidDataException("Empty administrator policy.");
}

public sealed record BrokerRequest(string Address, string Mask, int Mtu, string[] Routes, string[]? BypassAddresses = null,
    bool Forwarding = false, string[]? InboundDestinations = null, bool Broadcast = true);
internal sealed record BrokerResponse(bool Success, string? Error);

internal static class BrokerWire
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, WriteIndented = true, TypeInfoResolver = BrokerJsonContext.Default };
    internal static async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 65535) throw new InvalidDataException("Broker message too large.");
        byte[] header = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    internal static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[2]; await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (length == 0) throw new InvalidDataException("Empty broker message.");
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Invalid broker message.");
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
[System.Text.Json.Serialization.JsonSerializable(typeof(VpnBrokerConfiguration))]
[System.Text.Json.Serialization.JsonSerializable(typeof(BrokerRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(BrokerResponse))]
internal partial class BrokerJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }