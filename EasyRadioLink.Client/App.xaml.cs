using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Settings;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using Application = System.Windows.Application;

namespace EasyRadioLink.Client;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // name of the single instance mutex (per Windows session and settings folder)
    private const string SingleInstanceMutexPrefix = "EasyRadioLink.Client";

    private const string LogFileName = "clientlog.txt";
    private const string ArchiveLogFileName = "clientlog.old.txt";

    private static Logger Logger = LogManager.GetCurrentClassLogger();
    private NotifyIcon _notifyIcon;
    private bool loggingReady;

    public App()
    {
#if false
        // Useful to track down threading issues.
        if (!ThreadPool.SetMinThreads(1, 1))
        {
            Debug.Assert(false, "Unable to set min threads!");
        }

        if (!ThreadPool.SetMaxThreads(1, 1))
        {
            Debug.Assert(false, "Unable to set max threads!");
        }
#endif

        System.Windows.Forms.Application.EnableVisualStyles();

        AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;

        var location = AppPaths.ProgramDirectory;

        var dllsToValidate = new[] { "opus.dll", "speexdsp.dll" };
        foreach (var dll in dllsToValidate)
            if (!File.Exists(Path.Combine(location, dll)))
            {
                TaskDialog.ShowDialog(new TaskDialogPage
                {
                    Caption = "Installation Error!",
                    Heading = $"You are missing the {dll}",
                    Text = "Reinstall EasyRadioLink using the installer and don't move the client out of its installation directory!",
                    Icon = TaskDialogIcon.Error,
                    Buttons = { TaskDialogButton.OK }
                });

                Environment.Exit(1);
            }

        // before logging: the logs of a -cfg directory are written there
        var configDirectoryError = CheckConfigDirectory();

        SetupLogging();

        Logger.Info($"Starting {AppVersion.ProductAndVersion} client");

        ListArgs();

        if (configDirectoryError != null)
            Logger.Error(configDirectoryError, "The settings folder given with -cfg can't be used, using the default folder");

        Logger.Info($"Settings folder: {GlobalSettingsStore.Path}");

#if !DEBUG
        if (IsClientRunning())
        {
            //check environment flag
            var allowMultiple = false;

            foreach (var arg in Environment.GetCommandLineArgs())
                if (arg.Contains("-allowMultiple"))
                    //restart flag to promote to admin
                    allowMultiple = true;

            if (GlobalSettingsStore.Instance.GetClientSettingBool(GlobalSettingsKeys.AllowMultipleInstances) ||
                allowMultiple)
            {
                Logger.Warn(
                    "Another EasyRadioLink client is already running, allowing multiple instances due to config setting");
            }
            else
            {
                Logger.Warn("Another EasyRadioLink client is already running, preventing second instance startup");

                TaskDialog.ShowDialog(new TaskDialogPage
                {
                    Caption = "EasyRadioLink is already running",
                    Heading = "Another instance of the EasyRadioLink client is already running!",
                    Text = "This one will now quit. Check your system tray for the EasyRadioLink icon.\n\n" +
                           "To run a second client, start it with its own settings folder: -cfg=<folder>",
                    Icon = TaskDialogIcon.Error,
                    Buttons = { TaskDialogButton.OK }
                });

                Environment.Exit(0);
                return;
            }
        }
#endif

        RequireAdmin();

        InitNotificationIcon();
    }

    /// <summary>
    ///     Directory of the client log files: <c>Logs</c> in the settings folder (<c>%AppData%\EasyRadioLink\Logs</c>,
    ///     or <c>&lt;-cfg directory&gt;\Logs</c>), so clients with different settings folders don't share one log file.
    /// </summary>
    public static string LogDirectory => Path.Combine(GlobalSettingsStore.Path, "Logs");

    private Mutex SingleInstanceMutex { get; set; }

    /// <summary>
    ///     The settings folder must be writable. A <c>-cfg</c> folder that can't be used is reported and replaced by
    ///     the default folder (instead of a crash while the settings are loaded).
    /// </summary>
    /// <returns>the error if the <c>-cfg</c> folder was replaced, otherwise null</returns>
    private static Exception CheckConfigDirectory()
    {
        var directory = GlobalSettingsStore.Path;
        var error = DirectoryWriteError(directory);

        if (error == null) return null;

        var defaultDirectory = AppPaths.UserDataDirectory;
        var isDefault = string.Equals(Path.TrimEndingDirectorySeparator(directory),
            Path.TrimEndingDirectorySeparator(defaultDirectory), StringComparison.OrdinalIgnoreCase);

        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = AppVersion.Product,
            Heading = "The settings folder can't be used",
            Text = $"{directory}\n\n{error.Message}\n\n" + (isDefault
                ? "Your settings will not be saved."
                : $"EasyRadioLink uses the default settings folder instead:\n{defaultDirectory}"),
            Icon = TaskDialogIcon.Warning,
            Buttons = { TaskDialogButton.OK }
        });

        if (isDefault) return null;

        GlobalSettingsStore.Path = defaultDirectory;
        return error;
    }

    /// <summary>null if the directory exists (or was created) and a file can be written to it, otherwise the error.</summary>
    private static Exception DirectoryWriteError(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private void ListArgs()
    {
        Logger.Info("Arguments:");
        var args = Environment.GetCommandLineArgs();
        foreach (var s in args)
            // never write a server password into the log
            Logger.Info(s.StartsWith("-password=", StringComparison.OrdinalIgnoreCase) ? "-password=****" : s);
    }

    private void RequireAdmin()
    {
        var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
        var hasAdministrativeRight = principal.IsInRole(WindowsBuiltInRole.Administrator);

        Logger.Info($"User running as admin: {hasAdministrativeRight}");
        if (!GlobalSettingsStore.Instance.GetClientSettingBool(GlobalSettingsKeys.RequireAdmin))
        {
            Logger.Info("Admin rights not required");
            return;
        }

        if (!hasAdministrativeRight)
        {
            Logger.Info("Attempting to elevate to admin");

            Task.Run(async Task () =>
            {
                var startInfo = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppPaths.ProgramDirectory,
                    // the running executable - independent of the assembly name
                    FileName = Environment.ProcessPath ?? Path.Combine(AppPaths.ProgramDirectory, "EasyRadioLink.exe"),
                    Verb = "runas",
                    Arguments = GetArgsString() + " -allowMultiple"
                };
                try
                {
                    Process.Start(startInfo);
                    var dispatcher = Dispatcher;
                    if (dispatcher != null)
                        //shutdown this process as another has started
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_notifyIcon != null)
                                _notifyIcon.Visible = false;

                            Environment.Exit(0);
                        });
                }
                catch (Win32Exception)
                {
                    await TaskDialog.ShowDialogAsync(new TaskDialogPage
                    {
                        Caption = "UAC Error",
                        Heading = "EasyRadioLink could not restart with elevated privileges.",
                        Text =
                            "Unless you have a very specific need you should disable the Require Admin option in the settings.",
                        Icon = TaskDialogIcon.Warning,
                        Buttons = { TaskDialogButton.OK }
                    });
                }
            });
        }
    }

    /// <summary>
    ///     The command line of this process (without the executable) for the elevated restart. <c>-cfg=</c> is passed
    ///     as the full path of the settings folder in use, because the restart runs in another working directory.
    /// </summary>
    private static string GetArgsString()
    {
        var builder = new StringBuilder();
        var args = Environment.GetCommandLineArgs();

        // args[0] is the executable / entry assembly
        for (var i = 1; i < args.Length; i++)
        {
            var s = args[i];
            if (s.Trim().StartsWith("-cfg=")) s = "-cfg=" + GlobalSettingsStore.Path;

            if (builder.Length > 0) builder.Append(' ');

            var separator = s.IndexOf('=');
            if (s.StartsWith('-') && separator > 0 && (s.Contains(' ') || s.Contains('"')))
            {
                // -cfg=C:\My Folder\ -> -cfg="C:\My Folder\\"
                builder.Append(s, 0, separator + 1);
                AppendQuoted(builder, s.Substring(separator + 1));
            }
            else if (s.Contains(' ') || s.Contains('"'))
            {
                AppendQuoted(builder, s);
            }
            else
            {
                builder.Append(s);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Appends <paramref name="value" /> in quotes so the Windows command line parser returns it unchanged:
    ///     backslashes before a quote (also the closing one) are doubled, quotes are escaped.
    /// </summary>
    private static void AppendQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');

        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
                builder.Append('\\', backslashes * 2 + 1);
            else
                builder.Append('\\', backslashes);

            builder.Append(c);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
    }

    /// <summary>
    ///     True if a client with the same settings folder is running already. Clients with different <c>-cfg</c>
    ///     folders may run side by side (they don't share settings or log files).
    /// </summary>
    private bool IsClientRunning()
    {
        // mutex names can't contain '\' - use a hash of the settings folder
        var folder = Path.TrimEndingDirectorySeparator(GlobalSettingsStore.Path).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)), 0, 8);

        SingleInstanceMutex = new Mutex(true, $"{SingleInstanceMutexPrefix}.{hash}", out var created);
        return !created;
    }

    /*
     * Changes to the logging configuration in this method must be replicated in
     * this VS project's NLog.config file
     */
    private void SetupLogging()
    {
        // used by NLog.config (development builds; the release has no NLog.config)
        GlobalDiagnosticsContext.Set("ClientLogDirectory", LogDirectory);

        // If there is a configuration file then this will already be set
        if (LogManager.Configuration != null)
        {
            loggingReady = true;
            return;
        }

        var config = new LoggingConfiguration();
        var fileTarget = new FileTarget
        {
            FileName = Path.Combine(LogDirectory, LogFileName),
            ArchiveFileName = Path.Combine(LogDirectory, ArchiveLogFileName),
            MaxArchiveFiles = 1,
            ArchiveAboveSize = 104857600,
            Layout =
                @"${longdate} | ${logger} | ${message} ${exception:format=toString,Data:maxInnerExceptionLevel=1}"
        };

        var wrapper = new AsyncTargetWrapper(fileTarget, 5000, AsyncTargetWrapperOverflowAction.Discard);
        config.AddTarget("asyncFileTarget", wrapper);
        config.LoggingRules.Add(new LoggingRule("*", LogLevel.Info, wrapper));

        LogManager.Configuration = config;
        loggingReady = true;

        Logger = LogManager.GetCurrentClassLogger();
    }


    private void InitNotificationIcon()
    {
        if (_notifyIcon != null) return;
        var notifyIconContextMenuShow = new ToolStripMenuItem
        {
            Text = "Show"
        };
        notifyIconContextMenuShow.Click += NotifyIcon_Show;

        var notifyIconContextMenuQuit = new ToolStripMenuItem
        {
            Text = "Quit"
        };
        notifyIconContextMenuQuit.Click += NotifyIcon_Quit;

        var notifyIconContextMenu = new ContextMenuStrip();

        notifyIconContextMenu.Items.AddRange(new[] { notifyIconContextMenuShow, notifyIconContextMenuQuit });

        _notifyIcon = new NotifyIcon
        {
            Icon = Client.Properties.Resources.audio_headset,
            Text = AppVersion.Product,
            Visible = true
        };

        _notifyIcon.ContextMenuStrip = notifyIconContextMenu;
        _notifyIcon.DoubleClick += NotifyIcon_Show;
    }

    private void NotifyIcon_Show(object sender, EventArgs args)
    {
        if (MainWindow == null) return;

        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
    }

    private void NotifyIcon_Quit(object sender, EventArgs args)
    {
        MainWindow?.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_notifyIcon != null)
            _notifyIcon.Visible = false;
        base.OnExit(e);
    }

    private void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e)
    {
        if (loggingReady)
        {
            var logger = LogManager.GetCurrentClassLogger();
            logger.Error(e.ExceptionObject as Exception, "Received unhandled exception, {0}",
                e.IsTerminating ? "exiting" : "continuing");

            // the log is written asynchronously - write it before the process ends
            try
            {
                LogManager.Flush(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // nothing left to report to
            }
        }

        if (!e.IsTerminating) return;

        try
        {
            // WinExe: without a dialog the user doesn't see anything (any thread may get here - no WPF window owner)
            System.Windows.MessageBox.Show(
                $"EasyRadioLink stopped because of an unexpected error:\n{(e.ExceptionObject as Exception)?.Message}\n\n" +
                $"The details are in the log file in {LogDirectory}",
                AppVersion.Product, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // no dialog possible - the log has the details
        }
    }
}
