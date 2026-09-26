using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     Starts and stops the server parts (UDP voice router, TCP sync server, client export, HTTP API). If the server
///     can't be started (e.g. the port is in use) it is stopped again and a <see cref="ServerStartFailedMessage" /> is
///     published - what happens then (message box, exit code) is up to the host.
///     banned.txt and the client export live in the configuration folder (<see cref="ServerSettingsStore.ConfigDirectory" />).
/// </summary>
public class ServerState : IHandle<StartServerMessage>, IHandle<StopServerMessage>, IHandle<KickClientMessage>,
    IHandle<BanClientMessage>
{
    private static readonly string DEFAULT_CLIENT_EXPORT_FILE = "clients-list.json";
    private static readonly string BANNED_FILE = "banned.txt";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly HashSet<IPAddress> _bannedIps = new();

    private readonly ConcurrentDictionary<string, ClientInfo> _connectedClients =
        new();

    private readonly IEventAggregator _eventAggregator;

    // serialises start, stop and start failures
    private readonly object _lifecycleLock = new();

    private UDPVoiceRouter _serverListener;
    private ServerSync _serverSync;
    private CancellationTokenSource _exportCancellation;
    private HttpServer _httpServer;

    public ServerState(IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator;
        _eventAggregator.SubscribeOnPublishedThread(this);

        StartServer();
    }

    /// <summary>Why the last start failed (null if it did not fail).</summary>
    public string LastStartError { get; private set; }


    public async Task HandleAsync(BanClientMessage message, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            WriteBanIP(message.Client);
            KickClient(message.Client);
        });
    }

    public async Task HandleAsync(KickClientMessage message, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            var client = message.Client;
            KickClient(client);
        });
    }

    public async Task HandleAsync(StartServerMessage message, CancellationToken cancellationToken)
    {
        await Task.Run(StartServer);
    }

    public async Task HandleAsync(StopServerMessage message, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            StopServer();
            _eventAggregator.PublishOnUIThreadAsync(new ServerStateMessage(false,
                new List<ClientInfo>(_connectedClients.Values)));
        });
    }


    private static string DataDirectory => ServerSettingsStore.Instance.ConfigDirectory;

    /// <summary>
    ///     Starts all server parts and publishes a running <see cref="ServerStateMessage" />. Binding the ports happens in
    ///     the background; a failure stops the server again and publishes <see cref="ServerStartFailedMessage" />.
    /// </summary>
    private void StartServer()
    {
        Exception startError = null;

        lock (_lifecycleLock)
        {
            if (_serverListener != null) return;

            LastStartError = null;

            try
            {
                PopulateBanList();

                _serverListener = new UDPVoiceRouter(_connectedClients, _eventAggregator);
                _serverSync = new ServerSync(_connectedClients, _bannedIps, _eventAggregator);
            }
            catch (Exception ex)
            {
                StopServerLocked();
                startError = ex;
            }

            if (startError == null)
            {
                RunStartTask(_serverListener, _serverListener.Listen);
                RunStartTask(_serverSync, _serverSync.StartListeningAsync);

                StartExport();

                StartHttpServer();

                // published under the lock, so a start failure (published after it) always wins
                _eventAggregator.PublishOnUIThreadAsync(new ServerStateMessage(true,
                    new List<ClientInfo>(_connectedClients.Values)));
            }
        }

        // reported outside the lock - the host may show a (modal) message
        if (startError != null) ReportStartFailure($"Unable to start the server: {startError.Message}", startError);
    }

    /// <summary>Runs a start-up task of <paramref name="component" /> and stops the server if it fails.</summary>
    private void RunStartTask(object component, Func<Task> start)
    {
        Task.Run(async () =>
        {
            try
            {
                await start();
            }
            catch (Exception ex)
            {
                lock (_lifecycleLock)
                {
                    // a part of an earlier, already stopped run - nothing to report
                    if (!ReferenceEquals(component, _serverListener) && !ReferenceEquals(component, _serverSync))
                        return;

                    StopServerLocked();
                }

                ReportStartFailure(ex is ServerStartException ? ex.Message : $"Unable to start the server: {ex.Message}",
                    ex);
            }
        });
    }

    private void ReportStartFailure(string error, Exception ex)
    {
        LastStartError = error;
        Logger.Error(ex, error);

        _eventAggregator.PublishOnUIThreadAsync(new ServerStartFailedMessage(error));
        _eventAggregator.PublishOnUIThreadAsync(new ServerStateMessage(false, new List<ClientInfo>()));
    }

    private void StartHttpServer()
    {
        _httpServer = new HttpServer(_connectedClients, this);
        try
        {
            _httpServer.Start();
        }
        catch (Exception ex)
        {
            // e.g. access denied for a non-localhost address without a URL ACL - the voice server keeps running
            Logger.Error(ex, "Unable to start the HTTP API server - check HTTP_SERVER_ADDRESS / HTTP_SERVER_PORT");
            _httpServer = null;
        }
    }

    public void StopServer()
    {
        lock (_lifecycleLock)
        {
            StopServerLocked();
        }
    }

    private void StopServerLocked()
    {
        _exportCancellation?.Cancel();
        _exportCancellation = null;

        _serverSync?.RequestStop();
        _serverSync = null;
        _serverListener?.RequestStop();
        _serverListener = null;
        _httpServer?.Stop();
        _httpServer = null;
    }

    private void StartExport()
    {
        // one export loop per start: a loop of an earlier run that is still waiting must not keep writing
        var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        var token = cancellation.Token;

        Task.Run(async Task () =>
        {
            string exportFilePath = null;

            while (!token.IsCancellationRequested)
            {
                if (ServerSettingsStore.Instance.GetGeneralSetting(ServerSettingsKeys.CLIENT_EXPORT_ENABLED).BoolValue)
                {
                    exportFilePath ??= PrepareExportFile();

                    var data = new ClientListExport
                        { Clients = _connectedClients.Values, ServerVersion = AppVersion.Version };
                    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions()
                    {
                        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                        {
                            Modifiers = { JsonNetworkPropertiesResolver.StripNetworkIgnored }
                        },
                        IncludeFields = true,
                    }) + "\n";
                    try
                    {
                        await File.WriteAllTextAsync(exportFilePath, json, new UTF8Encoding(false, true), token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Logger.Error(e);
                    }
                }

                try
                {
                    await Task.Delay(5000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    /// <summary>Resolves the client export file and creates its folder (falls back to the default file on errors).</summary>
    private static string PrepareExportFile()
    {
        var dataDirectory = DataDirectory;
        var exportFilePath = ResolveExportFilePath(
            ServerSettingsStore.Instance.GetServerSetting(ServerSettingsKeys.CLIENT_EXPORT_FILE_PATH).StringValue,
            dataDirectory);

        var exportFileDirectory = Path.GetDirectoryName(exportFilePath);

        if (!string.IsNullOrEmpty(exportFileDirectory) && !Directory.Exists(exportFileDirectory))
        {
            Logger.Warn($"Client export directory \"{exportFileDirectory}\" does not exist, trying to create it");

            try
            {
                Directory.CreateDirectory(exportFileDirectory);
            }
            catch (Exception ex)
            {
                Logger.Error(ex,
                    $"Failed to create client export directory \"{exportFileDirectory}\", falling back to default path");

                // Failed to create desired client export directory, fall back to the default file in the configuration folder
                exportFilePath = Path.Combine(dataDirectory, DEFAULT_CLIENT_EXPORT_FILE);
            }
        }

        Logger.Info($"Exporting the client list to {exportFilePath}");
        return exportFilePath;
    }

    /// <summary>
    ///     Full path of the client export file: empty = clients-list.json in <paramref name="dataDirectory" />; a
    ///     relative path is resolved against <paramref name="dataDirectory" />; "file:///..." URIs are accepted. An
    ///     unusable value falls back to the default file (logged, never thrown).
    /// </summary>
    internal static string ResolveExportFilePath(string configuredPath, string dataDirectory)
    {
        var defaultPath = Path.Combine(dataDirectory, DEFAULT_CLIENT_EXPORT_FILE);

        var path = configuredPath?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(path)) return defaultPath;

        try
        {
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                path = new Uri(path).LocalPath;

            path = Path.GetFullPath(path, dataDirectory);

            if (string.IsNullOrEmpty(Path.GetFileName(path)))
                throw new ArgumentException("the path is a folder, not a file");

            return path;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Invalid client export path \"{configuredPath}\" - using {defaultPath}");
            return defaultPath;
        }
    }

    private void PopulateBanList()
    {
        try
        {
            _bannedIps.Clear();

            var path = Path.Combine(DataDirectory, BANNED_FILE);
            if (!File.Exists(path))
            {
                Logger.Info($"'{path}' was not found or you don't have permission to read the file");
                return;
            }

            foreach (var line in File.ReadAllLines(path))
                if (IPAddress.TryParse(line.Trim(), out var ip))
                {
                    Logger.Info($"Loaded Banned IP: {line}");
                    _bannedIps.Add(ip);
                }
        }
        catch (Exception)
        {
            Logger.Error($"Unable to read {BANNED_FILE}");
        }
    }


    public void KickClient(ClientInfo client)
    {
        if (client != null)
            try
            {
                _serverSync?.FindSession(client.ClientSession)?.Disconnect();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Error kicking client");
            }
    }

    public void WriteBanIP(ClientInfo client)
    {
        try
        {
            var remoteIpEndPoint = _serverSync?.FindSession(client.ClientSession)?.Socket.RemoteEndPoint as IPEndPoint;

            if (remoteIpEndPoint == null) return;

            _bannedIps.Add(remoteIpEndPoint.Address);

            File.AppendAllText(Path.Combine(DataDirectory, BANNED_FILE), remoteIpEndPoint.Address + "\r\n");

            KickClient(client);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error saving banned client");
        }
    }
}
