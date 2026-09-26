using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Settings.Input;
using NLog;
using SharpConfig;

namespace EasyRadioLink.Common.Settings;

/// <summary>
///     Settings of a profile (&lt;profile&gt;.cfg), persisted by NAME - renaming a member orphans the saved value.
/// </summary>
public enum ProfileSettingsKeys
{
    // Stereo balance of the radio (-1 left .. +1 right). The name is kept from the multi-radio versions so the saved
    // value survives the update.
    Radio1Channel,

    RadioEffectsRatio,
    RadioEffectsClipping,
    NATOTone, // FM tone (user-visible label "FM tone")

    // Radio sounds on / off: receive start / end ("When someone starts / stops talking") and transmit start / end
    // ("When I press / release push-to-talk"). The sound itself is the matching *Selection key.
    RadioRxEffects_Start,
    RadioRxEffects_End,
    RadioTxEffects_Start,
    RadioTxEffects_End,

    PTTReleaseDelay,

    // File names of the transmit start / end sounds (RADIO_TRANS_START*.wav / RADIO_TRANS_END*.wav).
    RadioTransmissionStartSelection,
    RadioTransmissionEndSelection,
    RadioBackgroundNoiseEffect,
    NATOToneVolume, // FM tone volume
    NoiseGainDB,
    HFNoiseGainDB,

    // Listener: transmissions sound like the sender's radio model (the model of the band, see BandPlan);
    // off = every transmission uses the standard sound.
    PerRadioModelEffects,

    PTTStartDelay,

    // Frequency steps (buttons, tuning knob, hotkeys) change only their digit and roll over from 9 to 0.
    RotaryStyleIncrement,

    // Short fading noise burst when a received AM/FM transmission ends (not for DIGITAL or clean frequencies).
    // Off by default since 1.3 (profiles that have the setting keep their value).
    RadioRxSquelchTail,

    // Sender: background sound mixed into your own transmissions ("" = none, or a name from
    // CachedAudioEffectProvider.AvailableBackgroundSounds, e.g. "jet", "prop", "helicopter") and its volume (0..1).
    // Written into PlayerRadioInfo.ambient (abType / vol) so everybody who hears you hears it.
    BackgroundSound,
    BackgroundSoundVolume,

    // Listener: play the background sounds of other users, and their relative volume.
    BackgroundSoundEffect,
    BackgroundSoundEffectVolume,

    // File names of the receive start / end sounds (RADIO_TRANS_START*.wav / RADIO_TRANS_END*.wav). Until 1.1 the
    // receive sounds were the transmit sounds: a profile without these keys starts with its transmit selections
    // (see ProfileSettingsStore.GetDefaultValue).
    RadioRxStartSelection,
    RadioRxEndSelection,

    // Listener: how much received voices are degraded (band narrowing, overdrive, lo-fi, fading, crackle / dropouts),
    // 0..100 percent. See VoiceDistortionProvider.
    VoiceDistortion
}

public class ProfileSettingsStore
{
    private static readonly object _lock = new();

