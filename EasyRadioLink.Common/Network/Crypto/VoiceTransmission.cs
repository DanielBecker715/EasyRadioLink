using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Server;

namespace EasyRadioLink.Common.Network.Crypto;

/// <summary>A listener a transmission key has to be wrapped for.</summary>
public readonly record struct VoiceKeyRecipient(string ClientGuid, string PublicKey);

/// <summary>
///     Sender side of one transmission (one PTT press on one frequency and modulation): a random key K and TxId
///     (<see cref="E2EVoiceCrypto" />), the frame encryption with K and the set of listeners K was already wrapped for,
///     so that listeners who tune in during the transmission get it too. K never leaves this object except wrapped for a
///     listener (and into this client's own receive cache, for the radio check echo). Thread-safe.
/// </summary>
public sealed class VoiceTransmission : IDisposable
{
    private readonly AesGcm _cipher;

    // listener id -> what K was wrapped for (public key and the listener's radio state as announced then)
    private readonly Dictionary<string, Coverage> _covered = new(StringComparer.Ordinal);

    // mark and sweep of _covered: listeners not seen hearing in the current pass lose their entry
    private int _pass;
    private readonly byte[] _key;
    private readonly object _lock = new();
    private readonly byte[] _txId;
    private bool _disposed;

    // key wraps still running in the background: a retired transmission keeps K until they are done
    private int _leases;
    private bool _retired;

    public VoiceTransmission(string senderGuid, double frequency, Modulation modulation)
    {
        if (!UdpTransportSession.IsValidClientGuid(senderGuid))
            throw new ArgumentException("Invalid sender id", nameof(senderGuid));

        SenderGuid = senderGuid;
        Frequency = frequency;
        Modulation = modulation;

        _key = RandomNumberGenerator.GetBytes(E2EVoiceCrypto.TransmissionKeyLength);
        _txId = E2EVoiceCrypto.NewTxId();
        TxId = E2EVoiceCrypto.TxIdToUInt64(_txId);
        _cipher = new AesGcm(_key, E2EVoiceCrypto.TagLength);
    }

    public string SenderGuid { get; }
    public double Frequency { get; }
    public Modulation Modulation { get; }

    /// <summary>The transmission id (big-endian value of its 8 bytes; not secret).</summary>
    public ulong TxId { get; }

    public string TxIdBase64 => Convert.ToBase64String(_txId);

