using System;
using System.Text.RegularExpressions;

namespace EasyRadioLink.Common.Models.Player;

/// <summary>
///     Network view of one radio: <c>{"enc","encKey","freq","modulation","secFreq","Model"}</c>.
///     <see cref="secFreq" /> is the effective guard/secondary frequency (0 or 1 = none).
/// </summary>
public partial class RadioBase
{
    public bool enc; // encryption enabled
    public byte encKey;
    public double freq = 1;
    public Modulation modulation = Modulation.DISABLED;
    public double secFreq = 1;

    private string _model = "";

    // Radio model of the sender (cb, walkie, airband, ...), lowercase alphanumeric only. Colours the transmission
    // on the receiving side.
    public string Model
    {
        get => _model;
        set
        {
            value ??= "";

            value = value.ToLowerInvariant().Trim();

            value = NormaliseRadioRegex().Replace(value, "");
            if (value.Length > 32) value = value.Substring(0, 32);
            _model = value;
        }
    }


    /**
     * Used to determine if we should send an update to the server or not
     * We only need to do that if something that would stop us Receiving happens which
     * is frequencies and modulation
     */
    public override bool Equals(object obj)
    {
        if (obj == null || GetType() != obj.GetType())
            return false;

        var compare = (RadioBase)obj;

        if (!FreqCloseEnough(freq, compare.freq)) return false;
        if (modulation != compare.modulation) return false;
        if (enc != compare.enc) return false;
        if (encKey != compare.encKey) return false;
        if (!FreqCloseEnough(secFreq, compare.secFreq)) return false;
        if (Model != compare.Model) return false;

        return true;
    }

    // Frequencies are compared with a tolerance in Equals, so they must not take part in the hash.
    public override int GetHashCode()
    {
        return HashCode.Combine(modulation, enc, encKey, Model);
    }

    //comparing doubles is risky - check that we're close enough to hear (within 500 Hz)
    public static bool FreqCloseEnough(double freq1, double freq2)
    {
        var diff = Math.Abs(freq1 - freq2);

        return diff < 500;
    }

    public RadioBase DeepClone()
    {
        return new RadioBase
        {
            enc = enc,
            modulation = modulation,
            secFreq = secFreq,
            encKey = encKey,
            freq = freq,
            Model = Model
        };
    }


    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NormaliseRadioRegex();
}
