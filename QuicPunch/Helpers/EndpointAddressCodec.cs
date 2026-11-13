using System.Net;
using System.Net.Sockets;

namespace QuicPunch.Helpers;

// IPv4 lists retain their original wire representation. Bit 7 of the bounded
// count announces version 2: every address is prefixed by its byte length.
// Old readers reject the extended count rather than misinterpreting an IPv6 address.
internal static class EndpointAddressCodec
{
    internal static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    internal static IPEndPoint Normalize(IPEndPoint endpoint) => new(Normalize(endpoint.Address), endpoint.Port);
    internal static bool CanAdvertise(IPAddress address) => Utilities.IsValidPeerAddress(address) &&
        (!address.IsIPv6LinkLocal && (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId == 0));

    internal static void WriteList(BinaryWriter writer, IEnumerable<IPAddress> input)
    {
        var addresses = input.Select(Normalize).Where(CanAdvertise).Distinct().Take(32).ToArray();
        bool extended = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        writer.Write((byte)(addresses.Length | (extended ? 128 : 0)));
        foreach (var address in addresses) Write(writer, address, extended);
    }

    internal static IPAddress[] ReadList(BinaryReader reader)
    {
        byte header = reader.ReadByte();
        int count = header & 127;
        if (count > 32) throw new InvalidDataException("Too many endpoint addresses.");
        var addresses = new List<IPAddress>(count);
        for (int i = 0; i < count; i++)
        {
            var address = Read(reader, (header & 128) != 0);
            if (!CanAdvertise(address)) throw new InvalidDataException("Invalid endpoint address.");
            if (!addresses.Contains(address)) addresses.Add(address);
        }
        return addresses.ToArray();
    }

    internal static void Write(BinaryWriter writer, IPAddress address, bool extended)
    {
        byte[] bytes = Normalize(address).GetAddressBytes();
        if (extended) writer.Write((byte)bytes.Length);
        else if (bytes.Length != 4) throw new InvalidDataException("IPv6 requires extended address framing.");
        writer.Write(bytes);
    }

    internal static IPAddress Read(BinaryReader reader, bool extended)
    {
        int length = extended ? reader.ReadByte() : 4;
        if (length is not (4 or 16)) throw new InvalidDataException("Invalid address family length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Truncated endpoint address.");
        return Normalize(new IPAddress(bytes));
    }

    internal static UdpClient CreateUdpClient()
    {
        if (!Socket.OSSupportsIPv6) return new UdpClient(AddressFamily.InterNetwork);
        var client = new UdpClient(AddressFamily.InterNetworkV6);
        client.Client.DualMode = true;
        return client;
    }

    internal static IPAddress BindAddress(UdpClient client) =>
        client.Client.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
}
