using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
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

public sealed class MainViewModel : Screen, IHandle<ServerStateMessage>, IHandle<ServerStartFailedMessage>
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);

    private readonly ClientAdminViewModel _clientAdminViewModel;
    private readonly IEventAggregator _eventAggregator;
    private readonly IWindowManager _windowManager;
    private readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private DispatcherTimer _passwordDebounceTimer;
    private DispatcherTimer _testFrequenciesDebounceTimer;
    private DispatcherTimer _cleanFrequenciesDebounceTimer;

    // the text boxes keep exactly what was typed (spaces included); values are only trimmed when they are saved
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

    public string ServerStatusText => IsServerRunning ? Resources.StatusRunning : Resources.StatusStopped;

    public int ClientsCount { get; private set; }

    public string ListeningPort => Store.GetServerPort().ToString(CultureInfo.InvariantCulture);

    /// <summary>Full path of server.cfg - every other server file lives in the same folder.</summary>
    public string ConfigFilePath => Store.ConfigFilePath;

    public bool ClientExportEnabled
    {
        get => GetSetting(ServerSettingsKeys.CLIENT_EXPORT_ENABLED);
        set => SetSetting(ServerSettingsKeys.CLIENT_EXPORT_ENABLED, value);
    }

    public bool HalfDuplexRadios
    {
        get => GetSetting(ServerSettingsKeys.IRL_RADIO_TX);
        set => SetSetting(ServerSettingsKeys.IRL_RADIO_TX, value);
    }

    public bool RadioInterference
    {
        get => GetSetting(ServerSettingsKeys.IRL_RADIO_RX_INTERFERENCE);
        set => SetSetting(ServerSettingsKeys.IRL_RADIO_RX_INTERFERENCE, value);
    }

    public bool ShowTunedCount
    {
        get => GetSetting(ServerSettingsKeys.SHOW_TUNED_COUNT);
        set => SetSetting(ServerSettingsKeys.SHOW_TUNED_COUNT, value);
    }

    public bool ShowTransmitterName
    {
        get => GetSetting(ServerSettingsKeys.SHOW_TRANSMITTER_NAME);
        set => SetSetting(ServerSettingsKeys.SHOW_TRANSMITTER_NAME, value);
    }

    public bool TransmissionLogEnabled
    {
        get => GetSetting(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED);
        set => SetSetting(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED, value);
    }

    /// <summary>Server password ("" = open server). Saved to [Server Settings] shortly after typing stops.</summary>
    public string ServerPassword
    {
        get => _serverPassword;
        set
        {
            _serverPassword = value ?? "";
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
            _testFrequencies = value ?? "";
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
            _cleanFrequencies = value ?? "";
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
            NotifyOfPropertyChange(() => ArchiveLimit);

            _eventAggregator.PublishOnBackgroundThreadAsync(new ServerSettingsChangedMessage());
        }
    }


    public Task HandleAsync(ServerStateMessage message, CancellationToken token)
    {
        IsServerRunning = message.IsRunning;
        ClientsCount = message.Count;
        return Task.CompletedTask;
    }

    public Task HandleAsync(ServerStartFailedMessage message, CancellationToken token)
    {
        IsServerRunning = false;
        ClientsCount = 0;

        // the window stays open (server stopped) so the admin can fix the setting and press Start
        MessageBox.Show(string.Format(Resources.MsgBoxStartFailed, message.Error, ConfigFilePath),
            Resources.TitleServer, MessageBoxButton.OK, MessageBoxImage.Error);

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

    protected override Task OnDeactivateAsync(bool close, CancellationToken cancellationToken)
    {
        // don't lose edits typed just before the window was closed
        if (close) FlushPendingEdits();

        return base.OnDeactivateAsync(close, cancellationToken);
    }

    private static bool GetSetting(ServerSettingsKeys key)
    {
        return Store.GetGeneralSetting(key).BoolValue;
    }

    private void SetSetting(ServerSettingsKeys key, bool value, [CallerMemberName] string propertyName = null)
    {
        if (GetSetting(key) == value) return;

        Store.SetGeneralSetting(key, value);
        NotifyOfPropertyChange(propertyName);

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

        // server-only setting: nothing to send to the clients, connected clients stay connected.
        // Spaces inside the password are kept; leading/trailing ones are ignored (also when clients log in).
        var password = _serverPassword.Trim();
        Store.SetServerPassword(password);
        Logger.Info(password.Length > 0 ? "Server password changed" : "Server password removed - open server");
    }

    private void TestFrequenciesDebounceTimerTick(object sender, EventArgs e)
    {
        StopTimer(ref _testFrequenciesDebounceTimer, TestFrequenciesDebounceTimerTick);

        var frequencies = _testFrequencies.Trim();
        Store.SetGeneralSetting(ServerSettingsKeys.TEST_FREQUENCIES, frequencies);

        _eventAggregator.PublishOnBackgroundThreadAsync(new ServerFrequenciesChanged
        {
            TestFrequencies = frequencies
        });
        _eventAggregator.PublishOnBackgroundThreadAsync(new ServerSettingsChangedMessage());
    }

    private void CleanFrequenciesDebounceTimerTick(object sender, EventArgs e)
    {
        StopTimer(ref _cleanFrequenciesDebounceTimer, CleanFrequenciesDebounceTimerTick);

        Store.SetGeneralSetting(ServerSettingsKeys.CLEAN_FREQUENCIES, _cleanFrequencies.Trim());

        _eventAggregator.PublishOnBackgroundThreadAsync(new ServerSettingsChangedMessage());
    }
}
