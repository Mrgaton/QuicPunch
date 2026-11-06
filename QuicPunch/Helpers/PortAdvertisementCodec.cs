using System.IO;

namespace QuicPunch.Helpers;

/// <summary>
/// Normalizes and serializes the port advertisement shared by discovery packets.
/// Legacy packets always contained two UInt16 values (minimum and maximum).
/// Structured packets retain that layout for Single/Range and use a counted list
/// only for Multiple, so new readers can distinguish old buggy Multiple packets.
/// </summary>
internal static class PortAdvertisementCodec
{
    public const int MaxExplicitPorts = 8;
    public const int MaxProbeEndpoints = 64;

    public static ushort[] Normalize(IEnumerable<ushort>? ports)
    {
        return (ports ?? Array.Empty<ushort>())
            .Where(static port => port > 0)
            .Distinct()
            .Order()
            .ToArray();
    }

    public static PortMode SelectMode(IReadOnlyCollection<ushort> ports)
    {
        return ports.Count switch
        {
            0 or 1 => PortMode.Single,
            <= MaxExplicitPorts => PortMode.Multiple,
            _ => PortMode.Range
        };
    }

    public static PortMode ResolveMode(IReadOnlyCollection<ushort> ports, PortMode advertisedMode)
    {
        return advertisedMode == PortMode.Range && ports.Count >= 2
            ? PortMode.Range
            : SelectMode(ports);
    }

    public static ushort[] NormalizeForMode(IEnumerable<ushort>? ports, PortMode mode)
    {
        ushort[] normalized = Normalize(ports);
        if (normalized.Length == 0)
            return normalized;

        if (mode != PortMode.Range || normalized.Length == 1)
            return normalized;

        // Keep ranges compact. Expanding a broad NAT range can otherwise allocate
        // tens of thousands of entries and lead to wasteful random probing.
        return [normalized[0], normalized[^1]];
    }

    public static void WriteDiscovery(BinaryWriter writer, PortMode mode, IReadOnlyList<ushort> ports)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (ports.Count == 0 || ports.Any(static port => port == 0))
            throw new InvalidDataException("A discovery packet must advertise at least one valid port.");

        switch (mode)
        {
            case PortMode.Single:
                writer.Write(ports[0]);
                writer.Write(ports[0]);
                break;

            case PortMode.Multiple:
                if (ports.Count is < 2 or > MaxExplicitPorts)
                    throw new InvalidDataException($"Multiple mode requires between 2 and {MaxExplicitPorts} ports.");
                writer.Write((byte)ports.Count);
                foreach (ushort port in ports)
                    writer.Write(port);
                break;

            case PortMode.Range:
                writer.Write(ports.Min());
                writer.Write(ports.Max());
                break;

            default:
                throw new InvalidDataException($"Unsupported port mode value: {(byte)mode}.");
        }
    }

    public static ushort ReadHostPort(BinaryReader reader, ConnectionFlags flags)
    {
        if (!flags.HasHostPort) return 0;
        ushort port = reader.ReadUInt16();
        if (port == 0) throw new InvalidDataException("Discovery contains host port 0.");
        return port;
    }

    public static ushort[] ReadDiscovery(BinaryReader reader, ConnectionFlags flags, string context)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(flags);

        if (flags.UsesStructuredPorts && flags.PortMode == PortMode.Multiple)
        {
            int count = reader.ReadByte();
            if (count is < 2 or > MaxExplicitPorts)
                throw new InvalidDataException($"{context} advertises an invalid explicit port count: {count}.");

            ushort[] ports = new ushort[count];
            for (int i = 0; i < ports.Length; i++)
            {
                ports[i] = reader.ReadUInt16();
                if (ports[i] == 0)
                    throw new InvalidDataException($"{context} contains port 0.");
            }

            ushort[] normalized = Normalize(ports);
            if (normalized.Length != ports.Length)
                throw new InvalidDataException($"{context} contains duplicate ports.");
            return normalized;
        }

        ushort minPort = reader.ReadUInt16();
        ushort maxPort = reader.ReadUInt16();
        if (minPort == 0 || maxPort < minPort)
            throw new InvalidDataException($"{context} contains an invalid port range {minPort}..{maxPort}.");

        if (flags.UsesStructuredPorts && flags.PortMode == PortMode.Single && minPort != maxPort)
            throw new InvalidDataException($"{context} marks a single port but advertises {minPort}..{maxPort}.");

        // Old packets wrote min/max even when their flag said Multiple. Treat those
        // as the legacy range they actually encoded.
        if (!flags.UsesStructuredPorts && minPort != maxPort)
            flags.PortMode = PortMode.Range;

        return minPort == maxPort ? [minPort] : [minPort, maxPort];
    }
}
