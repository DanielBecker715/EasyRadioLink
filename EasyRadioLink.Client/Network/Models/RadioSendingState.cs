using System.Text.Json.Serialization;

namespace EasyRadioLink.Client.Network.Models;

/// <summary>Transmit state of the local user, shown by the radio window (TX indicator).</summary>
public class RadioSendingState
{
    [JsonIgnore] public long LastSentAt { get; set; }

    public bool IsSending { get; set; }

    // radio slot that is transmitting (always PlayerRadioInfo.RadioId)
    public int SendingOn { get; set; }
}
