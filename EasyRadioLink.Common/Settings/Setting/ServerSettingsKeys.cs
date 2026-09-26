using System.Collections.Generic;
using EasyRadioLink.Common.Models;

namespace EasyRadioLink.Common.Settings.Setting;

/// <summary>
///     Server settings. The enum NAMES are the keys in server.cfg and on the wire (NetworkMessage.ServerSettings);
///     the numeric values are only kept stable for readability - never reuse a removed number (the highest number used
///     so far is 40, so a new key starts at 41).
/// </summary>
public enum ServerSettingsKeys
{
    SERVER_PORT = 0,
    CLIENT_EXPORT_ENABLED = 3,
    IRL_RADIO_TX = 6, // half-duplex radios: can't receive while transmitting
    IRL_RADIO_RX_INTERFERENCE = 7, // simultaneous transmissions interfere
    CLIENT_EXPORT_FILE_PATH = 13,
    TEST_FREQUENCIES = 16, // radio check (echo) frequencies, MHz list
    SHOW_TUNED_COUNT = 17,
    SHOW_TRANSMITTER_NAME = 19,
    UPNP_ENABLED = 23,
    TRANSMISSION_LOG_ENABLED = 26,
    TRANSMISSION_LOG_RETENTION = 27,
    SERVER_IP = 29,
    HTTP_SERVER_ENABLED = 32,
    HTTP_SERVER_PORT = 33,
    HTTP_SERVER_API_KEY = 34,
    HTTP_SERVER_ADDRESS = 38,
    SERVER_PASSWORD = 39, // [Server Settings] only - never broadcast; empty = open server
    CLEAN_FREQUENCIES = 40 // MHz list: transmissions on these frequencies are played without radio effects
}

public class DefaultServerSettings
{
    public const string DEFAULT_SERVER_PORT = "5010";

    public static readonly Dictionary<string, string> Defaults = new()
    {
        { ServerSettingsKeys.SERVER_PORT.ToString(), DEFAULT_SERVER_PORT },
        { ServerSettingsKeys.CLIENT_EXPORT_ENABLED.ToString(), "false" },
        { ServerSettingsKeys.IRL_RADIO_TX.ToString(), "false" },
        { ServerSettingsKeys.IRL_RADIO_RX_INTERFERENCE.ToString(), "false" },
        { ServerSettingsKeys.CLIENT_EXPORT_FILE_PATH.ToString(), "clients-list.json" },
        // radio check (echo): CB channel 40 and PMR446 channel 16
        { ServerSettingsKeys.TEST_FREQUENCIES.ToString(), "27.405,446.19375" },
        { ServerSettingsKeys.SHOW_TUNED_COUNT.ToString(), "true" },
        { ServerSettingsKeys.SHOW_TRANSMITTER_NAME.ToString(), "false" },
        { ServerSettingsKeys.UPNP_ENABLED.ToString(), "true" },
        { ServerSettingsKeys.TRANSMISSION_LOG_ENABLED.ToString(), "false" },
        { ServerSettingsKeys.TRANSMISSION_LOG_RETENTION.ToString(), "2" },
        { ServerSettingsKeys.SERVER_IP.ToString(), "0.0.0.0" },
        { ServerSettingsKeys.HTTP_SERVER_ENABLED.ToString(), "false" },
        { ServerSettingsKeys.HTTP_SERVER_PORT.ToString(), "8080" },
        { ServerSettingsKeys.HTTP_SERVER_API_KEY.ToString(), ShortGuid.NewGuid() },
        { ServerSettingsKeys.HTTP_SERVER_ADDRESS.ToString(), "localhost" },
        { ServerSettingsKeys.SERVER_PASSWORD.ToString(), "" },
        { ServerSettingsKeys.CLEAN_FREQUENCIES.ToString(), "" }
    };

    /// <summary>
    ///     Settings stored in the "[General Settings]" section of server.cfg and sent to every client.
    /// </summary>
    public static readonly IReadOnlyList<ServerSettingsKeys> BroadcastKeys = new[]
    {
        ServerSettingsKeys.CLIENT_EXPORT_ENABLED,
        ServerSettingsKeys.IRL_RADIO_TX,
        ServerSettingsKeys.IRL_RADIO_RX_INTERFERENCE,
        ServerSettingsKeys.TEST_FREQUENCIES,
        ServerSettingsKeys.CLEAN_FREQUENCIES,
        ServerSettingsKeys.SHOW_TUNED_COUNT,
        ServerSettingsKeys.SHOW_TRANSMITTER_NAME,
        ServerSettingsKeys.TRANSMISSION_LOG_ENABLED,
        ServerSettingsKeys.TRANSMISSION_LOG_RETENTION
    };

    /// <summary>
    ///     Settings stored in the "[Server Settings]" section of server.cfg. They are NEVER sent to clients, even if
    ///     somebody moves them into "[General Settings]" by hand.
    /// </summary>
    public static readonly IReadOnlyCollection<ServerSettingsKeys> PrivateKeys = new HashSet<ServerSettingsKeys>
    {
        ServerSettingsKeys.SERVER_PORT,
        ServerSettingsKeys.SERVER_IP,
        ServerSettingsKeys.UPNP_ENABLED,
        ServerSettingsKeys.CLIENT_EXPORT_FILE_PATH,
        ServerSettingsKeys.HTTP_SERVER_ENABLED,
        ServerSettingsKeys.HTTP_SERVER_PORT,
        ServerSettingsKeys.HTTP_SERVER_API_KEY,
        ServerSettingsKeys.HTTP_SERVER_ADDRESS,
        ServerSettingsKeys.SERVER_PASSWORD
    };

    /// <summary>Settings whose values are secrets and must be masked in any settings dump or log.</summary>
    public static readonly IReadOnlyCollection<ServerSettingsKeys> SecretKeys = new HashSet<ServerSettingsKeys>
    {
        ServerSettingsKeys.SERVER_PASSWORD,
        ServerSettingsKeys.HTTP_SERVER_API_KEY
    };
}
