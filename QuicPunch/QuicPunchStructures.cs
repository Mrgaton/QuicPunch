using System;
using System.Collections.Generic;
using System.Text;

namespace QuicPunch
{
    internal static class QuicPunchStructures
    {
        public enum MessageType : byte
        {
            Hello = (byte)('H'),
            Ping = (byte)('P'),
            Interrogation = (byte)('I'),
            Ack = (byte)('K'),
            Handshake = (byte)('S'),
            FinalHandshake = (byte)('F'),
            Data = (byte)('D'),
            Disconnect = (byte)('X')
        }
        public enum HandShakeType : byte
        {
            Request = (byte)('R'),
            Accept = (byte)('A'),
            Decline = (byte)('D'),
            Unsupported = (byte)('U') //Peer doesnt support the requested protocol
        }

    }
}
