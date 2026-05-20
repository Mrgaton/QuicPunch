using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace QuicPunch
{
    public class PeerInfo
    {
        public byte[] CertHash;
        public byte[] CurvePublicKey;
        public string Name;
        public IPEndPoint EndPoint;
        public DateTime LastSeen;
        public long? UpTicks { get; set; }
        public long? DownTicks { get; set; }
        public TimeSpan? Ping { get; set; }
        public PeerInfo() { }
    }
}
