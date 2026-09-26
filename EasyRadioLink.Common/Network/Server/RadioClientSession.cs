using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.NetCoreServer;
using EasyRadioLink.Common.Network.Singletons;
using NLog;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     One client connection of the <see cref="ServerSync" /> (TLS, see <see cref="SslSession" />): splits the decrypted
///     stream into JSON lines and hands them to the server. A 1.0 client (plain JSON, first byte '{') gets one plain
///     text VERSION_MISMATCH line and is closed.
/// </summary>
public class RadioClientSession : SslSession
{
    // A single JSON line is a few KB; anything bigger without a line break is garbage or an attack
    private const int MaxReceiveBufferLength = 512 * 1024;

    // Disconnect a client that does not read: more than 50 MB waiting to be sent
    private const long MaxPendingSendBytes = 50_000_000;

    // Flood protection. A normal client sends a few lines per second (radio updates at most every 200 ms).
    private const int MaxLinesPerWindow = 100;
    private static readonly TimeSpan LineRateWindow = TimeSpan.FromSeconds(5);
    private const int MaxInvalidLinesBeforeHandshake = 3;
    private const int MaxInvalidLines = 20;

    /// <summary>
    ///     VOICE_KEY messages accepted per second on average (a client sends one per PTT press plus one for listeners
    ///     who tune in, at most 8 per second): the refill rate of a token bucket of <see cref="VoiceKeyBurst" />.
    /// </summary>
    public const int MaxVoiceKeysPerSecond = 10;

    /// <summary>
    ///     VOICE_KEY messages accepted at once: messages that queued up during a TCP stall arrive together and must not
    ///     be dropped.
    /// </summary>
    public const int VoiceKeyBurst = 20;

    private static readonly TimeSpan VoiceKeyDropLogInterval = TimeSpan.FromMinutes(1);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly HashSet<IPAddress> _bannedIps;

    // Received data string (UTF-8 decoded with a stateful decoder, so characters split across packets survive).
    private readonly StringBuilder _receiveBuffer = new();
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private char[] _charBuffer = new char[4096];

    private int _invalidLines;
    private int _linesInWindow;
    private DateTime _lineWindowStartUtc = DateTime.UtcNow;

    // VOICE_KEY rate limit (messages of one session are handled one after the other - no lock needed)
    private readonly TokenBucket _voiceKeys = new(VoiceKeyBurst, MaxVoiceKeysPerSecond);
    private DateTime _lastVoiceKeyDropLogUtc = DateTime.MinValue;

    // the connection was announced (ClientConnectionMessage) - only connections that passed the checks are
    private bool _announced;
    private string _ip;
    private long _lastFullRadioSent;
    private int _port;
    private volatile bool _rejected;

    public RadioClientSession(ServerSync server,
        HashSet<IPAddress> bannedIps) : base(server)
    {
        _bannedIps = bannedIps;
        OptionSendBufferLimit = MaxPendingSendBytes;
    }

    public long LastMetaDataSent { get; set; }

    public long LastFullRadioSent
    {
        get => _lastFullRadioSent;
        set
        {
            _lastFullRadioSent = value;
            //metadata is always sent with a full update
            LastMetaDataSent = value;
        }
    }

    public long LastMessageReceived { get; set; }

    /// <summary>Client id of this connection - null until the SYNC handshake succeeded.</summary>
    public string ClientGuid { get; set; }

    /// <summary>When the TCP connection was accepted (UTC) - used for the handshake timeout (TLS + SYNC).</summary>
    public DateTime ConnectedAtUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>"ip:port" of the remote end (for logs).</summary>
    public string RemoteAddress => $"{_ip}:{_port}";

    /// <summary>IP address of the remote end.</summary>
    public IPAddress RemoteIp { get; private set; }

    /// <summary>
    ///     Set when the handshake was refused (e.g. wrong password): the connection is closed shortly and every further
    ///     message on it is ignored.
    /// </summary>
    public bool Rejected
    {
        get => _rejected;
        set => _rejected = value;
    }

    private ServerSync SyncServer => (ServerSync)Server;

