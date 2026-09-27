using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;
using Caliburn.Micro;
using EasyRadioLink.Client.Audio.Managers;
using EasyRadioLink.Client.GameIntegration;
using EasyRadioLink.Client.Properties;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Client.Settings.Favourites;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Client.UI.ClientWindow.ClientList;
using EasyRadioLink.Client.UI.ClientWindow.Favourites;
using EasyRadioLink.Client.UI.ClientWindow.RadioPanel;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Utility;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Client;
using EasyRadioLink.Common.Network.Crypto;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using NLog;
using Application = System.Windows.Application;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Client.UI.ClientWindow;

/// <summary>
///     Main window: connection (name, server, optional password), audio devices, windows.
///     <para>
///         Connect flow: <see cref="ConnectAsync" /> creates the <see cref="TCPClientHandler" /> (with the server
///         password) and shows "Connecting..."; <c>TCPClientStatusMessage(true)</c> arrives after the server accepted
///         the handshake - then audio and the <see cref="RadioStateSyncService" /> are started (the radio becomes usable)
///         and the radio window is opened if <c>AutoOpenRadioPanel</c> is set. Any disconnect stops both again; a
///         wrong password, an incompatible server or an unreachable server is reported with a dialog. A server whose
///         identity differs from the pinned one (known-servers.json) is not connected to: the user sees both
///         fingerprints and may trust the new identity and connect again.
///     </para>
/// </summary>
public class MainWindowViewModel : PropertyChangedBaseClass, IHandle<TCPClientStatusMessage>,
    IHandle<VOIPStatusMessage>, IHandle<ProfileChangedMessage>, IHandle<InvalidServerVersionMessage>,
    IHandle<ToggleRadioPanelMessage>
{
    private static readonly long RADIO_PANEL_DEBOUNCE = 500;

    // re-enable the connect button if the old connection never reports its disconnect
    private static readonly TimeSpan DisconnectWaitTimeout = TimeSpan.FromSeconds(3);

    private readonly AudioManager _audioManager;

    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;

    private readonly DispatcherTimer _updateTimer;
    private readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private AudioPreview _audioPreview;

    // a user initiated disconnect is waiting for the TCPClientStatusMessage(false) of the old connection
    private volatile bool _awaitingDisconnect;
    private TCPClientHandler _client;

    private ClientListWindow _clientListWindow;

    // "host:port" of the connection attempt - for error dialogs
    private string _connectAddress;

    // protocol version reported by an incompatible server (InvalidServerVersionMessage)
    private string _incompatibleServerVersion;

    private long _lastRadioPanelToggleTime;
    private RadioPanelWindow _radioPanel;
    private RadioStateSyncService _radioSync;
    private ServerAddress _selectedServerAddress;

    private ServerSettingsWindow.ServerSettingsWindow _serverSettingsWindow;

    public MainWindowViewModel()
    {
        _audioManager = new AudioManager(AudioOutput.WindowsN);

        PreviewCommand = new DelegateCommand(PreviewAudio);

        ConnectCommand = new DelegateCommand(async () => await ConnectAsync());

        RadioPanelCommand = new DelegateCommand(ToggleRadioPanel);

        //TODO might not need to do this - should be triggered by notifyproperty
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _updateTimer.Tick += UpdatePlayerCountAndVUMeters;
        _updateTimer.Start();

        EventBus.Instance.SubscribeOnUIThread(this);

        ServerSettingsCommand = new DelegateCommand(ToggleServerSettings);

        ClientListCommand = new DelegateCommand(ToggleClientList);

        // detects supported games (Microsoft Flight Simulator 2024) and links the radio with them
        GameIntegrationManager.Instance.Start();
    }

    public ICommand ClientListCommand { get; set; }

    public ICommand ServerSettingsCommand { get; set; }

    /// <summary>Shows / hides the radio window (button "Show Radio", hotkey InputBinding.RadioPanelToggle).</summary>
    public DelegateCommand RadioPanelCommand { get; set; }

    public ClientStateSingleton ClientState { get; } = ClientStateSingleton.Instance;
    public ConnectedClientsSingleton Clients { get; } = ConnectedClientsSingleton.Instance;
    public AudioInputSingleton AudioInput { get; } = AudioInputSingleton.Instance;
    public AudioOutputSingleton AudioOutput { get; } = AudioOutputSingleton.Instance;

    /// <summary>Connected: the server accepted the handshake (radio and audio are running).</summary>
    public bool IsConnected { get; set; }

    /// <summary>A connection attempt is running (between Connect and the server's answer).</summary>
    public bool IsConnecting { get; set; }

    //WPF cant invert a binding - this controls the input boxes for name, address and password
    public bool IsNotConnected => !IsConnected && !IsConnecting;

    public bool IsVoIPConnected { get; set; }

    public DelegateCommand ConnectCommand { get; set; }

    public ICommand PreviewCommand { get; set; }

    public bool PreviewEnabled => AudioInput.MicrophoneAvailable && !IsConnected && !IsConnecting;

    public float SpeakerVU
    {
        get
        {
            if (_audioPreview != null && _audioPreview.IsPreviewing)
                return _audioPreview.SpeakerMax;
            if (_audioManager != null) return _audioManager.SpeakerMax;

            return -100;
        }
    }

    public float MicVU
    {
        get
        {
            if (_audioPreview != null && _audioPreview.IsPreviewing)
                return _audioPreview.MicMax;
            if (_audioManager != null) return _audioManager.MicMax;

            return -100;
        }
    }


    public string PreviewText
    {
        get
        {
            if (_audioPreview == null || !_audioPreview.IsPreviewing || IsConnected)
                return Resources.PreviewAudio;
            return Resources.PreviewAudioStop;
        }
    }

    public string ConnectText
    {
        get
        {
            if (IsConnected)
                return Resources.StartStopDisconnect;
            if (IsConnecting)
                return Resources.StartStopConnecting;
            return Resources.StartStop;
        }
    }

    public double SpeakerBoost
    {
        get
        {
            var boost = _globalSettings.GetClientSetting(GlobalSettingsKeys.SpeakerBoost).DoubleValue;
            _audioManager.SpeakerBoost = VolumeConversionHelper.ConvertVolumeSliderToScale((float)boost);
            if (_audioPreview != null) _audioPreview.SpeakerBoost = _audioManager.SpeakerBoost;
            return boost;
        }
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.SpeakerBoost,
                value.ToString(CultureInfo.InvariantCulture));
            _audioManager.SpeakerBoost = VolumeConversionHelper.ConvertVolumeSliderToScale((float)value);

            if (_audioPreview != null) _audioPreview.SpeakerBoost = _audioManager.SpeakerBoost;
        }
    }

    public string SpeakerBoostText =>
        VolumeConversionHelper.ConvertLinearDiffToDB(
            VolumeConversionHelper.ConvertVolumeSliderToScale((float)SpeakerBoost));

    /// <summary>Favourite picked in the server drop down: fills the address and the password.</summary>
    public ServerAddress SelectedServerAddress
    {
        get => _selectedServerAddress;
        set
        {
            _selectedServerAddress = value;

            // the drop down pushes null when the selected favourite is removed (and when it is cleared below)
            if (value == null) return;

            ServerAddress = value.Address;
            ServerPassword = value.Password ?? "";

            // the drop down is only a picker: clear it again, so picking the same favourite again (e.g. after the
            // address was edited by hand) fills in its address and password again
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_selectedServerAddress, value)) return;

                _selectedServerAddress = null;
                NotifyPropertyChanged(nameof(SelectedServerAddress));
            });
        }
    }

    /// <summary>
    ///     Fills in the favourite of the last server (with its password), or else the favourite flagged as default.
    ///     Called once at start-up.
    /// </summary>
    public void SelectStartFavourite()
    {
        var favourites = FavouriteServersViewModel?.Addresses;
        if (favourites == null || favourites.Count == 0) return;

        var lastServer = ServerAddress.Trim();
        var favourite = favourites.FirstOrDefault(address =>
                            string.Equals(address.Address?.Trim(), lastServer, StringComparison.OrdinalIgnoreCase))
                        ?? FavouriteServersViewModel.DefaultServerAddress;

        if (favourite != null) SelectedServerAddress = favourite;
    }

    /// <summary>Password of the server to connect to ("" = open server). Not persisted - comes from a favourite.</summary>
    public string ServerPassword { get; set; } = "";

    public string ServerAddress
    {
        get
        {
            var savedAddress = _globalSettings.GetClientSetting(GlobalSettingsKeys.LastServer)?.RawValue;

            if (string.IsNullOrWhiteSpace(savedAddress))
                return "127.0.0.1:" + DefaultServerSettings.DEFAULT_SERVER_PORT;
            return savedAddress;
        }
        set
        {
            if (value != null)
            {
                _globalSettings.SetClientSetting(GlobalSettingsKeys.LastServer, value.Trim());
                NotifyPropertyChanged();
            }
        }
    }

    /// <summary>The user's display name (global.cfg LastSeenName).</summary>
    public string DisplayName
    {
        get => ClientState.LastSeenName;
        set
        {
            if (value != null) ClientState.LastSeenName = value;
        }
    }

    /// <summary>Name used when the name box is left empty (watermark of the name box).</summary>
    public string DefaultDisplayName => ClientStateSingleton.DefaultName;

    /// <summary>"Users (3)" - text of the button that opens the user list.</summary>
    public string UsersButtonText => string.Format(Resources.ShowClientList, Clients.Total);

    /// <summary>Radio sounds the microphone preview can be played through.</summary>
    public IReadOnlyList<RadioModelInfo> PreviewModels
    {
        get
        {
            try
            {
                return RadioModelFactory.Instance.AvailableModels;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unable to load the radio models");
                return Array.Empty<RadioModelInfo>();
            }
        }
    }

    /// <summary>Radio sound (RadioModelInfo.Key) of the microphone preview.</summary>
    public string PreviewModelKey { get; set; } = RadioModelFactory.DefaultModelKey;

    public bool AudioSettingsEnabled
    {
        get
        {
            if ((_audioPreview != null && _audioPreview.IsPreviewing) || IsConnected || IsConnecting) return false;

            return true;
        }
    }

    public string CurrentProfile => _globalSettings.ProfileSettingsStore.CurrentProfileName;

    public bool ConnectIsEnabled { get; set; } = true;
    public FavouriteServersViewModel FavouriteServersViewModel { get; set; }

    public Task HandleAsync(InvalidServerVersionMessage message, CancellationToken cancellationToken)
    {
        // shown together with the MISMATCHED_SERVER disconnect that follows
        _incompatibleServerVersion = message.ServerVersion;
        return Task.CompletedTask;
    }

    public Task HandleAsync(ProfileChangedMessage message, CancellationToken cancellationToken)
    {
        NotifyPropertyChanged(nameof(CurrentProfile));
        return Task.CompletedTask;
    }

    public async Task HandleAsync(TCPClientStatusMessage obj, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            if (obj.Connected)
                OnConnected(obj.Address, obj.UdpKey, obj.VoiceSession);
            else
                OnDisconnected(obj.Error, obj.IdentityMismatch, obj.ErrorDetail);
        }, cancellationToken);
    }

    public Task HandleAsync(ToggleRadioPanelMessage message, CancellationToken cancellationToken)
    {
        if (TimeSpan.FromTicks(DateTime.Now.Ticks - _lastRadioPanelToggleTime).TotalMilliseconds > RADIO_PANEL_DEBOUNCE)
        {
            _lastRadioPanelToggleTime = DateTime.Now.Ticks;
            //Debounce
            // Even though it should be the UI thread - it wasnt working so this forces the UI / STA thread
            Application.Current.Dispatcher.Invoke(DispatcherPriority.Normal, new Action(ToggleRadioPanel));
        }

        return Task.CompletedTask;
    }

    public Task HandleAsync(VOIPStatusMessage message, CancellationToken cancellationToken)
    {
        IsVoIPConnected = message.Connected;
        return Task.CompletedTask;
    }

    private void OnConnected(IPEndPoint address, UdpTransportKey udpKey, E2EVoiceSession voiceSession)
    {
        if (_client == null || !IsConnecting)
        {
            // late message of a connection that was cancelled in the meantime
            Logger.Warn("Ignoring connected message - no connection attempt running");
            return;
        }

        var client = _client;

        IsConnecting = false;
        IsConnected = true;
        ConnectIsEnabled = true;

        //connection sound
        if (_globalSettings.GetClientSettingBool(GlobalSettingsKeys.PlayConnectionSounds))
            try
            {
                Sounds.BeepConnected.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to play connect sound");
            }

        if (address == null || udpKey == null || voiceSession == null)
        {
            Logger.Error("TCPClientStatusMessage - Connect sent without address, UDP key or voice encryption - disconnecting");
            Stop();
            return;
        }

        // on the UI thread like the Disconnect button (Stop): a disconnect right after connecting must not leave audio
        // and the radio sync running without a connection
        var started = false;
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(client, _client) || !IsConnected)
            {
                Logger.Info("Disconnected while the connection was set up - not starting audio and the radio");
                return;
            }

            if (!StartAudio(address, udpKey, voiceSession))
            {
                Stop();
                return;
            }

            // switches the radio on (remembered frequency) and keeps the server up to date
            _radioSync?.Stop();
            _radioSync = new RadioStateSyncService();
            _radioSync.Start();

            started = true;
        });

        if (!started) return;

        if (_globalSettings.GetClientSettingBool(GlobalSettingsKeys.AutoOpenRadioPanel))
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (IsConnected && (_radioPanel == null || !_radioPanel.IsVisible)) ToggleRadioPanel();
            });
    }

    private void OnDisconnected(TCPClientStatusMessage.ErrorCode error, ServerIdentityMismatch identityMismatch,
        string errorDetail)
    {
        if (_awaitingDisconnect)
        {
            // the old connection finished after the user disconnected - everything is already stopped
            _awaitingDisconnect = false;
            ConnectIsEnabled = true;
            return;
        }

        var wasConnecting = IsConnecting;
        var address = _connectAddress;
        var serverVersion = _incompatibleServerVersion;

        Stop(false);

        switch (error)
        {
            case TCPClientStatusMessage.ErrorCode.AUTH_FAILED:
                ShowConnectionError(Resources.MsgBoxAuthFailed, Resources.MsgBoxAuthFailedHeading,
                    string.Format(Resources.MsgBoxAuthFailedText, address));
                break;
            case TCPClientStatusMessage.ErrorCode.MISMATCHED_SERVER:
                ShowConnectionError(Resources.MsgBoxServerMismatch, Resources.MsgBoxServerMismatchHeading,
                    string.Format(Resources.MsgBoxServerMismatchText, address,
                        string.IsNullOrWhiteSpace(serverVersion) ? Resources.ValueUnknown : serverVersion,
                        AppVersion.ProtocolVersion, AppVersion.MinimumProtocolVersion));
                break;
            case TCPClientStatusMessage.ErrorCode.SERVER_IDENTITY_CHANGED when identityMismatch != null:
                ShowServerIdentityChanged(identityMismatch, address);
                break;
            case TCPClientStatusMessage.ErrorCode.SERVER_IDENTITY_UNKNOWN when identityMismatch != null:
                ShowServerIdentityUnknown(identityMismatch, address);
                break;
            case TCPClientStatusMessage.ErrorCode.IDENTITY_CHECK_FAILED:
                ShowIdentityCheckFailed(address, errorDetail);
                break;
            case TCPClientStatusMessage.ErrorCode.INVALID_SERVER:
                ShowConnectionError(Resources.MsgBoxConnectFailed, Resources.MsgBoxInvalidServerHeading,
                    string.Format(Resources.MsgBoxInvalidServerText, address,
                        DefaultServerSettings.DEFAULT_SERVER_PORT));
                break;
            case TCPClientStatusMessage.ErrorCode.TIMEOUT:
                // lost connections are shown by the status icon - only report a failed connection attempt
                if (wasConnecting)
                    ShowConnectionError(Resources.MsgBoxConnectFailed, Resources.MsgBoxConnectFailedHeading,
                        string.Format(Resources.MsgBoxConnectFailedText, address,
                            DefaultServerSettings.DEFAULT_SERVER_PORT));
                break;
        }
    }

    private void ShowConnectionError(string caption, string heading, string text)
    {
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await TaskDialog.ShowDialogAsync(new TaskDialogPage
                {
                    Caption = caption,
                    Heading = heading,
                    Text = text,
                    Icon = TaskDialogIcon.Error,
                    Buttons = [TaskDialogButton.OK]
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unable to show the connection error dialog");
            }
        });
    }

    /// <summary>
    ///     The server presented another identity than the pinned one. Shows both fingerprints; only if the user chooses
    ///     "Connect anyway and trust the new identity" the new fingerprint is pinned and the connection is tried again.
    ///     Cancel (the default) keeps the old pin.
    /// </summary>
    private void ShowServerIdentityChanged(ServerIdentityMismatch mismatch, string address)
    {
        ShowTrustIdentityDialog(mismatch, Resources.MsgBoxIdentityChanged, Resources.MsgBoxIdentityChangedHeading,
            string.Format(Resources.MsgBoxIdentityChangedText, address, mismatch.PinnedFingerprint,
                mismatch.PresentedFingerprint), Resources.BtnTrustNewIdentity);
    }

    /// <summary>
    ///     The saved identity of the server (its entry in known-servers.json) is unreadable, so the server could not be
    ///     checked. Shows the presented fingerprint; only if the user chooses to trust it, it is pinned (replacing the
    ///     unreadable entry) and the connection is tried again.
    /// </summary>
    private void ShowServerIdentityUnknown(ServerIdentityMismatch mismatch, string address)
    {
        ShowTrustIdentityDialog(mismatch, Resources.MsgBoxIdentityUnknown, Resources.MsgBoxIdentityUnknownHeading,
            string.Format(Resources.MsgBoxIdentityUnknownText, address, mismatch.PresentedFingerprint),
            Resources.BtnTrustIdentity);
    }

    /// <summary>Asks whether to trust <see cref="ServerIdentityMismatch.PresentedFingerprint" />; Cancel is the default.</summary>
    private void ShowTrustIdentityDialog(ServerIdentityMismatch mismatch, string caption, string heading, string text,
        string trustLabel)
    {
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var trustButton = new TaskDialogButton(trustLabel);
                var cancelButton = TaskDialogButton.Cancel;

                var result = await TaskDialog.ShowDialogAsync(new TaskDialogPage
                {
                    Caption = caption,
                    Heading = heading,
                    Text = text,
                    Icon = TaskDialogIcon.ShieldWarningYellowBar,
                    Buttons = [trustButton, cancelButton],
                    DefaultButton = cancelButton
                });

                if (result != trustButton)
                {
                    Logger.Info($"Did not trust the identity {mismatch.PresentedFingerprint} of {mismatch.ServerKey} - not connecting");
                    return;
                }

                try
                {
                    KnownServersStore.Default.Trust(mismatch.ServerKey, mismatch.PresentedFingerprint);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    // e.g. known-servers.json became unreadable - it is never overwritten then
                    Logger.Error(ex, $"Unable to save the identity of {mismatch.ServerKey}");
                    ShowIdentityCheckFailed(_connectAddress, ex.Message);
                    return;
                }

                Logger.Warn($"The user trusted the identity of {mismatch.ServerKey}: " +
                            $"{mismatch.PresentedFingerprint} (was {mismatch.PinnedFingerprint ?? "unreadable"})");

                if (!IsConnected && !IsConnecting && !_awaitingDisconnect) await ConnectAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unable to show the server identity dialog");
            }
        });
    }

    /// <summary>
    ///     The identity of the server could not be checked (known-servers.json can't be read or is damaged): the
    ///     connection was refused and nothing is offered - the file has to be repaired first.
    /// </summary>
    private void ShowIdentityCheckFailed(string address, string detail)
    {
        ShowConnectionError(Resources.MsgBoxIdentityCheckFailed, Resources.MsgBoxIdentityCheckFailedHeading,
            string.Format(Resources.MsgBoxIdentityCheckFailedText, address,
                string.IsNullOrWhiteSpace(detail) ? Resources.ValueUnknown : detail));
    }

    private void UpdatePlayerCountAndVUMeters(object sender, EventArgs e)
    {
        NotifyPropertyChanged(nameof(SpeakerVU));
        NotifyPropertyChanged(nameof(MicVU));

        ConnectedClientsSingleton.Instance.NotifyAll();
        NotifyPropertyChanged(nameof(UsersButtonText));
    }

    /// <summary>Connect button: connects, or disconnects / cancels a running connection attempt.</summary>
    public async Task ConnectAsync()
    {
        if (IsConnected || IsConnecting)
        {
            Stop();
            return;
        }

        if (_awaitingDisconnect) return;

        ConnectIsEnabled = false;

        //stop preview
        _audioPreview?.StopEncoding();
        _audioPreview = null;
        NotifyPropertyChanged(nameof(PreviewText));

        await ShowMicPassthroughWarningAsync();

        _incompatibleServerVersion = null;
        _connectAddress = ServerAddress.Trim();

        try
        {
            var host = GetAddressFromTextBox();
            var port = GetPortFromTextBox();

            //process hostname
            var resolvedAddresses = await Dns.GetHostAddressesAsync(host);
            var ip = resolvedAddresses.FirstOrDefault(xa =>
                xa.AddressFamily ==
                AddressFamily
                    .InterNetwork); // Ensure we get an IPv4 address in case the host resolves to both IPv6 and IPv4

            if (ip != null)
            {
                IsConnecting = true;

                // the server's identity is pinned under the name the user typed (known-servers.json)
                _client = new TCPClientHandler(ClientState.ShortGUID,
                    new ClientInfo
                    {
                        AllowRecord =
                            _globalSettings.GetClientSettingBool(GlobalSettingsKeys.AllowRecording),
                        ClientGuid = ClientState.ShortGUID,
                        Name = ClientState.EffectiveName,
                        RadioInfo = ClientState.PlayerRadioInfo.ConvertToRadioBase()
                    },
                    ClientState.E2EKeys,
                    string.IsNullOrEmpty(ServerPassword) ? null : ServerPassword,
                    host);

                Logger.Info($"Connecting to {_connectAddress} ({ip}:{port})");
                _client.TryConnect(new IPEndPoint(ip, port));
            }
            else
            {
                await ShowInvalidAddressAsync();
            }
        }
        catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
        {
            Logger.Warn(ex, $"Invalid server address {_connectAddress}");
            await ShowInvalidAddressAsync();
        }

        ConnectIsEnabled = true;
    }

    private async Task ShowInvalidAddressAsync()
    {
        IsConnecting = false;

        await TaskDialog.ShowDialogAsync(new TaskDialogPage
        {
            Caption = Resources.MsgBoxInvalidIP,
            Heading = Resources.MsgBoxInvalidIPHeading,
            Text = string.Format(Resources.MsgBoxInvalidIPText, _connectAddress,
                DefaultServerSettings.DEFAULT_SERVER_PORT),
            Icon = TaskDialogIcon.Error,
            Buttons = [TaskDialogButton.OK]
        });
    }

    /// <summary>Stops audio, the radio and the connection.</summary>
    /// <param name="userInitiated">true: the user disconnected (the connection is closed without an error)</param>
    private void Stop(bool userInitiated = true)
    {
        if (IsConnected && _globalSettings.GetClientSettingBool(GlobalSettingsKeys.PlayConnectionSounds))
            try
            {
                Sounds.BeepDisconnected.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to play disconnect sound");
            }

        IsConnected = false;
        IsConnecting = false;

        try
        {
            _audioManager.StopEncoding();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to stop encoding audio");
        }

        try
        {
            // remembers the radio tuning and marks the radio unavailable
            _radioSync?.Stop();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to stop the radio state sync");
        }

        _radioSync = null;

        var client = _client;
        _client = null;

        if (client != null && userInitiated)
        {
            // the connection reports USER_DISCONNECTED when it is closed - don't allow a new connection until then,
            // otherwise that late message would stop the new connection
            _awaitingDisconnect = true;
            ConnectIsEnabled = false;

            _ = client.RequestDisconnectAsync(true);

            _ = Task.Run(async () =>
            {
                await Task.Delay(DisconnectWaitTimeout);
                if (_awaitingDisconnect)
                {
                    _awaitingDisconnect = false;
                    ConnectIsEnabled = true;
                }
            });
        }
        else
        {
            _ = client?.RequestDisconnectAsync();
            ConnectIsEnabled = true;
        }
    }

    private string GetAddressFromTextBox()
    {
        var addr = ServerAddress.Trim();

        if (addr.Contains(':')) return addr.Split(':')[0];

        return addr;
    }

    private int GetPortFromTextBox()
    {
        var addr = ServerAddress.Trim();

        if (addr.Contains(':'))
        {
            if (int.TryParse(addr.Split(':')[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535)
                return port;

            throw new ArgumentException("specified port is not valid");
        }

        return int.Parse(DefaultServerSettings.DEFAULT_SERVER_PORT, CultureInfo.InvariantCulture);
    }


    private async Task ShowMicPassthroughWarningAsync()
    {
        var micOutput = _globalSettings.GetClientSetting(GlobalSettingsKeys.MicAudioOutputDeviceId).RawValue;

        if (!string.IsNullOrEmpty(micOutput)
            && micOutput.Equals(_globalSettings.GetClientSetting(GlobalSettingsKeys.AudioOutputDeviceId).RawValue))
            await TaskDialog.ShowDialogAsync(new TaskDialogPage
            {
                Caption = Resources.MsgBoxMicPassthruCaption,
                Heading = Resources.MsgBoxMicPassthru,
                Text = Resources.MsgBoxMicPassthruText,
                Icon = TaskDialogIcon.Warning,
                Buttons = [TaskDialogButton.OK]
            });
    }

    private void PreviewAudio()
    {
        if (_audioPreview == null)
        {
            if (!AudioInput.MicrophoneAvailable)
            {
                Logger.Info("Unable to preview audio, no valid audio input device available or selected");
                return;
            }

            //get device
            try
            {
                Task.Run(async () => await ShowMicPassthroughWarningAsync()).Wait();

                var preview = new AudioPreview
                {
                    SpeakerBoost = VolumeConversionHelper.ConvertVolumeSliderToScale((float)SpeakerBoost)
                };

                // the microphone is played back through the selected radio sound
                if (preview.StartPreview(AudioOutput.WindowsN, PreviewModelKey)) _audioPreview = preview;
            }
            catch (Exception ex)
            {
                Logger.Error(ex,
                    "Unable to preview audio - likely output device error - Pick another. Error:" + ex.Message);
            }
        }
        else
        {
            _audioPreview.StopEncoding();
            _audioPreview = null;
        }

        NotifyPropertyChanged(nameof(PreviewText));
        NotifyPropertyChanged(nameof(AudioSettingsEnabled));
    }

    /// <returns>false if audio could not be started (a dialog was shown)</returns>
    private bool StartAudio(IPEndPoint endPoint, UdpTransportKey udpKey, E2EVoiceSession voiceSession)
    {
        var started = false;

        //Must be main thread
        Application.Current.Dispatcher.Invoke(delegate
        {
            try
            {
                started = _audioManager.StartEncoding(ClientState.ShortGUID, endPoint, udpKey, voiceSession);
            }
            catch (Exception ex)
            {
                Logger.Error(ex,
                    "Unable to get audio device - likely output device error - Pick another. Error:" +
                    ex.Message);

                TaskDialog.ShowDialog(new TaskDialogPage
                {
                    Caption = Resources.MsgBoxAudioError,
                    Heading = Resources.MsgBoxAudioErrorHeading,
                    Text = string.Format(Resources.MsgBoxAudioStartFailedText, App.LogDirectory),
                    Icon = TaskDialogIcon.Error,
                    Buttons = [TaskDialogButton.Close]
                });
            }
        });

        return started;
    }

    private void ToggleRadioPanel()
    {
        if (_radioPanel == null || !_radioPanel.IsVisible ||
            _radioPanel.WindowState == WindowState.Minimized)
        {
            _radioPanel?.Close();

            _radioPanel = new RadioPanelWindow();
            _radioPanel.ShowInTaskbar =
                !_globalSettings.GetClientSettingBool(GlobalSettingsKeys.RadioPanelTaskbarHide);
            _radioPanel.Show();
        }
        else
        {
            _radioPanel?.Close();
            _radioPanel = null;
        }
    }

    private void ToggleServerSettings()
    {
        if (_serverSettingsWindow == null || !_serverSettingsWindow.IsVisible ||
            _serverSettingsWindow.WindowState == WindowState.Minimized)
        {
            _serverSettingsWindow?.Close();

            _serverSettingsWindow = new ServerSettingsWindow.ServerSettingsWindow();
            _serverSettingsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _serverSettingsWindow.Owner = Application.Current.MainWindow;
            _serverSettingsWindow.Show();
        }
        else
        {
            _serverSettingsWindow?.Close();
            _serverSettingsWindow = null;
        }
    }

    private void ToggleClientList()
    {
        if (_clientListWindow == null || !_clientListWindow.IsVisible ||
            _clientListWindow.WindowState == WindowState.Minimized)
        {
            _clientListWindow?.Close();

            _clientListWindow = new ClientListWindow();
            _clientListWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _clientListWindow.Owner = Application.Current.MainWindow;
            _clientListWindow.Show();
        }
        else
        {
            _clientListWindow?.Close();
            _clientListWindow = null;
        }
    }


    public void OnClosing()
    {
        //stop timer
        _updateTimer?.Stop();

        GameIntegrationManager.Instance.Stop();

        // saves the radio tuning (RadioStateSyncService.Stop) and closes the connection
        Stop();

        _audioPreview?.StopEncoding();
        _audioPreview = null;

        _radioPanel?.Close();
        _radioPanel = null;

        _clientListWindow?.Close();
        _clientListWindow = null;

        _serverSettingsWindow?.Close();
        _serverSettingsWindow = null;
    }
}
