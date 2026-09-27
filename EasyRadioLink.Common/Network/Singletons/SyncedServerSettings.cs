using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings.Setting;

namespace EasyRadioLink.Common.Network.Singletons;

/// <summary>
///     Client-side mirror of the server's broadcast settings ("[General Settings]").
///     Missing keys fall back to <see cref="DefaultServerSettings.Defaults" /> - except for features that an older
///     server does not have (<see cref="BusyChannelLockout" />): those are off unless the server sends them.
/// </summary>
public class SyncedServerSettings
{
    private static SyncedServerSettings instance;
    private static readonly object _lock = new();
    private static readonly Dictionary<string, string> defaults = DefaultServerSettings.Defaults;

    private readonly ConcurrentDictionary<string, string> _settings;

    // the keys the server actually sent (not filled in from the defaults)
    private readonly ConcurrentDictionary<string, bool> _sentByServer = new();

    //cache of processed settings as bools to make lookup slightly quicker
    private readonly ConcurrentDictionary<string, bool> _settingsBool;

    public SyncedServerSettings()
    {
        _settings = new ConcurrentDictionary<string, string>();
        _settingsBool = new ConcurrentDictionary<string, bool>();
        UpdateFrequencyLists();
    }

    /// <summary>Radio check (echo) frequencies in Hz (TEST_FREQUENCIES).</summary>
    public IReadOnlyList<double> TestFrequencies { get; private set; } = Array.Empty<double>();

    /// <summary>Frequencies in Hz that are played without radio effects (CLEAN_FREQUENCIES).</summary>
    public IReadOnlyList<double> CleanFrequencies { get; private set; } = Array.Empty<double>();

    /// <summary>
    ///     One speaker per frequency (BUSY_CHANNEL_LOCKOUT): the server lets only one station transmit on a frequency
    ///     at a time. False for a server that does not send the setting (before 1.3 it does not lock anything out).
    /// </summary>
    public bool BusyChannelLockout =>
        WasSentByServer(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT) &&
        GetSettingAsBool(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT);

    /// <summary>Protocol version reported by the server in its SYNC reply.</summary>
    public string ServerVersion { get; set; }

    /// <summary>Fingerprint of the connected server's identity ("AB:CD:..."), verified against the pin.</summary>
    public string ServerIdentityFingerprint { get; set; }

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


    /// <summary>True if the connected server sent <paramref name="key" /> (it knows the setting).</summary>
    public bool WasSentByServer(ServerSettingsKeys key)
    {
        return _sentByServer.ContainsKey(key.ToString());
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

    /// <summary>Forgets everything received from a server (call on connect/disconnect).</summary>
    public void Reset()
    {
        _settings.Clear();
        _settingsBool.Clear();
        _sentByServer.Clear();
        ServerVersion = null;
        ServerIdentityFingerprint = null;
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
            _sentByServer[kvp.Key] = true;
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

    private void UpdateFrequencyLists()
    {
        TestFrequencies = RadioCalculator.ParseFrequencyListMHz(GetSetting(ServerSettingsKeys.TEST_FREQUENCIES));
        CleanFrequencies = RadioCalculator.ParseFrequencyListMHz(GetSetting(ServerSettingsKeys.CLEAN_FREQUENCIES));
    }
}
