using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Caliburn.Micro;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using EasyRadioLink.Server.Properties;
using EasyRadioLink.Server.UI.ClientAdmin;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Server.UI.MainWindow;

public sealed class MainViewModel : Screen, IHandle<ServerStateMessage>
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);

    private readonly ClientAdminViewModel _clientAdminViewModel;
    private readonly IEventAggregator _eventAggregator;
    private readonly IWindowManager _windowManager;
    private readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private DispatcherTimer _passwordDebounceTimer;
    private DispatcherTimer _testFrequenciesDebounceTimer;
    private DispatcherTimer _cleanFrequenciesDebounceTimer;

    private string _serverPassword = Store.GetServerPassword();
    private string _testFrequencies = Store.GetGeneralSetting(ServerSettingsKeys.TEST_FREQUENCIES).StringValue;
    private string _cleanFrequencies = Store.GetGeneralSetting(ServerSettingsKeys.CLEAN_FREQUENCIES).StringValue;

    public MainViewModel(IWindowManager windowManager, IEventAggregator eventAggregator,
        ClientAdminViewModel clientAdminViewModel)
    {
        _windowManager = windowManager;
        _eventAggregator = eventAggregator;
        _clientAdminViewModel = clientAdminViewModel;
        _eventAggregator.SubscribeOnUIThread(this);

        DisplayName = $"{Resources.TitleServer} v{AppVersion.Version}";

        Logger.Info($"EasyRadioLink Server running - {AppVersion.Version}");
    }

    private static ServerSettingsStore Store => ServerSettingsStore.Instance;

    public bool IsServerRunning { get; private set; } = true;

    public string ServerButtonText => IsServerRunning ? Resources.BtnStopServer : Resources.BtnStartServer;

    public int ClientsCount { get; private set; }

    public string ListeningPort => Store.GetServerSetting(ServerSettingsKeys.SERVER_PORT).StringValue;

    public string ExportListText => OnOffText(ServerSettingsKeys.CLIENT_EXPORT_ENABLED);

    public string RealRadioText => OnOffText(ServerSettingsKeys.IRL_RADIO_TX);

    public string IRLRadioRxText => OnOffText(ServerSettingsKeys.IRL_RADIO_RX_INTERFERENCE);

    public string AllowRadioEncryption => OnOffText(ServerSettingsKeys.ALLOW_RADIO_ENCRYPTION);

    public string StrictRadioEncryption => OnOffText(ServerSettingsKeys.STRICT_RADIO_ENCRYPTION);

    public string TunedCountText => OnOffText(ServerSettingsKeys.SHOW_TUNED_COUNT);

    public string ShowTransmitterNameText => OnOffText(ServerSettingsKeys.SHOW_TRANSMITTER_NAME);

    public string TransmissionLogEnabledText => OnOffText(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED);

    public string ServerPresetsEnabledText => OnOffText(ServerSettingsKeys.SERVER_PRESETS_ENABLED);

    public string ServerRadioPresetEnabledText => OnOffText(ServerSettingsKeys.SERVER_RADIO_PRESET_ENABLED);

    /// <summary>Server password ("" = open server). Saved to [Server Settings] shortly after typing stops.</summary>
    public string ServerPassword
    {
        get => _serverPassword;
        set
        {
            _serverPassword = value?.Trim() ?? "";
            Debounce(ref _passwordDebounceTimer, PasswordDebounceTimerTick);
            NotifyOfPropertyChange(() => ServerPassword);
        }
    }

    /// <summary>Radio check (echo) frequencies, MHz list with '.' as decimal separator.</summary>
    public string TestFrequencies
    {
        get => _testFrequencies;
        set
        {
            _testFrequencies = value?.Trim() ?? "";
            Debounce(ref _testFrequenciesDebounceTimer, TestFrequenciesDebounceTimerTick);
            NotifyOfPropertyChange(() => TestFrequencies);
        }
    }

    /// <summary>Clean (no radio effects) frequencies, MHz list with '.' as decimal separator.</summary>
    public string CleanFrequencies
    {
        get => _cleanFrequencies;
        set
        {
            _cleanFrequencies = value?.Trim() ?? "";
            Debounce(ref _cleanFrequenciesDebounceTimer, CleanFrequenciesDebounceTimerTick);
            NotifyOfPropertyChange(() => CleanFrequencies);
        }
    }

    public int ArchiveLimit
    {
        get => Store.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION).IntValue;
        set
        {
            Store.SetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION,
                value.ToString(CultureInfo.InvariantCulture));

            _eventAggregator.PublishOnBackgroundThreadAsync(new ServerSettingsChangedMessage());
        }
    }


    public Task HandleAsync(ServerStateMessage message, CancellationToken token)
    {
        IsServerRunning = message.IsRunning;
        ClientsCount = message.Count;
        return Task.CompletedTask;
    }

    public void ServerStartStop()
    {
        if (IsServerRunning)
            _eventAggregator.PublishOnBackgroundThreadAsync(new StopServerMessage());
        else
            _eventAggregator.PublishOnBackgroundThreadAsync(new StartServerMessage());
    }

    public void ShowClientList()
    {
        IDictionary<string, object> settings = new Dictionary<string, object>
        {
            { "Icon", new BitmapImage(new Uri("pack://application:,,,/server-10.ico")) },
            { "ResizeMode", ResizeMode.CanMinimize }
        };
        _windowManager.ShowWindowAsync(_clientAdminViewModel, null, settings);
    }

    public void ExportListToggle()
    {
        Toggle(ServerSettingsKeys.CLIENT_EXPORT_ENABLED, nameof(ExportListText));
    }

    public void RealRadioToggle()
    {
        Toggle(ServerSettingsKeys.IRL_RADIO_TX, nameof(RealRadioText));
    }

    public void IRLRadioRxBehaviourToggle()
    {
        Toggle(ServerSettingsKeys.IRL_RADIO_RX_INTERFERENCE, nameof(IRLRadioRxText));
    }

    public void AllowRadioEncryptionToggle()
    {
        Toggle(ServerSettingsKeys.ALLOW_RADIO_ENCRYPTION, nameof(AllowRadioEncryption));
    }

    public void StrictRadioEncryptionToggle()
    {
        Toggle(ServerSettingsKeys.STRICT_RADIO_ENCRYPTION, nameof(StrictRadioEncryption));
    }

    public void TunedCountToggle()
    {
        Toggle(ServerSettingsKeys.SHOW_TUNED_COUNT, nameof(TunedCountText));
    }

    public void ShowTransmitterNameToggle()
    {
        Toggle(ServerSettingsKeys.SHOW_TRANSMITTER_NAME, nameof(ShowTransmitterNameText));
    }

    public void TransmissionLogEnabledToggle()
    {
        Toggle(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED, nameof(TransmissionLogEnabledText));
    }

    public void ServerPresetsEnabledToggle()
    {
        Toggle(ServerSettingsKeys.SERVER_PRESETS_ENABLED, nameof(ServerPresetsEnabledText));
    }

    public void ServerRadioPresetEnabledToggle()
    {
        Toggle(ServerSettingsKeys.SERVER_RADIO_PRESET_ENABLED, nameof(ServerRadioPresetEnabledText));
    }

    protected override Task OnDeactivateAsync(bool close, CancellationToken cancellationToken)
    {
        // don't lose edits typed just before the window was closed
        if (close) FlushPendingEdits();

        return base.OnDeactivateAsync(close, cancellationToken);
    }

    private static string OnOffText(ServerSettingsKeys key)
    {
        return Store.GetGeneralSetting(key).BoolValue ? Resources.BtnOn : Resources.BtnOff;
    }

    private void Toggle(ServerSettingsKeys key, string textPropertyName)
    {
        var newSetting = !Store.GetGeneralSetting(key).BoolValue;
        Store.SetGeneralSetting(key, newSetting);
        NotifyOfPropertyChange(textPropertyName);

        _eventAggregator.PublishOnBackgroundThreadAsync(new ServerSettingsChangedMessage());
    }

    private static void Debounce(ref DispatcherTimer timer, EventHandler tick)
    {
        if (timer != null)
        {
            timer.Stop();
            timer.Tick -= tick;
        }

        timer = new DispatcherTimer { Interval = DebounceInterval };
        timer.Tick += tick;
        timer.Start();
    }

    private static void StopTimer(ref DispatcherTimer timer, EventHandler tick)
    {
        if (timer == null) return;

        timer.Stop();
        timer.Tick -= tick;
        timer = null;
    }

    private void FlushPendingEdits()
    {
        if (_passwordDebounceTimer != null) PasswordDebounceTimerTick(this, EventArgs.Empty);
        if (_testFrequenciesDebounceTimer != null) TestFrequenciesDebounceTimerTick(this, EventArgs.Empty);
        if (_cleanFrequenciesDebounceTimer != null) CleanFrequenciesDebounceTimerTick(this, EventArgs.Empty);
    }

    private void PasswordDebounceTimerTick(object sender, EventArgs e)
    {
        StopTimer(ref _passwordDebounceTimer, PasswordDebounceTimerTick);

        // server-only setting: nothing to send to the clients, connected clients stay connected
        Store.SetServerPassword(_serverPassword);
        Logger.Info(_serverPassword.Length > 0 ? "Server password changed" : "Server password removed - open server");
    }

    private void TestFrequenciesDebounceTimerTick(object sender, EventArgs e)
    {
        StopTimer(ref _testFrequenciesDebounceTimer, TestFrequenciesDebounceTimerTick);

        Store.SetGeneralSetting(ServerSettingsKeys.TEST_FREQUENCIES, _testFrequencies);

        _eventAggregator.PublishOnBackgroundThreadAsync(new ServerFrequenciesChanged
        {
            TestFrequencies = _testFrequencies
        });
        _eventAggregator.PublishOnBackgroundThreadAsync(new ServerSettingsChangedMessage());
    }

    private void CleanFrequenciesDebounceTimerTick(object sender, EventArgs e)
    {
        StopTimer(ref _cleanFrequenciesDebounceTimer, CleanFrequenciesDebounceTimerTick);

        Store.SetGeneralSetting(ServerSettingsKeys.CLEAN_FREQUENCIES, _cleanFrequencies);

        _eventAggregator.PublishOnBackgroundThreadAsync(new ServerSettingsChangedMessage());
    }
}
