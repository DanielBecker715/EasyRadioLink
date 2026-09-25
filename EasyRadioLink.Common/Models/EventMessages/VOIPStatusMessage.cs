namespace EasyRadioLink.Common.Models.EventMessages;

public class VOIPStatusMessage
{
    public VOIPStatusMessage(bool con)
    {
        Connected = con;
    }

    public bool Connected { get; }
}