    public static readonly Dictionary<string, string> DefaultSettingsProfileSettings = new()
    {
        { ProfileSettingsKeys.Radio1Channel.ToString(), "0" },

        { ProfileSettingsKeys.RadioEffectsRatio.ToString(), "1.0" },
        { ProfileSettingsKeys.RadioEffectsClipping.ToString(), "false" },

        { ProfileSettingsKeys.NATOTone.ToString(), "true" },

        { ProfileSettingsKeys.RadioRxEffects_Start.ToString(), "true" },
        { ProfileSettingsKeys.RadioRxEffects_End.ToString(), "true" },

        {
            ProfileSettingsKeys.RadioTransmissionStartSelection.ToString(),
            CachedAudioEffect.FancyReleaseFile
        },
        {
            ProfileSettingsKeys.RadioTransmissionEndSelection.ToString(),
            CachedAudioEffect.AlmostFancyFile
        },

        { ProfileSettingsKeys.RadioTxEffects_Start.ToString(), "true" },
        { ProfileSettingsKeys.RadioTxEffects_End.ToString(), "true" },

        { ProfileSettingsKeys.PTTReleaseDelay.ToString(), "0" },
        { ProfileSettingsKeys.PTTStartDelay.ToString(), "0" },

        { ProfileSettingsKeys.RadioBackgroundNoiseEffect.ToString(), "true" },

        { ProfileSettingsKeys.NATOToneVolume.ToString(), "1.2" },

        { ProfileSettingsKeys.NoiseGainDB.ToString(), "0" },
        { ProfileSettingsKeys.HFNoiseGainDB.ToString(), "0" },
        { ProfileSettingsKeys.PerRadioModelEffects.ToString(), "true" },

        { ProfileSettingsKeys.RotaryStyleIncrement.ToString(), "false" },

        { ProfileSettingsKeys.RadioRxSquelchTail.ToString(), "false" },

        { ProfileSettingsKeys.BackgroundSound.ToString(), "" }, // none
        { ProfileSettingsKeys.BackgroundSoundVolume.ToString(), "0.25" },
        { ProfileSettingsKeys.BackgroundSoundEffect.ToString(), "true" },
        {
            ProfileSettingsKeys.BackgroundSoundEffectVolume.ToString(), "1.0"
        }, //relative volume as the incoming volume is variable

        {
            ProfileSettingsKeys.RadioRxStartSelection.ToString(),
            CachedAudioEffect.FancyReleaseFile
        },
        {
            ProfileSettingsKeys.RadioRxEndSelection.ToString(),
            CachedAudioEffect.AlmostFancyFile
        },

        { ProfileSettingsKeys.VoiceDistortion.ToString(), "35" } // percent
    };

    /// <summary>
    ///     Settings added later whose first value is the value of an older setting of the same profile (if the profile
    ///     has it), so that an update keeps the behaviour: until 1.1 the receive sounds were the transmit sounds.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> InheritedDefaults = new Dictionary<string, string>
    {
        {
            ProfileSettingsKeys.RadioRxStartSelection.ToString(),
            ProfileSettingsKeys.RadioTransmissionStartSelection.ToString()
        },
        {
            ProfileSettingsKeys.RadioRxEndSelection.ToString(),
            ProfileSettingsKeys.RadioTransmissionEndSelection.ToString()
        }
    };

    private readonly GlobalSettingsStore _globalSettings;

    //cache all the settings in their correct types for speed
    //fixes issue where we access settings a lot and have issues
    private readonly ConcurrentDictionary<string, object> _settingsCache = new();

    private readonly Dictionary<string, Configuration> InputConfigs = new();
    private readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private string _currentProfileName = "default";

    public ProfileSettingsStore(GlobalSettingsStore globalSettingsStore)
    {
        _globalSettings = globalSettingsStore;
        Path = GlobalSettingsStore.Path;

        var profiles = GetProfiles();
        foreach (var profile in profiles)
        {
            Configuration _configuration = null;
            try
            {
                var count = 0;
                while (GlobalSettingsStore.IsFileLocked(new FileInfo(Path + GetProfileCfgFileName(profile))) &&
                       count < 10)
                {
                    Thread.Sleep(200);
                    count++;
                }

                _configuration = Configuration.LoadFromFile(Path + GetProfileCfgFileName(profile));
                InputConfigs[GetProfileCfgFileName(profile)] = _configuration;

                var inputProfile = new Dictionary<InputBinding, InputDevice>();
                InputProfiles[GetProfileName(profile)] = inputProfile;

                foreach (InputBinding bind in Enum.GetValues(typeof(InputBinding)))
                {
                    var device = GetControlSetting(bind, _configuration);

                    if (device != null) inputProfile[bind] = device;
                }

                SaveProfileFile(_configuration, profile);
            }
            catch (FileNotFoundException)
            {
                Logger.Info(
                    $"Did not find input config file at path {profile}, initialising with default config");
            }
            catch (ParserException)
            {
                Logger.Info(
                    "Error with input config - creating a new default ");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // e.g. a -cfg directory that can't be read - continue with a default profile in memory
                Logger.Error(ex, $"Unable to read the profile {profile}, using the defaults");
            }

            if (_configuration == null)
            {
                _configuration = new Configuration();
                var inputProfile = new Dictionary<InputBinding, InputDevice>();
                InputProfiles[GetProfileName(profile)] = inputProfile;
                InputConfigs[GetProfileCfgFileName(profile)] = _configuration;
                SaveProfileFile(_configuration, profile);
            }
        }

        //add default
        if (!InputProfiles.ContainsKey(GetProfileName("default")))
        {
            InputConfigs[GetProfileCfgFileName("default")] = new Configuration();

            var inputProfile = new Dictionary<InputBinding, InputDevice>();
            InputProfiles[GetProfileName("default")] = inputProfile;

            SaveProfileFile(InputConfigs[GetProfileCfgFileName("default")], "default");
        }
    }