    protected override void OnConnected()
    {
        ConnectedAtUtc = DateTime.UtcNow;

        var clientIp = (IPEndPoint)Socket.RemoteEndPoint;

        _ip = clientIp.Address.ToString();
        _port = clientIp.Port;
        RemoteIp = clientIp.Address;

        // Every check here runs before the TLS handshake - a refused connection costs no crypto. Refusals are logged at
        // debug level each and summarised once a minute (ServerSync.RecordRefusedConnection): a connection flood must
        // not flood the log.
        if (_bannedIps.Contains(clientIp.Address))
        {
            Logger.Debug($"Disconnecting {RemoteAddress} - banned address");
            SyncServer.RecordRefusedConnection(RefusedConnectionReason.Banned);

            Disconnect();
        }
        else if (SyncServer.IsLockedOut(clientIp.Address))
        {
            Logger.Debug($"Disconnecting {RemoteAddress} - too many wrong passwords, try again later");
            SyncServer.RecordRefusedConnection(RefusedConnectionReason.LockedOut);

            Disconnect();
        }
        else if (!SyncServer.AllowNewConnection(clientIp.Address, out var logRefusal))
        {
            if (logRefusal)
                Logger.Warn($"Disconnecting {RemoteAddress} - too many new connections from this address " +
                            "(further refusals in the next minute are not logged)");

            Disconnect();
        }
        else if (SyncServer.ExceedsConnectionLimits(this, out var reason, out var category))
        {
            Logger.Debug($"Disconnecting {RemoteAddress} - {reason}");
            SyncServer.RecordRefusedConnection(category);

            Disconnect();
        }
        else
        {
            // refused connections are not announced (the command line server prints every announced one)
            _announced = true;
            EventBus.Instance.PublishOnBackgroundThreadAsync(new ClientConnectionMessage
            {
                Connected = true,
                ClientIP = clientIp.ToString(),
                ClientGuid = ClientGuid
            });
        }
    }

    protected override Task OnNonTlsDataAsync(byte firstByte, CancellationToken token)
    {
        // debug level each, summarised once a minute (scanners would flood the log)
        if (firstByte != (byte)'{')
        {
            Logger.Debug($"Disconnecting {RemoteAddress} - not an EasyRadioLink 1.1 client (no TLS)");
            SyncServer.RecordRefusedConnection(RefusedConnectionReason.NotTls);
            return Task.CompletedTask;
        }

        // an EasyRadioLink 1.0 client (plain JSON): tell it why, so it shows "incompatible server" instead of a timeout
        Logger.Debug($"Disconnecting {RemoteAddress} - unencrypted EasyRadioLink 1.0 client, answered VERSION_MISMATCH");
        SyncServer.RecordRefusedConnection(RefusedConnectionReason.Version10Client);

        var reply = Encoding.UTF8.GetBytes(new NetworkMessage
            { MsgType = NetworkMessage.MessageType.VERSION_MISMATCH }.Encode());
        return ReplyInPlainTextAsync(reply, token);
    }

    protected override void OnHandshakeFailed(AuthenticationException exception)
    {
        // scanners and broken clients - not worth more than a debug line each
        Logger.Debug($"TLS handshake with {RemoteAddress} failed: {exception.Message}");
    }

    protected override void OnSendBufferOverflow(long queued)
    {
        Logger.Error($"Disconnecting {RemoteAddress} - it does not read ({queued} bytes waiting to be sent)");
    }

    protected override void OnSessionException(Exception exception)
    {
        Logger.Error(exception, $"Error on the connection of {RemoteAddress}");
    }

    protected override void OnDisconnected()
    {
        if (_announced)
            EventBus.Instance.PublishOnBackgroundThreadAsync(new ClientConnectionMessage
            {
                Connected = false,
                ClientGuid = ClientGuid,
                ClientIP = RemoteAddress
            });

        _receiveBuffer.Clear();
        SyncServer.HandleDisconnect(this);
    }

