using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings.Setting;
using NLog;

namespace EasyRadioLink.Common.Network.Singletons;

/// <summary>
///     Client-side mirror of the server's broadcast settings ("[General Settings]" + synthetic values).
///     Missing keys fall back to <see cref="DefaultServerSettings.Defaults" />.
/// </summary>
public class SyncedServerSettings
{
    private static SyncedServerSettings instance;
    private static readonly object _lock = new();
    private static readonly Dictionary<string, string> defaults = DefaultServerSettings.Defaults;

    private readonly ConcurrentDictionary<string, string> _settings;

    //cache of processed settings as bools to make lookup slightly quicker
    private readonly ConcurrentDictionary<string, bool> _settingsBool;
    private readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public SyncedServerSettings()
    {
        _settings = new ConcurrentDictionary<string, string>();
        _settingsBool = new ConcurrentDictionary<string, bool>();
        UpdateFrequencyLists();
    }

    private Dictionary<string, List<ServerPresetChannel>> ServerPresetChannels { get; set; } = new();

    /// <summary>
    ///     The server's radio layout (SERVER_RADIO_PRESET): exactly <see cref="Constants.MAX_RADIOS" /> validated entries,
    ///     or empty if the server does not provide one.
    /// </summary>
    public List<RadioDefinition> ServerRadioPreset { get; private set; } = new();

    /// <summary>Raw SERVER_RADIO_PRESET value as received ("" if none) - handy to detect layout changes.</summary>
    public string ServerRadioPresetJson { get; private set; } = "";

    /// <summary>Radio check (echo) frequencies in Hz (TEST_FREQUENCIES).</summary>
    public IReadOnlyList<double> TestFrequencies { get; private set; } = Array.Empty<double>();

    /// <summary>Frequencies in Hz that are played without radio effects (CLEAN_FREQUENCIES).</summary>
    public IReadOnlyList<double> CleanFrequencies { get; private set; } = Array.Empty<double>();

    /// <summary>Protocol version reported by the server in its SYNC reply.</summary>
    public string ServerVersion { get; set; }

    public static SyncedServerSettings Instance
    {
        get
        {
            lock (_lock)
            {
                if (instance == null) instance = new SyncedServerSettings();
            }

            return instance;
        }
    }


    public string GetSetting(ServerSettingsKeys key)
    {
        var setting = key.ToString();
        return _settings.GetOrAdd(setting, defaults.TryGetValue(setting, out var value) ? value : "");
    }

    public bool GetSettingAsBool(ServerSettingsKeys key)
    {
        var strKey = key.ToString();
        if (_settingsBool.TryGetValue(strKey, out var res)) return res;

        if (!bool.TryParse(GetSetting(key)?.Trim(), out res))
            res = defaults.TryGetValue(strKey, out var defaultValue) && bool.TryParse(defaultValue, out var parsed) &&
                  parsed;

        _settingsBool[strKey] = res;
        return res;
    }

    public int GetSettingAsInt(ServerSettingsKeys key)
    {
        if (int.TryParse(GetSetting(key)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var res))
            return res;

        return defaults.TryGetValue(key.ToString(), out var defaultValue) &&
               int.TryParse(defaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out res)
            ? res
            : 0;
    }

    /// <summary>True if <paramref name="frequencyHz" /> is one of the server's clean (no radio effects) frequencies.</summary>
    public bool IsCleanFrequency(double frequencyHz)
    {
        return ContainsFrequency(CleanFrequencies, frequencyHz);
    }

    /// <summary>True if <paramref name="frequencyHz" /> is one of the server's radio check (echo) frequencies.</summary>
    public bool IsTestFrequency(double frequencyHz)
    {
        return ContainsFrequency(TestFrequencies, frequencyHz);
    }

    private static bool ContainsFrequency(IReadOnlyList<double> list, double frequencyHz)
    {
        foreach (var frequency in list)
            if (RadioBase.FreqCloseEnough(frequency, frequencyHz))
                return true;

        return false;
    }

    public List<ServerPresetChannel> GetPresetChannels(string radio)
    {
        var presets = ServerPresetChannels;
        if (radio != null)
            foreach (var radioPreset in presets.Keys)
                if (radio.StartsWith(radioPreset))
                    return presets[radioPreset];

        return new List<ServerPresetChannel>();
    }

    /// <summary>Forgets everything received from a server (call on connect/disconnect).</summary>
    public void Reset()
    {
        _settings.Clear();
        _settingsBool.Clear();
        ServerPresetChannels = new Dictionary<string, List<ServerPresetChannel>>();
        ServerRadioPreset = new List<RadioDefinition>();
        ServerRadioPresetJson = "";
        ServerVersion = null;
        UpdateFrequencyLists();
    }

    /// <summary>
    ///     Applies settings received from the server. Publishes a <see cref="ServerSettingsUpdatedMessage" /> unless
    ///     <paramref name="publish" /> is false.
    /// </summary>
    public void Decode(Dictionary<string, string> encoded, bool publish = true)
    {
        if (encoded == null) return;

        foreach (var kvp in encoded)
        {
            if (kvp.Key == null) continue;

            var value = kvp.Value ?? "";
            _settings.AddOrUpdate(kvp.Key, value, (key, oldVal) => value);

            if (kvp.Key.Equals(ServerSettingsKeys.SERVER_PRESETS.ToString()))
            {
                try
                {
                    ServerPresetChannels =
                        JsonSerializer.Deserialize<Dictionary<string, List<ServerPresetChannel>>>(value,
                            new JsonSerializerOptions
                            {
                                AllowTrailingCommas = true,
                                PropertyNameCaseInsensitive = true,
                                ReadCommentHandling = JsonCommentHandling.Skip,
                                IncludeFields = true
                            }) ?? new Dictionary<string, List<ServerPresetChannel>>();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Unable to read the server preset channels");
                    ServerPresetChannels = new Dictionary<string, List<ServerPresetChannel>>();
                }
            }
            else if (kvp.Key.Equals(ServerSettingsKeys.SERVER_RADIO_PRESET.ToString()))
            {
                DecodeServerRadioPreset(value);
            }
        }

        UpdateFrequencyLists();

        //cache will be refilled
        _settingsBool.Clear();

        if (publish) PublishSettingsUpdated();
    }

    /// <summary>Publishes a <see cref="ServerSettingsUpdatedMessage" /> with the current settings.</summary>
    public void PublishSettingsUpdated()
    {
        EventBus.Instance.PublishOnBackgroundThreadAsync(new ServerSettingsUpdatedMessage(_settings));
    }

    private void DecodeServerRadioPreset(string json)
    {
        try
        {
            var radios = RadioDefinition.ParseList(json);

            ServerRadioPreset = radios.Count == 0
                ? new List<RadioDefinition>()
                : RadioDefinition.Normalise(radios);
            ServerRadioPresetJson = radios.Count == 0 ? "" : json;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unable to read the server radio layout");
            ServerRadioPreset = new List<RadioDefinition>();
            ServerRadioPresetJson = "";
        }
    }

    private void UpdateFrequencyLists()
    {
        TestFrequencies = RadioCalculator.ParseFrequencyListMHz(GetSetting(ServerSettingsKeys.TEST_FREQUENCIES));
        CleanFrequencies = RadioCalculator.ParseFrequencyListMHz(GetSetting(ServerSettingsKeys.CLEAN_FREQUENCIES));
    }
}
