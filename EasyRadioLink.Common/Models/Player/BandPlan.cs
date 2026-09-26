using System;
using System.Collections.Generic;

namespace EasyRadioLink.Common.Models.Player;

/// <summary>One band of the <see cref="BandPlan" />.</summary>
/// <param name="Label">Short label shown on the radio display ("CB", "PMR", "AIR", ...).</param>
/// <param name="Modulation">Modulation every radio uses on this band.</param>
/// <param name="Model">Radio model key (sound character, <c>RadioModels\&lt;key&gt;.json</c>).</param>
/// <param name="FirstFrequency">First frequency of the band on the 1 kHz grid, in Hz (inclusive).</param>
/// <param name="LastFrequency">Last frequency of the band on the 1 kHz grid, in Hz (inclusive).</param>
public sealed record RadioBand(
    string Label,
    Modulation Modulation,
    string Model,
    double FirstFrequency,
    double LastFrequency)
{
    /// <summary>Lower edge in Hz (inclusive): <see cref="FirstFrequency" /> - 500 Hz.</summary>
    public double LowerEdge => FirstFrequency - BandPlan.EdgeMargin;

    /// <summary>Upper edge in Hz (exclusive): <see cref="LastFrequency" /> + 500 Hz.</summary>
    public double UpperEdge => LastFrequency + BandPlan.EdgeMargin;

    /// <summary>True if <paramref name="frequencyHz" /> lies in [<see cref="LowerEdge" />, <see cref="UpperEdge" />).</summary>
    public bool Contains(double frequencyHz)
    {
        return frequencyHz >= LowerEdge && frequencyHz < UpperEdge;
    }
}

/// <summary>
///     The frequency plan of the single radio: the frequency alone decides the modulation, the radio model (sound) and
///     the band label. Sender and receiver apply the same rule, so everybody on the same frequency uses the same
///     modulation and hears the same radio sound.
///     <para>
///         Tuning range <see cref="MinFrequency" /> (1.000 MHz) to <see cref="MaxFrequency" /> (999.999 MHz), 1 kHz
///         steps; typed frequencies may be finer (e.g. PMR446 446.19375 MHz).
///     </para>
///     <para>
///         Boundaries: every band is listed with its first and last frequency on the 1 kHz grid (both inclusive,
///         e.g. CB 26.965 - 27.405 MHz). A frequency belongs to the band whose range
///         [first - 500 Hz, last + 500 Hz) contains it. The edges sit exactly half way between two kHz steps, so a
///         frequency on (or within floating point noise of) the kHz grid is never ambiguous: 27.405 MHz is CB,
///         27.406 MHz is HF, 446.19375 MHz is PMR and 446.200 MHz is UHF. The 500 Hz margin equals the receive
///         tolerance of <see cref="RadioBase.FreqCloseEnough" />.
///     </para>
///     <para>
///         Modulation edges: where the modulation changes (<see cref="ModulationEdges" />: 30, 108, 137, 225, 400 and
///         900 MHz) two radios less than 500 Hz apart could otherwise end up on different modulations (29.9996 MHz is
///         FM, 29.9994 MHz is AM) and not hear each other. The client therefore tunes to <see cref="Normalise" />d
///         frequencies only: closer than 1 kHz to such an edge they are rounded to the whole kHz.
///     </para>
///     Frequencies outside the tuning range belong to the nearest band (below: MW, above: DIG); use
///     <see cref="Clamp" /> to bring a frequency into the tuning range.
/// </summary>
public static class BandPlan
{
    /// <summary>Half a kHz step - the distance of a band edge from its first / last kHz.</summary>
    public const double EdgeMargin = 500;

    /// <summary>
    ///     <see cref="Normalise" /> rounds frequencies closer than this (1 kHz) to a <see cref="ModulationEdges" /> entry
    ///     to the whole kHz.
    /// </summary>
    public const double ModulationEdgeSnap = 1000;

    /// <summary>Lowest tunable frequency: 1.000 MHz.</summary>
    public const double MinFrequency = 1_000_000;

    /// <summary>Highest tunable frequency: 999.999 MHz.</summary>
    public const double MaxFrequency = 999_999_000;

    /// <summary>Start frequency of a new radio: 27.185 MHz (CB channel 19).</summary>
    public const double DefaultFrequency = 27_185_000;

