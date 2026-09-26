using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.NetCoreServer;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     TCP side of the server. Handshake: the first message of a connection must be a SYNC hello
///     (product + protocol version check -> VERSION_MISMATCH, password check -> AUTH_FAILED); only then the client is
///     registered, receives the client list + settings and is announced to everybody else. Messages are only sent to
///     registered sessions.
/// </summary>
public class ServerSync : TcpServer, IHandle<ServerSettingsChangedMessage>
{
    // Unregistered connections are closed after this time
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    // AUTH_FAILED is only sent after this delay - slows down password guessing
    private static readonly TimeSpan AuthFailedDelay = TimeSpan.FromSeconds(1);

    private const int MaxNameLength = 64;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly HashSet<IPAddress> _bannedIps;

    private readonly ConcurrentDictionary<string, ClientInfo> _clients = new();
    private readonly IEventAggregator _eventAggregator;
    private readonly NatHandler _natHandler;

    private readonly ServerSettingsStore _serverSettings;

    private readonly object _registrationLock = new();

    private readonly AuthFailureThrottle _authFailures = new();

    // guards Start()/RequestStop() so a stop during start-up can't leave a listening server behind
    private readonly object _lifecycleLock = new();
    private volatile bool _stopRequested;

    private Timer _handshakeTimer;

    public ServerSync(ConcurrentDictionary<string, ClientInfo> connectedClients, HashSet<IPAddress> _bannedIps,
        IEventAggregator eventAggregator) : base(ServerSettingsStore.Instance.GetServerIP(),
        ServerSettingsStore.Instance.GetServerPort())
    {
        _clients = connectedClients;
        this._bannedIps = _bannedIps;
        _eventAggregator = eventAggregator;
        _eventAggregator.SubscribeOnPublishedThread(this);
        _serverSettings = ServerSettingsStore.Instance;

        OptionKeepAlive = true;
        if (_serverSettings.GetServerSetting(ServerSettingsKeys.UPNP_ENABLED).BoolValue)
        {
            _natHandler = new(_serverSettings.GetServerPort());
        }
    }

    public async Task HandleAsync(ServerSettingsChangedMessage message, CancellationToken token)
    {
        await Task.Run(() =>
        {
            try
            {
                HandleServerSettingsMessage();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Exception Sending Server Settings ");
            }
        });
    }

    protected override TcpSession CreateSession()
    {
        return new RadioClientSession(this, _bannedIps);
    }

    protected override void OnError(SocketError error)
    {
        Logger.Error($"TCP SERVER ERROR: {error} ");
    }

    // Connection limits - generous for players behind one NAT, small enough that one address can't tie up the server.
    internal const int MaxConnectionsPerAddress = 32;
    internal const int MaxPendingHandshakesPerAddress = 8;
    internal const int MaxConnections = 1000;

    /// <summary>
    ///     True if accepting <paramref name="newSession" /> would exceed the connection limits (per address, pending
    ///     handshakes per address, total).
    /// </summary>
    public bool ExceedsConnectionLimits(RadioClientSession newSession, out string reason)
    {
        int total = 0, fromAddress = 0, pendingFromAddress = 0;
        foreach (var session in Sessions.Values)
        {
            if (session is not RadioClientSession other || !other.IsConnected) continue;

            total++;
            if (other == newSession || !Equals(other.RemoteIp, newSession.RemoteIp)) continue;

            fromAddress++;
            if (other.ClientGuid == null) pendingFromAddress++;
        }

        reason = total > MaxConnections ? $"server full ({MaxConnections} connections)"
            : fromAddress >= MaxConnectionsPerAddress ? $"too many connections from this address ({MaxConnectionsPerAddress})"
            : pendingFromAddress >= MaxPendingHandshakesPerAddress ? "too many unfinished handshakes from this address"
            : null;

        return reason != null;
    }

    /// <summary>True while connections from <paramref name="address" /> are refused after too many wrong passwords.</summary>
    public bool IsLockedOut(IPAddress address)
    {
        return _authFailures.IsLockedOut(address, DateTime.UtcNow);
    }

    /// <summary>
    ///     Starts listening, then opens the port on the router (UPnP/NAT-PMP) if enabled.
    ///     Throws <see cref="ServerStartException" /> if the TCP port can't be bound; does nothing if
    ///     <see cref="RequestStop" /> was called before.
    /// </summary>
    public async Task StartListeningAsync()
    {
        OptionKeepAlive = true;

        lock (_lifecycleLock)
        {
            if (_stopRequested) return;

            Exception error = null;
            bool started;
            try
            {
                started = Start();
            }
            catch (Exception ex)
            {
                error = ex;
                started = false;
            }

            if (!started)
                throw new ServerStartException(
                    $"Unable to start the server on TCP {Endpoint}: the port is already in use (is another EasyRadioLink server running?) or the bind IP is wrong.",
                    error);

            _handshakeTimer = new Timer(_ => DisconnectStaleHandshakes(), null, TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));
        }

