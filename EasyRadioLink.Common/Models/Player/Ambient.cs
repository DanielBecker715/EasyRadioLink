using System;

namespace EasyRadioLink.Common.Models.Player;

public class Ambient
{
    //  public float pitch = 1.0f;
    public string abType = "";
    public float vol = 0.15f;

    // "Boost my voice" of the sender, 0..1 (see VoiceBoost); unknown to 1.1 / 1.2, which ignore it
    public float voiceBoost;

    public override bool Equals(object obj)
    {
        if (obj == null || GetType() != obj.GetType())
            return false;

        var compare = (Ambient)obj;

        if (vol != compare.vol) return false;

        if (abType != compare.abType) return false;

        if (voiceBoost != compare.voiceBoost) return false;

        return true;
    }

    // https://stackoverflow.com/a/61730200
    public override int GetHashCode() => HashCode.Combine(vol, abType, voiceBoost);

    public Ambient Copy()
    {
        return new Ambient
        {
            vol = vol,
            abType = abType,
            voiceBoost = voiceBoost
        };
    }
}