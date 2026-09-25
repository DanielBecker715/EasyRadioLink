using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Navigation;
using EasyRadioLink.Client.Settings.Favourites;
using EasyRadioLink.Client.Settings.RadioChannels;
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

        BindServerPassword((MainWindowViewModel)DataContext);

        UpdatePresetsFolderLabel();

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

        var mainWindowX = (int)_globalSettings.GetPositionSetting(GlobalSettingsKeys.ClientX).DoubleValue;
        var mainWindowY = (int)_globalSettings.GetPositionSetting(GlobalSettingsKeys.ClientY).DoubleValue;
        var radioWindowX = (int)_globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioX).DoubleValue;
        var radioWindowY = (int)_globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioY).DoubleValue;

        Logger.Info($"Checking window visibility for main client window {{X={mainWindowX},Y={mainWindowY}}}");
        Logger.Info($"Checking window visibility for radio panel {{X={radioWindowX},Y={radioWindowY}}}");

        // Use WPF's virtual screen bounds so that the check runs in the same
        // DPI-independent coordinate space as the stored window positions.
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        Logger.Info(
            $"VirtualScreen bounds for visibility check: Left={virtualLeft}, Top={virtualTop}, Right={virtualRight}, Bottom={virtualBottom}");

        var mainWindowVisible = mainWindowX >= virtualLeft && mainWindowX <= virtualRight &&
                                mainWindowY >= virtualTop && mainWindowY <= virtualBottom;

        var radioWindowVisible = radioWindowX >= virtualLeft && radioWindowX <= virtualRight &&
                                 radioWindowY >= virtualTop && radioWindowY <= virtualBottom;

        // Use DPI-independent default positions based on virtual screen origin
        var defaultMainX = (int)(virtualLeft + 50);
        var defaultMainY = (int)(virtualTop + 50);
        var defaultRadioPanelX = (int)(virtualLeft + 100);
        var defaultRadioPanelY = (int)(virtualTop + 100);

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
                $"Radio panel window outside visible area of monitors, resetting position ({radioWindowX},{radioWindowY}) to defaults");

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

    #region Presets folder

    private void UpdatePresetsFolderLabel()
    {
        var presetsFolder = FilePresetChannelsStore.PresetsFolder;
        var isDefault = string.Equals(Path.GetFullPath(presetsFolder), Path.GetFullPath(AppPaths.PresetsDirectory),
            StringComparison.OrdinalIgnoreCase);

        PresetsFolderLabel.Content = isDefault
            ? Properties.Resources.PresetsFolderDefault
            : Path.GetFileName(presetsFolder.TrimEnd('\\', '/'));
        PresetsFolderLabel.ToolTip = presetsFolder;
    }

    private void PresetsFolderBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var selectPresetsFolder = new FolderBrowserDialog();
        selectPresetsFolder.SelectedPath = FilePresetChannelsStore.PresetsFolder;
        if (selectPresetsFolder.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.LastPresetsFolder, selectPresetsFolder.SelectedPath);
            UpdatePresetsFolderLabel();
        }
    }

    private void PresetsFolderResetButton_Click(object sender, RoutedEventArgs e)
    {
        _globalSettings.SetClientSetting(GlobalSettingsKeys.LastPresetsFolder, AppPaths.PresetsDirectory);
        UpdatePresetsFolderLabel();
    }

    #endregion

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
