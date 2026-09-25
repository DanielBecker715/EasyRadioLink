using System.ComponentModel;
using System.Windows;
using Caliburn.Micro;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Server.Properties;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Server.UI.ClientAdmin;

public class ClientViewModel : Screen
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IEventAggregator _eventAggregator;

    public ClientViewModel(ClientInfo client, IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator;
        Client = client;
        Client.PropertyChanged += ClientOnPropertyChanged;
    }

    public ClientInfo Client { get; }

    public string ClientName => Client.Name;

    public string TransmittingFrequency => Client.TransmittingFrequency;

    public bool ClientMuted => Client.Muted;

    private void ClientOnPropertyChanged(object sender, PropertyChangedEventArgs propertyChangedEventArgs)
    {
        if (propertyChangedEventArgs.PropertyName == nameof(ClientInfo.Name))
            NotifyOfPropertyChange(() => ClientName);
        else if (propertyChangedEventArgs.PropertyName == nameof(ClientInfo.TransmittingFrequency))
            NotifyOfPropertyChange(() => TransmittingFrequency);
    }

    public void KickClient()
    {
        var messageBoxResult = MessageBox.Show(string.Format(Resources.MsgBoxKick, Client.Name),
            Resources.MsgBoxKickTitle,
            MessageBoxButton.YesNo);
        if (messageBoxResult == MessageBoxResult.Yes)
            _eventAggregator.PublishOnBackgroundThreadAsync(new KickClientMessage(Client));
    }

    public void BanClient()
    {
        var messageBoxResult = MessageBox.Show(string.Format(Resources.MsgBoxBan, Client.Name),
            Resources.MsgBoxBanTitle,
            MessageBoxButton.YesNo);
        if (messageBoxResult == MessageBoxResult.Yes)
            _eventAggregator.PublishOnBackgroundThreadAsync(new BanClientMessage(Client));
    }

    public void ToggleClientMute()
    {
        Client.Muted = !Client.Muted;
        NotifyOfPropertyChange(() => ClientMuted);
    }
}