    /// <summary>The bands in ascending order, gapless from <see cref="MinFrequency" /> to <see cref="MaxFrequency" />.</summary>
    public static readonly IReadOnlyList<RadioBand> Bands = new[]
    {
        new RadioBand("MW", Modulation.AM, "vintage", KHz(1_000), KHz(2_999)),
        new RadioBand("HF", Modulation.AM, "hf", KHz(3_000), KHz(26_964)),
        new RadioBand("CB", Modulation.AM, "cb", KHz(26_965), KHz(27_405)),
        new RadioBand("HF", Modulation.AM, "hf", KHz(27_406), KHz(29_999)),
        new RadioBand("VHF", Modulation.FM, "tactical", KHz(30_000), KHz(87_999)),
        new RadioBand("FM", Modulation.FM, "walkie", KHz(88_000), KHz(107_999)),
        new RadioBand("AIR", Modulation.AM, "airband", KHz(108_000), KHz(136_999)),
        new RadioBand("VHF", Modulation.FM, "walkie", KHz(137_000), KHz(224_999)),
        new RadioBand("UHF", Modulation.AM, "tactical", KHz(225_000), KHz(399_999)),
        new RadioBand("UHF", Modulation.FM, "walkie", KHz(400_000), KHz(445_999)),
        new RadioBand("PMR", Modulation.FM, "walkie", KHz(446_000), KHz(446_199)),
        new RadioBand("UHF", Modulation.FM, "walkie", KHz(446_200), KHz(899_999)),
        new RadioBand("DIG", Modulation.DIGITAL, "digital", KHz(900_000), KHz(999_999))
    };

    /// <summary>
    ///     The band edges (<see cref="RadioBand.LowerEdge" />, in Hz, ascending) where the modulation changes:
    ///     29.9995, 107.9995, 136.9995, 224.9995, 399.9995 and 899.9995 MHz. Edges where only the label or the radio
    ///     model changes (CB, PMR, ...) are not included - radios on both sides hear each other anyway.
    /// </summary>
    public static readonly IReadOnlyList<double> ModulationEdges = FindModulationEdges();

    /// <summary>
    ///     The band of <paramref name="frequencyHz" />. Never null: below the tuning range the first band (MW), above
    ///     it the last band (DIG); NaN is treated as <see cref="DefaultFrequency" />.
    /// </summary>
    public static RadioBand GetBand(double frequencyHz)
    {
        if (double.IsNaN(frequencyHz)) frequencyHz = DefaultFrequency;

        if (frequencyHz < Bands[0].LowerEdge) return Bands[0];

        foreach (var band in Bands)
            if (band.Contains(frequencyHz))
                return band;

        return Bands[^1];
    }

    /// <summary>
    ///     Brings a frequency into the tuning range [<see cref="MinFrequency" />, <see cref="MaxFrequency" />] and
    ///     rounds it to whole Hz. NaN becomes <see cref="DefaultFrequency" />, infinities the nearest limit.
    /// </summary>
    public static double Clamp(double frequencyHz)
    {
        if (double.IsNaN(frequencyHz)) return DefaultFrequency;

        return Math.Round(Math.Clamp(frequencyHz, MinFrequency, MaxFrequency));
    }

    /// <summary>
    ///     The frequency the radio is tuned to when <paramref name="frequencyHz" /> is requested (knob, keys, direct
    ///     entry, hotkeys, the remembered frequency): <see cref="Clamp" />ed into the tuning range and - closer than
    ///     <see cref="ModulationEdgeSnap" /> (1 kHz) to one of the <see cref="ModulationEdges" /> - rounded to the whole
    ///     kHz (29.9996 MHz -> 30.000 MHz FM, 29.9994 MHz -> 29.999 MHz AM).
    ///     <para>
    ///         Near such an edge every normalised frequency is then either on the last / first kHz of the two bands or at
    ///         least 1 kHz away from the edge, so frequencies of different modulations are at least 1 kHz apart - more
    ///         than the receive tolerance of <see cref="RadioBase.FreqCloseEnough" /> (500 Hz). Two radios close enough
    ///         to hear each other always use the same modulation.
    ///     </para>
    /// </summary>
    public static double Normalise(double frequencyHz)
    {
        var clamped = Clamp(frequencyHz);

        foreach (var edge in ModulationEdges)
            if (Math.Abs(clamped - edge) < ModulationEdgeSnap)
                return KHz((int)Math.Round(clamped / 1000, MidpointRounding.AwayFromZero));

        return clamped;
    }

    /// <summary>True if <paramref name="frequencyHz" /> is inside the tuning range (1 Hz tolerance for rounding).</summary>
    public static bool IsInRange(double frequencyHz)
    {
        return double.IsFinite(frequencyHz) && frequencyHz >= MinFrequency - 1 && frequencyHz <= MaxFrequency + 1;
    }

    private static double KHz(int kiloHertz)
    {
        return kiloHertz * 1000d;
    }

    private static IReadOnlyList<double> FindModulationEdges()
    {
        var edges = new List<double>();
        for (var i = 1; i < Bands.Count; i++)
            if (Bands[i].Modulation != Bands[i - 1].Modulation)
                edges.Add(Bands[i].LowerEdge);

        return edges.ToArray();
    }
}
