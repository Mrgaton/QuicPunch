using System.Net;
using System.Net.Sockets;

namespace QuicPunch.Helpers;

internal static class Nat64Prefix
{
    internal static bool IsSupported(IPNetwork prefix) =>
        prefix.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 &&
        prefix.PrefixLength is 32 or 40 or 48 or 56 or 64 or 96;

    internal static IPAddress Embed(IPNetwork prefix, IPAddress ipv4)
    {
        byte[] bytes = prefix.BaseAddress.GetAddressBytes();
        byte[] address = ipv4.GetAddressBytes();
        int offset = prefix.PrefixLength / 8;
        foreach (byte octet in address)
        {
            // RFC 6052 reserves byte eight for prefixes shorter than /96.
            if (offset == 8) offset++;
            bytes[offset++] = octet;
        }
        return new IPAddress(bytes);
    }
}
