using System.Net;

namespace EasyRadioLink.Common.Models.EventMessages;

public class TCPClientStatusMessage
{
    public enum ErrorCode
    {
        MISMATCHED_SERVER, // not an EasyRadioLink server or unsupported protocol version
        TIMEOUT, // could not connect or the connection was lost
        INVALID_SERVER, // the server accepted the connection but did not complete the handshake
        USER_DISCONNECTED, // disconnect requested by the user - not an error
        AUTH_FAILED // wrong or missing server password
    }

    public TCPClientStatusMessage(bool connected)
    {
        Connected = connected;
    }

    public TCPClientStatusMessage(bool connected, IPEndPoint address)
    {
        Connected = connected;
        Address = address;
    }

    public TCPClientStatusMessage(bool connected, ErrorCode error)
    {
        Error = error;
        Connected = connected;
    }

    public ErrorCode Error { get; }
    public IPEndPoint Address { get; }

    public bool Connected { get; }
}