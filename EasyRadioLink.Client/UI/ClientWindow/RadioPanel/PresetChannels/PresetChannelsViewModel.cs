using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Caliburn.Micro;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Client.Settings.RadioChannels;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;

namespace EasyRadioLink.Client.UI.ClientWindow.RadioPanel.PresetChannels;

/// <summary>
///     Preset channels of one user radio (1..10): the radio's preset file (<c>&lt;radio name&gt;.txt</c> in the
///     presets folder) and/or the server's presets, depending on the profile setting <c>ServerPresetSelection</c>.
///     One instance per radio lives in <c>ClientStateSingleton.FixedChannels[radioId - 1]</c>;
///     <see cref="Min" />/<see cref="Max" /> are set by <c>RadioStateSyncService</c> when the radios are loaded.
/// </summary>
public class PresetChannelsViewModel : INotifyPropertyChanged, IHandle<ProfileChangedMessage>,
    IHandle<ServerSettingsPresetsSettingChangedMessage>
{
    private readonly IPresetChannelsStore _channelsStore;

    private readonly object _presetChannelLock = new();
    private readonly ProfileSettingsStore _profileSettings = GlobalSettingsStore.Instance.ProfileSettingsStore;
    private ObservableCollection<PresetChannel> _presetChannels;
    private int _radioId;
    private Visibility _showPresetCreate = Visibility.Visible;

    public PresetChannelsViewModel(IPresetChannelsStore channels, int radioId)
    {
        _radioId = radioId;
        _channelsStore = channels;
        ReloadCommand = new DelegateCommand(OnReload);
        DropDownClosedCommand = new DelegateCommand(DropDownClosed);
        PresetChannels = new ObservableCollection<PresetChannel>();
        PresetCreateCommand = new DelegateCommand(CreatePreset);
        EventBus.Instance.SubscribeOnUIThread(this);
    }

    public ICommand DropDownClosedCommand { get; }

    public DelegateCommand PresetCreateCommand { get; set; }

    public Visibility ShowPresetCreate
    {
        get => _showPresetCreate;
        set
        {
            _showPresetCreate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPresetCreate)));
        }
    }

    public ObservableCollection<PresetChannel> PresetChannels
    {
        get => _presetChannels;
        set
        {
            _presetChannels = value;
            BindingOperations.EnableCollectionSynchronization(_presetChannels, _presetChannelLock);
        }
    }

    public int RadioId
    {
        private get => _radioId;
        set
        {
            _radioId = value;
            Reload();
        }
    }

    public ICommand ReloadCommand { get; }

    public PresetChannel SelectedPresetChannel { get; set; }

    // frequency range of the radio in Hz - channels outside it are not offered
    public double Max { get; set; }
    public double Min { get; set; }

    public event PropertyChangedEventHandler PropertyChanged;

    public Task HandleAsync(ProfileChangedMessage message, CancellationToken cancellationToken)
    {
        ReloadCommand.Execute(null);
        return Task.CompletedTask;
    }

    public Task HandleAsync(ServerSettingsPresetsSettingChangedMessage message, CancellationToken cancellationToken)
    {
        ReloadCommand.Execute(null);
        return Task.CompletedTask;
    }

    private void DropDownClosed()
    {
        if (SelectedPresetChannel?.Value is double frequency && frequency > 0 && RadioId > 0)
            RadioHelper.SelectRadioChannel(SelectedPresetChannel, RadioId);
    }

    /// <summary>
    ///     Reads the preset channels again. Also called from the radio state sync thread: the collection is only
    ///     changed under the lock given to <c>BindingOperations.EnableCollectionSynchronization</c>.
    /// </summary>
    public void Reload()
    {
        Clear();
        ShowPresetCreate = Visibility.Collapsed;

        var radios = ClientStateSingleton.Instance.PlayerRadioInfo.radios;

        // user radios only (slot 0 is reserved)
        if (_radioId < PlayerRadioInfo.FirstUserRadio || _radioId >= radios.Length) return;

        var radio = radios[_radioId];
        if (radio == null || !radio.IsEnabled) return;

        if (!Enum.TryParse(_profileSettings.GetClientSettingString(ProfileSettingsKeys.ServerPresetSelection),
                out ServerPresetConfiguration presetConfiguration))
            presetConfiguration = ServerPresetConfiguration.USE_CLIENT_AND_SERVER_IF_SET;

        var clientChannels = new List<PresetChannel>();
        var serverChannels = new List<PresetChannel>();

        foreach (var channel in _channelsStore.LoadFromStore(radio.name))
            if (channel.Value is double channelFrequency
                && channelFrequency <= Max
                && channelFrequency >= Min)
                clientChannels.Add(channel);

        foreach (var channel in SyncedServerSettings.Instance.GetPresetChannels(
                     FilePresetChannelsStore.NormaliseString(radio.name)))
        {
            var freq = Math.Round(channel.Frequency * RadioCalculator.MHz); //convert to Hz from MHz
            if (freq <= Max && freq >= Min)
                serverChannels.Add(new PresetChannel
                {
                    Value = freq,
                    Text = channel.Name
                });
        }

        List<PresetChannel> channels = presetConfiguration switch
        {
            ServerPresetConfiguration.USE_CLIENT_ONLY => clientChannels,
            // the server's channels if it has any for this radio, otherwise the own ones
            ServerPresetConfiguration.USE_SERVER_ONLY_IF_SET => serverChannels.Count > 0
                ? serverChannels
                : clientChannels,
            // both - own channels first
            _ => [.. clientChannels, .. serverChannels]
        };

        var channelNumber = 1;
        foreach (var presetChannel in channels)
        {
            presetChannel.Channel = channelNumber++;
            NameChannel(presetChannel);
        }

        lock (_presetChannelLock)
        {
            foreach (var presetChannel in channels) PresetChannels.Add(presetChannel);
        }

        ShowPresetCreate = channels.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void NameChannel(PresetChannel presetChannel)
    {
        presetChannel.Text = presetChannel.Channel + ": " + presetChannel.Text;
    }

    private void OnReload()
    {
        Reload();
    }

    private void CreatePreset()
    {
        var radios = ClientStateSingleton.Instance.PlayerRadioInfo.radios;

        if (_radioId < PlayerRadioInfo.FirstUserRadio || _radioId >= radios.Length) return;

        var radio = radios[_radioId];

        if (radio == null || !radio.IsEnabled) return;

        var path = _channelsStore.CreatePresetFile(radio.name);
        if (path == null) return;

        var result = MessageBox.Show(string.Format(Properties.Resources.MsgBoxPresetCreatedText, path),
            Properties.Resources.MsgBoxPresetCreated, MessageBoxButton.YesNo, MessageBoxImage.Information,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // no editor registered for .txt - the path was shown above
        }
    }

    public void Clear()
    {
        lock (_presetChannelLock)
        {
            PresetChannels.Clear();
        }
    }
}
