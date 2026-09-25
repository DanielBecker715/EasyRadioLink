namespace EasyRadioLink.Common.Network.Client;

public class InvalidServerVersionMessage
{
    public InvalidServerVersionMessage(string serverVersion)
    {
        ServerVersion = serverVersion;
    }

    public string ServerVersion { get; }
}