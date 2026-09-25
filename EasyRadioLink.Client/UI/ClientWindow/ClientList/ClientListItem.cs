using System.Windows.Input;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;

namespace EasyRadioLink.Client.UI.ClientWindow.ClientList;

/// <summary>One row of the user list: name, recording allowed, muted (muting is local - you no longer hear that user).</summary>
public class ClientListItem : ClientInfo
{
    public ClientListItem(ClientInfo client)
    {
        AllowRecord = client.AllowRecord;
        Name = client.Name;
        ClientGuid = client.ClientGuid;
        LastTransmissionReceived = client.LastTransmissionReceived;
        Muted = client.Muted;
        RadioInfo = client.RadioInfo;
        TransmittingFrequency = client.TransmittingFrequency;

        ToggleMute = new DelegateCommand(ToggleClientMute);
    }

    public ICommand ToggleMute { get; }

    /// <summary>True for the own entry.</summary>
    public bool IsSelf => ClientGuid == ClientStateSingleton.Instance.ShortGUID;

    /// <summary>The own entry can't be muted.</summary>
    public bool CanMute => !IsSelf;

    /// <summary>Name, "(you)" appended for the own entry.</summary>
    public string DisplayName => IsSelf ? string.Format(Properties.Resources.ClientListYou, Name) : Name;

    public string RecordingText => AllowRecord ? Properties.Resources.ValueYes : Properties.Resources.ValueNo;

    public string IsMuted => Muted ? Properties.Resources.ClientListMutedValue : "";

    public string MuteButtonText => Muted ? Properties.Resources.ClientListUnmute : Properties.Resources.ClientListMute;

    public void ToggleClientMute()
    {
        if (!IsSelf && ConnectedClientsSingleton.Instance.TryGetValue(ClientGuid, out var client))
        {
            client.Muted = !client.Muted;
            Muted = !Muted;
            NotifyPropertyChanged(nameof(Muted));
            NotifyPropertyChanged(nameof(IsMuted));
            NotifyPropertyChanged(nameof(MuteButtonText));
        }
    }
}