    /// <summary>
    ///     Takes every complete line out of the receive buffer (one pass, no copy of the whole buffer per line) and
    ///     parses it. Junk is skipped cheaply; too much junk or too many lines close the connection.
    /// </summary>
    private List<NetworkMessage> GetNetworkMessages()
    {
        var messages = new List<NetworkMessage>();
        var lineStart = 0;

        for (var i = 0; i < _receiveBuffer.Length; i++)
        {
            if (_receiveBuffer[i] != '\n') continue;

            var line = _receiveBuffer.ToString(lineStart, i - lineStart).Trim();
            lineStart = i + 1;

            if (!CountLine())
            {
                Logger.Warn($"Disconnecting {RemoteAddress} - too many messages");
                return null;
            }

            if (line.Length == 0) continue;

            var message = TryDecode(line);
            if (message != null)
            {
                messages.Add(message);
                continue;
            }

            _invalidLines++;
            if (_invalidLines > MaxInvalidLines || (ClientGuid == null && _invalidLines > MaxInvalidLinesBeforeHandshake))
            {
                Logger.Warn($"Disconnecting {RemoteAddress} - invalid data");
                return null;
            }
        }

        if (lineStart > 0) _receiveBuffer.Remove(0, lineStart);

        return messages;
    }

    /// <summary>
    ///     Takes a token for one VOICE_KEY from a bucket of <see cref="VoiceKeyBurst" /> that refills with
    ///     <see cref="MaxVoiceKeysPerSecond" /> per second; false (drop the message) if it is empty.
    ///     <paramref name="logDrop" /> is true for at most one drop per minute.
    /// </summary>
    public bool AllowVoiceKey(DateTime nowUtc, out bool logDrop)
    {
        logDrop = false;

        if (_voiceKeys.TryTake(nowUtc)) return true;

        if (nowUtc - _lastVoiceKeyDropLogUtc >= VoiceKeyDropLogInterval)
        {
            _lastVoiceKeyDropLogUtc = nowUtc;
            logDrop = true;
        }

        return false;
    }

    /// <summary>Counts one received line; false if the connection sends more than it ever needs to.</summary>
    private bool CountLine()
    {
        var now = DateTime.UtcNow;
        if (now - _lineWindowStartUtc >= LineRateWindow)
        {
            _lineWindowStartUtc = now;
            _linesInWindow = 0;
        }

        return ++_linesInWindow <= MaxLinesPerWindow;
    }

    private static NetworkMessage TryDecode(string line)
    {
        // every message is one JSON object - reject anything else without paying for a parser exception
        if (line[0] != '{' || line[^1] != '}') return null;

        try
        {
            return NetworkMessage.Decode(line);
        }
        catch (Exception ex)
        {
            // Can be extremely noisy, use only for debugging! The line itself is never logged: it may be a SYNC hello
            // with the server password.
            if (Logger.IsTraceEnabled)
                Logger.Trace($"Unable to process a JSON line of {line.Length} characters: {ex.GetType().Name}");

            return null;
        }
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        var charCount = _utf8Decoder.GetCharCount(buffer, (int)offset, (int)size);
        if (_charBuffer.Length < charCount) _charBuffer = new char[Math.Max(charCount, _charBuffer.Length * 2)];
        var chars = _utf8Decoder.GetChars(buffer, (int)offset, (int)size, _charBuffer, 0);
        _receiveBuffer.Append(_charBuffer, 0, chars);

        if (_receiveBuffer.Length > MaxReceiveBufferLength)
        {
            Logger.Warn($"Disconnecting {RemoteAddress} - message too large");
            _receiveBuffer.Clear();
            Disconnect();
            return;
        }

        LastMessageReceived = DateTime.Now.Ticks;

        var messages = GetNetworkMessages();
        if (messages == null)
        {
            _receiveBuffer.Clear();
            Disconnect();
            return;
        }

        foreach (var s in messages)
        {
            if (!IsConnected) break;
            SyncServer.HandleMessage(this, s);
        }
    }

    /**
     * Send a full radio update every 175 seconds if one hasnt been sent
     * This will be triggered by a metadata UPDATE message
     */
    public bool ShouldSendFullRadioUpdate()
    {
        var lastSent = new TimeSpan(DateTime.Now.Ticks - LastFullRadioSent);
        return lastSent.TotalSeconds > Constants.CLIENT_UPDATE_INTERVAL_LIMIT - 5;
    }

    public bool ShouldSendMetadataUpdate()
    {
        var lastSent = new TimeSpan(DateTime.Now.Ticks - LastMetaDataSent);
        return lastSent.TotalSeconds > Constants.CLIENT_UPDATE_INTERVAL_LIMIT - 5;
    }
}
