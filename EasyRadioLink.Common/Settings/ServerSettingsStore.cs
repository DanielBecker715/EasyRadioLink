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

namespace EasyRadioLink.Common.Settings;

/// <summary>
///     server.cfg (SharpConfig). Two sections:
///     <list type="bullet">
///         <item>"[General Settings]" - sent to every client (<see cref="ToDictionary" />),</item>
///         <item>"[Server Settings]" - server only (port, bind IP, UPnP, HTTP API, client export path, SERVER_PASSWORD).</item>
///     </list>
///     The configuration file defaults to server.cfg next to the server executable (never the working directory). All
///     other server files (banned.txt, logs, transmission logs, client export) live in the same folder as the
///     configuration file (<see cref="ConfigDirectory" />).
/// </summary>
public class ServerSettingsStore
{
    public const string GENERAL_SECTION = "General Settings";
    public const string SERVER_SECTION = "Server Settings";

    /// <summary>Default name of the configuration file.</summary>
    public const string DEFAULT_CFG_FILE_NAME = "server.cfg";

    /// <summary>Replacement for secret values in settings dumps and logs.</summary>
    public const string MASKED_VALUE = "****";

    private static ServerSettingsStore instance;
    private static readonly object _instanceLock = new();

    //Full path of the configuration file used by Instance. Can be overridden by a command line flag (-cfg / --cfg)
    //through SetConfigFile - hence being static
    public static string CFG_FILE_NAME = ResolveConfigFilePath(null);

    private readonly object _lock = new();
    private readonly Configuration _configuration;
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public ServerSettingsStore() : this(CFG_FILE_NAME)
    {
    }

    /// <summary>Loads (or creates) the configuration file <paramref name="configFilePath" />.</summary>
    public ServerSettingsStore(string configFilePath)
    {
        ConfigFilePath = ResolveConfigFilePath(configFilePath);

        try
        {
            _configuration = Configuration.LoadFromFile(ConfigFilePath);
            RepairInvalidValues();
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

    /// <summary>Full path of the configuration file.</summary>
    public string ConfigFilePath { get; }

    /// <summary>
    ///     Folder of the configuration file. Holds every other server file too: banned.txt, the logs, the transmission
    ///     logs and the client export.
    /// </summary>
    public string ConfigDirectory => Path.GetDirectoryName(ConfigFilePath) ?? AppContext.BaseDirectory;

    /// <summary>Error message of the last failed save (null if the last save worked).</summary>
    public string LastSaveError { get; private set; }

    /// <summary>Full path of a server file in <see cref="ConfigDirectory" />, e.g. <c>GetDataFilePath("banned.txt")</c>.</summary>
    public string GetDataFilePath(string fileName)
    {
        return Path.Combine(ConfigDirectory, fileName);
    }

    /// <summary>
    ///     Full path of a configuration file given on the command line. Empty = server.cfg next to the server
    ///     executable; a relative path is resolved against the current working directory (once, at start-up).
    /// </summary>
    public static string ResolveConfigFilePath(string configFilePath)
    {
        var path = configFilePath?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(path)) return Path.Combine(AppContext.BaseDirectory, DEFAULT_CFG_FILE_NAME);

        return Path.GetFullPath(path);
    }

    /// <summary>Uses <paramref name="configFilePath" /> (see <see cref="ResolveConfigFilePath" />) for <see cref="Instance" />.</summary>
    public static void SetConfigFile(string configFilePath)
    {
        CFG_FILE_NAME = ResolveConfigFilePath(configFilePath);
    }

    /// <summary>TCP/UDP ports must be 1..65535.</summary>
    public static bool IsValidPort(int port)
    {
        return port is >= 1 and <= 65535;
    }

    private static Configuration CreateDefaultConfiguration()
    {
        var configuration = new Configuration();
        configuration.Add(new Section(GENERAL_SECTION));
        configuration.Add(new Section(SERVER_SECTION));
        return configuration;
    }

    /// <summary>
    ///     Replaces hand-edited values that can't be read (e.g. <c>SERVER_PORT = 5010x</c>, <c>UPNP_ENABLED = maybe</c>)
    ///     with their defaults, so reading them later never throws. Only on/off, number and port settings are checked.
    /// </summary>
    private void RepairInvalidValues()
    {
        foreach (var section in _configuration)
        foreach (var setting in section)
        {
            if (!DefaultServerSettings.Defaults.TryGetValue(setting.Name, out var defaultValue)) continue;
            if (IsValidValue(setting, defaultValue)) continue;

            _logger.Warn(
                $"Invalid value '{setting.RawValue}' for {setting.Name} in {ConfigFilePath} - using the default '{defaultValue}'");
            setting.StringValue = defaultValue;
        }
    }

    private static bool IsValidValue(SharpConfig.Setting setting, string defaultValue)
    {
        try
        {
            if (bool.TryParse(defaultValue, out _))
            {
                _ = setting.BoolValue;
                return true;
            }

            if (int.TryParse(defaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                var value = setting.IntValue;
                return setting.Name != nameof(ServerSettingsKeys.SERVER_PORT) &&
                       setting.Name != nameof(ServerSettingsKeys.HTTP_SERVER_PORT)
                       || IsValidPort(value);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
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
                LastSaveError = null;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                _logger.Error(ex, $"Unable to save settings to {ConfigFilePath}!");
            }
        }
    }

    /// <summary>The TCP/UDP port; the default port if the stored value is not a valid port.</summary>
    public int GetServerPort()
    {
        lock (_lock)
        {
            if (!_configuration.Contains(SERVER_SECTION))
                return ReadServerPort();

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

            return ReadServerPort();
        }
    }

    private int ReadServerPort()
    {
        var setting = GetServerSetting(ServerSettingsKeys.SERVER_PORT);
        try
        {
            if (IsValidPort(setting.IntValue)) return setting.IntValue;
        }
        catch (Exception)
        {
            // handled below
        }

        _logger.Warn(
            $"Invalid SERVER_PORT '{setting.RawValue}' in {ConfigFilePath} - using {DefaultServerSettings.DEFAULT_SERVER_PORT}");
        return int.Parse(DefaultServerSettings.DEFAULT_SERVER_PORT, CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     The settings sent to clients: every "[General Settings]" entry (defaults filled in). Private settings
    ///     (password, API key, ports, ...) are never included.
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

        return settings;
    }

    public IPAddress GetServerIP()
    {
        var str = GetServerSetting(ServerSettingsKeys.SERVER_IP).RawValue;

        if (IPAddress.TryParse(str?.Trim(), out var address)) return address;

        return IPAddress.Any;
    }
}
