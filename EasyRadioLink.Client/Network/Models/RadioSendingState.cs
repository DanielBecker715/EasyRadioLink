using System.Text.Json.Serialization;

namespace EasyRadioLink.Client.Network.Models;

/// <summary>Transmit state of the local user, shown by the radio panel (TX indicator).</summary>
public class RadioSendingState
{
    [JsonIgnore] public long LastSentAt { get; set; }

    public bool IsSending { get; set; }

    // radio index (1..10) that is transmitting
    public int SendingOn { get; set; }
}
