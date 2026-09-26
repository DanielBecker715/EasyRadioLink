using Caliburn.Micro;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Network.Server;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using CommandLine;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Server;

internal class Program : IHandle<ClientConnectionMessage>, IHandle<ServerStartFailedMessage>
{
    // exit codes: 0 = normal shutdown, 1 = the server could not be started, 2 = invalid command line options
    private const int ExitCodeStartFailed = 1;
    private const int ExitCodeInvalidOptions = 2;

    private readonly EventAggregator _eventAggregator = new();
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ServerState _serverState;

    public Program()
    {
        SetupLogging();
    }

    public bool ConsoleLogs { get; set; }

    public int ExitCode { get; private set; }

    private Task ShutdownRequested => _shutdownRequested.Task;

    public Task HandleAsync(ClientConnectionMessage message, CancellationToken cancellationToken)
    {
        if (ConsoleLogs)
        {
            if (message.Connected)
                Console.WriteLine($"Client connected: {message.ClientIP}");
            else
                Console.WriteLine($"Client disconnected: {message.ClientIP} - {message.ClientGuid}");
        }

        return Task.CompletedTask;
    }

    public Task HandleAsync(ServerStartFailedMessage message, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine(message.Error);
        ExitCode = ExitCodeStartFailed;
        RequestShutdown();

        return Task.CompletedTask;
    }

    private void RequestShutdown()
    {
        _shutdownRequested.TrySetResult();
    }

    private static async Task<int> Main(string[] args)
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

        // all numbers (frequencies!) are parsed and printed culture independent: "27.405" is always 27.405 MHz
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        var exitCode = 0;
        var parser = Parser.Default.ParseArguments<Options>(args);
        await parser.WithParsedAsync(async options => exitCode = await ProcessArgsAsync(options));
        parser.WithNotParsed(errors => exitCode = errors.IsHelp() || errors.IsVersion() ? 0 : ExitCodeInvalidOptions);

