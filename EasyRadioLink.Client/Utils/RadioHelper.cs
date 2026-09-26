using System;
using System.Threading;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings;

namespace EasyRadioLink.Client.Utils;

/// <summary>
///     All user actions on the radio (radio window, hotkeys). Every method is a no-op while
///     <see cref="RadiosAvailable" /> is false. A frequency change always tunes to a <see cref="BandPlan.Normalise" />d
///     frequency, applies the <see cref="BandPlan" /> (modulation + radio model follow the frequency) and marks the state
///     dirty (<c>ClientStateSingleton.LastSent = 0</c>); <see cref="RadioStateSyncService" /> sends it with its next tick,
///     so fast tuning is coalesced to at most one RADIO_UPDATE per sync interval.
///     <para>
///         A published <see cref="Radio" /> is never modified: every change works on a copy that replaces the radio in
///         <c>PlayerRadioInfo.radios</c> with one reference assignment (<see cref="Publish" />). The audio threads, the
///         sync loop and the radio window read the radio without locking and always see either the old or the new
///         radio - never a new frequency with the old modulation. Changes from the UI thread and the hotkey thread are
///         serialised by <see cref="ChangeLock" />.
///     </para>
/// </summary>
public static class RadioHelper
{
    // volume change of the RadioVolumeUp / RadioVolumeDown hotkeys
    private const float VolumeStep = 0.1f;

    private static readonly object ChangeLock = new();

    /// <summary>
    ///     THE availability predicate: connected and the radio is loaded for this connection.
    /// </summary>
    public static bool RadiosAvailable()
    {
        var clientState = ClientStateSingleton.Instance;
        return clientState.IsConnected && clientState.PlayerRadioInfo.IsActive;
    }

    /// <summary>
    ///     The radio, or null if it is not available (not connected / not loaded yet). A snapshot: every change replaces
    ///     the radio, so call it again for the current state and never modify the returned radio.
    /// </summary>
    public static Radio GetRadio()
    {
        if (!RadiosAvailable()) return null;

        var radio = ClientStateSingleton.Instance.PlayerRadioInfo.Radio;
        return radio != null && radio.IsEnabled ? radio : null;
    }

    private static void MarkDirty()
    {
        //make radio data stale to force resync
        ClientStateSingleton.Instance.LastSent = 0;
    }

    /// <summary>
    ///     Tunes the radio to <paramref name="frequencyHz" /> (clamped to the tuning range and normalised, see
    ///     <see cref="BandPlan.Normalise" />) and applies the band plan.
    /// </summary>
    /// <returns>false if the radio is not available or the frequency had to be clamped</returns>
    public static bool SetFrequency(double frequencyHz)
    {
        if (double.IsNaN(frequencyHz)) return false;

        var clamped = BandPlan.Clamp(frequencyHz);

        return Tune(_ => clamped) && Math.Abs(clamped - Math.Round(frequencyHz)) < 0.5;
    }

    /// <summary>
    ///     Changes the frequency by <paramref name="stepHz" /> (negative = down). With the profile setting
    ///     <see cref="ProfileSettingsKeys.RotaryStyleIncrement" /> only the digit of the step changes and rolls over
    ///     from 9 to 0 (and 0 to 9) instead of carrying into the next digit.
    /// </summary>
    /// <returns>false if the radio is not available or the frequency had to be clamped</returns>
    public static bool StepFrequency(double stepHz)
    {
        if (!double.IsFinite(stepHz) || stepHz == 0) return false;

        var rotary = GlobalSettingsStore.Instance.ProfileSettingsStore
            .GetClientSettingBool(ProfileSettingsKeys.RotaryStyleIncrement);

        var inRange = false;
        var tuned = Tune(radio =>
        {
            // from the current frequency - read under the lock, so fast steps from two threads all count
            var target = rotary ? RotaryStep(radio.freq, stepHz) : radio.freq + stepHz;
            var clamped = BandPlan.Clamp(target);
            inRange = Math.Abs(clamped - Math.Round(target)) < 0.5;
            return clamped;
        });

        return tuned && inRange;
    }

