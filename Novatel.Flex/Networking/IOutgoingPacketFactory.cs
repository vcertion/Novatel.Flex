﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Novatel.Flex.Networking
{
    internal interface IOutgoingPacketFactory : IPacketProcessor
    {
        Packet CreatePacket();
    }
}
