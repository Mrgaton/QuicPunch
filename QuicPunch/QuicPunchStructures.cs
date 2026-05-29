using System;
using System.Collections.Generic;
using System.Text;

namespace QuicPunch
{
    internal class QuicPunchStructures
    {
        public enum MessageType : byte
        {
            Hello = (byte)('H'),
            Ping = (byte)('P'),
            Interogation = (byte)('I'),
            Ack = (byte)('K'),
            Handshake = (byte)('S'),
            FinalHandshake = (byte)('F')
        }
        public enum HandShakeType : byte
        {
            Request = (byte)('R'),
            Accept = (byte)('A'),
            Decline = (byte)('D'),
            Unsuported = (byte)('U') //Peer doesnt support the requested protocol
        }

    }
}
