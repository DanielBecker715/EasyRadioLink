using System;
using System.Net;
using System.Text.Json.Serialization;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Network.Crypto;

namespace EasyRadioLink.Common.Models.Player;

/// <summary>
///     A connected user as seen on the wire: <c>{"ClientGuid","Name","AllowRecord","RadioInfo","E2EPublicKey"}</c>.
///     Everything marked <see cref="JsonIgnoreAttribute" /> is local runtime state of the server/client.
/// </summary>
public class ClientInfo : PropertyChangedBaseClass
{
    private string _name = "";

    // Used by server client list to display last frequency client transmitted on
    [JsonIgnore] private string _transmittingFrequency;

    public string ClientGuid { get; set; }

    public string Name
    {
        get => _name;
        set
        {
            if (value == null || value == "") value = "---";

            if (_name != value)
            {
                _name = value;
                NotifyPropertyChanged();
            }
        }
    }

    public bool AllowRecord { get; set; }

    public PlayerRadioInfoBase RadioInfo { get; set; }

    /// <summary>
    ///     The client's end-to-end voice public key (<see cref="E2EKeyPair.PublicKey" />: base64 DER SubjectPublicKeyInfo,
    ///     ECDH P-256, new on every app start). Sent in the SYNC hello, checked for its format by the server, fixed for the
    ///     connection and announced with the client list; senders wrap their transmission keys for it.
    /// </summary>
    public string E2EPublicKey { get; set; }

    [JsonIgnore] public string AllowRecordingStatus => AllowRecord ? "R" : "-";

    [JsonIgnore] public bool Muted { get; set; }

    [JsonIgnore] public IPEndPoint VoipPort { get; set; }

    /// <summary>
    ///     Server only: IP address of the TCP connection this client authenticated on. UDP voice and pings for this
    ///     client are only accepted from this address.
    /// </summary>
    [JsonIgnore] public IPAddress SessionAddress { get; set; }

    // server side: voice packet rate limit (see VoiceRouting.AllowVoicePacket)
    [JsonIgnore] internal long VoiceWindowStartTicks { get; set; }
    [JsonIgnore] internal int VoicePacketsInWindow { get; set; }

    /// <summary>
    ///     Server only: the UDP hop encryption of this client (its key was sent in the client's SYNC reply). Never
    ///     serialised, never copied, disposed when the client disconnects.
    /// </summary>
    [JsonIgnore] internal UdpTransportSession UdpTransport { get; set; }

    [JsonIgnore]
    public string TransmittingFrequency
    {
        get => _transmittingFrequency;
        set
        {
            if (_transmittingFrequency != value)
            {
                _transmittingFrequency = value;
                NotifyPropertyChanged();
            }
        }
    }

    // Used by server client list to remove last frequency client transmitted on after threshold
    [JsonIgnore] public DateTime LastTransmissionReceived { get; set; }

    [JsonIgnore] public Guid ClientSession { get; set; }

    public override string ToString()
    {
        return $"{(string.IsNullOrEmpty(Name) ? "Unknown" : Name)} ({ClientGuid})";
    }

    /// <summary>Compares the metadata that is sent with an UPDATE message (radio info is ignored).</summary>
    public bool MetaDataEquals(ClientInfo other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;

        return Name == other.Name
               && AllowRecord == other.AllowRecord
               && ClientGuid == other.ClientGuid;
    }

    public ClientInfo DeepClone()
    {
        return new ClientInfo
        {
            RadioInfo = RadioInfo?.DeepClone(),
            Name = Name,
            AllowRecord = AllowRecord,
            ClientGuid = ClientGuid,
            E2EPublicKey = E2EPublicKey
        };
    }
}