        return exitCode;
    }

    private static async Task<int> ProcessArgsAsync(Options options)
    {
        var optionsError = options.Validate();
        if (optionsError != null)
        {
            Console.Error.WriteLine(optionsError);
            return ExitCodeInvalidOptions;
        }

        foreach (var removedOption in options.RemovedOptionsUsed())
            Console.Error.WriteLine(
                $"Warning: {removedOption} was removed in EasyRadioLink 1.1 and is ignored - please remove it from your start script or service file.");

        // default: server.cfg next to the executable; a relative --cfg is relative to the current directory
        if (!string.IsNullOrWhiteSpace(options.ConfigFile))
            ServerSettingsStore.SetConfigFile(options.ConfigFile);

        Console.WriteLine($"{AppVersion.Product} Server (command line) {AppVersion.Version}");
        Console.WriteLine($"Settings From Command Line: \n{options}");
        Console.WriteLine($"Configuration file: {ServerSettingsStore.CFG_FILE_NAME}");

        var p = new Program();
        var serverThread = new Thread(() => { p.StartServer(options); });
        serverThread.Start();

        using var waitForMainExit = new ManualResetEventSlim();

        Console.CancelKeyPress += new((_, e) =>
        {
            // Ignore the cancel here and there, allow the program to terminate.
            e.Cancel = true;
            p.RequestShutdown();
            Console.WriteLine("Shutting down gracefully...");
        });
        using var termSignal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, (_) => {
            // We got a SIGTERM, signal that graceful shutdown has started
            p.RequestShutdown();
            Console.WriteLine("Shutting down gracefully...");
            // Don't unwind until main exists
            waitForMainExit.Wait();
        });

        Console.WriteLine("Waiting for shutdown SIGTERM");
        // Wait for shutdown to start (Ctrl+C, SIGTERM or a failed start)
        await p.ShutdownRequested;

        // This is where the application performs graceful shutdown
        p.StopServer();

        Console.WriteLine("Shutdown complete");
        // Now we're done with main, tell the shutdown handler
        waitForMainExit.Set();
        serverThread.Join(TimeSpan.FromSeconds(5));
        LogManager.Flush();

        return p.ExitCode;
    }

    private void StopServer()
    {
        _serverState?.StopServer();

        EventBus.Instance.Unsubscribe(this);
    }


    public void StartServer(Options options)
    {
        EventBus.Instance.SubscribeOnPublishedThread(this);
        // before the server exists: a start failure is reported through this aggregator
        _eventAggregator.SubscribeOnPublishedThread(this);

        ConsoleLogs = options.ConsoleLogs ?? true;

        var store = ServerSettingsStore.Instance;

        if (options.Port.HasValue)
            store.SetServerSetting(ServerSettingsKeys.SERVER_PORT,
                options.Port.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(options.ServerBindIP))
            store.SetServerSetting(ServerSettingsKeys.SERVER_IP, options.ServerBindIP);
        if (options.UpnpEnabled.HasValue)
            store.SetServerSetting(ServerSettingsKeys.UPNP_ENABLED, options.UpnpEnabled.Value);
        // "" is a valid value here: --password "" makes the server open again
        if (options.ServerPassword != null)
            store.SetServerPassword(options.ServerPassword);
        if (options.ClientExportEnabled.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.CLIENT_EXPORT_ENABLED, options.ClientExportEnabled.Value);
        if (!string.IsNullOrWhiteSpace(options.ClientExportPath))
            store.SetServerSetting(ServerSettingsKeys.CLIENT_EXPORT_FILE_PATH, options.ClientExportPath);
        if (options.RealRadioTX.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.IRL_RADIO_TX, options.RealRadioTX.Value);
        if (options.RealRadioRX.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.IRL_RADIO_RX_INTERFERENCE, options.RealRadioRX.Value);
        if (options.TestFrequencies != null)
            store.SetGeneralSetting(ServerSettingsKeys.TEST_FREQUENCIES,
                RadioCalculator.NormaliseFrequencyListMHz(options.TestFrequencies));
        if (options.CleanFrequencies != null)
            store.SetGeneralSetting(ServerSettingsKeys.CLEAN_FREQUENCIES,
                RadioCalculator.NormaliseFrequencyListMHz(options.CleanFrequencies));
        if (options.ShowTunedCount.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.SHOW_TUNED_COUNT, options.ShowTunedCount.Value);
        if (options.ShowTransmitterName.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.SHOW_TRANSMITTER_NAME, options.ShowTransmitterName.Value);
        if (options.TransmissionLogEnabled.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED, options.TransmissionLogEnabled.Value);
        if (options.TransmissionLogRetention.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION,
                Math.Clamp(options.TransmissionLogRetention.Value, 0, 365).ToString(CultureInfo.InvariantCulture));
        if (options.HttpServerEnabled.HasValue)
            store.SetServerSetting(ServerSettingsKeys.HTTP_SERVER_ENABLED, options.HttpServerEnabled.Value);
        if (options.HttpServerPort.HasValue)
            store.SetServerSetting(ServerSettingsKeys.HTTP_SERVER_PORT,
                options.HttpServerPort.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(options.HttpServerAddress))
            store.SetServerSetting(ServerSettingsKeys.HTTP_SERVER_ADDRESS, options.HttpServerAddress);

        if (store.LastSaveError != null)
            Console.Error.WriteLine(
                $"Warning: unable to save the settings to {store.ConfigFilePath} ({store.LastSaveError}) - " +
                "the options above only apply to this run. Use --cfg to put server.cfg in a writable folder.");

        Console.WriteLine("Final Settings (secrets masked):");
        foreach (var setting in store.GetAllSettings()) Console.WriteLine(setting);

        Console.WriteLine(store.IsPasswordProtected
            ? "Clients need the server password to connect."
            : "Open server - no password required.");

        _serverState = new ServerState(_eventAggregator);
    }

    private void SetupLogging()
    {
        // If there is a configuration file then this will already be set
        if (LogManager.Configuration != null) return;

        // the log lives next to the configuration file, like every other server file
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
    }
}

