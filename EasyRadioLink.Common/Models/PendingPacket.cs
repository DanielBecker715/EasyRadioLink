using System.Net;

namespace EasyRadioLink.Common.Models;

public class PendingPacket
{
    public IPEndPoint ReceivedFrom { get; set; }
    public byte[] RawBytes { get; set; }
}