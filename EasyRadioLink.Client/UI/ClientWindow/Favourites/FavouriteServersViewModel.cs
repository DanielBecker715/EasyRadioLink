using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using EasyRadioLink.Client.Settings.Favourites;
using EasyRadioLink.Client.Properties;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Settings;

namespace EasyRadioLink.Client.UI.ClientWindow.Favourites;

/// <summary>
///     Favourites tab: saved servers (name, address, optional password), drag to reorder, one default server.
///     Stored by <see cref="IFavouriteServerStore" /> (FavouriteServers.csv in the config folder).
/// </summary>
public class FavouriteServersViewModel : PropertyChangedBaseClass
{
    private readonly IFavouriteServerStore _favouriteServerStore;
    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;

    public FavouriteServersViewModel(IFavouriteServerStore favouriteServerStore)
    {
        _favouriteServerStore = favouriteServerStore;

        Addresses.CollectionChanged += AddressesCollectionChanged;

        foreach (var favourite in _favouriteServerStore.LoadFromStore()) Addresses.Add(favourite);

        NewAddressCommand = new DelegateCommand(OnNewAddress);
        RemoveSelectedCommand = new DelegateCommand(OnRemoveSelected);
        OnDefaultChangedCommand = new DelegateCommand(SetDefaultAddress);
    }

    public ObservableCollection<ServerAddress> Addresses { get; } = new();

    public string NewName { get; set; }

    public string NewAddress { get; set; }

    public string NewPassword { get; set; }

    public ICommand NewAddressCommand { get; }

    public ICommand RemoveSelectedCommand { get; set; }

    public ICommand OnDefaultChangedCommand { get; set; }

    public ServerAddress SelectedItem { get; set; }

    /// <summary>The favourite flagged as default (filled in at start-up if the last server is not a favourite), or null.</summary>
    public ServerAddress DefaultServerAddress => Addresses.FirstOrDefault(x => x.IsDefault);

    private void OnNewAddress()
    {
        var address = NewAddress?.Trim();
        if (string.IsNullOrEmpty(address)) return;

        var name = string.IsNullOrWhiteSpace(NewName) ? address : NewName.Trim();

        var isDefault = Addresses.Count == 0;
        Addresses.Add(new ServerAddress(name, address,
            string.IsNullOrWhiteSpace(NewPassword) ? null : NewPassword, isDefault));

        Save();

        // ready for the next one
        NewName = "";
        NewAddress = "";
        NewPassword = "";
    }

    private void OnRemoveSelected()
    {
        if (SelectedItem == null) return;

        Addresses.Remove(SelectedItem);

        if (Addresses.Count == 0 &&
            !string.IsNullOrEmpty(_globalSettings.GetClientSetting(GlobalSettingsKeys.LastServer).StringValue))
        {
            var oldAddress = new ServerAddress(
                _globalSettings.GetClientSetting(GlobalSettingsKeys.LastServer).StringValue,
                _globalSettings.GetClientSetting(GlobalSettingsKeys.LastServer).StringValue, null, true);
            Addresses.Add(oldAddress);
        }

        Save();
    }

    private void Save()
    {
        var saveSucceeded = _favouriteServerStore.SaveToStore(Addresses);
        if (!saveSucceeded)
            MessageBox.Show(Application.Current.MainWindow,
                Resources.MsgBoxFavSaveFailedText,
                Resources.MsgBoxFavSaveFailed,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
    }

    private void SetDefaultAddress(object obj)
    {
        var address = obj as ServerAddress;
        if (address == null) throw new InvalidOperationException();

        if (address.IsDefault) return;

        address.IsDefault = true;

        foreach (var serverAddress in Addresses)
            if (serverAddress != address)
                serverAddress.IsDefault = false;

        Save();
    }

    private void AddressesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (ServerAddress address in e.NewItems)
                address.PropertyChanged += ServerAddressPropertyChanged;

        if (e.OldItems != null)
            foreach (ServerAddress address in e.OldItems)
                address.PropertyChanged -= ServerAddressPropertyChanged;

        if (e.Action == NotifyCollectionChangedAction.Move) Save();
    }

    private void ServerAddressPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        // Saving after changing the default favourite is done by SetDefaultAddress
        if (e.PropertyName != "IsDefault") Save();
    }
}