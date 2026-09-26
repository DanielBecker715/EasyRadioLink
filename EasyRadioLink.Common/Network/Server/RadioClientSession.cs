using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.NetCoreServer;
using EasyRadioLink.Common.Network.Singletons;
using NLog;

namespace EasyRadioLink.Common.Network.Server;

public class RadioClientSession : TcpSession
{
    // A single JSON line is a few KB; anything bigger without a line break is garbage or an attack
    private const int MaxReceiveBufferLength = 512 * 1024;

    // Flood protection. A normal client sends a few lines per second (radio updates at most every 200 ms).
    private const int MaxLinesPerWindow = 100;
    private static readonly TimeSpan LineRateWindow = TimeSpan.FromSeconds(5);
    private const int MaxInvalidLinesBeforeHandshake = 3;
    private const int MaxInvalidLines = 20;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly HashSet<IPAddress> _bannedIps;

    // Received data string (UTF-8 decoded with a stateful decoder, so characters split across packets survive).
    private readonly StringBuilder _receiveBuffer = new();
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private char[] _charBuffer = new char[4096];

    private int _invalidLines;
    private int _linesInWindow;
    private DateTime _lineWindowStartUtc = DateTime.UtcNow;

    private string _ip;
    private long _lastFullRadioSent;
    private int _port;
    private volatile bool _rejected;

    public RadioClientSession(ServerSync server,
        HashSet<IPAddress> bannedIps) : base(server)
    {
        _bannedIps = bannedIps;
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

    /// <summary>When the TCP connection was accepted (UTC) - used for the handshake timeout.</summary>
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

    protected override void OnConnected()
    {
        ConnectedAtUtc = DateTime.UtcNow;

        var clientIp = (IPEndPoint)Socket.RemoteEndPoint;

        EventBus.Instance.PublishOnBackgroundThreadAsync(new ClientConnectionMessage
        {
            Connected = true,
            ClientIP = clientIp.ToString(),
            ClientGuid = ClientGuid
        });

        _ip = clientIp.Address.ToString();
        _port = clientIp.Port;
        RemoteIp = clientIp.Address;

        if (_bannedIps.Contains(clientIp.Address))
        {
            Logger.Warn("Disconnecting Banned Client -  " + clientIp.Address + " " + clientIp.Port);

            Disconnect();
        }
        else if (((ServerSync)Server).IsLockedOut(clientIp.Address))
        {
            Logger.Info($"Disconnecting {RemoteAddress} - too many wrong passwords, try again later");

            Disconnect();
        }
        else if (((ServerSync)Server).ExceedsConnectionLimits(this, out var reason))
        {
            Logger.Warn($"Disconnecting {RemoteAddress} - {reason}");

            Disconnect();
        }
    }

    protected override void OnSent(long sent, long pending)
    {
        // Disconnect slow client with 50MB send buffer
        if (pending > 5e+7)
        {
            Logger.Error("Disconnecting - pending is too large");
            Disconnect();
        }
    }

    protected override void OnDisconnected()
    {
        EventBus.Instance.PublishOnBackgroundThreadAsync(new ClientConnectionMessage
        {
            Connected = false,
            ClientGuid = ClientGuid,
            ClientIP = RemoteAddress
        });

        _receiveBuffer.Clear();
        ((ServerSync)Server).HandleDisconnect(this);
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
            // Can be extremely noisy, use only for debugging!
            if (Logger.IsTraceEnabled) Logger.Trace(ex, $"Unable to process JSON: \n {line}");

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
            ((ServerSync)Server).HandleMessage(this, s);
        }
    }

    protected override void OnTrySendException(Exception ex)
    {
        Logger.Error(ex, "Caught Client Session Exception");
    }

    protected override void OnError(SocketError error)
    {
        Logger.Error($"Caught Socket Error: {error}");
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
