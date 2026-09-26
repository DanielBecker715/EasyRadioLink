using System;
using System.Collections.Generic;

namespace EasyRadioLink.Client.Utils;

/// <summary>
///     The tuning steps of the radio window (STEP button): one decade per digit of the frequency display
///     <c>000.000</c> MHz, from 1 kHz (last digit) to 100 MHz (first digit).
/// </summary>
public static class TuningSteps
{
    /// <summary>1 kHz.</summary>
    public const int Default = 1_000;

    /// <summary>All steps in Hz, smallest first (the order the STEP button cycles through).</summary>
    public static readonly IReadOnlyList<int> All = new[] { 1_000, 10_000, 100_000, 1_000_000, 10_000_000, 100_000_000 };

    public static bool IsValid(int step)
    {
        foreach (var candidate in All)
            if (candidate == step)
                return true;

        return false;
    }

    /// <summary>The next larger step; after 100 MHz it starts again at 1 kHz. An invalid step gives <see cref="Default" />.</summary>
    public static int Next(int step)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i] == step)
                return All[(i + 1) % All.Count];

        return Default;
    }

    /// <summary>
    ///     Index of the digit of the display <c>000.000</c> (6 digits, 0 = hundreds of MHz) that the step changes:
    ///     1 kHz = 5 ... 100 MHz = 0.
    /// </summary>
    public static int DigitIndex(int step)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i] == step)
                return All.Count - 1 - i;

        return All.Count - 1;
    }

    /// <summary>"1 kHz", "10 kHz", "100 kHz", "1 MHz", "10 MHz", "100 MHz".</summary>
    public static string Label(int step)
    {
        return step >= 1_000_000 ? $"{step / 1_000_000} MHz" : $"{Math.Max(1, step / 1_000)} kHz";
    }
}
