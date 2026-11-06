using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace QuicNet.Vpn;

public readonly record struct Ipv4Prefix(uint Network, int Length)
{
    public uint Mask => Length == 0 ? 0 : uint.MaxValue << (32 - Length);
    public bool Contains(uint address) => (address & Mask) == Network;
    public bool Contains(Ipv4Prefix other) => Length <= other.Length && Contains(other.Network);
    public static uint Address(string value)
    {
        // Reject the abbreviated/integer forms accepted by IPAddress.TryParse.
        if (value.Split('.').Length != 4 || !IPAddress.TryParse(value, out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException($"Invalid IPv4 address: {value}");
        return BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
    }
    public static Ipv4Prefix Parse(string value)
    {
        string[] parts = value.Trim().Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int length) || length is < 0 or > 32)
            throw new InvalidDataException($"Expected an IPv4 CIDR prefix: {value}");
        uint mask = length == 0 ? 0 : uint.MaxValue << (32 - length);
        return new(Address(parts[0]) & mask, length);
    }
    public static int MaskLength(string mask)
    {
        uint bits = Address(mask);
        uint inverted = ~bits;
        if ((inverted & (inverted + 1)) != 0) throw new InvalidDataException("Subnet mask must be contiguous.");
        return System.Numerics.BitOperations.PopCount(bits);
    }
    public static string Format(uint address) => new IPAddress(new byte[] {
        (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address }).ToString();
    public override string ToString() => $"{Format(Network)}/{Length}";
}

public readonly record struct Ipv4Packet(uint Source, uint Destination, byte Protocol, int HeaderLength, int Length)
{
    public static bool TryRead(ReadOnlySpan<byte> bytes, out Ipv4Packet packet)
    {
        packet = default;
        if (bytes.Length < 20 || bytes[0] >> 4 != 4) return false;
        int header = (bytes[0] & 15) * 4;
        int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..4]);
        if (header < 20 || header > bytes.Length || length < header || length != bytes.Length || bytes[8] == 0) return false;
        // Source routing can change the destination after the ACL check. Never pass it to the host stack.
        for (int position = 20; position < header;)
        {
            byte option = bytes[position];
            if (option == 0) break;
            if (option == 1) { position++; continue; }
            if (option is 131 or 137 || position + 1 >= header || bytes[position + 1] < 2 || position + bytes[position + 1] > header) return false;
            position += bytes[position + 1];
        }
        // Header integrity is checked before either transport reaches the adapter.
        uint sum = 0;
        for (int i = 0; i < header; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i, 2));
        while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
        if (sum != 65535) return false;
        packet = new(BinaryPrimitives.ReadUInt32BigEndian(bytes[12..16]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]), bytes[9], header, length);
        return true;
    }
}
