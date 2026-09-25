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

internal class Program : IHandle<ClientConnectionMessage>
{
    private readonly EventAggregator _eventAggregator = new();
    private ServerState _serverState;

    public Program()
    {
        SetupLogging();
    }

    public bool ConsoleLogs { get; set; }

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

    private static async Task Main(string[] args)
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
        var parser = Parser.Default.ParseArguments<Options>(args);
        await parser.WithParsedAsync(ProcessArgsAsync);
    }

    private static async Task ProcessArgsAsync(Options options)
    {
        if (options.ConfigFile != null && options.ConfigFile.Trim().Length > 0)
            ServerSettingsStore.CFG_FILE_NAME = options.ConfigFile.Trim();

        Console.WriteLine($"{AppVersion.Product} Server (command line) {AppVersion.Version}");
        Console.WriteLine($"Settings From Command Line: \n{options}");

        var p = new Program();
        var serverThread = new Thread(() => { p.StartServer(options); });
        serverThread.Start();

        var completionSource = new TaskCompletionSource();
        using var waitForMainExit = new ManualResetEventSlim();

        Console.CancelKeyPress += new((_, e) =>
        {
            // Ignore the cancel here and there, allow the program to terminate.
            e.Cancel = true;
            completionSource.TrySetResult();
            Console.WriteLine("Shutting down gracefully...");
        });
        using var termSignal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, (_) => {
            // We got a SIGTERM, signal that graceful shutdown has started
            completionSource.TrySetResult();
            Console.WriteLine("Shutting down gracefully...");
            // Don't unwind until main exists
            waitForMainExit.Wait();
        });

        Console.WriteLine("Waiting for shutdown SIGTERM");
        // Wait for shutdown to start
        await completionSource.Task;

        // This is where the application performs graceful shutdown
        p.StopServer();

        Console.WriteLine("Shutdown complete");
        // Now we're done with main, tell the shutdown handler
        waitForMainExit.Set();
        serverThread.Join(TimeSpan.FromSeconds(5));
    }

    private void StopServer()
    {
        _serverState?.StopServer();

        EventBus.Instance.Unsubscribe(this);
    }


    public void StartServer(Options options)
    {
        EventBus.Instance.SubscribeOnPublishedThread(this);

        ConsoleLogs = options.ConsoleLogs;

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
        if (options.AllowRadioEncryption.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.ALLOW_RADIO_ENCRYPTION, options.AllowRadioEncryption.Value);
        if (options.StrictRadioEncryption.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.STRICT_RADIO_ENCRYPTION, options.StrictRadioEncryption.Value);
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
        if (options.ServerPresetChannelsEnabled.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.SERVER_PRESETS_ENABLED, options.ServerPresetChannelsEnabled.Value);
        if (options.ServerRadioPresetEnabled.HasValue)
            store.SetGeneralSetting(ServerSettingsKeys.SERVER_RADIO_PRESET_ENABLED, options.ServerRadioPresetEnabled.Value);
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

        var config = new LoggingConfiguration();
        var fileTarget = new FileTarget
        {
            FileName = "serverlog.txt",
            ArchiveFileName = "serverlog.old.txt",
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
        HelpText = "Show basic console logs (client connect/disconnect).",
        Default = true,
        Required = false)]
    public bool ConsoleLogs { get; set; }

    [Option('p', "port",
        HelpText = "TCP and UDP port - 5010 is the default",
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
        HelpText = "Sets a custom client export path. Default is clients-list.json next to the server. It must be the full path!",
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

    [Option("allow-encryption",
        HelpText = "Allows radios that support it to encrypt (scramble) their transmissions. Default is true",
        Required = false)]
    public bool? AllowRadioEncryption { get; set; }

    [Option("strict-encryption",
        HelpText =
            "Encrypted radios only understand transmissions with the same key (unencrypted ones are scrambled too). Default is false.",
        Required = false)]
    public bool? StrictRadioEncryption { get; set; }

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
            "Lets users see how many people are tuned to each frequency. Default is true",
        Required = false)]
    public bool? ShowTunedCount { get; set; }

    [Option("show-transmitter-name",
        HelpText = "Lets users see who's transmitting. Default is false",
        Required = false)]
    public bool? ShowTransmitterName { get; set; }

    [Option("server-presets",
        HelpText =
            "Offers server channel presets to clients - put one *.txt file per radio name (lines \"Name|MHz\") in a folder called Presets next to your server.cfg file. Default is false",
        Required = false)]
    public bool? ServerPresetChannelsEnabled { get; set; }

    [Option("server-radio-layout",
        HelpText =
            "Makes clients use the server radio layout - put a server-radios.json file (same format as the client's radios.json) next to your server.cfg file. Default is false",
        Required = false)]
    public bool? ServerRadioPresetEnabled { get; set; }

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
        HelpText = "Sets the HTTP Server Port if Enabled. Default is 8080.",
        Required = false)]
    public int? HttpServerPort { get; set; }

    [Option("http-address",
       HelpText = "Sets the HTTP Server Address if Enabled. Default is localhost.",
       Required = false)]
    public string HttpServerAddress { get; set; }

    [Option('c', "cfg", Required = false,
        HelpText =
            "Configuration file path, e.g. --cfg=C:\\some-path\\server.cfg (Presets and server-radios.json are read from the same folder)")]
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
            $"{nameof(AllowRadioEncryption)}: {AllowRadioEncryption}, \n" +
            $"{nameof(StrictRadioEncryption)}: {StrictRadioEncryption}, \n" +
            $"{nameof(TestFrequencies)}: {TestFrequencies}, \n" +
            $"{nameof(CleanFrequencies)}: {CleanFrequencies}, \n" +
            $"{nameof(ShowTunedCount)}: {ShowTunedCount}, \n" +
            $"{nameof(ShowTransmitterName)}: {ShowTransmitterName}, \n" +
            $"{nameof(ServerPresetChannelsEnabled)}: {ServerPresetChannelsEnabled}, \n" +
            $"{nameof(ServerRadioPresetEnabled)}: {ServerRadioPresetEnabled}, \n" +
            $"{nameof(TransmissionLogEnabled)}: {TransmissionLogEnabled}, \n" +
            $"{nameof(TransmissionLogRetention)}: {TransmissionLogRetention}, \n" +
            $"{nameof(HttpServerEnabled)}: {HttpServerEnabled}, \n" +
            $"{nameof(HttpServerPort)}: {HttpServerPort}, \n" +
            $"{nameof(HttpServerAddress)}: {HttpServerAddress}";
    }
}
