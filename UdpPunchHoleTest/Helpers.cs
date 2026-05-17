using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using UdpPunchHoleTest;

namespace QuicPunch
{
    internal class Helpers
    {

        public static Process CurrentProcess = Process.GetCurrentProcess();
        public static string FileName = CurrentProcess.MainModule.FileName;

        public static string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuicPunchV3");

        public static PeerInfo CurrentPeer = new PeerInfo()
        {
            Name = Environment.UserName,
            EndPoint = new IPEndPoint(QuicPunchCore.IPv4Address, QuicPunchCore.LocalPort),
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
    }
}
