using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NLog;

namespace EasyRadioLink.Installer
{
    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly string _currentDirectory;
        private ProgressBarDialog _progressBarDialog;

        /// <summary>True while an install or uninstall runs; the window cannot be closed then.</summary>
        private bool _busy;

        public MainWindow()
        {
            InitializeComponent();

            Title = Properties.Resources.Title;
            intro.Content = Properties.Resources.intro + " v" + SetupEngine.Version;
            introDescription.Text = Properties.Resources.IntroDescription;
            step2.Content = Properties.Resources.step2;
            srPathButton.Content = Properties.Resources.srPathButton;
            CreateStartMenuShortcut.Content = Properties.Resources.CreateStartMenuShortcut;
            CreateDesktopShortcut.Content = Properties.Resources.CreateDesktopShortcut;
            step4.Content = Properties.Resources.step4;
            InstallButton.Content = Properties.Resources.InstallButton;
            RemoveButton.Content = Properties.Resources.RemoveButton;
            DataNote.Text = Properties.Resources.DataNote;

            //allows click and drag anywhere on the window
            containerPanel.MouseLeftButtonDown += GridPanel_MouseLeftButtonDown;

            // Pre-fill from the previous installation (update) or use the default location.
            var installedPath = SetupEngine.ReadInstalledPath();
            srPath.Text = installedPath != "" ? installedPath : SetupEngine.DefaultInstallPath;
            CreateStartMenuShortcut.IsChecked =
                SetupEngine.ReadInstalledOption(SetupEngine.StartMenuShortcutsValue, true);
            CreateDesktopShortcut.IsChecked = SetupEngine.ReadInstalledOption(SetupEngine.DesktopShortcutValue, false);

            _currentDirectory = SetupEngine.PackageDirectory;

            // Only the package's program folders - the setup may have been extracted into a folder with other files.
            Logger.Info("Listing Files / Directories for: " + _currentDirectory);
            foreach (var folder in SetupEngine.ProgramFolders)
            {
                var folderPath = Path.Combine(_currentDirectory, folder);
                if (Directory.Exists(folderPath))
                {
                    ListFiles(folderPath);
                }
            }

            Logger.Info("Finished Listing Files / Directories");
        }