    public string CurrentProfileName
    {
        get => _currentProfileName;
        set
        {
            _settingsCache.Clear();
            _currentProfileName = value;
        }
    }

    public string Path { get; }


    public List<string> ProfileNames => new(InputProfiles.Keys);
    public Dictionary<string, Dictionary<InputBinding, InputDevice>> InputProfiles { get; set; } = new();

    public Dictionary<InputBinding, InputDevice> GetCurrentInputProfile()
    {
        return InputProfiles[GetProfileName(CurrentProfileName)];
    }

    public Configuration GetCurrentProfile()
    {
        return InputConfigs[GetProfileCfgFileName(CurrentProfileName)];
    }

    public List<string> GetProfiles()
    {
        var profiles = _globalSettings.GetClientSetting(GlobalSettingsKeys.SettingsProfiles).StringValueArray;

        if (profiles == null || profiles.Length == 0 || !profiles.Contains("default"))
        {
            profiles = new[] { "default" };
            _globalSettings.SetClientSetting(GlobalSettingsKeys.SettingsProfiles, profiles);
        }

        return new List<string>(profiles);
    }

    public void AddNewProfile(string profileName)
    {
        var profiles = InputProfiles.Keys.ToList();
        profiles.Add(profileName);

        _globalSettings.SetClientSetting(GlobalSettingsKeys.SettingsProfiles, profiles.ToArray());

        InputConfigs[GetProfileCfgFileName(profileName)] = new Configuration();

        var inputProfile = new Dictionary<InputBinding, InputDevice>();
        InputProfiles[GetProfileName(profileName)] = inputProfile;
    }

    private string GetProfileCfgFileName(string prof)
    {
        if (prof.Contains(".cfg")) return prof;

        return prof + ".cfg";
    }

    private string GetProfileName(string cfg)
    {
        if (cfg.Contains(".cfg")) return cfg.Replace(".cfg", "");

        return cfg;
    }

    public InputDevice GetControlSetting(InputBinding key, Configuration configuration)
    {
        if (!configuration.Contains(key.ToString())) return null;

        try
        {
            var device = new InputDevice();
            device.DeviceName = configuration[key.ToString()]["name"].StringValue;

            device.Button = configuration[key.ToString()]["button"].IntValue;
            device.InstanceGuid =
                Guid.Parse(configuration[key.ToString()]["guid"].RawValue);
            device.InputBind = key;

            device.ButtonValue = configuration[key.ToString()]["value"].IntValue;

            return device;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Error reading input device saved settings ");
        }


        return null;
    }

