using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace QuicPunch
{
    public class PeerInfo
    {
        public byte[] CertHash;
        public string Name;
        
        public QuicPunch.NetworkType NetworkType;
        
        public IPEndPoint ActiveEndPoint;
        
        public IPAddress[] Addresses;

        public int MinPort;
        public int MaxPort;
        
        public DateTime LastSeen;

        public ECDsa Curve;
        public long? UpTicks { get; set; }
        public long? DownTicks { get; set; }
        public TimeSpan? Ping { get; set; }
        public PeerInfo() { }
    }
}
