using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UdpPunchHoleTest;

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
        public static async Task<IPAddress?> GetPublicIP()
        {
            (string Host, int Port)[] servers =
            [
                ("stun.l.google.com",   19302),
                ("stun1.l.google.com",  19302),
                ("stun.cloudflare.com", 3478),
            ];

            foreach (var (host, port) in servers)
            {
                try
                {
                    using var udp = new UdpClient();

                    byte[] request = new byte[20];
                    BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), 0x0001);
                    Random.Shared.NextBytes(request.AsSpan(4, 16));

                    await udp.SendAsync(request.AsMemory(), host, port);
                    var result = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    var buffer = result.Buffer;

                    for (int i = 20; i < buffer.Length - 8; i++)
                    {
                        if (buffer[i] == 0x00 && buffer[i + 1] == 0x01)
                        {
                            var address = new IPAddress(buffer.AsSpan(i + 8, 4)); ;

                            return address;
                        }
                    }
                }
                catch { }
            }

            return null;
        }
        public static string EncodeEndpointToken(PeerInfo p)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(p.EndPoint.Address.GetAddressBytes());
                w.Write((ushort)p.EndPoint.Port);
                w.Write(p.CertHash);
                w.Write(p.CurvePublicKey);
                return Convert.ToBase64String(ms.ToArray());
            }
        }
        public static PeerInfo DecodeEndpointToken(string t)
        {
            using (var ms = new MemoryStream(Convert.FromBase64String(t)))
            using (var r = new BinaryReader(ms))
            {
                var addressBytes = r.ReadBytes(4);
                var port = r.ReadUInt16();
                var certHash = r.ReadBytes(512 / 8);
                var curvePublicKey = r.ReadBytes(32);

                return new PeerInfo
                {
                    EndPoint = new IPEndPoint(new IPAddress(addressBytes), port),
                    CertHash = certHash,
                    CurvePublicKey = curvePublicKey
                };
            }
        }

    }
}
