using NLog;
using SharpConfig;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Settings.Setting;

namespace EasyRadioLink.Common.Settings;

/// <summary>
///     Client settings in global.cfg (persisted by NAME - renaming a member orphans the saved value).
/// </summary>
public enum GlobalSettingsKeys
{
    Version,
    MinimiseToTray,
    StartMinimised,

    ExpandControls,
    RadioPanelTaskbarHide,

    AudioInputDeviceId,
    AudioOutputDeviceId,
    LastServer,
    SpeakerBoost,

    // Radio panel window geometry
    RadioX,
    RadioY,
    RadioOpacity,
    RadioWidth,
    RadioHeight,

    ClientX,
    ClientY,
    MicAudioOutputDeviceId,

    Denoise,
    DenoiseAttenuation,
    IncomingAudioAGC,
    IncomingAudioAGCTarget,
    IncomingAudioAGCDecrement,
    IncomingAudioAGCLevelMax,

    IncomingAudioDenoise,
    IncomingAudioDenoiseAttenuation,

    LastSeenName,

    AllowMultipleInstances, // Allow for more than one EasyRadioLink instance to be ran simultaneously. Config-file only!

    DisableWindowVisibilityCheck,
    PlayConnectionSounds,

    RequireAdmin,

    SettingsProfiles,

    ShowTransmitterName,

    AllowRecording,
    RecordAudio,
    SingleFileMixdown,
    RecordingQuality,
    RecordingFormat,
    DisallowedAudioTone,
    VOX,
    VOXMode,
    VOXMinimumTime,
    VOXMinimumDB,

    AllowXInputController,

    LastPresetsFolder,

    AutoOpenRadioPanel // open the radio panel automatically after connecting
}

/// <summary>
///     Hotkey bindings, persisted per profile by NAME.
///     Invariants used by the input code: modifier binding = main binding + 100, and
///     <c>SwitchN = 100 + radio index</c> (radios 1..10). Never change the numeric values.
/// </summary>
public enum InputBinding
{
    Switch1 = 101,
    ModifierSwitch1 = 201,

    Switch2 = 102,
    ModifierSwitch2 = 202,

    Switch3 = 103,
    ModifierSwitch3 = 203,

    Switch4 = 104,
    ModifierSwitch4 = 204,

    Switch5 = 105,
    ModifierSwitch5 = 205,

    Switch6 = 106,
    ModifierSwitch6 = 206,

    Switch7 = 107,
    ModifierSwitch7 = 207,

    Switch8 = 108,
    ModifierSwitch8 = 208,

    Switch9 = 109,
    ModifierSwitch9 = 209,

    Switch10 = 110,
    ModifierSwitch10 = 210,

    Ptt = 111,
    ModifierPtt = 211,

    Up100 = 113,
    ModifierUp100 = 213,

    Up10 = 114,
    ModifierUp10 = 214,

    Up1 = 115,
    ModifierUp1 = 215,

    Up01 = 116,
    ModifierUp01 = 216,

    Up001 = 117,
    ModifierUp001 = 217,

    Up0001 = 118,
    ModifierUp0001 = 218,

    Down100 = 119,
    ModifierDown100 = 219,

    Down10 = 120,
    ModifierDown10 = 220,

    Down1 = 121,
    ModifierDown1 = 221,

    Down01 = 122,
    ModifierDown01 = 222,

    Down001 = 123,
    ModifierDown001 = 223,

    Down0001 = 124,
    ModifierDown0001 = 224,

    NextRadio = 125,
    ModifierNextRadio = 225,

    PreviousRadio = 126,
    ModifierPreviousRadio = 226,

    ToggleGuard = 127,
    ModifierToggleGuard = 227,

    ToggleEncryption = 128,
    ModifierToggleEncryption = 228,

    EncryptionKeyIncrease = 129,
    ModifierEncryptionKeyIncrease = 229,

    EncryptionKeyDecrease = 130,
    ModifierEncryptionKeyDecrease = 230,

    RadioChannelUp = 131,
    ModifierRadioChannelUp = 231,

    RadioChannelDown = 132,
    ModifierRadioChannelDown = 232,

    RadioVolumeUp = 134,
    ModifierRadioVolumeUp = 234,

    RadioVolumeDown = 135,
    ModifierRadioVolumeDown = 235,

    RadioPanelToggle = 137,
    ModifierRadioPanelToggle = 237
}

public class GlobalSettingsStore
{
    private static readonly string CFG_FILE_NAME = "global.cfg";

    //private static readonly string PREVIOUS_CFG_FILE_NAME = "client.cfg";