        Logger.Info(
            $"EasyRadioLink server {AppVersion.Version} (protocol {AppVersion.ProtocolVersion}) listening on {Endpoint} - " +
            (_serverSettings.IsPasswordProtected ? "password protected" : "open server (no password)"));

        var handler = _natHandler;
        if (handler == null) return;

        await handler.OpenNATAsync();

        // stopped while the router was being asked - remove the port mapping again
        if (_stopRequested) await handler.CloseNATAsync();
    }

    public void HandleDisconnect(RadioClientSession state)
    {
        Logger.Info("Disconnecting Client");

        string guid;
        lock (_registrationLock)
        {
            guid = state?.ClientGuid;
            if (guid == null)
            {
                Logger.Info("Removed Disconnected Unknown Client");
                return;
            }

            state.ClientGuid = null;

            // Only remove the entry if it still belongs to this session (it may have been taken over by a reconnect)
            if (!_clients.TryGetValue(guid, out var registered) || registered.ClientSession != state.Id) return;

            _clients.TryRemove(guid, out _);
        }

        Logger.Info("Removed Disconnected Client " + guid);

        HandleClientDisconnect(state, guid);

        try
        {
            _eventAggregator.PublishOnUIThreadAsync(
                new ServerStateMessage(true, new List<ClientInfo>(_clients.Values), guid));
        }
        catch (Exception ex)
        {
            Logger.Info(ex, "Exception Publishing Client Update After Disconnect");
        }
    }


    public void HandleMessage(RadioClientSession state, NetworkMessage message)
    {
        try
        {
            if (message == null || state.Rejected) return;

            Logger.Debug($"Received:  Msg - {message.MsgType} from {state.ClientGuid}");

            if (message.MsgType == NetworkMessage.MessageType.SYNC)
            {
                HandleSync(state, message);
                return;
            }

            // everything else requires a completed handshake
            if (state.ClientGuid == null)
            {
                Logger.Warn($"Disconnecting {state.RemoteAddress} - {message.MsgType} received before the handshake");
                state.Disconnect();
                return;
            }

            switch (message.MsgType)
            {
                case NetworkMessage.MessageType.PING:
                    // Do nothing for now
                    break;
                case NetworkMessage.MessageType.UPDATE: //Partial metadata update
                    if (message.Client == null) break;

                    if (!HandleClientMetaDataUpdate(state, message, true, out var client) && client != null)
                        if (state.ShouldSendFullRadioUpdate())
                            SendFullRadioUpdate(state, client);

                    break;
                case NetworkMessage.MessageType.RADIO_UPDATE: //Full update with radio info
                    if (message.Client == null) break;

                    var sent = HandleClientMetaDataUpdate(state, message, false, out _);
                    HandleClientRadioUpdate(state, message, sent);
                    break;
                case NetworkMessage.MessageType.SERVER_SETTINGS:
                    SendServerSettings(state);
                    break;
                default:
                    Logger.Warn($"Received unknown message type {message.MsgType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Exception Handling Message " + ex.Message);
        }
    }

    /// <summary>
    ///     Handshake: validate the hello, check product/version and password, register the client, reply with the
    ///     client list and settings and announce the new client.
    /// </summary>
    private void HandleSync(RadioClientSession state, NetworkMessage message)
    {
        var remote = state.RemoteAddress;

        if (state.ClientGuid != null)
        {
            // already registered on this connection - just resend the current state
            if (message.Client?.ClientGuid == state.ClientGuid)
                SendSyncReply(state);
            else
                state.Disconnect();

            return;
        }

        if (!AppVersion.IsSupportedProduct(message.Product) || !AppVersion.IsSupportedProtocolVersion(message.Version))
        {
            Logger.Warn(
                $"Disconnecting {remote} - unsupported client (product '{message.Product}', protocol version '{message.Version}')");
            HandleVersionMismatch(state);
            state.Disconnect();
            return;
        }

        var srClient = message.Client;
        if (srClient?.ClientGuid == null || srClient.ClientGuid.Length != UDPVoicePacket.GuidLength ||
            Encoding.UTF8.GetByteCount(srClient.ClientGuid) != UDPVoicePacket.GuidLength)
        {
            Logger.Warn($"Disconnecting {remote} - invalid client id");
            state.Disconnect();
            return;
        }

        // connections opened before the address was locked out get no further attempts either
        if (IsLockedOut(state.RemoteIp))
        {
            Logger.Info($"Disconnecting {remote} - too many wrong passwords, try again later");
            state.Rejected = true;
            state.Disconnect();
            return;
        }

        if (!_serverSettings.CheckPassword(message.Password))
        {
            RejectWrongPassword(state, srClient);
            return;
        }

        _authFailures.RecordSuccess(state.RemoteIp);

        srClient.Name = SanitiseName(srClient.Name);
        srClient.RadioInfo ??= new PlayerRadioInfoBase();
        srClient.RadioInfo.EnsureValid();
        srClient.Muted = false;
        srClient.VoipPort = null;
        srClient.SessionAddress = state.RemoteIp;
        srClient.ClientSession = state.Id;

        RadioClientSession previousSession = null;
        lock (_registrationLock)
        {
            // Same client id reconnecting while its old connection is still open: the new connection takes over
            if (_clients.TryGetValue(srClient.ClientGuid, out var existing) && existing.ClientSession != state.Id)
            {
                previousSession = FindSession(existing.ClientSession) as RadioClientSession;
                if (previousSession != null) previousSession.ClientGuid = null;
            }

            _clients[srClient.ClientGuid] = srClient;
            state.ClientGuid = srClient.ClientGuid;
        }

        if (previousSession != null)
        {
            Logger.Info($"Client {srClient} reconnected from {remote} (previously {previousSession.RemoteAddress}) - closing its previous connection");
            previousSession.Disconnect();
        }

        Logger.Info($"Client {srClient} connected from {remote}");

        _eventAggregator.PublishOnUIThreadAsync(new ServerStateMessage(true,
            new List<ClientInfo>(_clients.Values)));

        SendSyncReply(state);

        //send update to everyone
        var update = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.RADIO_UPDATE,
            Client = new ClientInfo
            {
                ClientGuid = srClient.ClientGuid,
                RadioInfo = srClient.RadioInfo,
                Name = srClient.Name,
                AllowRecord = srClient.AllowRecord
            }
        };

        MulticastToRegistered(update.Encode());
        state.LastFullRadioSent = DateTime.Now.Ticks;
    }

    /// <summary>
    ///     Wrong password: ignore everything else on this connection, answer AUTH_FAILED after a short delay and close
    ///     it. Too many failures lock the IP address out for a while (<see cref="AuthFailureThrottle" />).
    /// </summary>
    private void RejectWrongPassword(RadioClientSession state, ClientInfo srClient)
    {
        state.Rejected = true;

        var lockedOut = _authFailures.RecordFailure(state.RemoteIp, DateTime.UtcNow);
        Logger.Warn($"Disconnecting {state.RemoteAddress} ({SanitiseName(srClient.Name)}) - wrong server password" +
                    (lockedOut
                        ? $" - too many attempts, connections from this address are refused for {AuthFailureThrottle.LockoutDuration.TotalMinutes} minutes"
                        : ""));

        Task.Delay(AuthFailedDelay).ContinueWith(_ =>
        {
            try
            {
                state.Send(new NetworkMessage { MsgType = NetworkMessage.MessageType.AUTH_FAILED }.Encode());
                state.Disconnect();
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Unable to send AUTH_FAILED");
            }
        });
    }

    private void SendSyncReply(RadioClientSession session)
    {
        var replyMessage = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.SYNC,
            Clients = new List<ClientInfo>(_clients.Values),
            ServerSettings = _serverSettings.ToDictionary()
        };

        session.Send(replyMessage.Encode());
    }

    private static string SanitiseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var builder = new StringBuilder(Math.Min(name.Length, MaxNameLength));
        foreach (var c in name.Trim())
        {
            if (char.IsControl(c)) continue;
            if (builder.Length >= MaxNameLength) break;
            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private void HandleServerSettingsMessage()
    {
        //send server settings
        var replyMessage = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.SERVER_SETTINGS,
            ServerSettings = _serverSettings.ToDictionary()
        };

        MulticastToRegistered(replyMessage.Encode());
    }

    private void SendServerSettings(RadioClientSession session)
    {
        var replyMessage = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.SERVER_SETTINGS,
            ServerSettings = _serverSettings.ToDictionary()
        };

        session.Send(replyMessage.Encode());
    }

    private void HandleVersionMismatch(RadioClientSession session)
    {
        var replyMessage = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.VERSION_MISMATCH
        };
        session.Send(replyMessage.Encode());
    }

    private bool HandleClientMetaDataUpdate(RadioClientSession session, NetworkMessage message, bool send,
        out ClientInfo client)
    {
        var changed = false;
        var guid = session.ClientGuid;
        client = null;

        if (guid != null && _clients.TryGetValue(guid, out client) && client != null)
        {
            var name = SanitiseName(message.Client.Name);

            changed = client.Name != (string.IsNullOrEmpty(name) ? "---" : name)
                      || client.AllowRecord != message.Client.AllowRecord;

            //check timeout
            if (changed || session.ShouldSendMetadataUpdate())
            {
                //copy the data we need
                client.Name = name;
                client.AllowRecord = message.Client.AllowRecord;

                if (send)
                {
                    //send update to everyone - without radio info
                    var replyMessage = new NetworkMessage
                    {
                        MsgType = NetworkMessage.MessageType.UPDATE,
                        Client = new ClientInfo
                        {
                            ClientGuid = client.ClientGuid,
                            Name = client.Name,
                            AllowRecord = client.AllowRecord,
                            RadioInfo = null
                        }
                    };

                    //Client state updated
                    MulticastToRegistered(replyMessage.Encode());
                    session.LastMetaDataSent = DateTime.Now.Ticks;
                    _eventAggregator.PublishOnUIThreadAsync(new ServerStateMessage(true,
                        new List<ClientInfo>(_clients.Values)));
                }
            }
        }

        return changed;
    }

    private void HandleClientDisconnect(RadioClientSession srsSession, string clientGuid)
    {
        var message = new NetworkMessage
        {
            Client = new ClientInfo { ClientGuid = clientGuid },
            MsgType = NetworkMessage.MessageType.CLIENT_DISCONNECT
        };

        MulticastToRegistered(message.Encode(), srsSession.Id);

        try
        {
            srsSession.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private void HandleClientRadioUpdate(RadioClientSession session, NetworkMessage message, bool send)
    {
        var guid = session.ClientGuid;
        if (guid != null && _clients.TryGetValue(guid, out var client) && client != null)
        {
            var radioInfo = message.Client.RadioInfo ?? new PlayerRadioInfoBase();
            radioInfo.EnsureValid();

            var changed = client.RadioInfo == null || !client.RadioInfo.Equals(radioInfo);

            client.RadioInfo = radioInfo;

            var lastSent = new TimeSpan(DateTime.Now.Ticks - session.LastFullRadioSent);

            //send update to everyone
            if (send || changed || lastSent.TotalSeconds > Constants.CLIENT_UPDATE_INTERVAL_LIMIT - 5)
                SendFullRadioUpdate(session, client);
        }
    }

    private void SendFullRadioUpdate(RadioClientSession session, ClientInfo client)
    {
        var replyMessage = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.RADIO_UPDATE,
            Client = new ClientInfo
            {
                ClientGuid = client.ClientGuid,
                Name = client.Name,
                RadioInfo = client.RadioInfo, //send radio info
                AllowRecord = client.AllowRecord
            }
        };
        MulticastToRegistered(replyMessage.Encode());
        //marks both as metadata is included in full radio
        session.LastFullRadioSent = DateTime.Now.Ticks;
        _eventAggregator.PublishOnUIThreadAsync(new ServerStateMessage(true,
            new List<ClientInfo>(_clients.Values)));
    }

    /// <summary>Sends to every session that completed the handshake (optionally except one).</summary>
    private void MulticastToRegistered(string text, Guid? except = null)
    {
        if (!IsStarted) return;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length == 0) return;

        foreach (var session in Sessions.Values)
        {
            if (session is not RadioClientSession clientSession || clientSession.ClientGuid == null) continue;
            if (except.HasValue && session.Id == except.Value) continue;

            session.SendAsync(bytes, 0, bytes.Length);
        }
    }

    private void DisconnectStaleHandshakes()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var session in Sessions.Values)
                if (session is RadioClientSession clientSession && clientSession.ClientGuid == null &&
                    clientSession.IsConnected && now - clientSession.ConnectedAtUtc > HandshakeTimeout)
                {
                    Logger.Info($"Disconnecting {clientSession.RemoteAddress} - no handshake within {HandshakeTimeout.TotalSeconds} s");
                    clientSession.Disconnect();
                }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Error checking pending connections");
        }
    }

    public void RequestStop()
    {
        try
        {
            _eventAggregator.Unsubscribe(this);
        }
        catch
        {
        }

        lock (_lifecycleLock)
        {
            _stopRequested = true;

            try
            {
                _handshakeTimer?.Dispose();
                _handshakeTimer = null;
            }
            catch
            {
            }

            try
            {
                _natHandler?.CloseNATAsync();
            }
            catch
            {
            }

            try
            {
                if (IsStarted)
                {
                    DisconnectAll();
                    Stop();
                }

                _clients.Clear();
            }
            catch (Exception)
            {
            }
        }
    }
}
