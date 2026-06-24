using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace QuicPunch
{
    internal static class Helpers
    {
        /*public static async Task BigSendAsync( this UdpClient udp, byte[] data, PeerInfo peerInfo)
           {
               if (peerInfo.MinPort > peerInfo.MaxPort || peerInfo.MinPort - peerInfo.MaxPort > ushort.MaxValue / 2)
                   throw new ArgumentException("Invalid port range.", nameof(peerInfo));

               var tasks = new List<Task>();

               foreach (var address in peerInfo.Addresses)
               {
                   for (int port = peerInfo.MinPort; port <= peerInfo.MaxPort; port++)
                   {
                       tasks.Add(udp.SendAsync(data, data.Length, new IPEndPoint(address, port)));
                   }
               }



               await Task.WhenAll(tasks);
           }*/public static async Task BigSendAsync( this UdpClient udp, byte[] data, PeerInfo peerInfo)
           {
               if ((peerInfo.MinPort > peerInfo.MaxPort && peerInfo.MinPort > 0 && peerInfo.MaxPort > 0) || peerInfo.MinPort - peerInfo.MaxPort > ushort.MaxValue / 2)
                   throw new ArgumentException("Invalid port range.", nameof(peerInfo));


               foreach (var address in peerInfo.Addresses)
               {
                   Console.WriteLine(peerInfo.MinPort + ", " + peerInfo.MaxPort);
                   
                   for (int port = peerInfo.MinPort; port <= peerInfo.MaxPort; port++)
                   {
                       Console.WriteLine("Sending packet to " + address + ":" + port); 
                       udp.SendAsync(data, data.Length, new IPEndPoint(address, port));
                   }
               }

           }
        


            
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

        
        static bool TryParseEndpoint(string line, out string host, out int port)
    {
        host = "";
        port = 0;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        line = line.Trim();

        int colon = line.LastIndexOf(':');
        if (colon <= 0 || colon == line.Length - 1)
            return false;

        host = line[..colon].Trim();
        return int.TryParse(line[(colon + 1)..].Trim(), out port);
    }

   public static IPEndPoint[]? ResolveEndpoint(string line)
    {
        if (!TryParseEndpoint(line, out var host, out var port))
            return null;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork) // IPv4 only
                return [new IPEndPoint(ip, port)];
        }

        try
        {
            var addresses = Dns.GetHostAddresses(host);

            return addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => new IPEndPoint(a, port)).ToArray();
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"DNS lookup failed for '{host}': {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to resolve '{host}': {ex.Message}");
            return null;
        }
    }


  public static QuicPunch.NetworkType GetNetworkType(ConcurrentDictionary<IPEndPoint, int> stunsHits)
    {
        if (stunsHits.Count == 0)
            throw new ArgumentException("No STUN hits provided.", nameof(stunsHits));
        
        var hitsByIp = stunsHits
            .GroupBy(x => x.Key.Address)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x => x.Value)
            ).OrderByDescending(e => e.Value).ToArray();

        var biggestAddressHits = hitsByIp[0].Value;
        var otherAddressHits = hitsByIp.Skip(1).Sum(e => e.Value);
        double addressRatio = (double)biggestAddressHits / (biggestAddressHits + otherAddressHits) ;
        
        var hitsByPort = stunsHits
            .GroupBy(x => x.Key.Port)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x => x.Value)
            ).OrderByDescending(e => e.Value).ToArray();

        var biggestPortHits = hitsByPort[0].Value;
        var otherPortHits = hitsByPort.Skip(1).Sum(e => e.Value);
        double portRatio = (double)biggestPortHits / (biggestPortHits + otherPortHits);

        if (addressRatio > 0.99 && portRatio > 0.99)
        {
            return QuicPunch.NetworkType.Static;
        }
        else if (addressRatio > 0.99 &&   portRatio < 0.99)
        {
            return QuicPunch.NetworkType.DynamicPort;
        }
        else if (addressRatio < 0.99 && portRatio > 0.99)
        {
            return QuicPunch.NetworkType.DynamicAddress;
        }

        return QuicPunch.NetworkType.DynamicPortAndAddress;
    }
  
   private const byte TokenVersionByte = 1;
        public static string EncodeEndpointToken(PeerInfo p)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                PackedFlags pf = new PackedFlags()
                {
                    NetworkType = p.NetworkType
                };

                w.Write((byte)pf.RawValue);

                if (p.NetworkType == QuicPunch.NetworkType.DynamicAddress || p.NetworkType == QuicPunch.NetworkType.DynamicPortAndAddress)
                {
                    w.Write((byte)p.Addresses.Length);

                    for (int i = 0; i < p.Addresses.Length; i++)
                    {
                        w.Write(p.Addresses[i].GetAddressBytes());
                    }
                }
                else
                {
                    w.Write(p.Addresses[0].GetAddressBytes());
                }

                if (pf.NetworkType == QuicPunch.NetworkType.DynamicPort || pf.NetworkType == QuicPunch.NetworkType.DynamicPortAndAddress)
                {
                    w.Write((short)p.MinPort);
                    w.Write((short)p.MaxPort);
                }
                else
                {
                    w.Write((ushort)p.MinPort);
                }

                //w.Write((byte)p.CertHash.Length);
                w.Write(p.CertHash);
                return Base64Url.EncodeToString(ms.ToArray());
            }
        }
        public static PeerInfo DecodeEndpointToken(string t)
        {
            var peer = new PeerInfo();
            
            using (var ms = new MemoryStream(Base64Url.DecodeFromChars(t)))
            using (var r = new BinaryReader(ms))
            {
                //var version = r.ReadByte();
                // if (version != TokenVersionByte) 
                //    throw new Exception("Invalid token version");

                PackedFlags pf = new PackedFlags(r.ReadByte());

                if (pf.NetworkType == QuicPunch.NetworkType.DynamicAddress || pf.NetworkType == QuicPunch.NetworkType.DynamicPortAndAddress)
                {
                   var addressesLength = r.ReadByte();

                   IPAddress[] addresses = new IPAddress[addressesLength];
                   
                    for (int i = 0; i < addressesLength; i++)
                    {
                        addresses[i] = new IPAddress(r.ReadBytes(4));
                    }

                    peer.Addresses = addresses;
                }
                else
                {
                    peer.Addresses = [new IPAddress(r.ReadBytes(4))];
                }

                if (pf.NetworkType == QuicPunch.NetworkType.DynamicPort || pf.NetworkType == QuicPunch.NetworkType.DynamicPortAndAddress)
                {
                    peer.MinPort = r.ReadUInt16();
                    peer.MaxPort = r.ReadUInt16();
                }
                else
                {
                    peer.MinPort = peer.MaxPort =  r.ReadUInt16();
                }
                
                var certHash = r.ReadBytes(384 / 8);
                peer.CertHash = certHash;
                return peer;
            }
        }
    }
}
