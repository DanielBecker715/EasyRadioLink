using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using EasyRadioLink.Client.Settings.Favourites;
using EasyRadioLink.Client.UI.ClientWindow.Favourites;
using EasyRadioLink.Client.UI.ClientWindow.RadioPanel;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using MahApps.Metro.Controls;
using NLog;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace EasyRadioLink.Client.UI.ClientWindow;

/// <summary>
///     Main window: tabs Radio (connection, radios, audio devices), Controls, Favourites, Settings and About, plus a
///     status bar. The logic lives in <see cref="MainWindowViewModel" />.
/// </summary>
public partial class MainWindow : MetroWindow
{
    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;
    private readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public MainWindow()
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        InitializeComponent();

        // Initialize images/icons
        Images.Init();

        // Initialise sounds
        Sounds.Init();

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _globalSettings.GetPositionSetting(GlobalSettingsKeys.ClientX).DoubleValue;
        Top = _globalSettings.GetPositionSetting(GlobalSettingsKeys.ClientY).DoubleValue;

        // "EasyRadioLink v1.0.0"
        Title = $"{AppVersion.Product} v{AppVersion.Version}";

        if (_globalSettings.GetClientSettingBool(GlobalSettingsKeys.StartMinimised))
        {
            Hide();
            WindowState = WindowState.Minimized;

            Logger.Info($"Started {AppVersion.ProductAndVersion} client minimized");
        }
        else
        {
            Logger.Info($"Started {AppVersion.ProductAndVersion} client");
        }

        DataContext = new MainWindowViewModel
        {
            FavouriteServersViewModel = new FavouriteServersViewModel(new CsvFavouriteServerStore())
        };

        FavouriteServersView.DataContext = ((MainWindowViewModel)DataContext).FavouriteServersViewModel;

        // before the password box is bound, so it starts with the favourite's password (-password= still wins)
        ((MainWindowViewModel)DataContext).SelectStartFavourite();

        BindServerPassword((MainWindowViewModel)DataContext);

        InitAboutText();

        InitFlowDocument();

        CheckWindowVisibility();

