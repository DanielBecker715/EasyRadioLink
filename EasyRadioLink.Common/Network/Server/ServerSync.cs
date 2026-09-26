using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.NetCoreServer;
using EasyRadioLink.Common.Network.Crypto;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     TCP side of the server, TLS encrypted (<see cref="SslServer" />; the server identity is a self-signed
///     certificate that clients pin, see <see cref="ServerIdentity" />). Handshake: the first message of a connection
///     must be a SYNC hello (product + protocol version check -> VERSION_MISMATCH, password check -> AUTH_FAILED); only
///     then the client is registered, receives the client list + settings + its own UDP key
///     (<see cref="NetworkMessage.UdpKey" />) and is announced to everybody else (with its end-to-end voice public key,
///     <see cref="ClientInfo.E2EPublicKey" />). Messages are only sent to registered sessions. A plain JSON 1.0 client gets
///     a plain VERSION_MISMATCH (<see cref="RadioClientSession" />).
///     <para>
///         VOICE_KEY (end-to-end voice, <see cref="HandleVoiceKey" />): the server forwards each listed recipient only
///         its own wrapped key - it never sees a transmission key and never sends the whole list.
///     </para>
/// </summary>
public class ServerSync : SslServer, IHandle<ServerSettingsChangedMessage>
{
    // Unregistered connections are closed after this time
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

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

    private readonly ConnectionRateLimiter _connectionRate = new();

    // refused and unfinished connections: a debug line each, a summary once a minute
    private readonly ConnectionLogSummary _connectionLog = new(DateTime.UtcNow);

    // guards Start()/RequestStop() so a stop during start-up can't leave a listening server behind
    private readonly object _lifecycleLock = new();
    private volatile bool _stopRequested;

    private Timer _handshakeTimer;

    /// <param name="identity">The server certificate with its private key (<see cref="ServerIdentity.LoadOrCreate" />).</param>
    public ServerSync(ConcurrentDictionary<string, ClientInfo> connectedClients, HashSet<IPAddress> _bannedIps,
        IEventAggregator eventAggregator, X509Certificate2 identity) : base(
        new IPEndPoint(ServerSettingsStore.Instance.GetServerIP(), ServerSettingsStore.Instance.GetServerPort()),
        identity)
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

    protected override SslSession CreateSession()
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
    /// <param name="category">For the log summary: server full or a per address limit.</param>
    public bool ExceedsConnectionLimits(RadioClientSession newSession, out string reason,
        out RefusedConnectionReason category)
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
        category = total > MaxConnections ? RefusedConnectionReason.ServerFull : RefusedConnectionReason.ConnectionLimit;

        return reason != null;
    }

    /// <summary>
    ///     Counts a refused or unfinished connection for the once-a-minute log summary (the caller logs it at debug
    ///     level) - a connection flood must not flood the log.
    /// </summary>
    public void RecordRefusedConnection(RefusedConnectionReason reason)
    {
        _connectionLog.Record(reason);
    }

    private void LogConnectionSummary(bool force = false)
    {
        var summary = _connectionLog.TakeSummary(DateTime.UtcNow, out var important, force);
        if (summary == null) return;

        if (important) Logger.Warn(summary);
        else Logger.Info(summary);
    }

    /// <summary>
    ///     Counts a new connection from <paramref name="address" />; false if the address opens connections too fast
    ///     (<see cref="ConnectionRateLimiter" />). Checked before the TLS handshake.
    /// </summary>
    public bool AllowNewConnection(IPAddress address, out bool logRefusal)
    {
        return _connectionRate.TryAcquire(address, DateTime.UtcNow, out logRefusal);
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
        string guid;
        lock (_registrationLock)
        {
            guid = state?.ClientGuid;
            if (guid == null)
            {
                // refused, failed or unfinished handshakes - one line each would flood the log during an attack
                Logger.Debug($"Closed unregistered connection {state?.RemoteAddress}");
                return;
            }

            state.ClientGuid = null;

            // Only remove the entry if it still belongs to this session (it may have been taken over by a reconnect)
            if (!_clients.TryGetValue(guid, out var registered) || registered.ClientSession != state.Id) return;

            _clients.TryRemove(guid, out _);

            // the UDP key dies with the connection
            registered.UdpTransport?.Dispose();
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
                case NetworkMessage.MessageType.VOICE_KEY:
                    HandleVoiceKey(state, message.VoiceKey);
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
            state.Rejected = true;
            HandleVersionMismatch(state);
            return;
        }

        var srClient = message.Client;
        if (!IsValidClientGuid(srClient?.ClientGuid))
        {
            Logger.Warn($"Disconnecting {remote} - invalid client id");
            state.Disconnect();
            return;
        }

        // every 1.1 client has an end-to-end voice key pair; without its public key nobody could talk to it
        if (!E2EKeyPair.IsValidPublicKey(srClient.E2EPublicKey))
        {
            Logger.Warn($"Disconnecting {remote} - no valid end-to-end voice public key (incompatible client)");
            state.Rejected = true;
            HandleVersionMismatch(state);
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

        // a fresh UDP key for this client: every datagram to and from it is encrypted and authenticated with it. It is
        // attached before the client is registered, so the voice router never sees a client without one.
        var udpKey = UdpTransportSession.GenerateKey();
        var udpKeyId = UdpTransportSession.GenerateKeyId();
        string udpKeyBase64;
        try
        {
            srClient.UdpTransport = new UdpTransportSession(srClient.ClientGuid, udpKey, udpKeyId, true);
            udpKeyBase64 = Convert.ToBase64String(udpKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(udpKey);
        }

        RadioClientSession previousSession = null;
        ClientInfo replaced = null;
        lock (_registrationLock)
        {
            // closed in the meantime (HandleDisconnect already ran): don't register a client without a connection.
            // A disconnect after this check waits for the lock and removes the client again.
            if (!state.IsConnected)
            {
                srClient.UdpTransport.Dispose();
                return;
            }

            // Same client id reconnecting while its old connection is still open: the new connection takes over
            if (_clients.TryGetValue(srClient.ClientGuid, out var existing) && existing.ClientSession != state.Id)
            {
                replaced = existing;
                previousSession = FindSession(existing.ClientSession) as RadioClientSession;
                if (previousSession != null) previousSession.ClientGuid = null;
            }

            _clients[srClient.ClientGuid] = srClient;
            state.ClientGuid = srClient.ClientGuid;
        }

        // the old connection's UDP key is no longer valid
        replaced?.UdpTransport?.Dispose();

        if (previousSession != null)
        {
            Logger.Info($"Client {srClient} reconnected from {remote} (previously {previousSession.RemoteAddress}) - closing its previous connection");
            previousSession.Disconnect();
        }

        if (replaced != null)
        {
            // Everybody else sees the old connection leave before the new one is announced: the transmission keys of
            // the old connection are forgotten, and a sender whose transmission is running wraps its key again for the
            // new connection (VoiceTransmission.TakeNewRecipients). The new connection itself gets its SYNC reply.
            HandleClientDisconnect(null, srClient.ClientGuid, state.Id);
        }

        Logger.Info($"Client {srClient} connected from {remote}");

        _eventAggregator.PublishOnUIThreadAsync(new ServerStateMessage(true,
            new List<ClientInfo>(_clients.Values)));

        Logger.Info($"Issued UDP key #{udpKeyId:x8} to {srClient.ClientGuid}");
        SendSyncReply(state, udpKeyBase64, udpKeyId);

        //send update to everyone
        var update = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.RADIO_UPDATE,
            Client = new ClientInfo
            {
                ClientGuid = srClient.ClientGuid,
                RadioInfo = srClient.RadioInfo,
                Name = srClient.Name,
                AllowRecord = srClient.AllowRecord,
                E2EPublicKey = srClient.E2EPublicKey
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
                state.DisconnectAfterSend(Encoding.UTF8.GetBytes(
                    new NetworkMessage { MsgType = NetworkMessage.MessageType.AUTH_FAILED }.Encode()));
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Unable to send AUTH_FAILED");
            }
        });
    }

    /// <param name="udpKey">
    ///     The client's UDP key (base64) - only in the first SYNC reply of a connection, never in any other message.
    /// </param>
    private void SendSyncReply(RadioClientSession session, string udpKey = null, uint? udpKeyId = null)
    {
        var replyMessage = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.SYNC,
            Clients = new List<ClientInfo>(_clients.Values),
            ServerSettings = _serverSettings.ToDictionary(),
            UdpKey = udpKey,
            UdpKeyId = udpKeyId
        };

        session.SendAsync(replyMessage.Encode());
    }

    /// <summary>A client id is a 22 character ShortGuid (URL-safe base64: A-Z a-z 0-9 - _).</summary>
    internal static bool IsValidClientGuid(string clientGuid)
    {
        return ShortGuid.IsWellFormed(clientGuid);
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

        session.SendAsync(replyMessage.Encode());
    }

    /// <summary>Answers VERSION_MISMATCH and closes the connection once it is sent.</summary>
    private static void HandleVersionMismatch(RadioClientSession session)
    {
        var replyMessage = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.VERSION_MISMATCH
        };
        session.DisconnectAfterSend(Encoding.UTF8.GetBytes(replyMessage.Encode()));
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
                            RadioInfo = null,
                            E2EPublicKey = client.E2EPublicKey
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

    /// <summary>
    ///     Announces CLIENT_DISCONNECT of <paramref name="clientGuid" /> to every registered session except
    ///     <paramref name="except" /> (default: the closed session) and disposes <paramref name="srsSession" /> (if any).
    /// </summary>
    private void HandleClientDisconnect(RadioClientSession srsSession, string clientGuid, Guid? except = null)
    {
        var message = new NetworkMessage
        {
            Client = new ClientInfo { ClientGuid = clientGuid },
            MsgType = NetworkMessage.MessageType.CLIENT_DISCONNECT
        };

        MulticastToRegistered(message.Encode(), except ?? srsSession?.Id);

        if (srsSession == null) return;

        try
        {
            srsSession.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }
    }

    /// <summary>
    ///     VOICE_KEY from a registered client: the key of one of its transmissions, wrapped for each listener. The
    ///     message must name the session's own client as sender and be well-formed (<see cref="VoiceKeyMessage.IsValid" />)
    ///     - otherwise the connection is closed; beyond a burst of <see cref="RadioClientSession.VoiceKeyBurst" />, more
    ///     than <see cref="RadioClientSession.MaxVoiceKeysPerSecond" /> per second are dropped. Each listed recipient that
    ///     is connected and can hear the frequency (the rule the voice packets are routed by) gets a VOICE_KEY with only
    ///     its own entry; the others are dropped (the sender wraps the key again when it sees the listener's next radio
    ///     update, see <see cref="VoiceTransmission.TakeNewRecipients" />). The wrapped keys are opaque to the server and
    ///     never logged.
    /// </summary>
    private void HandleVoiceKey(RadioClientSession session, VoiceKeyMessage voiceKey)
    {
        var senderGuid = session.ClientGuid;

        string reason = null;
        if (voiceKey == null || !voiceKey.IsValid(out _, out reason) || voiceKey.SenderGuid != senderGuid)
        {
            reason = voiceKey == null ? "no content" : reason ?? "the sender is not this connection's client";
            Logger.Warn($"Disconnecting {session.RemoteAddress} ({senderGuid}) - invalid VOICE_KEY: {reason}");
            session.Disconnect();
            return;
        }

        if (!session.AllowVoiceKey(DateTime.UtcNow, out var logDrop))
        {
            if (logDrop)
                Logger.Warn($"Dropping VOICE_KEY messages of {session.RemoteAddress} ({senderGuid}) - more than " +
                            $"{RadioClientSession.MaxVoiceKeysPerSecond} per second after a burst of " +
                            $"{RadioClientSession.VoiceKeyBurst} (further drops in the next minute are not logged)");
            return;
        }

        if (!_clients.TryGetValue(senderGuid, out var sender) || sender.ClientSession != session.Id || sender.Muted)
            return;

        var forwarded = 0;
        var notListening = 0;
        foreach (var (recipientGuid, wrappedKey) in voiceKey.Keys)
        {
            if (recipientGuid == senderGuid) continue;
            if (!_clients.TryGetValue(recipientGuid, out var recipient)) continue;
            if (!VoiceRouting.CanReceive(recipient.RadioInfo, voiceKey.Frequency, voiceKey.Modulation))
            {
                notListening++;
                continue;
            }

            if (FindSession(recipient.ClientSession) is not RadioClientSession recipientSession ||
                recipientSession.ClientGuid != recipientGuid)
                continue;

            var forward = new NetworkMessage
            {
                MsgType = NetworkMessage.MessageType.VOICE_KEY,
                VoiceKey = new VoiceKeyMessage
                {
                    SenderGuid = senderGuid,
                    TxId = voiceKey.TxId,
                    Frequency = voiceKey.Frequency,
                    Modulation = voiceKey.Modulation,
                    // never the whole map: each recipient only learns its own wrapped key
                    Keys = new Dictionary<string, string> { [recipientGuid] = wrappedKey }
                }
            };

            recipientSession.SendAsync(forward.Encode());
            forwarded++;
        }

        if (Logger.IsDebugEnabled)
            Logger.Debug($"VOICE_KEY from {senderGuid}: {voiceKey.Keys.Count} listed, forwarded to {forwarded}, " +
                         $"{notListening} not (or no longer) on the frequency");
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
                AllowRecord = client.AllowRecord,
                E2EPublicKey = client.E2EPublicKey
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

    /// <summary>Housekeeping timer (every 5 s): closes unfinished handshakes, writes the connection log summary.</summary>
    private void DisconnectStaleHandshakes()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var session in Sessions.Values)
                if (session is RadioClientSession clientSession && clientSession.ClientGuid == null &&
                    clientSession.IsConnected && now - clientSession.ConnectedAtUtc > HandshakeTimeout)
                {
                    // one line each would flood the log during a slow-handshake attack - counted and summarised
                    Logger.Debug($"Disconnecting {clientSession.RemoteAddress} - no handshake within {HandshakeTimeout.TotalSeconds} s");
                    RecordRefusedConnection(RefusedConnectionReason.HandshakeTimeout);
                    clientSession.Disconnect();
                }

            LogConnectionSummary();
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
                LogConnectionSummary(true);
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

                foreach (var client in _clients.Values) client.UdpTransport?.Dispose();
                _clients.Clear();
            }
            catch (Exception)
            {
            }
        }
    }
}
