using System;
using System.Text.Json.Serialization;
using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Client.Radios;

/// <summary>
///     One radio slot of the local user. Only slot <see cref="PlayerRadioInfo.RadioId" /> (1) is ever switched on; the
///     other slots stay <see cref="Modulation.DISABLED" /> (the network format keeps 11 slots).
///     The frequency is chosen by the user (<see cref="BandPlan.MinFrequency" /> .. <see cref="BandPlan.MaxFrequency" />),
///     modulation and model always follow from it (<see cref="ApplyBandPlan" />). Only <see cref="ToRadioBase" /> is
///     sent to the server; the volume is local.
/// </summary>
public class Radio
{
    // Radio model (sound character, RadioModels/*.json key) - the model of the band; sent as RadioBase.Model
    public string model = "";

    public Modulation modulation = Modulation.DISABLED;

    // Hz
    public double freq = 1;

    // local receive volume 0..1, never sent
    public float volume = 1.0f;

    [JsonIgnore] public bool IsEnabled => modulation != Modulation.DISABLED;

    /// <summary>The band of the current frequency (label, modulation, model).</summary>
    [JsonIgnore]
    public RadioBand Band => BandPlan.GetBand(freq);

    /// <summary>
    ///     A switched on radio tuned to <paramref name="frequencyHz" /> (clamped to the tuning range and normalised, see
    ///     <see cref="BandPlan.Normalise" />) with the modulation and model of its band.
    /// </summary>
    public static Radio Create(double frequencyHz, float volume = 1.0f)
    {
        var radio = new Radio
        {
            freq = BandPlan.Normalise(frequencyHz),
            volume = float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : 1.0f
        };

        radio.ApplyBandPlan();
        return radio;
    }

    /// <summary>Sets modulation and model from the band of the current frequency. Call after every frequency change.</summary>
    public void ApplyBandPlan()
    {
        var band = BandPlan.GetBand(freq);
        modulation = band.Modulation;
        model = band.Model;
    }

    /// <summary>
    ///     Compares only what is sent to the server (see <see cref="ToRadioBase" />): a difference means a RADIO_UPDATE
    ///     is needed. The volume is local.
    /// </summary>
    public override bool Equals(object obj)
    {
        if (obj == null || GetType() != obj.GetType()) return false;

        var compare = (Radio)obj;

        if (!string.Equals(model ?? "", compare.model ?? "", StringComparison.Ordinal)) return false;
        if (!RadioBase.FreqCloseEnough(freq, compare.freq)) return false;
        if (modulation != compare.modulation) return false;

        return true;
    }

    // frequencies are compared with a tolerance in Equals, so they must not take part in the hash
    public override int GetHashCode()
    {
        return HashCode.Combine(model ?? "", modulation);
    }

    public Radio DeepClone()
    {
        // every field is a value type or an immutable string
        return (Radio)MemberwiseClone();
    }

    /// <summary>
    ///     The network view of this radio. Encryption and the guard (secondary) frequency are no longer supported -
    ///     the protocol fields stay 0.
    /// </summary>
    public RadioBase ToRadioBase()
    {
        return new RadioBase
        {
            enc = false,
            encKey = 0,
            freq = freq,
            modulation = modulation,
            secFreq = 0,
            Model = model
        };
    }
}
