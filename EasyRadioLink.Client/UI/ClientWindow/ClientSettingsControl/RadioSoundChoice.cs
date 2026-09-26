using System;
using System.Collections.Generic;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;

namespace EasyRadioLink.Client.UI.ClientWindow.ClientSettingsControl;

/// <summary>An entry of a radio sound drop-down: "Off" or one of the sounds.</summary>
/// <param name="Value">file name of the sound, or <see cref="RadioSoundChoice.Off" /></param>
/// <param name="Name">text shown in the drop-down</param>
public sealed record RadioSoundOption(string Value, string Name)
{
    // what screen readers and the combo box's text search see
    public override string ToString() => Name;
}

/// <summary>
///     Maps a radio sound drop-down ("Off", "Click", "Roger beep", ...) to the two profile settings behind it: the
///     on/off key (e.g. RadioTxEffects_Start) and the selection key with the sound's file name (e.g.
///     RadioTransmissionStartSelection). "Off" only switches the sound off and keeps the stored file name.
/// </summary>
public static class RadioSoundChoice
{
    /// <summary>The value of the "Off" entry.</summary>
    public const string Off = "";

    /// <summary>"Off" followed by the sounds, in list order.</summary>
    public static IReadOnlyList<RadioSoundOption> BuildOptions(IEnumerable<CachedAudioEffect> effects, string offText)
    {
        var options = new List<RadioSoundOption> { new(Off, offText) };

        foreach (var effect in effects ?? Array.Empty<CachedAudioEffect>())
            options.Add(new RadioSoundOption(effect.FileName, effect.DisplayName));

        return options;
    }

    /// <summary>
    ///     The entry to show: <see cref="Off" /> if the sound is switched off, else the file name of the sound that
    ///     plays (the first sound if the stored file is not available any more).
    /// </summary>
    public static string FromSettings(bool enabled, string selectedFileName, IReadOnlyList<CachedAudioEffect> effects)
    {
        if (!enabled) return Off;

        return CachedAudioEffectProvider.FindEffect(effects, selectedFileName)?.FileName ?? Off;
    }

    /// <summary>
    ///     What choosing <paramref name="choice" /> writes: <paramref name="enabled" /> (false for "Off") and the file
    ///     name to store (null = keep the stored one). False if the choice is not one of the sounds (nothing to write).
    /// </summary>
    public static bool TryApply(string choice, IReadOnlyList<CachedAudioEffect> effects, out bool enabled,
        out string fileName)
    {
        enabled = false;
        fileName = null;

        if (string.IsNullOrEmpty(choice)) return true; // "Off"

        if (effects != null)
            foreach (var effect in effects)
                if (string.Equals(effect.FileName, choice, StringComparison.OrdinalIgnoreCase))
                {
                    enabled = true;
                    fileName = effect.FileName;
                    return true;
                }

        return false;
    }
}
