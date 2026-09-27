using System;

namespace EasyRadioLink.Common.Audio.Utility;

/// <summary>
///     How loud a background sound (jet, prop, helicopter, ...) is mixed into the sender's voice.
///     <para>
///         The level is set by the loudness in the radio band (<see cref="RadioBandLowHz" /> -
///         <see cref="RadioBandHighHz" />), the part a radio passes, so every sound is equally audible through a radio at
///         the same volume, whatever its bass content: <see cref="FullVolumeRadioBandDbfs" /> at 100 % (the loudness of the
///         other radio sounds, a little below speech after the receive AGC) and 20 * log10(volume) below that at lower
///         volumes (40 % = -8 dB, 25 % = -12 dB). That keeps it clearly above the static, also on HF and with distance,
///         and below the voice while someone talks.
///     </para>
///     <para>
///         A sound that is mostly bass is limited so its full band level stays at most <see cref="MaxFullBandAboveDb" />
///         above that (no droning rumble louder than the voice in the dry share of the mix), and no sound is raised by
///         more than <see cref="MaxGainDb" /> (an almost silent file stays quiet).
///     </para>
/// </summary>
public static class BackgroundSoundLevel
{
    /// <summary>Loudness (RMS in the radio band) of a background sound at 100 % volume.</summary>
    public const double FullVolumeRadioBandDbfs = -24;

    /// <summary>How far the full band RMS may be above the radio band target (limits bass-heavy sounds).</summary>
    public const double MaxFullBandAboveDb = 6;

    /// <summary>Largest gain applied to a background sound.</summary>
    public const double MaxGainDb = 20;

    public const float RadioBandLowHz = 300;
    public const float RadioBandHighHz = 3000;

    /// <summary>Background sounds lose their rumble below this frequency, like behind a headset microphone.</summary>
    public const float MicrophoneHighPassHz = 150;

    /// <summary>
    ///     Removes the rumble below <see cref="MicrophoneHighPassHz" /> from a background sound in place (2nd order
    ///     high-pass), like a headset microphone does: a radio does not pass it anyway, and in the dry share of the mix it
    ///     would be a drone louder than the voice. The filter is primed with the end of the sound, so the loop point stays
    ///     seamless.
    /// </summary>
    public static void RemoveRumble(float[] samples, int sampleRate)
    {
        if (samples == null || samples.Length == 0) return;

        var highPass = NAudio.Dsp.BiQuadFilter.HighPassFilter(sampleRate, MicrophoneHighPassHz, 0.7071f);
        for (var i = Math.Max(0, samples.Length - sampleRate / 10); i < samples.Length; i++)
            highPass.Transform(samples[i]);

        for (var i = 0; i < samples.Length; i++) samples[i] = highPass.Transform(samples[i]);
    }

    /// <summary>
    ///     Linear gain for a background sound with the given loudness (<paramref name="radioBandRmsDbfs" /> from
    ///     <see cref="RadioBandRms" />, <paramref name="rmsDbfs" /> full band) at the sender's <paramref name="volume" />
    ///     (0..1, clamped). 0 for volume 0, invalid values or a silent sound.
    /// </summary>
    public static float Gain(double radioBandRmsDbfs, double rmsDbfs, float volume)
    {
        if (!float.IsFinite(volume) || volume <= 0f) return 0f;
        if (!double.IsFinite(radioBandRmsDbfs) || !double.IsFinite(rmsDbfs)) return 0f;

        var target = FullVolumeRadioBandDbfs + 20 * Math.Log10(Math.Min(volume, 1f));
        var gainDb = Math.Min(target - radioBandRmsDbfs, target + MaxFullBandAboveDb - rmsDbfs);

        return (float)VolumeConversionHelper.DecibelsToLinear(Math.Min(gainDb, MaxGainDb));
    }

    /// <summary>RMS in dBFS of <paramref name="samples" /> in the radio band (2nd order high-pass and low-pass).</summary>
    public static double RadioBandRms(float[] samples, int sampleRate)
    {
        if (samples == null || samples.Length == 0) return double.NegativeInfinity;

        var highPass = NAudio.Dsp.BiQuadFilter.HighPassFilter(sampleRate, RadioBandLowHz, 0.7071f);
        var lowPass = NAudio.Dsp.BiQuadFilter.LowPassFilter(sampleRate, RadioBandHighHz, 0.7071f);

        double sum = 0;
        foreach (var sample in samples)
        {
            double filtered = lowPass.Transform(highPass.Transform(sample));
            sum += filtered * filtered;
        }

        return sum > 0 ? 10 * Math.Log10(sum / samples.Length) : double.NegativeInfinity;
    }
}
