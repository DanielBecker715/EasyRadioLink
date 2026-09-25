using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Common.Models.EventMessages;

public class ClientUpdateMessage
{
    public ClientUpdateMessage(ClientInfo srClient, bool connected = true)
    {
        SrClient = srClient;
        Connected = connected;
    }

    public ClientInfo SrClient { get; }
    public bool Connected { get; }
}