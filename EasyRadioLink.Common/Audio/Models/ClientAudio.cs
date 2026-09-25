using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Common.Audio.Models;

/// <summary>One received (or locally transmitted, for passthrough) Opus packet for one local radio.</summary>
public class ClientAudio
{
    public byte[] EncodedAudio { get; set; }

    // Guid of the client the packet came from (null for the local passthrough).
    public string ClientGuid { get; set; }
    public long ReceiveTime { get; set; }

    // Local radio (1..10) that receives the audio.
    public int ReceivedRadio { get; set; }
    public double Frequency { get; set; }

    // (short)Modulation of the transmission.
    public short Modulation { get; set; }

    // Linear volume of the receiving radio (0..1).
    public float Volume { get; set; }
    public short Encryption { get; set; }
    public bool Decryptable { get; set; }
    public ulong PacketNumber { get; set; }
    public string OriginalClientGuid { get; set; }

    // Play without radio effects (clean frequency). Clean frequencies of the server are also applied by
    // ClientAudioProvider, so callers only need to set this for their own reasons.
    public bool NoAudioEffects { get; set; }

    // Background sound of the SENDER (sender's PlayerRadioInfo.ambient); may be null.
    public Ambient Ambient { get; set; }
}
