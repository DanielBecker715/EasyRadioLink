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

public class ServerState : IHandle<StartServerMessage>, IHandle<StopServerMessage>, IHandle<KickClientMessage>,
    IHandle<BanClientMessage>
{
    private static readonly string DEFAULT_CLIENT_EXPORT_FILE = "clients-list.json";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly HashSet<IPAddress> _bannedIps = new();

    private readonly ConcurrentDictionary<string, ClientInfo> _connectedClients =
        new();

    private readonly IEventAggregator _eventAggregator;
    private UDPVoiceRouter _serverListener;
    private ServerSync _serverSync;
    private volatile bool _stop = true;
    private HttpServer _httpServer;

    public ServerState(IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator;
        _eventAggregator.SubscribeOnPublishedThread(this);

        StartServer();
    }


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
        await Task.Run(() =>
        {
            StartServer();
            _eventAggregator.PublishOnUIThreadAsync(new ServerStateMessage(true,
                new List<ClientInfo>(_connectedClients.Values)));
        });
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


    private static string GetCurrentDirectory()
    {
        //To get the location the assembly normally resides on disk or the install directory
        var currentPath = AppContext.BaseDirectory;

        //once you have the path you get the directory with:
        var currentDirectory = Path.GetDirectoryName(currentPath);

        if (currentDirectory.StartsWith("file:\\")) currentDirectory = currentDirectory.Replace("file:\\", "");

        return currentDirectory;
    }

    private void StartServer()
    {
        if (_serverListener == null)
        {
            PopulateBanList();
            _stop = false;
            _serverListener = new UDPVoiceRouter(_connectedClients, _eventAggregator);
            Task.Run(_serverListener.Listen);

            _serverSync = new ServerSync(_connectedClients, _bannedIps, _eventAggregator);
            Task.Run(_serverSync.StartListeningAsync);

            StartExport();

            StartHttpServer();
        }
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
        if (_serverListener != null)
        {
            _stop = true;
            _serverSync.RequestStop();
            _serverSync = null;
            _serverListener.RequestStop();
            _serverListener = null;
            _httpServer?.Stop();
            _httpServer = null;
        }
    }

    private void StartExport()
    {
        _stop = false;

        var exportFilePath = ServerSettingsStore.Instance.GetServerSetting(ServerSettingsKeys.CLIENT_EXPORT_FILE_PATH)
            .StringValue;
        if (string.IsNullOrWhiteSpace(exportFilePath) || exportFilePath == DEFAULT_CLIENT_EXPORT_FILE)
            // Make sure we're using a full file path in case we're falling back to default values
            exportFilePath = Path.Combine(GetCurrentDirectory(), DEFAULT_CLIENT_EXPORT_FILE);
        else
            // Normalize file path read from config to ensure properly escaped local path
            exportFilePath = NormalizePath(exportFilePath);

        var exportFileDirectory = Path.GetDirectoryName(exportFilePath);

        if (!Directory.Exists(exportFileDirectory))
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

                // Failed to create desired client export directory, fall back to default path in current application directory
                exportFilePath = NormalizePath(Path.Combine(GetCurrentDirectory(), DEFAULT_CLIENT_EXPORT_FILE));
            }
        }

        Task.Run(async Task () =>
        {
            while (!_stop)
            {
                if (ServerSettingsStore.Instance.GetGeneralSetting(ServerSettingsKeys.CLIENT_EXPORT_ENABLED).BoolValue)
                {
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
                        await File.WriteAllTextAsync(exportFilePath, json, new UTF8Encoding(false, true));
                    }
                    catch (IOException e)
                    {
                        Logger.Error(e);
                    }
                }

                await Task.Delay(5000);
            }
        });
    }

    private void PopulateBanList()
    {
        try
        {
            _bannedIps.Clear();

            var path = Path.Combine(GetCurrentDirectory(), "banned.txt");
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
            Logger.Error("Unable to read banned.txt");
        }
    }


    private static string NormalizePath(string path)
    {
        // Taken from https://stackoverflow.com/a/21058121 on 2018-06-22
        return Path.GetFullPath(new Uri(path).LocalPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }


    public void KickClient(ClientInfo client)
    {
        if (client != null)
            try
            {
                _serverSync.FindSession(client.ClientSession)?.Disconnect();
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
            var remoteIpEndPoint = _serverSync.FindSession(client.ClientSession)?.Socket.RemoteEndPoint as IPEndPoint;

            if (remoteIpEndPoint == null) return;

            _bannedIps.Add(remoteIpEndPoint.Address);

            File.AppendAllText(GetCurrentDirectory() + Path.DirectorySeparatorChar + "banned.txt",
                remoteIpEndPoint.Address + "\r\n");
            
            KickClient(client);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error saving banned client");
        }
    }
}