    public void SetControlSetting(InputDevice device)
    {
        RemoveControlSetting(device.InputBind);

        var configuration = GetCurrentProfile();

        configuration.Add(new Section(device.InputBind.ToString()));

        //create the sections
        var section = configuration[device.InputBind.ToString()];

        section.Add(new SharpConfig.Setting("name", device.DeviceName.Replace("\0", "")));
        section.Add(new SharpConfig.Setting("button", device.Button));
        section.Add(new SharpConfig.Setting("value", device.ButtonValue));
        section.Add(new SharpConfig.Setting("guid", device.InstanceGuid.ToString()));

        var inputDevices = GetCurrentInputProfile();

        inputDevices[device.InputBind] = device;

        Save();
    }

    public void RemoveControlSetting(InputBinding binding)
    {
        var configuration = GetCurrentProfile();

        if (configuration.Contains(binding.ToString())) configuration.Remove(binding.ToString());

        var inputDevices = GetCurrentInputProfile();
        inputDevices.Remove(binding);

        Save();
    }

    private SharpConfig.Setting GetSetting(string section, string setting)
    {
        var _configuration = GetCurrentProfile();


        if (!_configuration.Contains(section)) _configuration.Add(section);

        if (!_configuration[section].Contains(setting))
        {
            var defaultValue = GetDefaultValue(_configuration[section], setting);
            if (defaultValue == null)
            {
                Logger.Warn("Setting {0} not found in default settings, creating with empty value", setting);
            }

            //save
            _configuration[section]
                .Add(new SharpConfig.Setting(setting, defaultValue));

            Save();
        }

        return _configuration[section][setting];
    }

    /// <summary>
    ///     The value a setting that is missing in <paramref name="section" /> starts with: the value of the older
    ///     setting it inherits from (<see cref="InheritedDefaults" />) if the section has it, else the default of
    ///     <see cref="DefaultSettingsProfileSettings" />; null for an unknown setting.
    /// </summary>
    internal static string GetDefaultValue(Section section, string setting)
    {
        if (section != null && InheritedDefaults.TryGetValue(setting, out var source) && section.Contains(source))
            return section[source].RawValue;

        return DefaultSettingsProfileSettings.TryGetValue(setting, out var defaultValue) ? defaultValue : null;
    }

    public bool GetClientSettingBool(ProfileSettingsKeys key)
    {
        if (_settingsCache.TryGetValue(key.ToString(), out var val)) return (bool)val;

        var setting = GetSetting("Client Settings", key.ToString()).GetValueOrDefault(false);
        _settingsCache[key.ToString()] = setting;

        return setting;
    }

    public float GetClientSettingFloat(ProfileSettingsKeys key)
    {
        if (_settingsCache.TryGetValue(key.ToString(), out var val))
        {
            if (val == null) return 0f;
            return (float)val;
        }

        var setting = GetSetting("Client Settings", key.ToString()).GetValueOrDefault(0f);
        _settingsCache[key.ToString()] = setting;
        

        return setting;
    }

    public string GetClientSettingString(ProfileSettingsKeys key)
    {
        if (_settingsCache.TryGetValue(key.ToString(), out var val)) return (string)val;

        var setting = GetSetting("Client Settings", key.ToString()).RawValue;

        _settingsCache[key.ToString()] = setting;

        return setting;
    }


    public void SetClientSettingBool(ProfileSettingsKeys key, bool value)
    {
        SetSetting("Client Settings", key.ToString(), value);

        _settingsCache.TryRemove(key.ToString(), out var res);
    }

    public void SetClientSettingFloat(ProfileSettingsKeys key, float value)
    {
        SetSetting("Client Settings", key.ToString(), value);

        _settingsCache.TryRemove(key.ToString(), out var res);
    }

    public void SetClientSettingString(ProfileSettingsKeys key, string value)
    {
        SetSetting("Client Settings", key.ToString(), value);
        _settingsCache.TryRemove(key.ToString(), out var res);
    }

