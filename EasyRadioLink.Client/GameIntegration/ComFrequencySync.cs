using System;

namespace EasyRadioLink.Client.GameIntegration;

/// <summary>What <see cref="ComFrequencySync.Update" /> asks the caller to do.</summary>
public enum ComSyncDirection
{
    None,

    /// <summary>Tune the app's radio to <see cref="ComSyncAction.FrequencyHz" /> (the cockpit radio was changed).</summary>
    SimToApp,

    /// <summary>Tune the cockpit radio to <see cref="ComSyncAction.FrequencyHz" /> (the app's radio was changed).</summary>
    AppToSim
}

public readonly record struct ComSyncAction(ComSyncDirection Direction, double FrequencyHz)
{
    public static readonly ComSyncAction None = new(ComSyncDirection.None, 0);
}

/// <summary>
///     Keeps a cockpit COM radio and the app's radio on the same frequency, in both directions. Pure logic: the caller
///     feeds the current frequencies of both radios (<see cref="Update" />) and carries out the returned action.
///     <list type="bullet">
///         <item>
///             First contact (or after <see cref="Reset" />): the app's frequency is sent to the cockpit if it is a COM
///             frequency (<see cref="IsComFrequency" />), so a frequency agreed on in the app survives starting the sim;
///             otherwise (e.g. the default CB frequency) the app follows the cockpit.
///         </item>
///         <item>
///             Afterwards whichever radio changed is copied to the other. The app wins if both changed at once. An app
///             frequency outside the COM range (CB, PMR, ...) is not sent - the cockpit radio keeps its frequency.
///         </item>
///         <item>
///             After a frequency is sent to the cockpit the cockpit is not read for <see cref="EchoHoldOff" />: the sim
///             reports the old frequency for a moment, which must not tune the app back. Afterwards the cockpit wins -
///             a frequency the sim rounded to its channel spacing (or refused) is copied back to the app, so both show
///             the same.
///         </item>
///     </list>
///     Frequencies closer than <see cref="Tolerance" /> count as equal (the receive tolerance of the radio).
/// </summary>
public sealed class ComFrequencySync
{
    /// <summary>First COM frequency of the cockpit radios: 118.000 MHz.</summary>
    public const double MinComFrequency = 118_000_000;

    /// <summary>Last COM frequency of the cockpit radios (8.33 kHz channel 136.990 MHz).</summary>
    public const double MaxComFrequency = 136_990_000;

    /// <summary>Frequencies closer than this (Hz) are the same.</summary>
    public const double Tolerance = 500;

    public static readonly TimeSpan EchoHoldOff = TimeSpan.FromSeconds(1);

    private double _lastApp = double.NaN;
    private double _lastSim = double.NaN;
    private DateTime _holdUntil = DateTime.MinValue;

    /// <summary>True if the cockpit COM radio can be tuned to <paramref name="frequencyHz" />.</summary>
    public static bool IsComFrequency(double frequencyHz)
    {
        return double.IsFinite(frequencyHz)
               && frequencyHz >= MinComFrequency - Tolerance
               && frequencyHz <= MaxComFrequency + Tolerance;
    }

    /// <summary>Forget both radios: the next <see cref="Update" /> is a first contact again.</summary>
    public void Reset()
    {
        _lastApp = double.NaN;
        _lastSim = double.NaN;
        _holdUntil = DateTime.MinValue;
    }

    /// <param name="simHz">Current frequency of the cockpit radio.</param>
    /// <param name="appHz">Current frequency of the app's radio.</param>
    /// <param name="now">Current time (for <see cref="EchoHoldOff" />).</param>
    /// <returns>
    ///     The action to carry out. The sync assumes it succeeds; call <see cref="Reset" /> if it did not (e.g. the app's
    ///     radio is not available).
    /// </returns>
    public ComSyncAction Update(double simHz, double appHz, DateTime now)
    {
        if (!double.IsFinite(simHz) || !double.IsFinite(appHz)) return ComSyncAction.None;

        if (double.IsNaN(_lastApp) || double.IsNaN(_lastSim))
        {
            _lastApp = appHz;
            _lastSim = simHz;

            if (Same(simHz, appHz)) return ComSyncAction.None;

            return IsComFrequency(appHz) ? SendToSim(appHz, now) : CopyToApp(simHz);
        }

        if (!Same(appHz, _lastApp))
        {
            _lastApp = appHz;

            // e.g. CB: the cockpit radio can't go there and keeps its frequency
            return IsComFrequency(appHz) ? SendToSim(appHz, now) : ComSyncAction.None;
        }

        // the sim still reports the frequency from before our change
        if (now < _holdUntil) return ComSyncAction.None;

        if (Same(simHz, _lastSim)) return ComSyncAction.None;

        _lastSim = simHz;

        return Same(simHz, appHz) ? ComSyncAction.None : CopyToApp(simHz);
    }

    private ComSyncAction SendToSim(double appHz, DateTime now)
    {
        // what the cockpit should report once it has the new frequency
        _lastSim = appHz;
        _holdUntil = now + EchoHoldOff;
        return new ComSyncAction(ComSyncDirection.AppToSim, appHz);
    }

    private ComSyncAction CopyToApp(double simHz)
    {
        _lastApp = simHz;
        return new ComSyncAction(ComSyncDirection.SimToApp, simHz);
    }

    private static bool Same(double a, double b)
    {
        return Math.Abs(a - b) < Tolerance;
    }
}
