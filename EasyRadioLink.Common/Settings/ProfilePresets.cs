using System;
using System.Collections.Generic;
using SharpConfig;

namespace EasyRadioLink.Common.Settings;

/// <summary>A ready-made profile: its name and the settings that differ from the defaults.</summary>
public sealed record ProfilePreset(string Name, IReadOnlyDictionary<ProfileSettingsKeys, string> Settings);

/// <summary>
///     The ready-made profiles every user gets once (<see cref="ProfileSettingsStore" /> creates each of them the first
///     time it runs with this version - a preset the user deleted or renamed does not come back, and an existing
///     profile with the same name is left alone).
/// </summary>
public static class ProfilePresets
{
    /// <summary>
    ///     Talking from a helicopter: rotor noise behind the voice, the voice boosted to stand out from it, a light radio
    ///     sound, a little distance.
    /// </summary>
    public static readonly ProfilePreset Helicopter = new("Helicopter",
        new Dictionary<ProfileSettingsKeys, string>
        {
            { ProfileSettingsKeys.BackgroundSound, "helicopter" },
            { ProfileSettingsKeys.BackgroundSoundVolume, "0.4" },
            { ProfileSettingsKeys.VoiceBoost, "0.4" },
            { ProfileSettingsKeys.RadioEffectsRatio, "0.2" },
            { ProfileSettingsKeys.VoiceDistortion, "15" }
        });

    public static readonly IReadOnlyList<ProfilePreset> All = new[] { Helicopter };

    /// <summary>
    ///     The profile file of <paramref name="preset" />: the key bindings of <paramref name="bindingsFrom" /> (so
    ///     push-to-talk works at once, may be null) and the preset's settings; everything else starts with the defaults.
    /// </summary>
    internal static Configuration CreateProfile(ProfilePreset preset, Configuration bindingsFrom)
    {
        var configuration = new Configuration();

        var settings = new Section(ProfileSettingsStore.ClientSettingsSection);
        foreach (var (key, value) in preset.Settings) settings.Add(new SharpConfig.Setting(key.ToString()) { RawValue = value });
        configuration.Add(settings);

        if (bindingsFrom != null)
            foreach (var binding in Enum.GetNames<InputBinding>())
            {
                if (!bindingsFrom.Contains(binding)) continue;

                var section = new Section(binding);
                foreach (var setting in bindingsFrom[binding])
                    section.Add(new SharpConfig.Setting(setting.Name) { RawValue = setting.RawValue });
                configuration.Add(section);
            }

        return configuration;
    }
}
