using System;
using System.Windows;
using NLog;
using SetupResources = EasyRadioLink.Installer.Properties.Resources;

namespace EasyRadioLink.Installer
{
    /// <summary>
    ///     Interaction logic for App.xaml
    /// </summary>
    /// <remarks>
    ///     Command line:
    ///     <list type="bullet">
    ///         <item>no arguments - shows the setup window (install / update / uninstall).</item>
    ///         <item>
    ///             <c>-uninstall [-path="&lt;install folder&gt;"]</c> - uninstalls without showing the setup window (used by
    ///             "Apps &amp; Features" and the Start menu). Without <c>-path</c> the registered install folder is used.
    ///         </item>
    ///         <item><c>-waitpid=&lt;pid&gt;</c> - internal: wait for that process to exit first (temporary uninstaller copy).</item>
    ///     </list>
    /// </remarks>
    public partial class App : Application
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public string[] Arguments = new string[0];

        private bool _exiting;

        /// <summary>True when started with <c>-uninstall</c>.</summary>
        public bool IsUninstall { get; private set; }

        /// <summary>Value of <c>-path=</c>, or null.</summary>
        public string PathArgument { get; private set; }

        /// <summary>Value of <c>-waitpid=</c>, or 0.</summary>
        public int WaitForProcessId { get; private set; }

        private async void ApplicationStartup(object sender, StartupEventArgs e)
        {
            if (e.Args.Length > 0)
            {
                Arguments = e.Args;
            }

            ParseArguments(Arguments);

            // A temporary uninstaller copy first waits for the setup that started it, so it can delete that setup's folder.
            if (WaitForProcessId > 0)
            {
                UninstallFlow.WaitForProcessExit(WaitForProcessId);
            }

            SetupLog.Initialize(IsUninstall ? SetupLog.UninstallLogPath : SetupLog.InstallLogPath);
            Logger.Info($"EasyRadioLink Setup {SetupEngine.Version} started from {SetupEngine.SetupProcessPath} " +
                        $"with arguments: {string.Join(" ", Arguments)}");

            DispatcherUnhandledException += (_, args) =>
            {
                Logger.Error(args.Exception, "Unhandled exception");
                MessageBox.Show(string.Format(SetupResources.MsgBoxInstallErrorText, SetupLog.LogFilePath),
                    SetupResources.MsgBoxInstallError, MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
                ExitSetup(1);
            };

            if (IsUninstall)
            {
                var exitCode = 0;
                try
                {
                    exitCode = await UninstallFlow.RunFromCommandLineAsync(PathArgument);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Uninstall failed");
                    MessageBox.Show(string.Format(SetupResources.MsgBoxUninstallErrorText, SetupLog.LogFilePath),
                        SetupResources.MsgBoxUninstallError, MessageBoxButton.OK, MessageBoxImage.Error);
                    exitCode = 1;
                }
                finally
                {
                    UninstallFlow.ScheduleTempCleanup();
                }

                ExitSetup(exitCode);
                return;
            }

            if (!SetupEngine.IsPackageComplete(SetupEngine.PackageDirectory))
            {
                Logger.Warn("Files missing from the installation package");
                MessageBox.Show(SetupResources.MsgBoxExtractedText, SetupResources.MsgBoxExtracted,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ExitSetup(1);
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Closed += (_, _) => ExitSetup(0);
            window.Show();
        }

        private void ParseArguments(string[] args)
        {
            foreach (var rawArgument in args)
            {
                var argument = rawArgument.Trim();
                if (argument.StartsWith("--") || argument.StartsWith("/"))
                {
                    argument = "-" + argument.TrimStart('-', '/');
                }

                if (argument.Equals(SetupEngine.UninstallArgument, StringComparison.OrdinalIgnoreCase))
                {
                    IsUninstall = true;
                }
                else if (argument.StartsWith(SetupEngine.PathArgumentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    PathArgument = argument.Substring(SetupEngine.PathArgumentPrefix.Length).Trim().Trim('"');
                }
                else if (argument.StartsWith(SetupEngine.WaitPidArgumentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(argument.Substring(SetupEngine.WaitPidArgumentPrefix.Length), out var pid))
                    {
                        WaitForProcessId = pid;
                    }
                }
            }
        }

        public void ExitSetup(int exitCode)
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
            Logger.Info($"Setup finished with exit code {exitCode}");
            LogManager.Shutdown();
            Shutdown(exitCode);
        }
    }
}
