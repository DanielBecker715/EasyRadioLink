using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings.Setting;
using NLog;
using SharpConfig;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EasyRadioLink.Common.Settings;

/// <summary>
///     server.cfg (SharpConfig). Two sections:
///     <list type="bullet">
///         <item>"[General Settings]" - sent to every client (<see cref="ToDictionary" />),</item>
///         <item>"[Server Settings]" - server only (port, bind IP, UPnP, HTTP API, client export path, SERVER_PASSWORD).</item>
///     </list>
///     All server files (server.cfg, Presets/*.txt, server-radios.json) live next to the configuration file.
/// </summary>
public class ServerSettingsStore
{
    public const string GENERAL_SECTION = "General Settings";
    public const string SERVER_SECTION = "Server Settings";

    /// <summary>Server radio layout file (same schema as the client's radios.json), next to server.cfg.</summary>
    public const string SERVER_RADIOS_FILE = "server-radios.json";

    /// <summary>Replacement for secret values in settings dumps and logs.</summary>
    public const string MASKED_VALUE = "****";

    private static ServerSettingsStore instance;
    private static readonly object _instanceLock = new();

    //Can be overridden by a command line flag - hence being static
    //if overwritten, it will contain a full path
    public static string CFG_FILE_NAME = "server.cfg";

    private readonly object _lock = new();
    private readonly Configuration _configuration;
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private ServerChannelPresetHelper _serverChannelPresetHelper;

    private string _serverRadioPresetJson;
    private DateTime _serverRadioPresetTimestamp;
    private bool _serverRadioPresetProblemLogged;

    public ServerSettingsStore() : this(CFG_FILE_NAME)
    {
    }

    /// <summary>Loads (or creates) the configuration file <paramref name="configFilePath" />.</summary>
    public ServerSettingsStore(string configFilePath)
    {
        ConfigFilePath = string.IsNullOrWhiteSpace(configFilePath) ? "server.cfg" : configFilePath.Trim();

        try
        {
            _configuration = Configuration.LoadFromFile(ConfigFilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.Info($"Did not find server config file {ConfigFilePath}, initialising with default config");

            _configuration = CreateDefaultConfiguration();

            Save();
        }
        catch (ParserException ex)
        {
            _logger.Error(ex,
                "Failed to parse server config, potentially corrupted. Creating backing and re-initialising with default config");

            try
            {
                File.Copy(ConfigFilePath, ConfigFilePath + ".bak", true);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to create backup of corrupted config file, ignoring");
            }

            _configuration = CreateDefaultConfiguration();

            Save();
        }
    }

    public static ServerSettingsStore Instance
    {
        get
        {
            lock (_instanceLock)
            {
                if (instance == null) instance = new ServerSettingsStore();
            }

            return instance;
        }
    }

    /// <summary>Path of the configuration file as passed in (may be relative to the working directory).</summary>
    public string ConfigFilePath { get; }

    /// <summary>Directory that holds server.cfg, Presets/ and server-radios.json ("" = working directory).</summary>
    public string ConfigDirectory => Path.GetDirectoryName(ConfigFilePath) ?? "";

    /// <summary>Full path of the server radio layout file.</summary>
    public string ServerRadiosFilePath => Path.Combine(ConfigDirectory, SERVER_RADIOS_FILE);

    private static Configuration CreateDefaultConfiguration()
    {
        var configuration = new Configuration();
        configuration.Add(new Section(GENERAL_SECTION));
        configuration.Add(new Section(SERVER_SECTION));
        return configuration;
    }

    /// <summary>
    ///     Every setting as "NAME = value" for console/log output. Secrets (SERVER_PASSWORD, HTTP_SERVER_API_KEY)
    ///     are masked.
    /// </summary>
    public List<string> GetAllSettings()
    {
        var secretNames = new HashSet<string>();
        foreach (var key in DefaultServerSettings.SecretKeys) secretNames.Add(key.ToString());

        var list = new List<string>();
        lock (_lock)
        {
            foreach (var section in _configuration)
            foreach (var setting in section)
            {
                var value = setting.RawValue;
                if (secretNames.Contains(setting.Name)) value = MaskSecret(value);

                list.Add($"{setting.Name} = {value}");
            }
        }

        return list;
    }

    /// <summary>"" for an empty secret, otherwise <see cref="MASKED_VALUE" />.</summary>
    public static string MaskSecret(string secret)
    {
        return string.IsNullOrEmpty(secret) ? "" : MASKED_VALUE;
    }

    public SharpConfig.Setting GetGeneralSetting(ServerSettingsKeys key)
    {
        return GetSetting(GENERAL_SECTION, key.ToString());
    }

    public void SetGeneralSetting(ServerSettingsKeys key, bool value)
    {
        SetSetting(GENERAL_SECTION, key.ToString(), value.ToString(CultureInfo.InvariantCulture));
    }

    public void SetGeneralSetting(ServerSettingsKeys key, string value)
    {
        SetSetting(GENERAL_SECTION, key.ToString(), value?.Trim());
    }

    public SharpConfig.Setting GetServerSetting(ServerSettingsKeys key)
    {
        return GetSetting(SERVER_SECTION, key.ToString());
    }

    public void SetServerSetting(ServerSettingsKeys key, bool value)
    {
        SetSetting(SERVER_SECTION, key.ToString(), value.ToString(CultureInfo.InvariantCulture));
    }

    public void SetServerSetting(ServerSettingsKeys key, string value)
    {
        SetSetting(SERVER_SECTION, key.ToString(), value?.Trim());
    }

    /// <summary>The server password ("" = open server). Stored in "[Server Settings]", never broadcast.</summary>
    public string GetServerPassword()
    {
        return GetServerSetting(ServerSettingsKeys.SERVER_PASSWORD).StringValue?.Trim() ?? "";
    }

    /// <summary>Sets the server password. Leading/trailing whitespace is ignored; null or "" = open server.</summary>
    public void SetServerPassword(string password)
    {
        SetServerSetting(ServerSettingsKeys.SERVER_PASSWORD, password ?? "");
    }

    public bool IsPasswordProtected => GetServerPassword().Length > 0;

    /// <summary>True if the server is open or <paramref name="candidate" /> matches the server password.</summary>
    public bool CheckPassword(string candidate)
    {
        return PasswordMatches(GetServerPassword(), candidate);
    }

    /// <summary>
    ///     Constant-time password check. An empty <paramref name="expected" /> password means "open server" and accepts
    ///     anything. Leading/trailing whitespace is ignored on both sides.
    /// </summary>
    public static bool PasswordMatches(string expected, string candidate)
    {
        expected = expected?.Trim() ?? "";
        if (expected.Length == 0) return true;

        candidate = candidate?.Trim() ?? "";

        // hash first so the comparison time does not depend on the password length either
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));

