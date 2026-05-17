using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace QuicPunch
{
    internal class PeerInfo
    {
        public byte[] CertHash;
        public byte[] CurvePublicKey;
        public string Name;
        public IPEndPoint EndPoint;
        public DateTime LastSeen;
        public PeerInfo() { }
    }
}
