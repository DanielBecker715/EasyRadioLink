using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Common.Network.Client;

/// <summary>
///     TCP connection of the client.
///     <para>
///         Handshake: connect -> send SYNC {Client, Password, Version, Product} -> wait for the server's SYNC reply ->
///         <see cref="SyncedServerSettings" /> is filled, then <c>TCPClientStatusMessage(true, endpoint)</c> is published
///         (awaited on the UI thread) and finally a <see cref="ServerSettingsUpdatedMessage" />.
///     </para>
///     <para>
///         Every disconnect publishes exactly one <c>TCPClientStatusMessage(false, reason)</c> with reason
///         TIMEOUT (could not connect / connection lost), AUTH_FAILED (wrong password), MISMATCHED_SERVER (not an
///         EasyRadioLink server or unsupported version), INVALID_SERVER (no handshake reply) or USER_DISCONNECTED.
///     </para>
///     Radio/metadata changes are sent when a <see cref="UnitUpdateMessage" /> is published on the <see cref="EventBus" />
///     (FullUpdate = RADIO_UPDATE, otherwise UPDATE); unchanged state is only re-sent after
///     <see cref="Constants.CLIENT_UPDATE_INTERVAL_LIMIT" /> seconds.
/// </summary>
public class TCPClientHandler : IHandle<UnitUpdateMessage>
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private static readonly int MAX_DECODE_ERRORS = 5;
    private readonly ConnectedClientsSingleton _clients = ConnectedClientsSingleton.Instance;
    private readonly string _guid;
    private readonly string _serverPassword;

    private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private long _lastSent = -1;
    private ClientInfo _playerUnitState;
    private bool _pendingFullUpdate;
    private IPEndPoint _serverEndpoint;
    private TcpClient _tcpClient;
    private CancellationTokenSource _cts;

    private volatile bool _handshakeComplete;
    private volatile bool _stopRequested;
    private int _disconnectReason = (int)TCPClientStatusMessage.ErrorCode.TIMEOUT;

    /// <param name="guid">The client's 22 character ShortGuid (also used for UDP).</param>
    /// <param name="playerUnitState">Initial state sent with the SYNC hello (ClientGuid, Name, AllowRecord, RadioInfo).</param>
    /// <param name="serverPassword">Server password, null or "" for an open server.</param>
    public TCPClientHandler(string guid, ClientInfo playerUnitState, string serverPassword = null)
    {
        _clients.Clear();
        _guid = guid;
        _serverPassword = string.IsNullOrEmpty(serverPassword) ? null : serverPassword;
        _playerUnitState = playerUnitState?.DeepClone() ?? new ClientInfo();
        _playerUnitState.ClientGuid ??= guid;
    }

    /// <summary>True once the server accepted the SYNC handshake (until disconnected).</summary>
    public bool IsConnected => _handshakeComplete;

    public async Task HandleAsync(UnitUpdateMessage message, CancellationToken cancellationToken)
    {
        var update = message.UnitUpdate;
        if (update == null) return;

        update.ClientGuid = _guid;

        if (!_handshakeComplete)
        {
            // the server only accepts updates after the handshake - remember the latest state and send it afterwards
            lock (_stateLock)
            {
                if (message.FullUpdate)
                {
                    _playerUnitState = update;
                }
                else
                {
                    _playerUnitState.Name = update.Name;
                    _playerUnitState.AllowRecord = update.AllowRecord;
                }

                _pendingFullUpdate = true;
            }

            return;
        }

        if (message.FullUpdate)
            await ClientRadioUpdatedAsync(update);
        else
            await ClientMetadataUpdateAsync(update);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void TryConnect(IPEndPoint endpoint)
    {
        _serverEndpoint = endpoint;
        _stopRequested = false;

        //make absolutely sure we only connect once
        try
        {
            _tcpClient?.Close();
        }
        catch (Exception)
        {
        }

        Logger.Info($"TryConnect @ {_serverEndpoint}");
        var tcpThread = new Thread(Connect) { IsBackground = true };
        tcpThread.Start();
    }

    private async void Connect()
    {
        _lastSent = DateTime.Now.Ticks;
        _handshakeComplete = false;
        Volatile.Write(ref _disconnectReason, (int)TCPClientStatusMessage.ErrorCode.TIMEOUT);

        // forget the settings of a previous server
        _serverSettings.Reset();

        using (_cts = new CancellationTokenSource())
        {
            if (_stopRequested)
            {
                // the user cancelled before this thread started
                Volatile.Write(ref _disconnectReason, (int)TCPClientStatusMessage.ErrorCode.USER_DISCONNECTED);
                _cts.Cancel();
            }

            var token = _cts.Token;
            using (_tcpClient = new TcpClient())
            {
                try
                {
                    _tcpClient.SendTimeout = 90000;
                    _tcpClient.NoDelay = true;
                    using (var connectioncts = new CancellationTokenSource(ConnectTimeout))
                    {
                        using (var linkedcts = CancellationTokenSource.CreateLinkedTokenSource([token, connectioncts.Token]))
                        {
                            // Wait before aborting connection attempt - no server running/port opened in that case
                            // Also abort if the user decides to cancel.
                            await _tcpClient.ConnectAsync(_serverEndpoint.Address, _serverEndpoint.Port, linkedcts.Token);
                        }
                    }

                    if (_tcpClient.Connected && !token.IsCancellationRequested)
                    {
                        _tcpClient.NoDelay = true;

                        _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                        await ClientSyncLoopAsync(token);
                    }
                    else
                    {
                        Logger.Error($"Failed to connect to server @ {_serverEndpoint}");
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info($"Connection to {_serverEndpoint} cancelled or timed out");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Could not connect to server");
                }

                Disconnect();
            }
        }

        _cts = null;
    }

    private async Task ClientRadioUpdatedAsync(ClientInfo updatedUnitState)
    {
        bool send;
        lock (_stateLock)
        {
            //Only send if there is an actual change
            send = !_playerUnitState.MetaDataEquals(updatedUnitState)
                   || !RadioInfoEquals(_playerUnitState.RadioInfo, updatedUnitState.RadioInfo)
                   || TimeSpan.FromTicks(DateTime.Now.Ticks - _lastSent).TotalSeconds >
                   Constants.CLIENT_UPDATE_INTERVAL_LIMIT;

            if (send) _playerUnitState = updatedUnitState;
        }

        if (!send) return;

        Logger.Debug("Sending radio update to the server");

        await SendToServerAsync(new NetworkMessage
        {
            Client = updatedUnitState,
            MsgType = NetworkMessage.MessageType.RADIO_UPDATE
        });
    }

    private static bool RadioInfoEquals(PlayerRadioInfoBase a, PlayerRadioInfoBase b)
    {
        if (a == null || b == null) return ReferenceEquals(a, b);

        return a.Equals(b);
    }

    private async Task ClientMetadataUpdateAsync(ClientInfo updatedMetadata)
    {
        ClientInfo metadata = null;
        lock (_stateLock)
        {
            //only send if there is an actual change to metadata or the interval has passed since last update
            if (!_playerUnitState.MetaDataEquals(updatedMetadata)
                || TimeSpan.FromTicks(DateTime.Now.Ticks - _lastSent).TotalSeconds >
                Constants.CLIENT_UPDATE_INTERVAL_LIMIT)
            {
                _playerUnitState.AllowRecord = updatedMetadata.AllowRecord;
                _playerUnitState.Name = updatedMetadata.Name;

                metadata = new ClientInfo
                {
                    ClientGuid = _guid,
                    Name = updatedMetadata.Name,
                    AllowRecord = updatedMetadata.AllowRecord,
                    RadioInfo = null
                };
            }
        }

        if (metadata == null) return;

        Logger.Debug("Sending metadata update to the server");

        await SendToServerAsync(new NetworkMessage
        {
            Client = metadata,
            MsgType = NetworkMessage.MessageType.UPDATE
        });
    }

    private async Task ClientSyncLoopAsync(CancellationToken token)
    {
        EventBus.Instance.SubscribeOnBackgroundThread(this);
        //clear the clients list
        _clients.Clear();
        var decodeErrors = 0; //if the JSON is unreadable - not an EasyRadioLink server or a new version

        using (var reader = new StreamReader(_tcpClient.GetStream(), Encoding.UTF8))
        {
            try
            {
                ClientInfo hello;
                lock (_stateLock)
                {
                    hello = _playerUnitState.DeepClone();
                    _pendingFullUpdate = false;
                }

                //start the loop off by sending a SYNC Request
                await SendToServerAsync(new NetworkMessage
                {
                    Client = hello,
                    MsgType = NetworkMessage.MessageType.SYNC,
                    Password = _serverPassword
                });

                _ = WatchHandshakeAsync(token);

                string line;
                while ((line = await reader.ReadLineAsync(token)) != null)
                    try
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var serverMessage = NetworkMessage.Decode(line);
                        decodeErrors = 0; //reset counter
                        if (serverMessage == null) continue;

                        Logger.Debug("Received " + serverMessage.MsgType);

                        switch (serverMessage.MsgType)
                        {
                            case NetworkMessage.MessageType.PING:
                                // Do nothing for now
                                break;
                            case NetworkMessage.MessageType.RADIO_UPDATE:
                            case NetworkMessage.MessageType.UPDATE:

                                if (serverMessage.ServerSettings != null)
                                    _serverSettings.Decode(serverMessage.ServerSettings);

                                if (serverMessage.Client?.ClientGuid == null) break;

                                ClientInfo srClient;
                                if (_clients.TryGetValue(serverMessage.Client.ClientGuid, out srClient))
                                {
                                    if (serverMessage.MsgType == NetworkMessage.MessageType.RADIO_UPDATE)
                                        HandleFullUpdate(serverMessage, srClient);
                                    else
                                        HandlePartialUpdate(serverMessage, srClient);
                                }
                                else
                                {
                                    srClient = serverMessage.Client;
                                    srClient.RadioInfo?.EnsureValid();

                                    _clients[srClient.ClientGuid] = srClient;
                                }

                                await EventBus.Instance.PublishOnBackgroundThreadAsync(new ClientUpdateMessage(srClient));

                                break;
                            case NetworkMessage.MessageType.SYNC:
                                await HandleSyncReplyAsync(serverMessage);
                                break;

                            case NetworkMessage.MessageType.SERVER_SETTINGS:

                                _serverSettings.Decode(serverMessage.ServerSettings);

                                break;
                            case NetworkMessage.MessageType.CLIENT_DISCONNECT:

                                if (serverMessage.Client?.ClientGuid == null) break;

                                _clients.TryRemove(serverMessage.Client.ClientGuid, out var outClient);

                                if (outClient != null)
                                    await EventBus.Instance.PublishOnBackgroundThreadAsync(
                                        new ClientUpdateMessage(outClient, false));

                                break;
                            case NetworkMessage.MessageType.VERSION_MISMATCH:
                                Logger.Error(
                                    $"Server rejected this client (protocol {AppVersion.ProtocolVersion}) - server protocol {serverMessage.Version} - Disconnecting");

                                SetDisconnectReason(TCPClientStatusMessage.ErrorCode.MISMATCHED_SERVER);
                                ShowVersionMismatchWarning(serverMessage.Version);

                                await RequestDisconnectAsync();
                                break;
                            case NetworkMessage.MessageType.AUTH_FAILED:
                                Logger.Warn("Server rejected the password - Disconnecting");

                                SetDisconnectReason(TCPClientStatusMessage.ErrorCode.AUTH_FAILED);

                                await RequestDisconnectAsync();
                                break;
                            default:
                                Logger.Error($"Received unknown {line}");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        decodeErrors++;
                        Logger.Error(ex, "Client exception reading from socket ");

                        if (decodeErrors > MAX_DECODE_ERRORS)
                        {
                            SetDisconnectReason(TCPClientStatusMessage.ErrorCode.MISMATCHED_SERVER);
                            ShowVersionMismatchWarning("unknown");
                            await RequestDisconnectAsync();
                            break;
                        }
                    }

                // do something with line
            }
            catch (OperationCanceledException)
            {
                Logger.Info("Disconnecting.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Client exception reading - Disconnecting ");
            }
        }

        //clear the clients list
        _clients.Clear();
    }

    private async Task HandleSyncReplyAsync(NetworkMessage serverMessage)
    {
        //check the server product and protocol version
        if (!AppVersion.IsSupportedProduct(serverMessage.Product) ||
            !AppVersion.IsSupportedProtocolVersion(serverMessage.Version))
        {
            Logger.Error(
                $"Server '{serverMessage.Product}' protocol version '{serverMessage.Version}' is not supported (minimum {AppVersion.MinimumProtocolVersion}) - disconnecting");

            SetDisconnectReason(TCPClientStatusMessage.ErrorCode.MISMATCHED_SERVER);
            ShowVersionMismatchWarning(serverMessage.Version);

            await RequestDisconnectAsync();
            return;
        }

        if (serverMessage.Clients != null)
            foreach (var client in serverMessage.Clients)
            {
                if (client?.ClientGuid == null) continue;

                client.RadioInfo?.EnsureValid();
                _clients[client.ClientGuid] = client;

                await EventBus.Instance.PublishOnBackgroundThreadAsync(
                    new ClientUpdateMessage(client));
            }

        //add server settings
        _serverSettings.Decode(serverMessage.ServerSettings ?? new Dictionary<string, string>(), false);
        _serverSettings.ServerVersion = serverMessage.Version;

        if (_handshakeComplete)
        {
            _serverSettings.PublishSettingsUpdated();
            return;
        }

        _handshakeComplete = true;
        Logger.Info($"Connected to EasyRadioLink server @ {_serverEndpoint} (protocol {serverMessage.Version})");

        // "connected" = authenticated and synchronised; the UI starts audio and the radio sync on this message
        await EventBus.Instance.PublishOnUIThreadAsync(new TCPClientStatusMessage(true, _serverEndpoint));

        _serverSettings.PublishSettingsUpdated();

        // send anything that was published while the handshake was running
        ClientInfo pending = null;
        lock (_stateLock)
        {
            if (_pendingFullUpdate)
            {
                pending = _playerUnitState.DeepClone();
                _pendingFullUpdate = false;
            }
        }

        if (pending != null)
            await SendToServerAsync(new NetworkMessage
            {
                Client = pending,
                MsgType = NetworkMessage.MessageType.RADIO_UPDATE
            });
    }

    private async Task WatchHandshakeAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(HandshakeTimeout, token);

            if (!_handshakeComplete)
            {
                Logger.Error($"No handshake reply from {_serverEndpoint} within {HandshakeTimeout.TotalSeconds} s - disconnecting");
                SetDisconnectReason(TCPClientStatusMessage.ErrorCode.INVALID_SERVER);
                await RequestDisconnectAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // disconnected before the timeout
        }
    }

    private void HandlePartialUpdate(NetworkMessage networkMessage, ClientInfo client)
    {
        var updatedSrClient = networkMessage.Client;

        client.AllowRecord = updatedSrClient.AllowRecord;
        client.ClientGuid = updatedSrClient.ClientGuid;
        client.Name = updatedSrClient.Name;
    }

    private void HandleFullUpdate(NetworkMessage networkMessage, ClientInfo client)
    {
        HandlePartialUpdate(networkMessage, client);

        if (networkMessage.Client.RadioInfo != null)
        {
            networkMessage.Client.RadioInfo.EnsureValid();
            client.RadioInfo = networkMessage.Client.RadioInfo;
        }
    }

    private void ShowVersionMismatchWarning(string serverVersion)
    {
        EventBus.Instance.PublishOnUIThreadAsync(new InvalidServerVersionMessage(serverVersion));
    }

    private void SetDisconnectReason(TCPClientStatusMessage.ErrorCode reason)
    {
        // the first specific reason wins
        Interlocked.CompareExchange(ref _disconnectReason, (int)reason,
            (int)TCPClientStatusMessage.ErrorCode.TIMEOUT);
    }

    private async Task SendToServerAsync(NetworkMessage message)
    {
        var tcpClient = _tcpClient;
        if (tcpClient == null) return;

        try
        {
            var json = message.Encode();

            if (message.MsgType == NetworkMessage.MessageType.RADIO_UPDATE)
                Logger.Debug("Sending Radio Update To Server: " + json);

            var bytes = Encoding.UTF8.GetBytes(json);

            await _writeLock.WaitAsync();
            try
            {
                _lastSent = DateTime.Now.Ticks;
                await tcpClient.GetStream().WriteAsync(bytes);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Client exception sending to server");

            await RequestDisconnectAsync();
        }
    }

    /// <summary>
    ///     Closes the connection. <paramref name="userInitiated" /> = true reports
    ///     <see cref="TCPClientStatusMessage.ErrorCode.USER_DISCONNECTED" /> (no error UI).
    /// </summary>
    public async Task RequestDisconnectAsync(bool userInitiated = false)
    {
        if (userInitiated)
        {
            Volatile.Write(ref _disconnectReason, (int)TCPClientStatusMessage.ErrorCode.USER_DISCONNECTED);
            _stopRequested = true;
        }

        var cts = _cts;
        if (cts != null)
        {
            try
            {
                await cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // already disconnected
            }
        }
    }

    private void Disconnect()
    {
        Logger.Info("Disconnecting from server...");
        EventBus.Instance.Unsubscribe(this);

        _lastSent = DateTime.Now.Ticks;
        _handshakeComplete = false;

        try
        {
            _tcpClient?.Close(); // this'll stop the socket blocking
            _tcpClient = null;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Error closing tcp client");
        }

        var reason = (TCPClientStatusMessage.ErrorCode)Volatile.Read(ref _disconnectReason);
        Logger.Info($"Disconnected ({reason}).");

        EventBus.Instance.PublishOnUIThreadAsync(new TCPClientStatusMessage(false, reason));
    }
}
