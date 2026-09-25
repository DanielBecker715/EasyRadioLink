using System;
using System.Text.Json.Serialization;
using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Client.Radios;

/// <summary>
///     One radio of the local user (slot 1..10; slot 0 is reserved and always disabled).
///     The layout part (name, modulation, range, guard, encryption capability, ...) comes from a
///     <see cref="RadioDefinition" /> (radios.json, radios-custom.json or the server's radio layout); the tuning part
///     (freq, model, guardEnabled, enc, encKey, volume, channel, simul) belongs to the user.
///     Only <see cref="ToRadioBase" /> is sent to the server.
/// </summary>
public class Radio
{
    // Frequencies below this value are placeholders (the defaults are 1 Hz), never a real guard frequency.
    public const double MinimumGuardFrequency = 10000;

    public string name = "";

    // Radio model (sound character, RadioModels/*.json key); sent as RadioBase.Model
    public string model = "";

    public Modulation modulation = Modulation.DISABLED;

    // Hz
    public double freq = 1;
    public double freqMin = 1;
    public double freqMax = 1;

    // configured guard / secondary receive frequency in Hz, 0 = the radio has no guard receiver
    public double guardFreq;

    // user toggle for the guard receiver (RadioHelper.ToggleGuard / SetGuard)
    public bool guardEnabled = true;

    // the radio may encrypt (scrambler controls are shown); the server must also allow encryption
    public bool encCapable;
    public bool enc;
    public byte encKey = RadioDefinition.MinEncryptionKey;

    // local receive volume 0..1, never sent
    public float volume = 1.0f;

    // selected preset channel (1-based), -1 = none
    public int channel = -1;

    // receive-only radio (scanner / monitor)
    public bool rxOnly;

    // part of simultaneous transmission
    public bool simul;

    /// <summary>Effective guard / secondary frequency as seen by the server and other users (0 = none / off).</summary>
    [JsonIgnore]
    public double secFreq => guardEnabled && HasGuard ? guardFreq : 0;

    /// <summary>True if the radio has a guard receiver that can be toggled.</summary>
    [JsonIgnore]
    public bool HasGuard => guardFreq > MinimumGuardFrequency;

    [JsonIgnore] public bool IsEnabled => modulation != Modulation.DISABLED;

    /// <summary>
    ///     Compares only what is sent to the server (see <see cref="ToRadioBase" />): a difference means a RADIO_UPDATE
    ///     is needed. Volume, channel, simul, name and the frequency range are local.
    /// </summary>
    public override bool Equals(object obj)
    {
        if (obj == null || GetType() != obj.GetType()) return false;

        var compare = (Radio)obj;

        if (!string.Equals(model ?? "", compare.model ?? "", StringComparison.Ordinal)) return false;
        if (!RadioBase.FreqCloseEnough(freq, compare.freq)) return false;
        if (modulation != compare.modulation) return false;
        if (enc != compare.enc) return false;
        if (encKey != compare.encKey) return false;
        if (!RadioBase.FreqCloseEnough(secFreq, compare.secFreq)) return false;

        return true;
    }

    // frequencies are compared with a tolerance in Equals, so they must not take part in the hash
    public override int GetHashCode()
    {
        return HashCode.Combine(model ?? "", modulation, enc, encKey);
    }

    public Radio DeepClone()
    {
        // every field is a value type or an immutable string
        return (Radio)MemberwiseClone();
    }

    /// <summary>The network view of this radio.</summary>
    public RadioBase ToRadioBase()
    {
        return new RadioBase
        {
            enc = enc,
            encKey = encKey,
            freq = freq,
            modulation = modulation,
            secFreq = secFreq,
            Model = model
        };
    }

    /// <summary>Creates a radio from a (validated) layout entry. The guard receiver starts switched on.</summary>
    public static Radio FromDefinition(RadioDefinition definition)
    {
        if (definition == null) return new Radio();

        return new Radio
        {
            name = definition.name ?? "",
            model = definition.model ?? "",
            modulation = definition.modulation,
            freq = definition.freq,
            freqMin = definition.freqMin,
            freqMax = definition.freqMax,
            guardFreq = definition.guardFreq,
            guardEnabled = true,
            encCapable = definition.encCapable,
            enc = definition.encCapable && definition.enc,
            encKey = Math.Clamp(definition.encKey, RadioDefinition.MinEncryptionKey,
                RadioDefinition.MaxEncryptionKey),
            volume = 1.0f,
            channel = definition.channel,
            rxOnly = definition.rxOnly,
            simul = definition.simul
        };
    }
}
