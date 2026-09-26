using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Crypto;
using EasyRadioLink.Common.Network.Singletons;
using NLog;

namespace EasyRadioLink.Common.Network.Client;

/// <summary>
///     End-to-end voice encryption of one connection, client side (<see cref="E2EVoiceCrypto" />).
///     <list type="bullet">
///         <item>
///             Sending (<see cref="CreateVoicePacket" />, audio thread): every PTT press on a frequency is a new
///             transmission with its own key K. K is wrapped for every other client whose radio hears the frequency (from
///             the server's client list) and sent as VOICE_KEY over TCP - in the background, and again for listeners
///             that tune in (or come back) while the transmission lasts (<see cref="VoiceTransmission.TakeNewRecipients" />).
///             On a radio check (echo) frequency K also goes into this client's own receive cache, so the echo can be
///             played; on every other frequency the client never receives its own voice, so K stays out of the cache.
///         </item>
///         <item>
///             Receiving: <see cref="HandleVoiceKey" /> (TCP thread) unwraps this client's entry of a VOICE_KEY into
///             <see cref="Receiver" />, which decrypts the voice packets (audio decode thread). Keys of transmissions
///             that are over are refused, and frames already played are never played again - also after a reconnect
///             (<see cref="E2EKeyPair.ReceivedTransmissions" />, kept for the whole app run).
///         </item>
///     </list>
///     At most one VOICE_KEY is sent per <see cref="MinKeyMessageInterval" /> (the server accepts a burst of 20, then
///     10 per second). Keys, wrapped keys and audio are never logged; voice keys that are refused are logged at debug
///     level and summarised at most once per <see cref="RefusedKeyLogInterval" />.
/// </summary>
public sealed class E2EVoiceSession : IDisposable
{
    /// <summary>Spacing of VOICE_KEY messages (8 per second - below the server's limit of 10).</summary>
    public static readonly TimeSpan MinKeyMessageInterval = TimeSpan.FromMilliseconds(125);

    /// <summary>Refused voice keys (did not authenticate, old transmission) are summarised in the log this often.</summary>
    public static readonly TimeSpan RefusedKeyLogInterval = TimeSpan.FromMinutes(1);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<string, ClientInfo> _clients;
    private readonly Func<double, bool> _isRadioCheckFrequency;
    private readonly E2EKeyPair _keys;
    private readonly byte[] _ownGuidBytes;
    private readonly Func<NetworkMessage, Task> _sendToServer;
    private readonly object _sendLock = new();

    private volatile bool _disposed;
    private DateTime _nextKeyMessageUtc = DateTime.MinValue;
    private VoiceTransmission _transmission;

    // refused voice keys since the last summary (see RefusedKeyLogInterval)
    private readonly object _refusedLock = new();
    private int _refusedKeys;
    private bool _refusedLogged;
    private DateTime _lastRefusedLogUtc;

    /// <param name="ownGuid">This client's id.</param>
    /// <param name="keys">This client's key pair (its public key is in the SYNC hello).</param>
    /// <param name="clients">The connected clients as the server announced them (radios and public keys).</param>
    /// <param name="sendToServer">Sends a message over this connection's TLS stream.</param>
    /// <param name="isRadioCheckFrequency">
    ///     True for the server's radio check (echo) frequencies (only there the client keeps the key of its own
    ///     transmission to play the echo); null = <see cref="SyncedServerSettings.IsTestFrequency" /> of the connected
    ///     server.
    /// </param>
    public E2EVoiceSession(string ownGuid, E2EKeyPair keys, ConcurrentDictionary<string, ClientInfo> clients,
        Func<NetworkMessage, Task> sendToServer, Func<double, bool> isRadioCheckFrequency = null)
    {
        if (!UdpTransportSession.IsValidClientGuid(ownGuid)) throw new ArgumentException("Invalid client id", nameof(ownGuid));

        OwnGuid = ownGuid;
        _ownGuidBytes = Encoding.ASCII.GetBytes(ownGuid);
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _sendToServer = sendToServer ?? throw new ArgumentNullException(nameof(sendToServer));
        _isRadioCheckFrequency = isRadioCheckFrequency ?? SyncedServerSettings.Instance.IsTestFrequency;
    }

    public string OwnGuid { get; }

    /// <summary>Decrypts received voice packets with the keys delivered by <see cref="HandleVoiceKey" />.</summary>
    public E2EVoiceReceiver Receiver { get; } = new();

    /// <summary>The running transmission (tests).</summary>
    internal VoiceTransmission CurrentTransmission
    {
        get
        {
            lock (_sendLock)
            {
                return _transmission;
            }
        }
    }