        HandleCommandLine();
    }

    /// <summary>
    ///     PasswordBox.Password is not bindable: keep the box and <see cref="MainWindowViewModel.ServerPassword" /> in
    ///     sync in both directions (a selected favourite fills in its password).
    /// </summary>
    private void BindServerPassword(MainWindowViewModel viewModel)
    {
        var updating = false;

        ServerPasswordBox.Password = viewModel.ServerPassword ?? "";

        ServerPasswordBox.PasswordChanged += (_, _) =>
        {
            if (!updating) viewModel.ServerPassword = ServerPasswordBox.Password;
        };

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainWindowViewModel.ServerPassword)) return;

            Dispatcher.InvokeAsync(() =>
            {
                var password = viewModel.ServerPassword ?? "";
                if (ServerPasswordBox.Password == password) return;

                updating = true;
                ServerPasswordBox.Password = password;
                updating = false;
            });
        };
    }

    /// <summary>
    ///     Optional command line arguments: <c>-name=&lt;display name&gt;</c>, <c>-password=&lt;server password&gt;</c>
    ///     and <c>-host=&lt;address[:port]&gt;</c> (connects automatically after start-up).
    /// </summary>
    private void HandleCommandLine()
    {
        var context = (MainWindowViewModel)DataContext;
        string host = null;

        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith("-name=", StringComparison.OrdinalIgnoreCase))
            {
                var name = arg.Substring("-name=".Length).Trim().Trim('"');
                if (name.Length > 0) context.DisplayName = name;
            }
            else if (arg.StartsWith("-password=", StringComparison.OrdinalIgnoreCase))
            {
                context.ServerPassword = arg.Substring("-password=".Length).Trim('"');
            }
            else if (arg.StartsWith("-host=", StringComparison.OrdinalIgnoreCase))
            {
                host = arg.Substring("-host=".Length).Trim().Trim('"');
            }
        }

        if (string.IsNullOrWhiteSpace(host)) return;

        Application.Current.Dispatcher.InvokeAsync(async Task () =>
        {
            await Task.Delay(2000);

            try
            {
                Logger.Info($"Received -host={host} argument, connecting to {host}");
                context.ServerAddress = host;
                await context.ConnectAsync();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to connect to {host}");
            }
        });
    }

    private void CheckWindowVisibility()
    {
        if (_globalSettings.GetClientSettingBool(GlobalSettingsKeys.DisableWindowVisibilityCheck))
        {
            Logger.Info("Window visibility check is disabled, skipping");
            return;
        }

        var mainWindowX = _globalSettings.GetPositionSetting(GlobalSettingsKeys.ClientX).DoubleValue;
        var mainWindowY = _globalSettings.GetPositionSetting(GlobalSettingsKeys.ClientY).DoubleValue;
        var radioWindowX = _globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioX).DoubleValue;
        var radioWindowY = _globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioY).DoubleValue;
        var radioWindowWidth = RadioPanelWindow.NaturalWidth * RadioPanelWindow.ReadSavedScale(_globalSettings);

        Logger.Info($"Checking window visibility for main client window {{X={mainWindowX},Y={mainWindowY}}}");
        Logger.Info($"Checking window visibility for the radio window {{X={radioWindowX},Y={radioWindowY}}}");

        // The title bar (the radio's header) must be on one of the connected monitors, so the window can be dragged.
        // The virtual screen is not enough: with monitors of different sizes it contains areas no monitor shows.
        var mainWindowVisible = ScreenHelper.IsTitleVisible(mainWindowX, mainWindowY, Width);

        var radioWindowVisible = ScreenHelper.IsTitleVisible(radioWindowX, radioWindowY, radioWindowWidth);

        // defaults on the primary monitor (WPF units, like the stored positions)
        var primaryArea = SystemParameters.WorkArea;
        var defaultMainX = (int)(primaryArea.Left + 50);
        var defaultMainY = (int)(primaryArea.Top + 50);
        var defaultRadioPanelX = (int)(primaryArea.Left + 100);
        var defaultRadioPanelY = (int)(primaryArea.Top + 100);

        if (!mainWindowVisible)
        {
            MessageBox.Show(this,
                Properties.Resources.MsgBoxNotVisibleText,
                Properties.Resources.MsgBoxNotVisible,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Logger.Warn(
                $"Main client window outside visible area of monitors, resetting position ({mainWindowX},{mainWindowY}) to defaults");

            _globalSettings.SetPositionSetting(GlobalSettingsKeys.ClientX, defaultMainX);
            _globalSettings.SetPositionSetting(GlobalSettingsKeys.ClientY, defaultMainY);

            Left = defaultMainX;
            Top = defaultMainY;
        }

        if (!radioWindowVisible)
        {
            MessageBox.Show(this,
                Properties.Resources.MsgBoxRadioPanelNotVisibleText,
                Properties.Resources.MsgBoxNotVisible,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Logger.Warn(
                $"Radio window outside visible area of monitors, resetting position ({radioWindowX},{radioWindowY}) to defaults");

            EventBus.Instance.PublishOnUIThreadAsync(new ResetRadioPanelMessage());

            _globalSettings.SetPositionSetting(GlobalSettingsKeys.RadioX, defaultRadioPanelX);
            _globalSettings.SetPositionSetting(GlobalSettingsKeys.RadioY, defaultRadioPanelY);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ((MainWindowViewModel)DataContext).OnClosing();

        //save window position
        _globalSettings.SetPositionSetting(GlobalSettingsKeys.ClientX, Left);
        _globalSettings.SetPositionSetting(GlobalSettingsKeys.ClientY, Top);

        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized &&
            _globalSettings.GetClientSettingBool(GlobalSettingsKeys.MinimiseToTray)) Hide();

        base.OnStateChanged(e);
    }

    private void LaunchAddressTab(object sender, RoutedEventArgs e)
    {
        TabControl.SelectedItem = FavouritesSeversTab;
    }

    #region About tab

    /// <summary>Version and the folders shown on the About tab (the folder links open Explorer).</summary>
    private void InitAboutText()
    {
        AboutProductRun.Text = AppVersion.Product;
        AboutVersionRun.Text = string.Format(Properties.Resources.AboutVersion, AppVersion.Version,
            AppVersion.ProtocolVersion);

        SetFolderLink(ConfigFolderLink, ConfigFolderRun, GlobalSettingsStore.Path);
        SetFolderLink(LogFolderLink, LogFolderRun, App.LogDirectory);
        SetFolderLink(RecordingsFolderLink, RecordingsFolderRun, RecordingsFolder());
        SetFolderLink(RadioModelsFolderLink, RadioModelsFolderRun, RadioModelFactory.CustomModelsFolder);
    }

    /// <summary>Documents\EasyRadioLink\Recordings - without creating it (it is created when a recording starts).</summary>
    private static string RecordingsFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return string.IsNullOrWhiteSpace(documents)
            ? Path.Combine(AppPaths.UserDataDirectory, "Recordings")
            : Path.Combine(documents, AppPaths.AppFolderName, "Recordings");
    }

    private void SetFolderLink(Hyperlink link, Run text, string folder)
    {
        try
        {
            folder = Path.GetFullPath(folder.TrimEnd('\\', '/'));
            text.Text = folder;
            link.NavigateUri = new Uri(folder);
            link.ToolTip = Properties.Resources.ToolTipOpenFolder;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Unable to show the folder {folder}");
            text.Text = folder ?? "";
        }
    }

    private void InitFlowDocument()
    {
        //make hyperlinks work
        var hyperlinks = WPFElementHelper.GetVisuals(AboutFlowDocument).OfType<Hyperlink>();
        foreach (var link in hyperlinks)
            link.RequestNavigate += Hyperlink_RequestNavigate;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs args)
    {
        args.Handled = true;

        if (args.Uri == null) return;

        try
        {
            if (args.Uri.IsFile)
            {
                // a folder: create it if needed and open it in Explorer
                var folder = args.Uri.LocalPath;
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Unable to open {args.Uri}");
        }
    }

    #endregion
}