public class Options
{
    private string _configFile;

    [Option("console-logs",
        HelpText =
            "Print client connects/disconnects to the console (--console-logs=false hides them, not saved to server.cfg). Default is true.",
        Required = false)]
    public bool? ConsoleLogs { get; set; }

    [Option('p', "port",
        HelpText = "TCP and UDP port (1-65535) - 5010 is the default",
        Required = false)]
    public int? Port { get; set; }

    [Option("bind-ip",
        HelpText = "Server Bind IP. Default is 0.0.0.0 (all interfaces). Don't change unless you know what you're doing!",
        Required = false)]
    public string ServerBindIP { get; set; }

    [Option("upnp",
        HelpText = "Open the port on the router automatically (UPnP/NAT-PMP). Default is true.",
        Required = false)]
    public bool? UpnpEnabled { get; set; }

    [Option("password",
        HelpText =
            "Server password clients must enter to connect. Empty (--password \"\") = open server. Sent unencrypted - don't reuse an important password.",
        Required = false)]
    public string ServerPassword { get; set; }

    [Option("client-export",
        HelpText = "Exports the current clients every 5 seconds to a .json file. Default is false.",
        Required = false)]
    public bool? ClientExportEnabled { get; set; }

    [Option("client-export-path",
        HelpText =
            "Sets a custom client export file. Default is clients-list.json next to server.cfg; a relative path is relative to the folder of server.cfg.",
        Required = false)]
    public string ClientExportPath { get; set; }

    [Option("half-duplex",
        HelpText =
            "Half-duplex radios: a radio can't receive while it is transmitting. Default is false",
        Required = false)]
    public bool? RealRadioTX { get; set; }

    [Option("radio-interference",
        HelpText = "Radio interference: simultaneous transmissions on one frequency interfere. Default is false",
        Required = false)]
    public bool? RealRadioRX { get; set; }

    [Option("test-frequencies",
        HelpText =
            "Radio check (echo) frequencies in MHz, comma separated with '.' as decimal separator. Transmissions on them are played back to the sender. Default is 27.405,446.19375",
        Required = false)]
    public string TestFrequencies { get; set; }

    [Option("clean-frequencies",
        HelpText =
            "Clean frequencies in MHz, comma separated with '.' as decimal separator. Transmissions on them are played without radio effects. Default is none",
        Required = false)]
    public string CleanFrequencies { get; set; }

    [Option("show-tuned-count",
        HelpText =
            "Lets users see how many people are tuned to their frequency. Default is true",
        Required = false)]
    public bool? ShowTunedCount { get; set; }

    [Option("show-transmitter-name",
        HelpText = "Lets users see who's transmitting. Default is false",
        Required = false)]
    public bool? ShowTransmitterName { get; set; }

    // Removed in 1.1 (encryption, server channel presets and the server radio layout are gone). Still accepted -
    // hidden from --help and ignored with a warning - so start scripts and service files of 1.0 keep working.

    [Option("allow-encryption", Hidden = true, Required = false)]
    public bool? RemovedAllowEncryption { get; set; }

    [Option("strict-encryption", Hidden = true, Required = false)]
    public bool? RemovedStrictEncryption { get; set; }

    [Option("server-presets", Hidden = true, Required = false)]
    public bool? RemovedServerPresets { get; set; }

    [Option("server-radio-layout", Hidden = true, Required = false)]
    public bool? RemovedServerRadioLayout { get; set; }

    [Option("transmission-log",
        HelpText = "Log all transmissions to a CSV. Default is false.",
        Required = false)]
    public bool? TransmissionLogEnabled { get; set; }

    [Option("transmission-log-retention",
        HelpText = "Number of days of transmission logs to keep. Default is 2.",
        Required = false)]
    public int? TransmissionLogRetention { get; set; }

