using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Common.Models;

/// <summary>
///     TCP wire message: one JSON object per line (UTF-8, terminated by '\n').
///     Property names are case sensitive and enums travel as integers - never renumber <see cref="MessageType" />.
/// </summary>
public class NetworkMessage
{
    public enum MessageType
    {
        UPDATE = 0, // metadata update (Name, AllowRecord) - no radio information
        PING = 1,
        SYNC = 2, // C->S: hello {Client, Password, Version, Product}; S->C: {Clients, ServerSettings, Version, Product}
        RADIO_UPDATE = 3, // full client state incl. RadioInfo
        SERVER_SETTINGS = 4, // S->C push; C->S request (answered to the requester only)
        CLIENT_DISCONNECT = 5, // S->C: a client left
        VERSION_MISMATCH = 6, // S->C: unsupported product/protocol version, the server closes the connection
        AUTH_FAILED = 7 // S->C: wrong or missing server password, the server closes the connection
    }

    private static readonly JsonSerializerOptions EncodeOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { JsonNetworkPropertiesResolver.StripNetworkIgnored } // strip out things not required for the TCP sync
        },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true
    };

    private static readonly JsonSerializerOptions DecodeOptions = new()
    {
        IncludeFields = true
    };

    public ClientInfo Client { get; set; }

    public MessageType MsgType { get; set; }

    public List<ClientInfo> Clients { get; set; }

    public Dictionary<string, string> ServerSettings { get; set; }

    /// <summary>Server password - only set by the client in its SYNC hello. Never sent by the server.</summary>
    public string Password { get; set; }

    /// <summary>Protocol version (<see cref="AppVersion.ProtocolVersion" />), set by <see cref="Encode" />.</summary>
    public string Version { get; set; }

    /// <summary>Product marker (<see cref="AppVersion.Product" />), set by <see cref="Encode" />.</summary>
    public string Product { get; set; }

    /// <summary>Serialises the message as one JSON line (including the trailing '\n').</summary>
    public string Encode()
    {
        Version = AppVersion.ProtocolVersion;
        Product = AppVersion.Product;
        return JsonSerializer.Serialize(this, EncodeOptions) + "\n";
    }

    /// <summary>Parses one JSON line. Throws <see cref="JsonException" /> for invalid JSON.</summary>
    public static NetworkMessage Decode(string json)
    {
        return JsonSerializer.Deserialize<NetworkMessage>(json, DecodeOptions);
    }
}
