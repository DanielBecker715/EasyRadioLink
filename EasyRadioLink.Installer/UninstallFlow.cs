using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using NLog;
using EasyRadioLink.Installer.Properties;

namespace EasyRadioLink.Installer
{
    public enum UninstallOutcome
    {
        Cancelled,
        Succeeded,
        Failed
    }

    /// <summary>
    ///     User-facing uninstall steps (confirmation, progress, result), shared by the Uninstall button of the setup window and
    ///     the <c>-uninstall</c> command line used by "Apps &amp; Features" and the Start menu.
    /// </summary>
    /// <remarks>
    ///     The setup copy inside the install folder cannot delete itself. When the uninstall is started from there, the setup
    ///     copies itself to %TEMP%\EasyRadioLink-Uninstall-&lt;id&gt;\, starts that copy with
    ///     <c>-uninstall -path="&lt;install folder&gt;" -waitpid=&lt;pid&gt;</c> and exits. The temporary copy removes itself
    ///     when it is finished.
    /// </remarks>
    internal static class UninstallFlow
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private const int MaxListedFiles = 8;

        /// <summary>Handles <c>-uninstall</c>. Returns the process exit code.</summary>
        public static async Task<int> RunFromCommandLineAsync(string pathArgument)
        {
            // Install folder: -path= argument, else the registered folder, else the folder of this setup copy
            // (if it is an installation, e.g. the registry entries were removed by hand).
            var registeredPath = SetupEngine.ReadInstalledPath();
            var candidate = SetupEngine.TryNormalizeDirectory(!string.IsNullOrWhiteSpace(pathArgument)
                ? pathArgument
                : registeredPath);

            if (candidate == null && SetupEngine.LooksLikeInstallation(SetupEngine.PackageDirectory))
            {
                candidate = SetupEngine.PackageDirectory;
            }

            if (candidate == null || !SetupEngine.IsSafeInstallDirectory(candidate))
            {
                Logger.Warn($"No EasyRadioLink installation found (path argument: '{pathArgument}', registry: '{registeredPath}')");
                var shownPath = candidate ?? (string.IsNullOrWhiteSpace(pathArgument)
                    ? SetupEngine.DefaultInstallPath
                    : pathArgument);
                MessageBox.Show(string.Format(Resources.MsgBoxNotInstalledText, shownPath),
                    Resources.MsgBoxInstallTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return 1;
            }

            if (!SetupEngine.LooksLikeInstallation(candidate))
            {
                return RemoveDanglingRegistration(candidate, registeredPath) ? 0 : 1;
            }

            if (RelaunchFromTempIfNeeded(candidate))
            {
                return 0;
            }

            var outcome = await RunAsync(null, candidate);
            return outcome == UninstallOutcome.Failed ? 1 : 0;
        }

        /// <summary>
        ///     Asks for confirmation, closes running programs and uninstalls from <paramref name="installDirectory" />.
        ///     Must be called on the UI thread; the setup must not run from inside the install folder (see
        ///     <see cref="RelaunchFromTempIfNeeded" />).
        /// </summary>
        public static async Task<UninstallOutcome> RunAsync(Window owner, string installDirectory)
        {
            if (Show(owner, string.Format(Resources.MsgBoxUninstallConfirm, installDirectory),
                    Resources.MsgBoxInstallTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) !=
                MessageBoxResult.Yes)
            {
                Logger.Info("Uninstall cancelled by the user");
                return UninstallOutcome.Cancelled;
            }

            var deleteUserFiles = false;
            var userFiles = SetupEngine.FindUserFiles(installDirectory);
            if (userFiles.Count > 0)
            {
                var list = string.Join(Environment.NewLine, userFiles.Take(MaxListedFiles).Select(f => "  " + f));
                if (userFiles.Count > MaxListedFiles)
                {
                    list += Environment.NewLine + "  " +
                            string.Format(Resources.MsgBoxMoreFiles, userFiles.Count - MaxListedFiles);
                }

                var answer = Show(owner, string.Format(Resources.MsgBoxUninstallDataText, list),
                    Resources.MsgBoxInstallTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.None)
                {
                    Logger.Info("Uninstall cancelled by the user");
                    return UninstallOutcome.Cancelled;
                }

                deleteUserFiles = answer == MessageBoxResult.Yes;
            }

            if (!ConfirmCloseRunningApps(owner))
            {
                Logger.Info("Uninstall cancelled - running programs were not closed");
                return UninstallOutcome.Cancelled;
            }

            // A working directory inside the install folder would keep that folder from being deleted.
            if (SetupEngine.IsInsideDirectory(Environment.CurrentDirectory, installDirectory))
            {
                Environment.CurrentDirectory = Path.GetTempPath();
            }

            var progressDialog = CreateProgressDialog(owner);
            progressDialog.Show();
            progressDialog.UpdateProgress(false, Resources.MsgBoxUninstalling);

            UninstallResult result;
            try
            {
                result = await Task.Run(() => SetupEngine.Uninstall(installDirectory, deleteUserFiles,
                    text => progressDialog.UpdateProgress(false, text)));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error running the uninstaller");
                progressDialog.UpdateProgress(true, Resources.MsgBoxUninstallError);
                Show(owner, string.Format(Resources.MsgBoxUninstallErrorText, SetupLog.LogFilePath),
                    Resources.MsgBoxUninstallError, MessageBoxButton.OK, MessageBoxImage.Error);
                return UninstallOutcome.Failed;
            }

            progressDialog.UpdateProgress(true, Resources.MsgBoxRemovedText2);

            if (result.RemainingDirectory != null)
            {
                Show(owner, string.Format(Resources.MsgBoxRemovedKeptText, result.RemainingDirectory),
                    Resources.MsgBoxInstallTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Show(owner, Resources.MsgBoxRemovedText, Resources.MsgBoxInstallTitle, MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return UninstallOutcome.Succeeded;
        }

        /// <summary>
        ///     If this setup runs from inside the install folder, starts a temporary copy that performs the uninstall and
        ///     returns true (the caller must exit). Otherwise returns false.
        /// </summary>
        public static bool RelaunchFromTempIfNeeded(string installDirectory)
        {
            if (!SetupEngine.IsInsideDirectory(SetupEngine.SetupProcessPath, installDirectory))
            {
                return false;
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(),
                SetupEngine.TempUninstallFolderPrefix + Guid.NewGuid().ToString("N"));
            var tempSetup = SetupEngine.CopySetupTo(tempDirectory);

            var arguments = $"{SetupEngine.UninstallArgument} {SetupEngine.PathArgumentPrefix}\"{installDirectory}\" " +
                            $"{SetupEngine.WaitPidArgumentPrefix}{Environment.ProcessId}";
            Logger.Info($"Starting temporary uninstaller {tempSetup} {arguments}");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = tempSetup,
                Arguments = arguments,
                WorkingDirectory = tempDirectory,
                UseShellExecute = false
            });

            return true;
        }

        /// <summary>Asks the user to close the running EasyRadioLink programs. True if none run or the user agreed.</summary>
        public static bool ConfirmCloseRunningApps(Window owner)
        {
            var running = SetupEngine.FindRunningApps();
            try
            {
                if (running.Count == 0)
                {
                    return true;
                }

                var names = running.Select(p =>
                    {
                        try
                        {
                            return p.ProcessName;
                        }
                        catch (Exception)
                        {
                            return null;
                        }
                    })
                    .Where(n => n != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(n => "  " + n);

                return Show(owner, string.Format(Resources.MsgBoxCloseAppsText, string.Join(Environment.NewLine, names)),
                    Resources.MsgBoxCloseApps, MessageBoxButton.OKCancel, MessageBoxImage.Warning) ==
                       MessageBoxResult.OK;
            }
            finally
            {
                foreach (var process in running)
                {
                    process.Dispose();
                }
            }
        }

        public static ProgressBarDialog CreateProgressDialog(Window owner)
        {
            var dialog = new ProgressBarDialog();
            if (owner != null)
            {
                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            return dialog;
        }

        /// <summary>The install folder is gone or broken, but "Apps &amp; Features" still lists EasyRadioLink.</summary>
        private static bool RemoveDanglingRegistration(string installDirectory, string registeredPath)
        {
            if (registeredPath == "" || !SetupEngine.PathsEqual(registeredPath, installDirectory))
            {
                Logger.Warn($"{installDirectory} is not an EasyRadioLink installation");
                MessageBox.Show(string.Format(Resources.MsgBoxNotInstalledText, installDirectory),
                    Resources.MsgBoxInstallTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            Logger.Warn($"Registered install folder {installDirectory} does not contain EasyRadioLink");
            if (MessageBox.Show(string.Format(Resources.MsgBoxRemoveEntryText, installDirectory),
                    Resources.MsgBoxInstallTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) !=
                MessageBoxResult.Yes)
            {
                return false;
            }

            SetupEngine.RemoveRegistration();
            return true;
        }

        /// <summary>Waits (max. 15 s) until the process that started this temporary uninstaller has exited.</summary>
        public static void WaitForProcessExit(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.WaitForExit(15000))
                {
                    Logger.Warn($"Process {processId} did not exit within 15 seconds");
                }
            }
            catch (ArgumentException)
            {
                // already exited
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to wait for process {processId}");
            }
        }

        /// <summary>
        ///     If this is a temporary uninstaller copy, deletes its folder a few seconds after this process has exited.
        /// </summary>
        public static void ScheduleTempCleanup()
        {
            try
            {
                var setupDirectory = Path.GetDirectoryName(SetupEngine.SetupProcessPath);
                if (string.IsNullOrEmpty(setupDirectory) ||
                    !Path.GetFileName(setupDirectory).StartsWith(SetupEngine.TempUninstallFolderPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                    !SetupEngine.IsInsideDirectory(setupDirectory, Path.GetTempPath()))
                {
                    return;
                }

                Logger.Info($"Scheduling removal of {setupDirectory}");
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = $"/d /c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{setupDirectory}\"",
                    WorkingDirectory = Path.GetTempPath(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Unable to schedule the removal of the temporary uninstaller");
            }
        }

        private static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton buttons,
            MessageBoxImage image)
        {
            return owner != null
                ? MessageBox.Show(owner, text, caption, buttons, image)
                : MessageBox.Show(text, caption, buttons, image);
        }
    }
}
