using System;
using System.Net;
using System.Text.Json.Serialization;
using EasyRadioLink.Common.Helpers;

namespace EasyRadioLink.Common.Models.Player;

/// <summary>
///     A connected user as seen on the wire: <c>{"ClientGuid","Name","AllowRecord","RadioInfo"}</c>.
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

    [JsonIgnore] public string AllowRecordingStatus => AllowRecord ? "R" : "-";

    [JsonIgnore] public bool Muted { get; set; }

    [JsonIgnore] public IPEndPoint VoipPort { get; set; }

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
            ClientGuid = ClientGuid
        };
    }
}
