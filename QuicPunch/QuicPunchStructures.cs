using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QuicPunch
{
    internal static class QuicPunchStructures
    {
        public enum MessageType : byte
        {
            Hello = (byte)('H'),
            Ping = (byte)('P'),
            Interrogation = (byte)('I'),
            Ack = (byte)('K'),
            Handshake = (byte)('S'),
            FinalHandshake = (byte)('F'),
            // 'D' is reserved for the retired UDP application-data format. Do not reuse it.
            Disconnect = (byte)('X'),
            QuicReady = (byte)('Q')
        }
        public enum HandShakeType : byte
        {
            Request = (byte)('R'),
            Accept = (byte)('A'),
            Decline = (byte)('D'),
            Unsupported = (byte)('U') //Peer doesnt support the requested protocol
        }
    }

    public enum CandidateType : byte
    {
        Host = 1,
        ServerReflexive = 2,
        PeerReflexive = 3
    }

    public sealed record CandidateEndpoint(System.Net.IPEndPoint EndPoint, CandidateType Type, uint Priority = 0);

    public enum HandshakeSessionState : byte
    {
        Pending = 0,
        Completed = 1,
        Rejected = 2
    }

    /// <summary>
    /// Represents point-in-time telemetry and connection metadata for an active application protocol session.
    /// </summary>
    public sealed class QuicSessionTelemetryInfo
    {
        /// <summary>Gets the unique identifier of the remote peer.</summary>
        public Guid PeerId { get; init; }

        /// <summary>Gets the display name of the remote peer.</summary>
        public string? PeerName { get; init; }

        /// <summary>Gets the protocol identifier associated with the active session.</summary>
        public Guid ProtocolId { get; init; }

        /// <summary>Gets the human-readable registered name of the protocol.</summary>
        public string? ProtocolName { get; init; }

        /// <summary>Gets the underlying transport layer classification (WAN or Tor).</summary>
        public QuicPunch.TransportType TransportType { get; init; }

        /// <summary>Gets the comprehensive telemetry metrics queried directly from the native MsQuic engine.</summary>
        public Helpers.QuicConnectionTelemetry Telemetry { get; init; } = null!;

        /// <summary>Gets the UTC timestamp at which this telemetry snapshot was captured.</summary>
        public DateTime SampleTimeUtc { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Modo de configuración para los puertos (2 bits).
    /// </summary>
    public enum PortMode : byte
    {
        Single = 0,    // 0b00: Un solo puerto
        Multiple = 1,  // 0b01: Lista o array de puertos
        Range = 2      // 0b10: Rango de puertos (mínimo - máximo)
    }

    /// <summary>
    /// Representa el tipo y configuración de red empaquetados en un solo byte.
    /// </summary>
    public class ConnectionFlags
    {
        // Constantes de máscaras de bits
        private const byte MaskMultipleIps = 0b0000_0001; // Bit 0
        private const byte MaskPortMode = 0b0000_0110; // Bits 1 y 2
        private const byte MaskStructuredPorts = 0b0000_1000; // Bit 3
        private const byte MaskHostPort = 0b0001_0000; // Bit 4: separate host/LAN control port
        private const byte MaskExternalPortMayDiffer = 0b0010_0000; // Bit 5
        private const byte MaskNat = 0b0100_0000; // Bit 6
        private const byte MaskTor = 0b1000_0000; // Bit 7

        private byte _data;

        public ConnectionFlags() { }

        public ConnectionFlags(byte value)
        {
            _data = value;
        }

        /// <summary>
        /// Acceso directo al valor en crudo del byte empaquetado.
        /// </summary>
        public byte RawValue
        {
            get => _data;
            set => _data = value;
        }

        /// <summary>
        /// Bit 7: Indica si la conexión se enruta por TOR.
        /// </summary>
        public bool IsTor
        {
            get => (_data & MaskTor) != 0;
            set
            {
                if (value)
                    _data |= MaskTor;
                else
                    _data &= (byte)(MaskTor ^ 0xFF);
            }
        }

        /// <summary>
        /// Bit 6: Indica si el host se encuentra detrás de un NAT (true) o si tiene IP pública directa (false).
        /// </summary>
        public bool IsNat
        {
            get => (_data & MaskNat) != 0;
            set
            {
                if (value)
                    _data |= MaskNat;
                else
                    _data &= (byte)(MaskNat ^ 0xFF);
            }
        }

        /// <summary>
        /// Inverso de IsNat: true si la conexión es pública/directa sin NAT intermediario.
        /// </summary>
        public bool IsDirect
        {
            get => !IsNat;
            set => IsNat = !value;
        }

        /// <summary>
        /// Bit 0: false = 1 sola IP, true = múltiples IPs (array).
        /// </summary>
        public bool MultipleIps
        {
            get => (_data & MaskMultipleIps) != 0;
            set
            {
                if (value)
                    _data |= MaskMultipleIps;
                else
                    _data &= (byte)(MaskMultipleIps ^ 0xFF);
            }
        }

        /// <summary>Discovery/token carries an exact host port after its WAN port advertisement.</summary>
        public bool HasHostPort
        {
            get => (_data & MaskHostPort) != 0;
            set => _data = value ? (byte)(_data | MaskHostPort) : (byte)(_data & ~MaskHostPort);
        }

        /// <summary>
        /// Bit 5: external ports differ from the bound host port, or a list/range
        /// is advertised. Informational only; false does not guarantee port stability.
        /// </summary>
        public bool ExternalPortMayDiffer
        {
            get => (_data & MaskExternalPortMayDiffer) != 0;
            set => _data = value ? (byte)(_data | MaskExternalPortMayDiffer) : (byte)(_data & ~MaskExternalPortMayDiffer);
        }

        public bool IsSingleIp
        {
            get => !MultipleIps;
            set => MultipleIps = !value;
        }

        /// <summary>
        /// Bits 1 y 2: Determina el modo de puerto (Single, Multiple o Range).
        /// </summary>
        public PortMode PortMode
        {
            get => (PortMode)((_data & MaskPortMode) >> 1);
            set
            {
                _data = (byte)((_data & ~MaskPortMode) | (((byte)value << 1) & MaskPortMode));
            }
        }

        public bool IsSinglePort => PortMode == PortMode.Single;
        public bool IsMultiplePorts => PortMode == PortMode.Multiple;
        public bool IsPortRange => PortMode == PortMode.Range;

        /// <summary>
        /// Bit 3: the wire payload uses the mode-aware port encoding. Discovery
        /// readers accept the legacy minimum/maximum pair when this bit is false;
        /// range endpoint tokens use it to carry an exact preferred port.
        /// </summary>
        public bool UsesStructuredPorts
        {
            get => (_data & MaskStructuredPorts) != 0;
            set
            {
                if (value)
                    _data |= MaskStructuredPorts;
                else
                    _data &= (byte)(MaskStructuredPorts ^ 0xFF);
            }
        }

        // Conversiones implícitas para trabajar directamente con bytes
        public static implicit operator byte(ConnectionFlags flags) => flags?._data ?? 0;
        public static implicit operator ConnectionFlags(byte rawByte) => new ConnectionFlags(rawByte);

        public override string ToString()
        {
            if (IsTor)
                return $"ConnectionFlags [TOR, NAT: {(IsNat ? "Yes" : "No")}] (0x{_data:X2})";

            return $"ConnectionFlags [NAT: {(IsNat ? "Yes" : "No")}, IP: {(MultipleIps ? "Multiple" : "Single")}, Port: {PortMode}, ExternalPortMayDiffer: {ExternalPortMayDiffer}] (0x{_data:X2})";
        }
    }

    public sealed class DiscoveredPeerInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Discovered Peer";
        public string Token { get; set; } = "";
        public byte[] CertHash { get; set; } = Array.Empty<byte>();
        public string CertHashBase64 { get; set; } = "";
        public string[] Addresses { get; set; } = Array.Empty<string>();
        public string OnionAddress { get; set; } = "";
        public ushort[] PortArray { get; set; } = Array.Empty<ushort>();
        public ushort PreferredPort { get; set; }
        public QuicPunch.NetworkType NetworkType { get; set; } = QuicPunch.NetworkType.Static;
        public ConnectionFlags ConnectionFlags { get; set; } = new();
        public string? NostrPubKey { get; set; }
        public string Source { get; set; } = "Nostr";
        public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        private long _lastNostrAnnouncementTicks;
        public DateTime? LastNostrAnnouncementUtc
        {
            get
            {
                long ticks = Interlocked.Read(ref _lastNostrAnnouncementTicks);
                return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
            }
            set => Interlocked.Exchange(ref _lastNostrAnnouncementTicks, value?.ToUniversalTime().Ticks ?? 0);
        }
        internal void ObserveNostrAnnouncement(DateTime announcedAtUtc)
        {
            long ticks = announcedAtUtc.ToUniversalTime().Ticks;
            long previous = Interlocked.Read(ref _lastNostrAnnouncementTicks);
            while (ticks > previous)
            {
                long observed = Interlocked.CompareExchange(ref _lastNostrAnnouncementTicks, ticks, previous);
                if (observed == previous) return;
                previous = observed;
            }
        }
        public DateTime? DiscoveryAvailableUntilUtc => IsCertVerified && LastNostrAnnouncementUtc is { } announced
            ? announced + NostrDiscovery.AnnouncementMaxAge : null;
        public bool IsTor => NetworkType == QuicPunch.NetworkType.Tor || !string.IsNullOrEmpty(OnionAddress);
        public bool IsCertVerified { get; set; }
        public byte[]? CertPublicKey { get; set; }
        public string? CertPublicKeyBase64 { get; set; }
    }
}
