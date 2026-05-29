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
            var endpoint = await StunClient.GetMappedEndpointAsync(
                udp,
                TimeSpan.FromSeconds(2),
                cancellationToken);

            if (endpoint is null)
                throw new Exception("Failed to get public EndPoint");

            return endpoint;
        }

        public static async Task<IPAddress?> GetPublicIP()
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);

            var endpoint = await StunClient.GetMappedEndpointAsync(
                udp,
                TimeSpan.FromSeconds(2));

            if (endpoint is null)
                throw new Exception("Failed to get public IP");

            return endpoint.Address;
        }

      

    }
}
