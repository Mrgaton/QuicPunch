using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace QuicPunch
{
    internal class Helpers
    {
        public static byte[] Combine(params byte[][] arrays)
        {
            int len = 0;
            foreach (var a in arrays) len += a.Length;

            var r = new byte[len];
            int o = 0;

            foreach (var a in arrays)
            {
                Buffer.BlockCopy(a, 0, r, o, a.Length);
                o += a.Length;
            }

            return r;
        }
        public static async Task<IPEndPoint?> GetPublicEndPoint(UdpClient udp, CancellationToken cancellationToken = default)
        {
            return await StunClient.GetMappedEndpointAsync(
                udp,
                TimeSpan.FromSeconds(2),
                cancellationToken);
        }

        public static async Task<IPAddress?> GetPublicIP()
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);

            var endpoint = await StunClient.GetMappedEndpointAsync(
                udp,
                TimeSpan.FromSeconds(2));

            return endpoint?.Address;
        }

        private const byte TokenVersionByte = 1;
        public static string EncodeEndpointToken(PeerInfo p)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(TokenVersionByte);
                w.Write(p.EndPoint.Address.GetAddressBytes());
                w.Write((ushort)p.EndPoint.Port);

                w.Write((byte)p.CertHash.Length);
                w.Write(p.CertHash);
                return Convert.ToBase64String(ms.ToArray());
            }
        }
        public static PeerInfo DecodeEndpointToken(string t)
        {
            using (var ms = new MemoryStream(Convert.FromBase64String(t)))
            using (var r = new BinaryReader(ms))
            {
                var version = r.ReadByte();
                if (version != TokenVersionByte) 
                    throw new Exception("Invalid token version");

                var addressBytes = r.ReadBytes(4);
                var port = r.ReadUInt16();

                var certHashLength = r.ReadByte();
                var certHash = r.ReadBytes(certHashLength);

                return new PeerInfo
                {
                    EndPoint = new IPEndPoint(new IPAddress(addressBytes), port),
                    CertHash = certHash,
                };
            }
        }

    }
}