    /// <summary>Destroys K at once (running key wraps produce nothing any more).</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            DisposeLocked();
        }
    }

    private void DisposeLocked()
    {
        if (_disposed) return;

        _disposed = true;
        _cipher.Dispose();
        CryptographicOperations.ZeroMemory(_key);
        _covered.Clear();
    }

    /// <summary>
    ///     The transmission is over (PTT released, other frequency): K is destroyed as soon as the key wraps that are
    ///     still running (<see cref="AcquireLease" />) have finished, so a short transmission still reaches its listeners.
    /// </summary>
    public void Retire()
    {
        lock (_lock)
        {
            _retired = true;
            if (_leases == 0) DisposeLocked();
        }
    }

    /// <summary>Keeps K alive for a background key wrap until the lease is disposed (null if K is gone already).</summary>
    public IDisposable AcquireLease()
    {
        lock (_lock)
        {
            if (_disposed) return null;

            _leases++;
            return new Lease(this);
        }
    }

    private void ReleaseLease()
    {
        lock (_lock)
        {
            _leases--;
            if (_retired && _leases == 0) DisposeLocked();
        }
    }

    /// <summary>True if a frame on this frequency and modulation belongs to this transmission.</summary>
    public bool IsFor(double frequency, Modulation modulation)
    {
        return frequency.Equals(Frequency) && modulation == Modulation;
    }

    /// <summary>Encrypts one Opus frame into an audio segment; null once the transmission is over (fail closed).</summary>
    public byte[] EncryptFrame(ReadOnlySpan<byte> opus, ulong packetNumber, ReadOnlySpan<byte> aad)
    {
        lock (_lock)
        {
            if (_disposed) return null;

            return E2EVoiceCrypto.EncryptFrame(_cipher, _txId, packetNumber, aad, opus);
        }
    }

    /// <summary>Puts K into a receive cache (the sender's own, for the radio check echo).</summary>
    public bool AddTo(VoiceKeyCache cache, DateTime nowUtc)
    {
        lock (_lock)
        {
            return !_disposed && cache.TryAdd(SenderGuid, TxId, _key, nowUtc);
        }
    }

    /// <summary>
    ///     The listeners among <paramref name="clients" /> (the complete current client list) that need K and marks
    ///     them as covered. A listener is every other client whose radio can hear this frequency and modulation - the
    ///     rule the server routes by (<see cref="VoiceRouting.CanReceive(PlayerRadioInfoBase,double,Modulation)" />).
    ///     K is wrapped (again) for a listener
    ///     <list type="bullet">
    ///         <item>that was not covered yet (tuned in, joined, or came back - see below),</item>
    ///         <item>whose public key changed (reconnected with a new key pair, id taken over),</item>
    ///         <item>
    ///             whose radio state was announced again since K was wrapped for it (a new <c>RadioInfo</c> object): the
    ///             server forwards a key only to clients it sees on the frequency at that moment, and the sender's view
    ///             of a listener can be a moment behind the server's (a tuning knob sweeping past the frequency) - so a
    ///             key the server dropped is sent again once the listener's new state arrives.
    ///         </item>
    ///     </list>
    ///     Clients that are gone or can't hear the frequency any more lose their entry, so they get K again when they
    ///     come back. Wrapping K again for the same listener is harmless: the same wrap key, K and zero nonce give the
    ///     identical blob, and a receiver keeps the first key of a transmission. Clients without a valid public key are
    ///     skipped (they would hear the transmission scrambled).
    /// </summary>
    public List<VoiceKeyRecipient> TakeNewRecipients(IEnumerable<ClientInfo> clients)
    {
        var recipients = new List<VoiceKeyRecipient>();
        if (clients == null) return recipients;

        lock (_lock)
        {
            if (_disposed) return recipients;

            var pass = unchecked(++_pass);
            foreach (var client in clients)
            {
                var guid = client?.ClientGuid;
                if (guid == null || guid == SenderGuid) continue;

                var radio = client.RadioInfo;
                if (!VoiceRouting.CanReceive(radio, Frequency, Modulation)) continue;

                var publicKey = client.E2EPublicKey;
                if (publicKey == null) continue;

                if (_covered.TryGetValue(guid, out var coverage) && coverage.PublicKey == publicKey &&
                    ReferenceEquals(coverage.Radio, radio))
                {
                    coverage.Pass = pass;
                    continue;
                }

                _covered[guid] = new Coverage(publicKey, radio, pass);
                if (E2EKeyPair.IsValidPublicKey(publicKey)) recipients.Add(new VoiceKeyRecipient(guid, publicKey));
            }

            // gone, or can't hear the frequency (any more): wrapped again when they come back
            List<string> stale = null;
            foreach (var (guid, coverage) in _covered)
                if (coverage.Pass != pass)
                    (stale ??= new List<string>()).Add(guid);

            if (stale != null)
                foreach (var guid in stale)
                    _covered.Remove(guid);
        }

        return recipients;
    }

    /// <summary>
    ///     A client left (or its id was taken over): K is wrapped for it again if it listens again during this
    ///     transmission.
    /// </summary>
    public void ForgetListener(string clientGuid)
    {
        if (clientGuid == null) return;

        lock (_lock)
        {
            _covered.Remove(clientGuid);
        }
    }

    /// <summary>
    ///     Wraps K for each recipient and builds the VOICE_KEY messages, at most <see cref="VoiceKeyMessage.MaxKeys" />
    ///     entries each. The first wrap for a listener costs an ECDH (about a millisecond, then cached by the key pair) -
    ///     call it off the audio thread.
    ///     Recipients whose public key can't be used are left out. Empty once the transmission is over.
    /// </summary>
    public List<VoiceKeyMessage> WrapFor(E2EKeyPair senderKeys, IReadOnlyList<VoiceKeyRecipient> recipients)
    {
        ArgumentNullException.ThrowIfNull(senderKeys);

        var messages = new List<VoiceKeyMessage>();
        if (recipients == null || recipients.Count == 0) return messages;

        VoiceKeyMessage current = null;
        foreach (var recipient in recipients)
        {
            // the expensive part (ECDH on first contact with this listener) runs outside the lock, so audio frames
            // keep being encrypted meanwhile
            if (!senderKeys.TryPrepare(recipient.PublicKey)) continue;

            byte[] wrapped;
            lock (_lock)
            {
                if (_disposed) return new List<VoiceKeyMessage>();

                try
                {
                    wrapped = E2EVoiceCrypto.WrapKey(senderKeys, SenderGuid, recipient.PublicKey, recipient.ClientGuid,
                        _txId, _key);
                }
                catch (Exception ex) when (ex is CryptographicException or ArgumentException)
                {
                    // one unusable listener must not cost the others their key
                    continue;
                }
            }

            if (wrapped == null) continue;

            if (current == null || current.Keys.Count >= VoiceKeyMessage.MaxKeys)
            {
                current = new VoiceKeyMessage
                {
                    SenderGuid = SenderGuid,
                    TxId = TxIdBase64,
                    Frequency = Frequency,
                    Modulation = Modulation,
                    Keys = new Dictionary<string, string>(StringComparer.Ordinal)
                };
                messages.Add(current);
            }

            current.Keys[recipient.ClientGuid] = Convert.ToBase64String(wrapped);
        }

        return messages;
    }

    private sealed class Coverage
    {
        public Coverage(string publicKey, PlayerRadioInfoBase radio, int pass)
        {
            PublicKey = publicKey;
            Radio = radio;
            Pass = pass;
        }

        public string PublicKey { get; }

        // compared by reference: every radio update from the server brings a new object
        public PlayerRadioInfoBase Radio { get; }

        public int Pass { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private VoiceTransmission _owner;

        public Lease(VoiceTransmission owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            System.Threading.Interlocked.Exchange(ref _owner, null)?.ReleaseLease();
        }
    }

    /// <summary>Test hook: a copy of K (tests check that it never appears anywhere else).</summary>
    internal byte[] CopyKeyForTests()
    {
        lock (_lock)
        {
            return _disposed ? null : (byte[])_key.Clone();
        }
    }

    /// <summary>True once K is destroyed.</summary>
    internal bool IsDisposed
    {
        get
        {
            lock (_lock)
            {
                return _disposed;
            }
        }
    }
}