    [Option("http-server",
        HelpText = "Enables the HTTP admin API (X-API-KEY header, key in server.cfg / the log). Default is false.",
        Required = false)]
    public bool? HttpServerEnabled { get; set; }

    [Option("http-port",
        HelpText = "Sets the HTTP Server Port (1-65535) if Enabled. Default is 8080.",
        Required = false)]
    public int? HttpServerPort { get; set; }

    [Option("http-address",
       HelpText = "Sets the HTTP Server Address if Enabled. Default is localhost.",
       Required = false)]
    public string HttpServerAddress { get; set; }

    [Option('c', "cfg", Required = false,
        HelpText =
            "Configuration file path, e.g. --cfg=C:\\some-path\\server.cfg. Default is server.cfg next to the server executable; a relative path is relative to the current directory. banned.txt, the logs, the transmission logs and the client export live in the same folder.")]
    public string ConfigFile
    {
        get => _configFile;
        set
        {
            //tidy up if the value is  fg=xxxxx as it strips -c of the -cfg with a single -
            _configFile = value;

            if (_configFile != null)
            {
                _configFile = _configFile.Trim();
                if (_configFile.StartsWith("fg=")) _configFile = _configFile.Substring("fg=".Length);
            }
        }
    }

    /// <summary>Checks the values the parser can't check. Returns an error message, or null if everything is fine.</summary>
    public string Validate()
    {
        if (Port.HasValue && !ServerSettingsStore.IsValidPort(Port.Value))
            return $"Invalid --port {Port.Value}: the port must be between 1 and 65535.";

        if (HttpServerPort.HasValue && !ServerSettingsStore.IsValidPort(HttpServerPort.Value))
            return $"Invalid --http-port {HttpServerPort.Value}: the port must be between 1 and 65535.";

        return null;
    }

    /// <summary>The options of 1.0 that were removed in 1.1 and are given on the command line (they are ignored).</summary>
    public IEnumerable<string> RemovedOptionsUsed()
    {
        if (RemovedAllowEncryption.HasValue) yield return "--allow-encryption";
        if (RemovedStrictEncryption.HasValue) yield return "--strict-encryption";
        if (RemovedServerPresets.HasValue) yield return "--server-presets";
        if (RemovedServerRadioLayout.HasValue) yield return "--server-radio-layout";
    }

    public override string ToString()
    {
        return
            $"{nameof(ConfigFile)}: {ConfigFile}, \n" +
            $"{nameof(ConsoleLogs)}: {ConsoleLogs}, \n" +
            $"{nameof(Port)}: {Port}, \n" +
            $"{nameof(ServerBindIP)}: {ServerBindIP}, \n" +
            $"{nameof(UpnpEnabled)}: {UpnpEnabled}, \n" +
            $"{nameof(ServerPassword)}: {(ServerPassword == null ? "" : ServerSettingsStore.MaskSecret(ServerPassword))}, \n" +
            $"{nameof(ClientExportEnabled)}: {ClientExportEnabled}, \n" +
            $"{nameof(ClientExportPath)}: {ClientExportPath}, \n" +
            $"{nameof(RealRadioTX)}: {RealRadioTX}, \n" +
            $"{nameof(RealRadioRX)}: {RealRadioRX}, \n" +
            $"{nameof(TestFrequencies)}: {TestFrequencies}, \n" +
            $"{nameof(CleanFrequencies)}: {CleanFrequencies}, \n" +
            $"{nameof(ShowTunedCount)}: {ShowTunedCount}, \n" +
            $"{nameof(ShowTransmitterName)}: {ShowTransmitterName}, \n" +
            $"{nameof(TransmissionLogEnabled)}: {TransmissionLogEnabled}, \n" +
            $"{nameof(TransmissionLogRetention)}: {TransmissionLogRetention}, \n" +
            $"{nameof(HttpServerEnabled)}: {HttpServerEnabled}, \n" +
            $"{nameof(HttpServerPort)}: {HttpServerPort}, \n" +
            $"{nameof(HttpServerAddress)}: {HttpServerAddress}";
    }
}
