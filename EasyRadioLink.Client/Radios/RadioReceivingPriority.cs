using EasyRadioLink.Common.Models;

namespace EasyRadioLink.Client.Radios;

/// <summary>One local radio that can receive a voice packet (used to pick the radio that plays it).</summary>
public class RadioReceivingPriority
{
    public bool Decryptable;
    public byte Encryption;
    public double Frequency;
    public short Modulation;
    public Radio ReceivingRadio;

    public RadioReceivingState ReceivingState;
}