    private void SetSetting(string section, string key, object setting)
    {
        var _configuration = GetCurrentProfile();

        if (setting == null) setting = "";
        if (!_configuration.Contains(section)) _configuration.Add(section);

        if (!_configuration[section].Contains(key))
        {
            _configuration[section].Add(new SharpConfig.Setting(key, setting));
        }
        else
        {
            if (setting is bool)
                _configuration[section][key].BoolValue = (bool)setting;
            else if (setting is float)
                _configuration[section][key].FloatValue = (float)setting;
            else if (setting is double)
                _configuration[section][key].DoubleValue = (double)setting;
            else if (setting is int)
                _configuration[section][key].DoubleValue = (int)setting;
            else if (setting.GetType() == typeof(string))
                _configuration[section][key].StringValue = setting as string;
            else if (setting is string[])
                _configuration[section][key].StringValueArray = setting as string[];
            else
                Logger.Error("Unknown Setting Type - Not Saved ");
        }

        Save();
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var configuration = GetCurrentProfile();
                configuration.SaveToFile(Path + GetProfileCfgFileName(CurrentProfileName), new UTF8Encoding(false, true));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unable to save settings!");
            }
        }
    }

    public void RemoveProfile(string profile)
    {
        InputConfigs.Remove(GetProfileCfgFileName(profile));
        InputProfiles.Remove(GetProfileName(profile));

        var profiles = InputProfiles.Keys.ToList();
        _globalSettings.SetClientSetting(GlobalSettingsKeys.SettingsProfiles, profiles.ToArray());

        try
        {
            File.Delete(Path + GetProfileCfgFileName(profile));
        }
        catch
        {
        }

        CurrentProfileName = "default";
    }

    /// <summary>Renames a profile and makes it the current profile.</summary>
    public void RenameProfile(string oldName, string newName)
    {
        // renaming to the same name would remove the profile
        if (string.Equals(GetProfileName(oldName), GetProfileName(newName), StringComparison.Ordinal)) return;

        InputConfigs[GetProfileCfgFileName(newName)] = InputConfigs[GetProfileCfgFileName(oldName)];
        InputProfiles[GetProfileName(newName)] = InputProfiles[GetProfileName(oldName)];

        InputConfigs.Remove(GetProfileCfgFileName(oldName));
        InputProfiles.Remove(GetProfileName(oldName));

        var profiles = InputProfiles.Keys.ToList();
        _globalSettings.SetClientSetting(GlobalSettingsKeys.SettingsProfiles, profiles.ToArray());

        CurrentProfileName = GetProfileName(newName);

        SaveProfileFile(InputConfigs[GetProfileCfgFileName(newName)], newName);

        try
        {
            File.Delete(Path + GetProfileCfgFileName(oldName));
        }
        catch
        {
        }
    }

    /// <summary>Copies a profile and makes the copy the current profile.</summary>
    public void CopyProfile(string profileToCopy, string profileName)
    {
        var config = Configuration.LoadFromFile(Path + GetProfileCfgFileName(profileToCopy));
        InputConfigs[GetProfileCfgFileName(profileName)] = config;

        var inputProfile = new Dictionary<InputBinding, InputDevice>();
        InputProfiles[GetProfileName(profileName)] = inputProfile;

        foreach (InputBinding bind in Enum.GetValues(typeof(InputBinding)))
        {
            var device = GetControlSetting(bind, config);

            if (device != null) inputProfile[bind] = device;
        }

        var profiles = InputProfiles.Keys.ToList();
        _globalSettings.SetClientSetting(GlobalSettingsKeys.SettingsProfiles, profiles.ToArray());

        CurrentProfileName = GetProfileName(profileName);

        SaveProfileFile(InputConfigs[GetProfileCfgFileName(profileName)], profileName);
    }

    /// <summary>Writes a profile file; a failure (e.g. a read-only configuration directory) is only logged.</summary>
    private void SaveProfileFile(Configuration configuration, string profile)
    {
        try
        {
            configuration.SaveToFile(Path + GetProfileCfgFileName(profile), new UTF8Encoding(false, true));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Unable to save the profile {profile} to {Path}");
        }
    }
}