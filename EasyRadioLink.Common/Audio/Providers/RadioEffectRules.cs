using System;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;

namespace EasyRadioLink.Common.Audio.Providers;

/// <summary>Which radio effects apply to a received transmission - kept in one place for the receive pipeline.</summary>
internal static class RadioEffectRules
{
    /// <summary>
    ///     Transmissions at or below this frequency get the grainy HF static (pink noise shaped by the internal
    ///     "hfnoise" model); above it they get white noise. 30 MHz so CB (26.965 - 27.405 MHz) sounds like HF.
    /// </summary>
    public const double HfNoiseFrequencyCutoff = 30e6;

    // Below 1 MHz the frequency based noise gain would become positive (and infinite at 0 Hz).
    private const double MinimumNoiseReferenceFrequency = 1e6;

    /// <summary>
    ///     A transmission is played without radio effects when the caller flagged it (<c>ClientAudio.NoAudioEffects</c>)
    ///     or when it is on one of the server's clean frequencies (CLEAN_FREQUENCIES, +-500 Hz).
    /// </summary>
    public static bool PlayWithoutRadioEffects(bool flagged, double frequency, SyncedServerSettings serverSettings)
    {
        return flagged || (serverSettings != null && serverSettings.IsCleanFrequency(frequency));
    }

    /// <summary>DIGITAL uses the clean path: no static, no FM tone, no FM capture, no squelch tail.</summary>
    public static bool IsCleanPath(Modulation modulation)
    {
        return modulation == Modulation.DIGITAL;
    }

    public static bool IsHfNoise(double frequency)
    {
        return frequency <= HfNoiseFrequencyCutoff;
    }

    /// <summary>
    ///     Base static level in dB for a frequency: 0 dB at 1 MHz, about -17 dB at 30 MHz, -24 dB at 120 MHz and
    ///     -28 dB at 250 MHz (HF is much more susceptible to noise than V/UHF). Never positive.
    /// </summary>
    public static double FrequencyNoiseGainDb(double frequency)
    {
        if (double.IsNaN(frequency) || frequency < MinimumNoiseReferenceFrequency)
            frequency = MinimumNoiseReferenceFrequency;

        return -Math.Log(frequency / MinimumNoiseReferenceFrequency) * 10 / 2;
    }

    /// <summary>The FM tone (ex NATO tone) is only mixed into FM transmissions.</summary>
    public static bool HasFmTone(Modulation modulation)
    {
        return modulation == Modulation.FM;
    }

    /// <summary>Only AM and FM transmissions with radio effects end with a squelch tail.</summary>
    public static bool HasSquelchTail(Modulation modulation, bool noAudioEffects)
    {
        return !noAudioEffects && modulation is Modulation.AM or Modulation.FM;
    }

    /// <summary>With radio interference enabled, FM transmissions (with radio effects) capture the channel.</summary>
    public static bool TakesPartInFmCapture(Modulation modulation, bool noAudioEffects)
    {
        return !noAudioEffects && modulation == Modulation.FM;
    }
}

/// <summary>
///     The short noise burst ("kssht") a receiver makes when the carrier drops and the squelch closes.
/// </summary>
internal static class SquelchTail
{
    public const int DurationMs = 250;

    // 2 ms fade in so the burst does not start with a click
    private const int AttackSamples = 96;

    public static int Samples => Constants.OUTPUT_SAMPLE_RATE * DurationMs / 1000;

    /// <summary>
    ///     Writes a fading squelch tail into <paramref name="destination" />: <paramref name="noise" /> from
    ///     <paramref name="startOffset" />, scaled by <paramref name="gain" />, with a short fade in and a quadratic
    ///     fade out to silence. Returns the number of samples written.
    /// </summary>
    public static int Render(ReadOnlySpan<float> noise, int startOffset, Span<float> destination, float gain)
    {
        if (startOffset < 0 || startOffset >= noise.Length) startOffset = 0;

        var count = Math.Min(destination.Length, noise.Length - startOffset);
        if (count <= 0) return 0;

        gain = Math.Clamp(gain, 0f, 1f);

        for (var i = 0; i < count; i++)
        {
            var attack = i < AttackSamples ? (float)i / AttackSamples : 1f;
            var remaining = 1f - (float)i / count;

            destination[i] = noise[startOffset + i] * gain * attack * remaining * remaining;
        }

        return count;
    }
}