        return CryptographicOperations.FixedTimeEquals(expectedHash, candidateHash);
    }

    private SharpConfig.Setting GetSetting(string section, string setting)
    {
        lock (_lock)
        {
            if (!_configuration.Contains(section)) _configuration.Add(section);

            if (!_configuration[section].Contains(setting))
            {
                if (DefaultServerSettings.Defaults.ContainsKey(setting))
                    _configuration[section].Add(new SharpConfig.Setting(setting, DefaultServerSettings.Defaults[setting]));
                else
                    _configuration[section].Add(new SharpConfig.Setting(setting, ""));

                Save();
            }

            return _configuration[section][setting];
        }
    }

    private void SetSetting(string section, string key, string setting)
    {
        if (setting == null) setting = "";

        lock (_lock)
        {
            if (!_configuration.Contains(section)) _configuration.Add(section);

            if (!_configuration[section].Contains(key))
                _configuration[section].Add(new SharpConfig.Setting(key, setting));
            else
                _configuration[section][key].StringValue = setting;

            Save();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var directory = ConfigDirectory;
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                _configuration.SaveToFile(ConfigFilePath, new UTF8Encoding(false, true));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to save settings!");
            }
        }
    }

    public int GetServerPort()
    {
        lock (_lock)
        {
            if (!_configuration.Contains(SERVER_SECTION))
                return GetServerSetting(ServerSettingsKeys.SERVER_PORT).IntValue;

            // Migrate from old "port" setting value to new "SERVER_PORT" one
            if (_configuration[SERVER_SECTION].Contains("port"))
            {
                var oldSetting = _configuration[SERVER_SECTION]["port"];
                if (!string.IsNullOrWhiteSpace(oldSetting.StringValue))
                {
                    _logger.Info(
                        $"Migrating old port value {oldSetting.StringValue} to current SERVER_PORT server setting");

                    GetServerSetting(ServerSettingsKeys.SERVER_PORT).StringValue = oldSetting.StringValue;
                }

                _logger.Info("Removing old port value from server settings");

                _configuration[SERVER_SECTION].Remove(oldSetting);

                Save();
            }

            return GetServerSetting(ServerSettingsKeys.SERVER_PORT).IntValue;
        }
    }

    /// <summary>
    ///     The settings sent to clients: every "[General Settings]" entry (defaults filled in) plus the synthetic
    ///     SERVER_PRESETS and SERVER_RADIO_PRESET JSON values. Private settings (password, API key, ports, ...) are never
    ///     included.
    /// </summary>
    public Dictionary<string, string> ToDictionary()
    {
        var settings = new Dictionary<string, string>();

        lock (_lock)
        {
            // make sure every broadcast setting exists (and therefore has its default value)
            foreach (var key in DefaultServerSettings.BroadcastKeys) GetGeneralSetting(key);

            foreach (var setting in _configuration[GENERAL_SECTION]) settings[setting.Name] = setting.StringValue;
        }

        foreach (var privateKey in DefaultServerSettings.PrivateKeys) settings.Remove(privateKey.ToString());

        if (GetGeneralSetting(ServerSettingsKeys.SERVER_PRESETS_ENABLED).BoolValue)
        {
            //load presets
            if (_serverChannelPresetHelper == null)
            {
                _serverChannelPresetHelper = new ServerChannelPresetHelper(ConfigDirectory);
                _serverChannelPresetHelper.LoadPresets();
            }

            settings[nameof(ServerSettingsKeys.SERVER_PRESETS)] =
                JsonSerializer.Serialize(_serverChannelPresetHelper.Presets, new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    IncludeFields = true
                });
        }
        else
        {
            settings[nameof(ServerSettingsKeys.SERVER_PRESETS)] =
                JsonSerializer.Serialize(new Dictionary<string, List<ServerPresetChannel>>());
        }

        settings[nameof(ServerSettingsKeys.SERVER_RADIO_PRESET)] =
            GetGeneralSetting(ServerSettingsKeys.SERVER_RADIO_PRESET_ENABLED).BoolValue
                ? GetServerRadioPresetJson()
                : "[]";

        return settings;
    }

    /// <summary>
    ///     Validated server radio layout as JSON ("[]" if the file is missing or invalid). The file is re-read when it
    ///     changes on disk.
    /// </summary>
    private string GetServerRadioPresetJson()
    {
        var path = ServerRadiosFilePath;

        lock (_lock)
        {
            try
            {
                if (!File.Exists(path))
                {
                    if (!_serverRadioPresetProblemLogged)
                        _logger.Error($"Server radio layout is enabled but {Path.GetFullPath(path)} was not found");

                    _serverRadioPresetProblemLogged = true;
                    _serverRadioPresetJson = null;
                    return "[]";
                }

                var timestamp = File.GetLastWriteTimeUtc(path);
                if (_serverRadioPresetJson != null && timestamp == _serverRadioPresetTimestamp)
                    return _serverRadioPresetJson;

                var radios = RadioDefinition.ParseList(File.ReadAllText(path));

                string json;
                if (radios.Count == 0)
                {
                    _logger.Error($"Server radio layout {path} does not contain any radios");
                    json = "[]";
                }
                else
                {
                    if (radios.Count != Constants.MAX_RADIOS)
                        _logger.Warn(
                            $"Server radio layout {path} has {radios.Count} entries, expected {Constants.MAX_RADIOS} (slot 0 reserved) - adjusting");

                    json = JsonSerializer.Serialize(RadioDefinition.Normalise(radios), RadioDefinition.JsonOptions);
                    _logger.Info($"Loaded server radio layout from {path}");
                }

                _serverRadioPresetJson = json;
                _serverRadioPresetTimestamp = timestamp;
                _serverRadioPresetProblemLogged = false;
                return json;
            }
            catch (Exception ex)
            {
                if (!_serverRadioPresetProblemLogged)
                    _logger.Error(ex, $"Unable to read server radio layout {path}");

                _serverRadioPresetProblemLogged = true;
                _serverRadioPresetJson = null;
                return "[]";
            }
        }
    }

    public IPAddress GetServerIP()
    {
        var str = GetServerSetting(ServerSettingsKeys.SERVER_IP).RawValue;

        if (IPAddress.TryParse(str?.Trim(), out var address)) return address;

        return IPAddress.Any;
    }
}
