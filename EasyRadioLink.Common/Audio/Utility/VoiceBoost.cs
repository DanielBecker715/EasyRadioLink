using System;

namespace EasyRadioLink.Common.Audio.Utility;

/// <summary>
///     "Boost my voice": the sender makes their voice louder for everybody who hears them, so it stands out more from
///     their background sound and the static.
///     <para>
///         Every listener normalises the received voices (receive AGC), so a louder microphone alone would change
///         nothing. The boost (0..1) is therefore sent with the radio information (<c>ambient.voiceBoost</c>) and applied
///         by the listener to the received voice after the AGC and before the background sound and the radio effects:
///         0 = normal, 1 = +<see cref="MaxBoostDb" /> dB. Peaks above <see cref="LimiterKnee" /> are rounded off
///         smoothly towards <see cref="LimiterCeiling" /> (a "hot" microphone, no hard clipping, no clicks).
///     </para>
/// </summary>
public static class VoiceBoost
{
    /// <summary>Gain at 100 %.</summary>
    public const double MaxBoostDb = 10;

    /// <summary>Level above which the boosted voice is rounded off.</summary>
    public const float LimiterKnee = 0.5f;

    /// <summary>The boosted voice never exceeds this level.</summary>
    public const float LimiterCeiling = 0.95f;

    /// <summary>Linear gain for <paramref name="boost" /> (0..1, clamped; invalid = 0 = 1.0).</summary>
    public static float GainFor(float boost)
    {
        if (!float.IsFinite(boost) || boost <= 0f) return 1f;

        return (float)Math.Pow(10, MaxBoostDb * Math.Min(boost, 1f) / 20);
    }

    /// <summary>Boosts <paramref name="audio" /> in place; nothing changes at 0.</summary>
    public static void Apply(Span<float> audio, float boost)
    {
        var gain = GainFor(boost);
        if (gain == 1f) return;

        const float range = LimiterCeiling - LimiterKnee;
        for (var i = 0; i < audio.Length; i++)
        {
            var x = audio[i] * gain;
            var magnitude = Math.Abs(x);
            if (magnitude > LimiterKnee)
            {
                // tanh knee: same slope (1) at the knee, approaches the ceiling
                var limited = LimiterKnee + range * MathF.Tanh((magnitude - LimiterKnee) / range);
                x = x < 0 ? -limited : limited;
            }

            audio[i] = x;
        }
    }
}