    /// <summary>
    ///     Frequency change used by the hotkeys: <paramref name="requestedValue" /> is a step (<paramref name="delta" />)
    ///     or an absolute frequency, in MHz (<paramref name="inMHz" />) or Hz.
    /// </summary>
    /// <returns>false if the radio is not available or the frequency had to be clamped</returns>
    public static bool UpdateRadioFrequency(double requestedValue, bool delta = true, bool inMHz = true)
    {
        if (!double.IsFinite(requestedValue)) return false;

        var value = inMHz ? requestedValue * RadioCalculator.MHz : requestedValue;

        return delta ? StepFrequency(Math.Round(value)) : SetFrequency(value);
    }

    /// <summary>
    ///     Rotary style: adds <paramref name="stepHz" /> to the digit it belongs to without carrying, e.g. with 1 kHz
    ///     steps 27.189 -> 27.180 (up) and 27.180 -> 27.189 (down).
    /// </summary>
    internal static double RotaryStep(double frequencyHz, double stepHz)
    {
        var step = Math.Abs(stepHz);
        var digit = (long)Math.Floor(frequencyHz / step + 1e-9) % 10;

        if (stepHz > 0)
            return digit == 9 ? frequencyHz - 9 * step : frequencyHz + step;

        return digit == 0 ? frequencyHz + 9 * step : frequencyHz - step;
    }

    /// <summary>
    ///     Tunes the radio to the <see cref="BandPlan.Normalise" />d result of <paramref name="frequency" /> (called with
    ///     the current radio, under <see cref="ChangeLock" />) with the modulation and model of its band.
    /// </summary>
    /// <returns>false if the radio is not available</returns>
    private static bool Tune(Func<Radio, double> frequency)
    {
        lock (ChangeLock)
        {
            var radio = GetRadio();
            if (radio == null) return false;

            var frequencyHz = BandPlan.Normalise(frequency(radio));
            var band = BandPlan.GetBand(frequencyHz);

            // unchanged (e.g. at the end of the range) - nothing to send
            if (Math.Abs(radio.freq - frequencyHz) < 0.5 && radio.modulation == band.Modulation &&
                string.Equals(radio.model, band.Model, StringComparison.Ordinal))
                return true;

            var tuned = radio.DeepClone();
            tuned.freq = frequencyHz;
            tuned.modulation = band.Modulation;
            tuned.model = band.Model;

            if (!Publish(radio, tuned)) return false;

            MarkDirty();
            return true;
        }
    }

    /// <summary>
    ///     Replaces <paramref name="current" /> by <paramref name="changed" /> with one atomic reference assignment. Fails
    ///     if the radio was replaced in the meantime (switched off / on again by <see cref="RadioStateSyncService" />) -
    ///     the change must not bring the old radio back.
    /// </summary>
    private static bool Publish(Radio current, Radio changed)
    {
        var radios = ClientStateSingleton.Instance.PlayerRadioInfo.radios;

        return ReferenceEquals(
            Interlocked.CompareExchange(ref radios[PlayerRadioInfo.RadioId], changed, current), current);
    }

    /// <summary>Local receive volume 0..1 (not sent to the server).</summary>
    public static void SetRadioVolume(float volume)
    {
        ChangeVolume(_ => volume);
    }

    public static void RadioVolumeUp()
    {
        ChangeVolume(volume => (float)Math.Round(volume + VolumeStep, 2));
    }

    public static void RadioVolumeDown()
    {
        ChangeVolume(volume => (float)Math.Round(volume - VolumeStep, 2));
    }

    /// <summary>Sets the volume to <paramref name="volume" /> (called with the current volume), clamped to 0..1.</summary>
    private static void ChangeVolume(Func<float, float> volume)
    {
        lock (ChangeLock)
        {
            var radio = GetRadio();
            if (radio == null) return;

            var newVolume = volume(radio.volume);
            if (!float.IsFinite(newVolume)) return;

            newVolume = Math.Clamp(newVolume, 0f, 1f);
            if (newVolume == radio.volume) return;

            var changed = radio.DeepClone();
            changed.volume = newVolume;

            Publish(radio, changed);
        }
    }
}
