using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    // name of the single instance mutex (per Windows session)
    private const string SingleInstanceMutexName = "EasyRadioLink.Client";

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

        SetupLogging();

        Logger.Info($"Starting {AppVersion.ProductAndVersion} client");

        ListArgs();

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
                    Text = "This one will now quit. Check your system tray for the EasyRadioLink icon.",
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

    /// <summary>Directory of the client log files: <c>%AppData%\EasyRadioLink\Logs</c>.</summary>
    public static string LogDirectory => Path.Combine(AppPaths.UserDataDirectory, "Logs");

    private Mutex SingleInstanceMutex { get; set; }

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

    /// <summary>The command line of this process (without the executable) for the elevated restart.</summary>
    private static string GetArgsString()
    {
        var builder = new StringBuilder();
        var args = Environment.GetCommandLineArgs();

        // args[0] is the executable / entry assembly
        for (var i = 1; i < args.Length; i++)
        {
            var s = args[i];
            if (builder.Length > 0) builder.Append(' ');

            var separator = s.IndexOf('=');
            if (s.StartsWith('-') && separator > 0 && s.Contains(' '))
            {
                // -cfg=C:\My Folder -> -cfg="C:\My Folder"
                builder.Append(s, 0, separator + 1);
                builder.Append('"');
                builder.Append(s.Substring(separator + 1));
                builder.Append('"');
            }
            else if (s.Contains(' '))
            {
                builder.Append('"').Append(s).Append('"');
            }
            else
            {
                builder.Append(s);
            }
        }

        return builder.ToString();
    }

    private bool IsClientRunning()
    {
        SingleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var created);
        return !created;
    }

    /*
     * Changes to the logging configuration in this method must be replicated in
     * this VS project's NLog.config file
     */
    private void SetupLogging()
    {
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
            logger.Error((Exception)e.ExceptionObject, "Received unhandled exception, {0}",
                e.IsTerminating ? "exiting" : "continuing");
        }
    }
}