    private static readonly object _lock = new();

    private static GlobalSettingsStore _instance;
    private readonly Configuration _configuration;

    //cache all the settings in their correct types for speed
    //fixes issue where we access settings a lot and have issues
    private readonly ConcurrentDictionary<string, object> _settingsCache = new();

    private readonly Dictionary<string, string[]> defaultArraySettings = new()
    {
        { GlobalSettingsKeys.SettingsProfiles.ToString(), new[] { "default.cfg" } }
    };

    private static readonly int CurrentVersion = 1;

    private readonly Dictionary<string, string> defaultGlobalSettings = new()
    {
        { GlobalSettingsKeys.Version.ToString(), "0" },
        { GlobalSettingsKeys.RadioPanelTaskbarHide.ToString(), "false" },
        { GlobalSettingsKeys.ExpandControls.ToString(), "false" },

        { GlobalSettingsKeys.MinimiseToTray.ToString(), "false" },
        { GlobalSettingsKeys.StartMinimised.ToString(), "false" },


        { GlobalSettingsKeys.AudioInputDeviceId.ToString(), "" },
        { GlobalSettingsKeys.AudioOutputDeviceId.ToString(), "" },
        { GlobalSettingsKeys.MicAudioOutputDeviceId.ToString(), "" },

        { GlobalSettingsKeys.LastServer.ToString(), "127.0.0.1:" + DefaultServerSettings.DEFAULT_SERVER_PORT },

        { GlobalSettingsKeys.SpeakerBoost.ToString(), "0.514" },

        { GlobalSettingsKeys.RadioX.ToString(), "300" },
        { GlobalSettingsKeys.RadioY.ToString(), "300" },
        { GlobalSettingsKeys.RadioOpacity.ToString(), "1.0" },

        { GlobalSettingsKeys.RadioWidth.ToString(), "122" },
        { GlobalSettingsKeys.RadioHeight.ToString(), "270" },

        { GlobalSettingsKeys.ClientX.ToString(), "200" },
        { GlobalSettingsKeys.ClientY.ToString(), "200" },

        { GlobalSettingsKeys.Denoise.ToString(), "true" },
        { GlobalSettingsKeys.DenoiseAttenuation.ToString(), "-30" },

        { GlobalSettingsKeys.IncomingAudioAGC.ToString(), "true" },
        { GlobalSettingsKeys.IncomingAudioAGCTarget.ToString(), "14000" },
        { GlobalSettingsKeys.IncomingAudioAGCDecrement.ToString(), "-60" },
        { GlobalSettingsKeys.IncomingAudioAGCLevelMax.ToString(), "40" },

        { GlobalSettingsKeys.IncomingAudioDenoise.ToString(), "true" },
        { GlobalSettingsKeys.IncomingAudioDenoiseAttenuation.ToString(), "-30" },

        { GlobalSettingsKeys.LastSeenName.ToString(), "" },

        { GlobalSettingsKeys.AllowMultipleInstances.ToString(), "false" },

        { GlobalSettingsKeys.DisableWindowVisibilityCheck.ToString(), "false" },
        { GlobalSettingsKeys.PlayConnectionSounds.ToString(), "true" },

        { GlobalSettingsKeys.RequireAdmin.ToString(), "false" },

        { GlobalSettingsKeys.ShowTransmitterName.ToString(), "true" },

        { GlobalSettingsKeys.AllowRecording.ToString(), "false" },
        { GlobalSettingsKeys.RecordAudio.ToString(), "false" },
        { GlobalSettingsKeys.SingleFileMixdown.ToString(), "false" },
        { GlobalSettingsKeys.RecordingQuality.ToString(), "V3" },
        { GlobalSettingsKeys.RecordingFormat.ToString(), "mp3" },
        { GlobalSettingsKeys.DisallowedAudioTone.ToString(), "false" },

        { GlobalSettingsKeys.VOX.ToString(), "false" },
        { GlobalSettingsKeys.VOXMode.ToString(), "3" },
        { GlobalSettingsKeys.VOXMinimumTime.ToString(), "300" },
        { GlobalSettingsKeys.VOXMinimumDB.ToString(), "-59.0" },


        { GlobalSettingsKeys.AllowXInputController.ToString(), "false" },
        { GlobalSettingsKeys.LastPresetsFolder.ToString(), AppPaths.PresetsDirectory },

        { GlobalSettingsKeys.AutoOpenRadioPanel.ToString(), "true" }
    };

