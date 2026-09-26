using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Caliburn.Micro;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Network.Server;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using EasyRadioLink.Server.UI.ClientAdmin;
using EasyRadioLink.Server.UI.MainWindow;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Server;

public class Bootstrapper : BootstrapperBase
{
    private readonly SimpleContainer _simpleContainer = new();
    private bool loggingReady;

    public Bootstrapper()
    {
#if false
        // Useful to track down threading issues.
        if (!ThreadPool.SetMinThreads(1, 1))
        {
            Debug.Assert(false, "Unable to set min threads!");
        }

        // NOTE: Needs at least two because of OpenNAT. DiscoverDeviceAsync() used in OpenNATAsync() isn't async through and through,
        // and needs at least one spare thread in the pool to be able to run its tasks.
        if (!ThreadPool.SetMaxThreads(2, 1))
        {
            Debug.Assert(false, "Unable to set max threads!");
        }
#endif
        InitCfgPath();

        Initialize();
        SetupLogging();

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
    }

    private void InitCfgPath()
    {
        //check commandline: -cfg=<path to server.cfg> (or --cfg=). Default: server.cfg next to the executable
        var args = Environment.GetCommandLineArgs();

        foreach (var arg in args)
            if (arg.StartsWith("-cfg="))
                ServerSettingsStore.SetConfigFile(arg.Substring("-cfg=".Length));
            else if (arg.StartsWith("--cfg="))
                ServerSettingsStore.SetConfigFile(arg.Substring("--cfg=".Length));

        // every server file (logs included) lives next to the configuration file - NLog.config uses this too
        GlobalDiagnosticsContext.Set("ServerDataDirectory",
            Path.GetDirectoryName(ServerSettingsStore.CFG_FILE_NAME) + Path.DirectorySeparatorChar);
    }

    private void SetupLogging()
    {
        // If there is a configuration file then this will already be set
        if (LogManager.Configuration != null)
        {
            loggingReady = true;
            LogConfigLocation();
            return;
        }

        var dataDirectory = Path.GetDirectoryName(ServerSettingsStore.CFG_FILE_NAME) ?? "";

        var config = new LoggingConfiguration();
        var fileTarget = new FileTarget
        {
            FileName = Path.Combine(dataDirectory, "serverlog.txt"),
            ArchiveFileName = Path.Combine(dataDirectory, "serverlog.old.txt"),
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

        // only add transmission logging at launch if its enabled, defer rule and target creation otherwise
        var store = ServerSettingsStore.Instance;
        if (store.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED).BoolValue)
        {
            LogManager.Configuration = LoggingHelper.GenerateTransmissionLoggingConfig(LogManager.Configuration,
                store.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION).IntValue, store.ConfigDirectory);
        }

        LogConfigLocation();
    }

    private static void LogConfigLocation()
    {
        LogManager.GetCurrentClassLogger().Info($"Configuration file: {ServerSettingsStore.Instance.ConfigFilePath}");
    }


    protected override void Configure()
    {
        _simpleContainer.Singleton<IWindowManager, WindowManager>();
        _simpleContainer.Singleton<IEventAggregator, EventAggregator>();
        _simpleContainer.Singleton<ServerState>();

        _simpleContainer.Singleton<MainViewModel>();
        _simpleContainer.Singleton<ClientAdminViewModel>();
    }

    protected override object GetInstance(Type service, string key)
    {
        var instance = _simpleContainer.GetInstance(service, key);
        if (instance != null)
            return instance;

        throw new InvalidOperationException("Could not locate any instances.");
    }

    protected override IEnumerable<object> GetAllInstances(Type service)
    {
        return _simpleContainer.GetAllInstances(service);
    }


    protected override void OnStartup(object sender, StartupEventArgs e)
    {
        IDictionary<string, object> settings = new Dictionary<string, object>
        {
            { "Icon", new BitmapImage(new Uri("pack://application:,,,/server-10.ico")) },
            { "ResizeMode", ResizeMode.CanMinimize }
        };
        // create the main view model first: it must be subscribed before the server can report a start failure
        _simpleContainer.GetInstance(typeof(MainViewModel), null);

        //create an instance of serverState to actually start the server
        _simpleContainer.GetInstance(typeof(ServerState), null);

        DisplayRootViewForAsync<MainViewModel>(settings);
    }

    protected override void BuildUp(object instance)
    {
        _simpleContainer.BuildUp(instance);
    }


    protected override void OnExit(object sender, EventArgs e)
    {
        var serverState = (ServerState)_simpleContainer.GetInstance(typeof(ServerState), null);
        serverState.StopServer();
    }

    protected override void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (loggingReady)
        {
            var logger = LogManager.GetCurrentClassLogger();
            logger.Error(e.Exception, "Received unhandled exception, exiting");
        }

        base.OnUnhandledException(sender, e);
    }
}