    public void Dispose()
    {
        lock (_sendLock)
        {
            _disposed = true;
            _transmission?.Dispose();
            _transmission = null;
        }

        Receiver.Dispose();

        lock (_refusedLock)
        {
            if (_refusedKeys > 0)
                Logger.Warn($"E2E: {_refusedKeys} more voice key(s) were refused (did not authenticate or belonged " +
                            "to an old transmission)");
            _refusedKeys = 0;
        }
    }

    /// <summary>
    ///     Builds the voice packet for one Opus frame on <paramref name="frequency" />: starts a new transmission if
    ///     none is running or the frequency/modulation changed, encrypts the frame with the transmission key and hands
    ///     the key to listeners that do not have it yet. Null (send nothing) once the session is closed.
    /// </summary>
    public UDPVoicePacket CreateVoicePacket(byte[] opus, ulong packetNumber, double frequency, Modulation modulation,
        DateTime? nowUtc = null)
    {
        if (opus == null || opus.Length == 0 || opus.Length > E2EVoiceCrypto.MaxOpusFrameLength) return null;

        var now = nowUtc ?? DateTime.UtcNow;
        var frequencies = new[] { frequency };
        var modulations = new[] { (byte)modulation };

        VoiceTransmission transmission;
        lock (_sendLock)
        {
            if (_disposed) return null;

            if (_transmission == null || !_transmission.IsFor(frequency, modulation))
            {
                _transmission?.Retire();
                _transmission = new VoiceTransmission(OwnGuid, frequency, modulation);

                // the sender knows K: its own radio check echo is decrypted with it. Only there - the server echoes
                // nothing else, so on other frequencies K stays out of the receive cache.
                if (IsRadioCheckFrequency(frequency)) _transmission.AddTo(Receiver.Keys, now);
            }

            transmission = _transmission;
            AnnounceToNewListeners(transmission, now);
        }

        var segment = transmission.EncryptFrame(opus, packetNumber,
            E2EVoiceCrypto.BuildAad(_ownGuidBytes, frequencies, modulations));
        if (segment == null) return null;

        return new UDPVoicePacket
        {
            GuidBytes = _ownGuidBytes,
            OriginalClientGuidBytes = _ownGuidBytes,
            AudioPart1Bytes = segment,
            AudioPart1Length = (ushort)segment.Length,
            Frequencies = frequencies,
            Modulations = modulations,
            Encryptions = new byte[1],
            UnitId = 0,
            PacketNumber = packetNumber,
            RetransmissionCount = 0
        };
    }

    /// <summary>PTT released: the next frame starts a new transmission with a new key.</summary>
    public void EndTransmission()
    {
        lock (_sendLock)
        {
            _transmission?.Retire();
            _transmission = null;
        }
    }