    private readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private GlobalSettingsStore()
    {
        // "-cfg=<directory>" overrides the configuration directory - resolved by Path (the client may have replaced
        // an unusable -cfg directory by the default one before the store is created)
        if (CommandLineConfigDirectory() != null) Logger.Info($"Found -cfg loading: {Path + ConfigFileName}");

        try
        {
            Directory.CreateDirectory(Path);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Unable to create configuration directory {Path}");
        }

        try
        {
            var count = 0;
            while (IsFileLocked(new FileInfo(Path + ConfigFileName)) && count < 10)
            {
                Thread.Sleep(200);
                count++;
            }

            _configuration = Configuration.LoadFromFile(Path + ConfigFileName);
            UpgradeSettings();
        }
        catch (FileNotFoundException)
        {
            Logger.Info(
                $"Did not find client config file at path {Path}{ConfigFileName}, initialising with default config");

            _configuration = new Configuration
            {
                new Section("Position Settings"),
                new Section("Client Settings")
            };
            SetClientSetting(GlobalSettingsKeys.Version, CurrentVersion);
            Save();
        }
        catch (ParserException ex)
        {
            Logger.Error(ex,
                "Failed to parse client config, potentially corrupted. Creating backing and re-initialising with default config");

            try
            {
                File.Copy(Path + ConfigFileName, Path + ConfigFileName + ".bak", true);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to create backup of corrupted config file, ignoring");
            }

            _configuration = new Configuration
            {
                new Section("Position Settings"),
                new Section("Client Settings")
            };
            SetClientSetting(GlobalSettingsKeys.Version, CurrentVersion);

            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // e.g. a -cfg directory that can't be read - continue with the defaults (saving fails and is logged)
            Logger.Error(ex, $"Unable to read the client config file {Path}{ConfigFileName}, using the default config");

            _configuration = new Configuration
            {
                new Section("Position Settings"),
                new Section("Client Settings")
            };
            SetClientSetting(GlobalSettingsKeys.Version, CurrentVersion);
        }

        ProfileSettingsStore = new ProfileSettingsStore(this);
    }

    public string ConfigFileName { get; } = CFG_FILE_NAME;
    public ProfileSettingsStore ProfileSettingsStore { get; }

    /// <summary>
    ///     Directory of global.cfg and the profile *.cfg files, always with a trailing directory separator.
    ///     Defaults to <see cref="AppPaths.UserDataDirectory" />; the command line argument <c>-cfg=&lt;directory&gt;</c>
    ///     overrides it.
    /// </summary>
    public static string Path
    {
        // the -cfg argument is also honoured when Path is read before the store is created
        get => _path ??= WithTrailingSeparator(CommandLineConfigDirectory() ?? AppPaths.UserDataDirectory);
        set => _path = string.IsNullOrWhiteSpace(value)
            ? WithTrailingSeparator(AppPaths.UserDataDirectory)
            : WithTrailingSeparator(value.Trim());
    }

    private static string _path;

    /// <summary>
    ///     The directory of the (last) <c>-cfg=&lt;directory&gt;</c> command line argument as a full path (a relative
    ///     path is resolved against the working directory once), or null.
    /// </summary>
    private static string CommandLineConfigDirectory()
    {
        string directory = null;

        foreach (var arg in Environment.GetCommandLineArgs())
            if (arg.Trim().StartsWith("-cfg="))
            {
                var value = arg.Trim().Substring("-cfg=".Length).Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(value)) directory = value;
            }

        if (directory == null) return null;

