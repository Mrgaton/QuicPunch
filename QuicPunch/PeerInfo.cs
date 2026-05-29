using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace QuicPunch
{
    public class PeerInfo
    {
        public byte[] PasswordProofData;

        public byte[] CertHash;
        public string Name;
        public IPEndPoint EndPoint;
        public DateTime LastSeen;

        public ECDsa Curve;
        public long? UpTicks { get; set; }
        public long? DownTicks { get; set; }
        public TimeSpan? Ping { get; set; }
        public PeerInfo() { }
    }
}