    /// <summary>
    ///     A VOICE_KEY from the server: unwraps this client's entry with the sender's public key from the client list
    ///     and caches the transmission key. False if the message is not for this client, the sender is unknown or the
    ///     wrapped key does not authenticate (then nothing is cached).
    /// </summary>
    public bool HandleVoiceKey(VoiceKeyMessage message, DateTime? nowUtc = null)
    {
        try
        {
            return TryHandleVoiceKey(message, nowUtc ?? DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // fail closed (nothing cached) - and never let one bad key message break the connection
            Logger.Warn(ex, "E2E: unable to handle a voice key");
            return false;
        }
    }

    private bool TryHandleVoiceKey(VoiceKeyMessage message, DateTime nowUtc)
    {
        if (_disposed || message == null || message.SenderGuid == OwnGuid) return false;

        if (!message.IsValid(out var txId, out var reason))
        {
            Logger.Debug($"E2E: ignoring an invalid voice key ({reason})");
            return false;
        }

        if (!message.Keys.TryGetValue(OwnGuid, out var wrapped)) return false;

        if (!_clients.TryGetValue(message.SenderGuid, out var sender))
        {
            Logger.Debug($"E2E: voice key from unknown client {message.SenderGuid} ignored");
            return false;
        }

        var senderPublicKey = sender.E2EPublicKey;
        if (!E2EKeyPair.IsValidPublicKey(senderPublicKey))
        {
            Logger.Debug($"E2E: {message.SenderGuid} has no usable public key - voice key ignored");
            return false;
        }

        var txIdBytes = Convert.FromBase64String(message.TxId);
        if (!E2EVoiceCrypto.TryUnwrapKey(_keys, OwnGuid, senderPublicKey, message.SenderGuid, txIdBytes, wrapped,
                out var key))
        {
            // a malicious server could send these by the thousand: no log line each
            Logger.Debug($"E2E: the voice key from {message.SenderGuid} (transmission {txId:x16}) did not " +
                         "authenticate - ignored");
            CountRefusedKey(nowUtc);
            return false;
        }

        try
        {
            // replay protection for the whole app run: the key of an old transmission is not accepted again, and the
            // frames of a known one continue its packet number window (a reconnect does not start it over)
            var replayState = _keys.ReceivedTransmissions.Register(senderPublicKey, txId, nowUtc);
            if (replayState == null)
            {
                Logger.Debug($"E2E: the voice key from {message.SenderGuid} belongs to an old transmission " +
                             $"({txId:x16}) - replay refused");
                CountRefusedKey(nowUtc);
                return false;
            }

            return Receiver.Keys.TryAdd(message.SenderGuid, txId, key, nowUtc, replayState);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private bool IsRadioCheckFrequency(double frequency)
    {
        try
        {
            return _isRadioCheckFrequency(frequency);
        }
        catch (Exception ex)
        {
            // fail closed: no echo rather than K in the receive cache
            Logger.Debug(ex, "E2E: unable to check the radio check frequencies");
            return false;
        }
    }

    /// <summary>
    ///     Counts a refused voice key. The first one is logged as a warning, the ones after it at most once per
    ///     <see cref="RefusedKeyLogInterval" /> as a count.
    /// </summary>
    private void CountRefusedKey(DateTime nowUtc)
    {
        lock (_refusedLock)
        {
            _refusedKeys++;
            if (_refusedLogged && nowUtc >= _lastRefusedLogUtc && nowUtc - _lastRefusedLogUtc < RefusedKeyLogInterval)
                return;

            Logger.Warn($"E2E: {_refusedKeys} voice key(s) were refused (did not authenticate or belonged to an old " +
                        $"transmission) - further ones are summarised at most once per {RefusedKeyLogInterval.TotalSeconds:0} s");
            _refusedKeys = 0;
            _refusedLogged = true;
            _lastRefusedLogUtc = nowUtc;
        }
    }

    /// <summary>
    ///     Computes the ECDH secrets with these clients in the background (about a millisecond each on Windows, once
    ///     per client and app run), so that the first PTT press or voice key with them costs only microseconds.
    /// </summary>
    public void PrepareKeys(IEnumerable<ClientInfo> clients)
    {
        if (_disposed || clients == null) return;

        var publicKeys = new List<string>();
        foreach (var client in clients)
            if (client != null && client.ClientGuid != OwnGuid && E2EKeyPair.IsValidPublicKey(client.E2EPublicKey))
                publicKeys.Add(client.E2EPublicKey);

        if (publicKeys.Count == 0) return;

        _ = Task.Run(() =>
        {
            foreach (var publicKey in publicKeys)
            {
                if (_disposed) return;

                try
                {
                    // an unusable key is simply not kept - wrapping for it is skipped later
                    _keys.TryPrepare(publicKey);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        });
    }

    /// <summary>
    ///     A client left or changed its public key: forget its transmission keys, and if it listened to the running
    ///     transmission, wrap the key for it again once it is back (reconnect, id taken over).
    /// </summary>
    public void ForgetClient(string clientGuid)
    {
        Receiver.RemoveSender(clientGuid);

        lock (_sendLock)
        {
            _transmission?.ForgetListener(clientGuid);
        }
    }

    private void AnnounceToNewListeners(VoiceTransmission transmission, DateTime now)
    {
        if (now < _nextKeyMessageUtc) return;

        var recipients = transmission.TakeNewRecipients(EnumerateClients());
        if (recipients.Count == 0) return;

        var lease = transmission.AcquireLease();
        if (lease == null) return;

        _nextKeyMessageUtc = now + MinKeyMessageInterval;
        _ = Task.Run(() => SendKeysAsync(transmission, recipients, lease));
    }

    private async Task SendKeysAsync(VoiceTransmission transmission, List<VoiceKeyRecipient> recipients,
        IDisposable lease)
    {
        try
        {
            List<VoiceKeyMessage> messages;
            using (lease)
            {
                messages = transmission.WrapFor(_keys, recipients);
            }

            foreach (var message in messages)
            {
                if (_disposed) return;

                Logger.Debug($"E2E: key of transmission {transmission.TxId:x16} for {message.Keys.Count} listener(s)");
                await _sendToServer(new NetworkMessage
                {
                    MsgType = NetworkMessage.MessageType.VOICE_KEY,
                    VoiceKey = message
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "E2E: unable to send a voice key");
        }
    }

    // lock-free walk over the client list (no snapshot copy per audio frame)
    private IEnumerable<ClientInfo> EnumerateClients()
    {
        foreach (var pair in _clients) yield return pair.Value;
    }
}
