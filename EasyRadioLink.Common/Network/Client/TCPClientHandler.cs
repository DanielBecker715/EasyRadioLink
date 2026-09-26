using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Crypto;
using EasyRadioLink.Common.Network.Singletons;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Common.Network.Client;

/// <summary>
///     TCP connection of the client, TLS encrypted.
///     <para>
///         Handshake: connect -> TLS (1.2/1.3) where only the pinned server identity decides
///         (<see cref="KnownServersStore" />, trust on first use; the certificate is self-signed, so chain and host name
///         errors are ignored) -> send SYNC {Client (with the end-to-end voice public key), Password, Version, Product}
///         -> wait for the server's SYNC reply with this client's UDP key -> <see cref="SyncedServerSettings" /> is
///         filled, then <c>TCPClientStatusMessage(endpoint, udpKey, voiceSession)</c> is published (awaited on the UI
///         thread) and finally a <see cref="ServerSettingsUpdatedMessage" />. Nothing - not even the password - is sent
///         before the identity check passed.
///     </para>
///     <para>
///         End-to-end voice: each connection has an <see cref="E2EVoiceSession" /> (created with the handshake, closed
///         with the connection). VOICE_KEY messages from the server go to it; the keys it wraps for listeners are sent
///         over this connection.
///     </para>
///     <para>
///         Every disconnect publishes exactly one <c>TCPClientStatusMessage(false, reason)</c> with reason
///         TIMEOUT (could not connect / connection lost), AUTH_FAILED (wrong password), MISMATCHED_SERVER (not an
///         EasyRadioLink server or unsupported version), INVALID_SERVER (no TLS or no handshake reply, e.g. a 1.0
///         server), SERVER_IDENTITY_CHANGED (another identity than the pinned one, with the fingerprints),
///         SERVER_IDENTITY_UNKNOWN (the pin of this server is unreadable - the user may check and trust the presented
///         fingerprint), IDENTITY_CHECK_FAILED (known-servers.json unreadable/damaged or the check failed - nothing is
///         offered) or USER_DISCONNECTED. Only a matching pin or a first use connects; every error of the identity check
///         refuses the connection (fail closed).
///     </para>
///     <para>
///         Lines from the server are read with a 512 KB limit (<see cref="BoundedLineReader" />); a longer line closes
///         the connection. TLS renegotiation is not allowed.
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
    private readonly E2EKeyPair _e2eKeys;
    private readonly string _guid;
    private readonly KnownServersStore _knownServers;
    private readonly string _serverHost;
    private readonly string _serverPassword;

    private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private long _lastSent = -1;
    private ClientInfo _playerUnitState;
    private bool _pendingFullUpdate;
    private IPEndPoint _serverEndpoint;
    private string _serverKey;
    private TcpClient _tcpClient;
    private SslStream _sslStream;
    private CancellationTokenSource _cts;
    private ServerIdentityMismatch _identityMismatch;
    private string _identityCheckError;
    private UdpTransportKey _udpKey;
    private E2EVoiceSession _voiceSession;

    private volatile bool _handshakeComplete;
    private volatile bool _stopRequested;
    private int _disconnectReason = (int)TCPClientStatusMessage.ErrorCode.TIMEOUT;

    /// <param name="guid">The client's 22 character ShortGuid (also used for UDP).</param>
    /// <param name="playerUnitState">Initial state sent with the SYNC hello (ClientGuid, Name, AllowRecord, RadioInfo).</param>
    /// <param name="e2eKeys">
    ///     The end-to-end voice key pair of this app run; its public key is sent with the SYNC hello.
    /// </param>
    /// <param name="serverPassword">Server password, null or "" for an open server.</param>
    /// <param name="serverHost">
    ///     The server name as the user entered it (pin key together with the port, TLS server name); null = the IP
    ///     address.
    /// </param>
    /// <param name="knownServers">The pin store; null = <see cref="KnownServersStore.Default" />.</param>
    public TCPClientHandler(string guid, ClientInfo playerUnitState, E2EKeyPair e2eKeys, string serverPassword = null,
        string serverHost = null, KnownServersStore knownServers = null)
    {
        _clients.Clear();
        _guid = guid;
        _e2eKeys = e2eKeys ?? throw new ArgumentNullException(nameof(e2eKeys));
        _serverPassword = string.IsNullOrEmpty(serverPassword) ? null : serverPassword;
        _serverHost = string.IsNullOrWhiteSpace(serverHost) ? null : serverHost.Trim();
        _knownServers = knownServers ?? KnownServersStore.Default;
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
        _serverKey = KnownServersStore.ServerKey(_serverHost ?? endpoint.Address.ToString(), endpoint.Port);
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
        _identityMismatch = null;
        _identityCheckError = null;
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

                        if (await AuthenticateServerAsync(token)) await ClientSyncLoopAsync(token);
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

    /// <summary>
    ///     TLS handshake. The server certificate is accepted only if its public key matches the pin of
    ///     <see cref="_serverKey" /> (or on first use - then it is pinned once the handshake proved that the server holds
    ///     the private key). An unreadable pin, an unreadable known-servers.json or any error while checking refuses the
    ///     connection. False (with the disconnect reason set) if the connection must not be used.
    /// </summary>
    private async Task<bool> AuthenticateServerAsync(CancellationToken token)
    {
        var serverKey = _serverKey;
        string presented = null;
        string pinned = null;
        string problem = null;
        Exception checkError = null;
        var status = ServerPinStatus.Mismatch;

        var sslStream = new SslStream(_tcpClient.GetStream(), false);
        _sslStream = sslStream;

        var options = new SslClientAuthenticationOptions
        {
            // server name (SNI) - its certificate is self-signed, so name and chain are not checked, only the pin
            TargetHost = _serverHost ?? _serverEndpoint.Address.ToString(),
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            AllowRenegotiation = false,
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                // nothing may escape from here: SslStream would report it as a failed handshake of some kind, and the
                // user would see a wrong reason (e.g. "identity changed" without a known fingerprint)
                try
                {
                    if (certificate == null) return false;

                    presented = ServerIdentity.GetFingerprint(certificate);
                    status = _knownServers.Check(serverKey, presented, out pinned, out problem);
                    return status is ServerPinStatus.Match or ServerPinStatus.FirstUse;
                }
                catch (Exception ex)
                {
                    checkError = ex;
                    return false;
                }
            }
        };

        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshakeTimeout.CancelAfter(HandshakeTimeout);

            await sslStream.AuthenticateAsClientAsync(options, handshakeTimeout.Token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            if (checkError != null)
            {
                Logger.Error(checkError, $"Unable to check the identity of {serverKey} - not connecting (nothing was sent)");
                return RefuseUncheckedIdentity($"the identity check failed: {checkError.Message}");
            }

            if (presented != null)
                switch (status)
                {
                    case ServerPinStatus.Mismatch:
                        Logger.Warn($"The identity of {serverKey} changed: pinned {pinned}, presented {presented} - " +
                                    "not connecting (nothing was sent)");
                        _identityMismatch = new ServerIdentityMismatch(serverKey, pinned, presented);
                        SetDisconnectReason(TCPClientStatusMessage.ErrorCode.SERVER_IDENTITY_CHANGED);
                        return false;

                    case ServerPinStatus.Unknown:
                        Logger.Warn($"The saved identity of {serverKey} in {_knownServers.FilePath} is unreadable - " +
                                    $"the server presents {presented}; not connecting unless the user trusts it " +
                                    "(nothing was sent)");
                        _identityMismatch = new ServerIdentityMismatch(serverKey, null, presented);
                        SetDisconnectReason(TCPClientStatusMessage.ErrorCode.SERVER_IDENTITY_UNKNOWN);
                        return false;

                    case ServerPinStatus.StoreUnreadable:
                        Logger.Error($"{_knownServers.FilePath}: {problem} - the identity of {serverKey} can't be " +
                                     "checked, not connecting (nothing was sent)");
                        return RefuseUncheckedIdentity(problem);
                }

            // e.g. an EasyRadioLink 1.0 server (no TLS) or something else on that port
            Logger.Error(ex, $"TLS handshake with {_serverEndpoint} failed - not an EasyRadioLink 1.1 server?");
            SetDisconnectReason(TCPClientStatusMessage.ErrorCode.INVALID_SERVER);
            return false;
        }

        if (status == ServerPinStatus.FirstUse)
        {
            ServerPinStatus pinStatus;
            try
            {
                // checked again and pinned under the store's lock: another instance may have pinned something since
                pinStatus = _knownServers.TrustFirstUse(serverKey, presented, out pinned);
            }
            catch (Exception ex)
            {
                // the check itself passed (no pin): still connect, the pin is taken next time
                Logger.Error(ex, $"Unable to remember the identity of {serverKey} in {_knownServers.FilePath}");
                pinStatus = ServerPinStatus.FirstUse;
            }

            switch (pinStatus)
            {
                case ServerPinStatus.FirstUse:
                    Logger.Info($"First connection to {serverKey} - trusting its identity {presented}");
                    break;
                case ServerPinStatus.Match:
                    Logger.Info($"Identity of {serverKey} verified ({presented})");
                    break;
                case ServerPinStatus.Mismatch:
                    Logger.Warn($"The identity of {serverKey} was pinned as {pinned} meanwhile, presented {presented} - " +
                                "not connecting (nothing was sent)");
                    _identityMismatch = new ServerIdentityMismatch(serverKey, pinned, presented);
                    SetDisconnectReason(TCPClientStatusMessage.ErrorCode.SERVER_IDENTITY_CHANGED);
                    return false;
                case ServerPinStatus.Unknown:
                    _identityMismatch = new ServerIdentityMismatch(serverKey, null, presented);
                    SetDisconnectReason(TCPClientStatusMessage.ErrorCode.SERVER_IDENTITY_UNKNOWN);
                    return false;
                default:
                    return RefuseUncheckedIdentity($"{_knownServers.FilePath} can't be read");
            }
        }
        else
        {
            Logger.Info($"Identity of {serverKey} verified ({presented})");
        }

        Logger.Info($"Encrypted connection: {sslStream.SslProtocol}, {sslStream.NegotiatedCipherSuite}");
        _serverSettings.ServerIdentityFingerprint = presented;

        return true;
    }

    /// <summary>The identity could not be checked at all: refuse, and tell the user why (no trust offer).</summary>
    private bool RefuseUncheckedIdentity(string reason)
    {
        _identityCheckError = $"{_knownServers.FilePath}: {reason}";
        SetDisconnectReason(TCPClientStatusMessage.ErrorCode.IDENTITY_CHECK_FAILED);
        return false;
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

        // lines are capped at 512 KB like on the server: a server can't make the client buffer without limit
        var reader = new BoundedLineReader(_sslStream);
        try
        {
            ClientInfo hello;
            lock (_stateLock)
            {
                hello = _playerUnitState.DeepClone();
                _pendingFullUpdate = false;
            }

            hello.ClientGuid = _guid;
            hello.E2EPublicKey = _e2eKeys.PublicKey;

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
                                // a new public key (the id was taken over): the old transmission keys are void
                                if (serverMessage.Client.E2EPublicKey != null &&
                                    serverMessage.Client.E2EPublicKey != srClient.E2EPublicKey)
                                {
                                    _voiceSession?.ForgetClient(srClient.ClientGuid);
                                    _voiceSession?.PrepareKeys([serverMessage.Client]);
                                }

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
                                _voiceSession?.PrepareKeys([srClient]);
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
                            _voiceSession?.ForgetClient(serverMessage.Client.ClientGuid);

                            if (outClient != null)
                                await EventBus.Instance.PublishOnBackgroundThreadAsync(
                                    new ClientUpdateMessage(outClient, false));

                            break;
                        case NetworkMessage.MessageType.VOICE_KEY:
                            // the key of a transmission this client can hear (only its own entry)
                            if (_handshakeComplete) _voiceSession?.HandleVoiceKey(serverMessage.VoiceKey);

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
        catch (LineTooLongException ex)
        {
            Logger.Error($"{ex.Message} from the server {_serverEndpoint} - disconnecting");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Client exception reading - Disconnecting ");
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
            // a later SYNC reply never changes the UDP key of this connection
            _serverSettings.PublishSettingsUpdated();
            return;
        }

        // every voice datagram is encrypted with this key - without it the connection is useless (fail closed)
        if (!UdpTransportKey.TryParse(serverMessage.UdpKey, serverMessage.UdpKeyId, out var udpKey))
        {
            Logger.Error($"Server {_serverEndpoint} sent no valid UDP key - disconnecting");

            SetDisconnectReason(TCPClientStatusMessage.ErrorCode.MISMATCHED_SERVER);
            ShowVersionMismatchWarning(serverMessage.Version);

            await RequestDisconnectAsync();
            return;
        }

        _udpKey = udpKey;
        var voiceSession = new E2EVoiceSession(_guid, _e2eKeys, _clients.Clients, SendToServerAsync,
            _serverSettings.IsTestFrequency);
        _voiceSession = voiceSession;

        // the ECDH with every client already here, in the background: the first PTT press stays cheap
        voiceSession.PrepareKeys(_clients.Values);
        _handshakeComplete = true;
        Logger.Info($"Connected to EasyRadioLink server @ {_serverEndpoint} (protocol {serverMessage.Version}, " +
                    $"UDP key #{udpKey.KeyId:x8}, end-to-end voice encryption on)");

        // "connected" = authenticated and synchronised; the UI starts audio and the radio sync on this message
        await EventBus.Instance.PublishOnUIThreadAsync(new TCPClientStatusMessage(_serverEndpoint, udpKey, voiceSession));

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

        // the server announces the key of the connection that holds this id
        if (updatedSrClient.E2EPublicKey != null) client.E2EPublicKey = updatedSrClient.E2EPublicKey;
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
        var sslStream = _sslStream;
        if (sslStream == null) return;

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
                await sslStream.WriteAsync(bytes);
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
            _sslStream?.Dispose();
            _sslStream = null;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Error closing the TLS stream");
        }

        try
        {
            _tcpClient?.Close(); // this'll stop the socket blocking
            _tcpClient = null;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Error closing tcp client");
        }

        // the voice handler of this connection has its own copy of the key by now
        _udpKey?.Clear();
        _udpKey = null;

        // every transmission key of this connection is destroyed
        _voiceSession?.Dispose();
        _voiceSession = null;

        var reason = (TCPClientStatusMessage.ErrorCode)Volatile.Read(ref _disconnectReason);
        Logger.Info($"Disconnected ({reason}).");

        EventBus.Instance.PublishOnUIThreadAsync(new TCPClientStatusMessage(false, reason,
            reason is TCPClientStatusMessage.ErrorCode.SERVER_IDENTITY_CHANGED
                or TCPClientStatusMessage.ErrorCode.SERVER_IDENTITY_UNKNOWN
                ? _identityMismatch
                : null,
            reason == TCPClientStatusMessage.ErrorCode.IDENTITY_CHECK_FAILED ? _identityCheckError : null));
    }
}
