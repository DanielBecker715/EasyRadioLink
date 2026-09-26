using System.Collections.Generic;
using System.Net;

namespace EasyRadioLink.Common.Models;

public class OutgoingUDPPackets
{
    public IReadOnlyCollection<IPEndPoint> OutgoingEndPoints { get; set; }
    public byte[] ReceivedPacket { get; set; }
}