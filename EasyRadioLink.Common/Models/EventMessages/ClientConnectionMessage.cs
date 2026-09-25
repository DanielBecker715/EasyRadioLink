namespace EasyRadioLink.Common.Models.EventMessages;

public class ClientConnectionMessage
{
    public bool Connected { get; set; }
    public string ClientIP { get; set; }
    public string ClientGuid { get; set; }
}