        try
        {
            return System.IO.Path.GetFullPath(directory);
        }
        catch (Exception)
        {
            // invalid path - reported when the directory is used
            return directory;
        }
    }

    private static string WithTrailingSeparator(string directory)
    {
        if (directory.EndsWith(System.IO.Path.DirectorySeparatorChar) ||
            directory.EndsWith(System.IO.Path.AltDirectorySeparatorChar))
            return directory;

        return directory + System.IO.Path.DirectorySeparatorChar;
    }

    public static GlobalSettingsStore Instance
    {
        get
        {
            if (_instance == null)
                _instance = new GlobalSettingsStore();

            //stops cyclic init
            return _instance;
        }
    }

    public static bool IsFileLocked(FileInfo file)
    {
        if (!file.Exists) return false;

        try
        {
            using (var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None))
            {
                stream.Close();
            }
        }
        catch (IOException)
        {
            //the file is unavailable because it is:
            //still being written to
            //or being processed by another thread
            //or does not exist (has already been processed)
            return true;
        }

        //file is not locked
        return false;
    }

    public void SetClientSetting(GlobalSettingsKeys key, string[] strArray)
    {
        SetSetting("Client Settings", key.ToString(), strArray);
    }

    public SharpConfig.Setting GetPositionSetting(GlobalSettingsKeys key)
    {
        return GetSetting("Position Settings", key.ToString());
    }

    public void SetPositionSetting(GlobalSettingsKeys key, double value)
    {
        _settingsCache.TryRemove(key.ToString(), out _);
        SetSetting("Position Settings", key.ToString(), value.ToString(CultureInfo.InvariantCulture));
    }

    public int GetClientSettingInt(GlobalSettingsKeys key)
    {
        if (_settingsCache.TryGetValue(key.ToString(), out var val)) return (int)val;

        var setting = GetSetting("Client Settings", key.ToString());
        if (setting.RawValue.Length == 0) return 0;
        _settingsCache[key.ToString()] = setting.IntValue;
        return setting.IntValue;
    }

    public double GetClientSettingDouble(GlobalSettingsKeys key)
    {
        if (_settingsCache.TryGetValue(key.ToString(), out var val)) return (double)val;

        var setting = GetSetting("Client Settings", key.ToString());
        if (setting.RawValue.Length == 0) return 0D;
        _settingsCache[key.ToString()] = setting.DoubleValue;
        return setting.DoubleValue;
    }

    public bool GetClientSettingBool(GlobalSettingsKeys key)
    {
        if (_settingsCache.TryGetValue(key.ToString(), out var val)) return (bool)val;

        var setting = GetSetting("Client Settings", key.ToString());
        if (setting.RawValue.Length == 0) return false;
        _settingsCache[key.ToString()] = setting.BoolValue;
        return setting.BoolValue;
    }

    public SharpConfig.Setting GetClientSetting(GlobalSettingsKeys key)
    {
        return GetSetting("Client Settings", key.ToString());
    }

    public void SetClientSetting(GlobalSettingsKeys key, string value, bool raw = false)
    {
        _settingsCache.TryRemove(key.ToString(), out _);
        SetSetting("Client Settings", key.ToString(), value, raw);
    }

    public void SetClientSetting(GlobalSettingsKeys key, bool value)
    {
        _settingsCache.TryRemove(key.ToString(), out _);
        SetSetting("Client Settings", key.ToString(), value);
    }

    public void SetClientSetting(GlobalSettingsKeys key, int value)
    {
        _settingsCache.TryRemove(key.ToString(), out _);
        SetSetting("Client Settings", key.ToString(), value);
    }

    public void SetClientSetting(GlobalSettingsKeys key, double value)
    {
        _settingsCache.TryRemove(key.ToString(), out _);
        SetSetting("Client Settings", key.ToString(), value);
    }


    private SharpConfig.Setting GetSetting(string section, string setting)
    {
        if (!_configuration.Contains(section)) _configuration.Add(section);

        if (!_configuration[section].Contains(setting))
        {
            if (defaultGlobalSettings.ContainsKey(setting))
            {
                //save
                _configuration[section]
                    .Add(new SharpConfig.Setting(setting, defaultGlobalSettings[setting]));

                Save();
            }
            else if (defaultArraySettings.ContainsKey(setting))
            {
                //save
                _configuration[section]
                    .Add(new SharpConfig.Setting(setting, defaultArraySettings[setting]));

                Save();
            }
            else
            {
                _configuration[section]
                    .Add(new SharpConfig.Setting(setting, ""));
                Save();
            }
        }

        return _configuration[section][setting];
    }

    private void SetSetting(string section, string key, object setting, bool raw = false)
    {
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
            else if (setting.GetType() == typeof(string))
            {
                if (raw)
                    _configuration[section][key].RawValue = setting as string;
                else
                    _configuration[section][key].StringValue = setting as string;
            }
            else if (setting is string[])
                _configuration[section][key].StringValueArray = setting as string[];
            else if (setting is int)
                _configuration[section][key].IntValue = (int)setting;
            else if (setting is double)
                _configuration[section][key].DoubleValue = (double)setting;
            else
                Logger.Error("Unknown Setting Type - Not Saved ");
        }

        Save();
    }


    private void Save()
    {
        lock (_lock)
        {
            try
            {
                _configuration.SaveToFile(Path + ConfigFileName, new UTF8Encoding(false, true));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Unable to save settings to {Path}{ConfigFileName}!");
            }
        }
    }

    private void UpgradeSettings()
    {
        var loadedVersion = GetClientSettingInt(GlobalSettingsKeys.Version);
        if (loadedVersion < CurrentVersion)
        {
            SetClientSetting(GlobalSettingsKeys.Version, CurrentVersion);

            Save();
        }
    }
}
