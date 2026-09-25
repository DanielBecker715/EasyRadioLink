using System;
using System.Collections.Generic;
using System.Globalization;

namespace EasyRadioLink.Common.Helpers;

/// <summary>
///     Frequency unit helpers. All parsing and formatting is culture independent (<see cref="CultureInfo.InvariantCulture" />):
///     "27.405" is always 27.405 MHz, also on a German Windows.
/// </summary>
public static class RadioCalculator
{
    public const double MHz = 1e6;

    private static readonly char[] ListSeparators = { ',', ';' };

    /// <summary>
    ///     Parses a comma (or semicolon) separated list of frequencies in MHz, e.g. <c>"27.405,446.19375"</c>, into Hz.
    ///     Invalid, non-positive or non-finite entries are skipped. Never throws.
    /// </summary>
    public static List<double> ParseFrequencyListMHz(string mhzList)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(mhzList)) return result;

        foreach (var part in mhzList.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
                && double.IsFinite(mhz) && mhz > 0)
                result.Add(Math.Round(mhz * MHz));

        return result;
    }

    /// <summary>
    ///     Normalises a user entered MHz list to the canonical invariant form ("27.405,446.19375").
    ///     Invalid entries are dropped.
    /// </summary>
    public static string NormaliseFrequencyListMHz(string mhzList)
    {
        var frequencies = ParseFrequencyListMHz(mhzList);
        var formatted = new List<string>(frequencies.Count);
        foreach (var hz in frequencies) formatted.Add(FormatMHz(hz));

        return string.Join(",", formatted);
    }

    /// <summary>
    ///     Parses a single frequency typed by a user, in MHz, into Hz. Uses the invariant culture; as a convenience a
    ///     single comma is accepted as decimal separator when the text contains no dot ("27,405" = 27.405 MHz).
    /// </summary>
    public static bool TryParseMHz(string text, out double frequencyHz)
    {
        frequencyHz = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (!trimmed.Contains('.') && trimmed.IndexOf(',') == trimmed.LastIndexOf(','))
            trimmed = trimmed.Replace(',', '.');

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
            || !double.IsFinite(mhz) || mhz <= 0)
            return false;

        frequencyHz = Math.Round(mhz * MHz);
        return true;
    }

    /// <summary>Formats a frequency in Hz as MHz with 3 to 5 decimals, invariant culture ("27.185", "446.00625").</summary>
    public static string FormatMHz(double frequencyHz)
    {
        return (frequencyHz / MHz).ToString("0.000##", CultureInfo.InvariantCulture);
    }
}
