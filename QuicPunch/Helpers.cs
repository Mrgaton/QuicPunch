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
        public static string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuicPunchV3");

        public static PeerInfo CurrentPeer = new PeerInfo()
        {
            Name = Environment.UserName,
            EndPoint = new IPEndPoint(GetPublicIP().Result, QuicPunchCore.LocalPort),
            CertHash = CertManager.PeerCertPublicHash,
            CurvePublicKey = CertManager.Curve.ExportSubjectPublicKeyInfo()
        };
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
        public static string EncodeEndpointToken(IPAddress ip, int port) => EncodeEndpointToken(new IPEndPoint(ip, port));
        public static string EncodeEndpointToken(IPEndPoint ep)
        {
            byte[] r = new byte[6];
            Buffer.BlockCopy(ep.Address.GetAddressBytes(), 0, r, 0, 4);
            r[4] = (byte)(ep.Port >> 8);
            r[5] = (byte)ep.Port; return Convert.ToBase64String(r);
        }
        public static IPEndPoint DecodeEndpointToken(string t)
        {
            byte[] r = Convert.FromBase64String(t); 
            return new IPEndPoint(new IPAddress(r.Take(4).ToArray()), (r[4] << 8) | r[5]); 
        }

    }
}