        private string GetValidatedInstallPath()
        {
            var path = SetupEngine.TryNormalizeDirectory(srPath.Text);
            if (path == null || !SetupEngine.IsSafeInstallDirectory(path))
            {
                Logger.Warn($"Invalid install path: {srPath.Text}");
                MessageBox.Show(this, Properties.Resources.MsgBoxFolderText, Properties.Resources.MsgBoxFolder,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            return path;
        }

        private async void InstallReleaseButton(object sender, RoutedEventArgs e)
        {
            var installPath = GetValidatedInstallPath();
            if (installPath == null)
            {
                return;
            }

            if (SetupEngine.LooksLikeInstallation(_currentDirectory) ||
                SetupEngine.PathsEqual(installPath, _currentDirectory) ||
                SetupEngine.IsInsideDirectory(SetupEngine.SetupProcessPath, installPath) &&
                SetupEngine.LooksLikeInstallation(installPath))
            {
                // Installing from an installation would delete the files it is copying.
                MessageBox.Show(this, Properties.Resources.MsgBoxRunningFromInstallText, Properties.Resources.MsgBoxInstallTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SetupEngine.IsForeignFolder(installPath))
            {
                // Never install into (and later clean up) a folder that holds files of another program.
                Logger.Warn($"Install folder {installPath} contains files of another program");
                MessageBox.Show(this,
                    string.Format(Properties.Resources.MsgBoxFolderNotEmptyText, installPath,
                        Path.Combine(installPath, SetupEngine.ProductName)),
                    Properties.Resources.MsgBoxFolder, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!UninstallFlow.ConfirmCloseRunningApps(this))
            {
                return;
            }

            var options = new InstallOptions
            {
                PackageDirectory = _currentDirectory,
                InstallDirectory = installPath,
                StartMenuShortcuts = CreateStartMenuShortcut.IsChecked ?? true,
                DesktopShortcut = CreateDesktopShortcut.IsChecked ?? false
            };

            SetButtonsEnabled(false);
            InstallButton.Content = Properties.Resources.InstallingButton;
            _busy = true;

            _progressBarDialog = UninstallFlow.CreateProgressDialog(this);
            _progressBarDialog.Show();

            InstallResult result;
            try
            {
                result = await Task.Run(() =>
                    SetupEngine.Install(options, text => _progressBarDialog.UpdateProgress(false, text)));
                _busy = false;
            }
            catch (Exception ex)
            {
                _busy = false;
                Logger.Error(ex, "Error Running Installer");
                _progressBarDialog.UpdateProgress(true, Properties.Resources.MsgBoxInstallError);

                MessageBox.Show(this, string.Format(Properties.Resources.MsgBoxInstallErrorText, SetupLog.LogFilePath),
                    Properties.Resources.MsgBoxInstallError, MessageBoxButton.OK, MessageBoxImage.Error);
                OpenLogFolder();

                SetButtonsEnabled(true);
                InstallButton.Content = Properties.Resources.InstallButton;
                return;
            }

            _progressBarDialog.UpdateProgress(true, Properties.Resources.ProgressDone);
            Logger.Info("Installed EasyRadioLink Successfully!");

            var message = string.Format(Properties.Resources.MsgBoxInstallSuccessText, SetupEngine.Version, installPath);
            var image = MessageBoxImage.Information;

            if (result.VcRedistFailed)
            {
                message += Environment.NewLine + Environment.NewLine +
                           string.Format(Properties.Resources.MsgBoxVcRedistWarningText,
                               result.VcRedistExitCode?.ToString() ?? "-");
                image = MessageBoxImage.Warning;
            }

            if (result.ShortcutsFailed)
            {
                message += Environment.NewLine + Environment.NewLine + Properties.Resources.MsgBoxShortcutsWarningText;
                image = MessageBoxImage.Warning;
            }

            if (result.RebootRequired)
            {
                message += Environment.NewLine + Environment.NewLine + Properties.Resources.MsgBoxRebootText;
            }

            MessageBox.Show(this, message, Properties.Resources.MsgBoxInstallTitle, MessageBoxButton.OK, image);
            Close();
        }

        private async void UninstallButton_Click(object sender, RoutedEventArgs e)
        {
            var installPath = GetValidatedInstallPath();
            if (installPath == null)
            {
                return;
            }

            if (!SetupEngine.LooksLikeInstallation(installPath))
            {
                Logger.Warn($"No EasyRadioLink installation found at {installPath}");
                MessageBox.Show(this, string.Format(Properties.Resources.MsgBoxNotInstalledText, installPath),
                    Properties.Resources.MsgBoxInstallTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // The setup inside the install folder cannot delete itself - hand over to a temporary copy.
            if (UninstallFlow.RelaunchFromTempIfNeeded(installPath))
            {
                Close();
                return;
            }

            SetButtonsEnabled(false);
            RemoveButton.Content = Properties.Resources.RemovingButton;

            UninstallOutcome outcome;
            _busy = true;
            try
            {
                outcome = await UninstallFlow.RunAsync(this, installPath);
            }
            finally
            {
                _busy = false;
            }

            if (outcome == UninstallOutcome.Cancelled)
            {
                SetButtonsEnabled(true);
                RemoveButton.Content = Properties.Resources.RemoveButton;
                return;
            }

            if (outcome == UninstallOutcome.Failed)
            {
                OpenLogFolder();
            }

            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Closing the window ends the setup; while it installs or uninstalls that would stop it halfway.
            if (_busy)
            {
                Logger.Info("Close request ignored - setup is busy");
                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
        }

        private void SetButtonsEnabled(bool enabled)
        {
            InstallButton.IsEnabled = enabled;
            RemoveButton.IsEnabled = enabled;
            srPathButton.IsEnabled = enabled;
            srPath.IsEnabled = enabled;
            CreateStartMenuShortcut.IsEnabled = enabled;
            CreateDesktopShortcut.IsEnabled = enabled;
        }

        private static void OpenLogFolder()
        {
            try
            {
                if (File.Exists(SetupLog.LogFilePath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SetupLog.LogFilePath}\"")
                        { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Unable to open the log folder");
            }
        }

        private static void ListFiles(string sDir)
        {
            try
            {
                foreach (string f in Directory.GetFiles(sDir))
                {
                    Logger.Info(f);
                }

                foreach (string d in Directory.GetDirectories(sDir))
                {
                    ListFiles(d);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error listing files");
            }
        }

        private void GridPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Set_Install_Path(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = Properties.Resources.step2
            };

            var current = SetupEngine.TryNormalizeDirectory(srPath.Text);
            var parent = current != null ? Path.GetDirectoryName(current) : null;
            if (parent != null && Directory.Exists(parent))
            {
                dialog.InitialDirectory = parent;
            }

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var folder = SetupEngine.TryNormalizeDirectory(dialog.FolderName);
            if (folder == null)
            {
                return;
            }

            // Always install into a dedicated "EasyRadioLink" folder.
            if (!string.Equals(Path.GetFileName(folder), SetupEngine.ProductName, StringComparison.OrdinalIgnoreCase))
            {
                folder = Path.Combine(folder, SetupEngine.ProductName);
            }

            srPath.Text = folder;
        }
    }
}
