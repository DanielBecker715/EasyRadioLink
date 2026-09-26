using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EasyRadioLink.Common.Network.Crypto;

/// <summary>
///     Layout of an encrypted UDP datagram (both directions, protocol 1.1):
///     <code>
///     [22 B client id, ASCII, clear] [8 B counter, big-endian, clear] [AES-256-GCM ciphertext of the body] [16 B tag]
///     </code>
///     The client id is always the id of the client that owns the key (the sender for client -> server, the recipient
///     for server -> client). Nonce = 4 byte direction (big-endian <see cref="UdpTransportDirection" />) + the 8 byte
///     counter; AAD = client id + counter (the first <see cref="HeaderLength" /> bytes). The body is
///     <see cref="PingBody" /> for a ping/pong, otherwise an encoded <c>UDPVoicePacket</c>.
/// </summary>
public static class UdpDatagram
{
    public const int GuidLength = 22;
    public const int CounterLength = sizeof(ulong);

    /// <summary>Client id + counter: the clear header, also the AAD.</summary>
    public const int HeaderLength = GuidLength + CounterLength;

    public const int TagLength = 16;

    /// <summary>Bytes an encrypted datagram adds to its body.</summary>
    public const int Overhead = HeaderLength + TagLength;

    /// <summary>Largest body accepted/produced (a voice packet is a few hundred bytes).</summary>
    public const int MaxBodyLength = 4096;

    public const int MinLength = Overhead + 4;
    public const int MaxLength = Overhead + MaxBodyLength;

    /// <summary>Body of a ping (client -> server) and of its answer (server -> client).</summary>
    public static ReadOnlySpan<byte> PingBody => "PING"u8;

    /// <summary>True if <paramref name="length" /> can be an encrypted datagram at all (checked before any lookup).</summary>
    public static bool HasValidLength(int length)
    {
        return length is >= MinLength and <= MaxLength;
    }

    /// <summary>The clear client id of a datagram (not authenticated yet!), or null if it is too short.</summary>
    public static string ReadClientGuid(byte[] datagram)
    {
        if (datagram == null || datagram.Length < HeaderLength) return null;

        return Encoding.ASCII.GetString(datagram, 0, GuidLength);
    }

    public static bool IsPing(ReadOnlySpan<byte> body)
    {
        return body.SequenceEqual(PingBody);
    }
}

/// <summary>First 4 nonce bytes: separates the two directions that share one key.</summary>
public enum UdpTransportDirection : uint
{
    ClientToServer = 1,
    ServerToClient = 2
}

public enum UdpOpenResult
{
    Ok,

    /// <summary>Wrong length or not addressed to this key's client id.</summary>
    Malformed,

    /// <summary>Counter already seen or older than the replay window.</summary>
    Replayed,

    /// <summary>Tag check failed: wrong key, tampered or forged.</summary>
    AuthenticationFailed,

    /// <summary>The session was disposed (client gone).</summary>
    Closed
}

/// <summary>
///     Anti-replay sliding window over 64 packets (RFC 4303 style): accepts every counter above the highest one seen and
///     unseen counters up to 63 below it. Call <see cref="IsAcceptable" /> before and <see cref="Accept" /> only after a
///     successful authentication, so forged packets can never move the window. Counter 0 is never valid.
///     Not thread-safe.
/// </summary>
public sealed class ReplayWindow
{
    public const int Size = 64;

    private ulong _bitmap; // bit i set = counter (_highest - i) was accepted
    private ulong _highest;

    /// <summary>Highest accepted counter (0 = none yet).</summary>
    public ulong Highest => _highest;

    public bool IsAcceptable(ulong counter)
    {
        if (counter == 0) return false;
        if (counter > _highest) return true;

        var age = _highest - counter;
        if (age >= Size) return false;

        return (_bitmap & (1UL << (int)age)) == 0;
    }

    public void Accept(ulong counter)
    {
        if (counter == 0) return;

        if (counter > _highest)
        {
            var shift = counter - _highest;
            _bitmap = shift >= Size ? 0 : _bitmap << (int)shift;
            _bitmap |= 1;
            _highest = counter;
            return;
        }

        var age = _highest - counter;
        if (age < Size) _bitmap |= 1UL << (int)age;
    }
}

/// <summary>
///     Hop encryption of one client's UDP traffic (AES-256-GCM, <see cref="UdpDatagram" /> layout). The server creates
///     a random key per client after a successful SYNC and sends it inside the TLS protected SYNC reply; the client and
///     the server each hold one session with the same key: the client seals client -> server and opens server -> client,
///     the server the other way round.
///     <para>
///         <see cref="Seal" /> and <see cref="Open" /> are thread-safe (each direction has its own cipher and lock).
///         Sealing fails closed (null) once the 64 bit counter is used up; opening checks the length, the client id, the
///         replay window, then the tag, and only then commits the counter.
///     </para>
/// </summary>
public sealed class UdpTransportSession : IDisposable
{
    public const int KeyLength = 32;

    private const int NonceLength = 12;

    private readonly byte[] _guidBytes;
    private readonly AesGcm _openCipher;
    private readonly object _openLock = new();
    private readonly UdpTransportDirection _receiveDirection;
    private readonly ReplayWindow _replayWindow = new();
    private readonly AesGcm _sealCipher;
    private readonly object _sealLock = new();
    private readonly UdpTransportDirection _sendDirection;

    private long _authenticationFailures;
    private bool _closed;
    private bool _exhausted;
    private ulong _nextCounter = 1;
    private long _replays;

    /// <param name="clientGuid">The 22 character (ASCII) id of the client that owns the key.</param>
    /// <param name="key">The <see cref="KeyLength" /> byte key (copied; the caller may clear its buffer).</param>
    /// <param name="keyId">Identifies the key in logs (never the key itself).</param>
    /// <param name="isServer">True on the server (seals server -> client, opens client -> server).</param>
    public UdpTransportSession(string clientGuid, ReadOnlySpan<byte> key, uint keyId, bool isServer)
    {
        if (!IsValidClientGuid(clientGuid))
            throw new ArgumentException($"The client id must be {UdpDatagram.GuidLength} ASCII characters", nameof(clientGuid));
        if (key.Length != KeyLength) throw new ArgumentException($"The key must be {KeyLength} bytes", nameof(key));

        ClientGuid = clientGuid;
        KeyId = keyId;
        _guidBytes = Encoding.ASCII.GetBytes(clientGuid);
        _sealCipher = new AesGcm(key, UdpDatagram.TagLength);
        _openCipher = new AesGcm(key, UdpDatagram.TagLength);

        _sendDirection = isServer ? UdpTransportDirection.ServerToClient : UdpTransportDirection.ClientToServer;
        _receiveDirection = isServer ? UdpTransportDirection.ClientToServer : UdpTransportDirection.ServerToClient;
    }

    public string ClientGuid { get; }

    public uint KeyId { get; }

    /// <summary>Datagrams whose tag did not verify (wrong key, tampered, forged).</summary>
    public long AuthenticationFailures => Interlocked.Read(ref _authenticationFailures);

    /// <summary>Datagrams rejected by the replay window.</summary>
    public long ReplaysRejected => Interlocked.Read(ref _replays);

    public void Dispose()
    {
        lock (_sealLock)
        lock (_openLock)
        {
            if (_closed) return;

            _closed = true;
            _sealCipher.Dispose();
            _openCipher.Dispose();
        }
    }

    /// <summary>A new random key (<see cref="KeyLength" /> bytes).</summary>
    public static byte[] GenerateKey()
    {
        return RandomNumberGenerator.GetBytes(KeyLength);
    }

    /// <summary>A random key id for logs.</summary>
    public static uint GenerateKeyId()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>True for a 22 character id made of printable ASCII (the ShortGuid alphabet in practice).</summary>
    public static bool IsValidClientGuid(string clientGuid)
    {
        if (clientGuid == null || clientGuid.Length != UdpDatagram.GuidLength) return false;

        foreach (var c in clientGuid)
            if (c is < '!' or > '~')
                return false;

        return true;
    }

    /// <summary>
    ///     Encrypts <paramref name="body" /> into a new datagram with the next counter. Returns null once the counter is
    ///     used up or the session is closed - the caller must not send anything then.
    /// </summary>
    public byte[] Seal(ReadOnlySpan<byte> body)
    {
        if (body.Length > UdpDatagram.MaxBodyLength)
            throw new ArgumentException($"The body must not exceed {UdpDatagram.MaxBodyLength} bytes", nameof(body));

        var datagram = new byte[UdpDatagram.Overhead + body.Length];
        _guidBytes.CopyTo(datagram, 0);

        Span<byte> nonce = stackalloc byte[NonceLength];

        lock (_sealLock)
        {
            if (_closed || _exhausted) return null;

            var counter = _nextCounter;
            if (counter == ulong.MaxValue) _exhausted = true;
            else _nextCounter = counter + 1;

            BinaryPrimitives.WriteUInt64BigEndian(datagram.AsSpan(UdpDatagram.GuidLength, UdpDatagram.CounterLength),
                counter);
            WriteNonce(nonce, _sendDirection, counter);

            _sealCipher.Encrypt(nonce, body,
                datagram.AsSpan(UdpDatagram.HeaderLength, body.Length),
                datagram.AsSpan(UdpDatagram.HeaderLength + body.Length, UdpDatagram.TagLength),
                datagram.AsSpan(0, UdpDatagram.HeaderLength));
        }

        return datagram;
    }

    /// <summary>Checks and decrypts a received datagram. <paramref name="body" /> is only set for <see cref="UdpOpenResult.Ok" />.</summary>
    public UdpOpenResult Open(ReadOnlySpan<byte> datagram, out byte[] body)
    {
        body = null;

        if (!UdpDatagram.HasValidLength(datagram.Length)) return UdpOpenResult.Malformed;
        if (!datagram[..UdpDatagram.GuidLength].SequenceEqual(_guidBytes)) return UdpOpenResult.Malformed;

        var counter = BinaryPrimitives.ReadUInt64BigEndian(
            datagram.Slice(UdpDatagram.GuidLength, UdpDatagram.CounterLength));
        var bodyLength = datagram.Length - UdpDatagram.Overhead;
        var plaintext = new byte[bodyLength];

        Span<byte> nonce = stackalloc byte[NonceLength];
        WriteNonce(nonce, _receiveDirection, counter);

        lock (_openLock)
        {
            if (_closed) return UdpOpenResult.Closed;

            // cheap rejection of duplicates/old packets before any crypto
            if (!_replayWindow.IsAcceptable(counter))
            {
                Interlocked.Increment(ref _replays);
                return UdpOpenResult.Replayed;
            }

            try
            {
                _openCipher.Decrypt(nonce,
                    datagram.Slice(UdpDatagram.HeaderLength, bodyLength),
                    datagram.Slice(UdpDatagram.HeaderLength + bodyLength, UdpDatagram.TagLength),
                    plaintext,
                    datagram[..UdpDatagram.HeaderLength]);
            }
            catch (CryptographicException)
            {
                // AesGcm clears the output on failure; nothing of the forged packet is kept
                Interlocked.Increment(ref _authenticationFailures);
                return UdpOpenResult.AuthenticationFailed;
            }

            _replayWindow.Accept(counter);
        }

        body = plaintext;
        return UdpOpenResult.Ok;
    }

    private static void WriteNonce(Span<byte> nonce, UdpTransportDirection direction, ulong counter)
    {
        BinaryPrimitives.WriteUInt32BigEndian(nonce, (uint)direction);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }

    /// <summary>Test hook: continue sealing at <paramref name="counter" /> (counter wrap tests).</summary>
    internal void SetNextCounterForTests(ulong counter)
    {
        lock (_sealLock)
        {
            _nextCounter = counter;
            _exhausted = counter == 0;
        }
    }
}

/// <summary>A UDP key as received in the SYNC reply (client side), handed from the TCP connection to the voice handler.</summary>
public sealed class UdpTransportKey
{
    public UdpTransportKey(byte[] key, uint keyId)
    {
        if (key == null || key.Length != UdpTransportSession.KeyLength)
            throw new ArgumentException($"The key must be {UdpTransportSession.KeyLength} bytes", nameof(key));

        Key = key;
        KeyId = keyId;
    }

    public byte[] Key { get; }
    public uint KeyId { get; }

    /// <summary>Parses the SYNC reply fields; false unless it is exactly a base64 encoded <see cref="UdpTransportSession.KeyLength" /> byte key.</summary>
    public static bool TryParse(string base64Key, uint? keyId, out UdpTransportKey key)
    {
        key = null;
        if (string.IsNullOrEmpty(base64Key) || keyId == null) return false;

        var buffer = new byte[UdpTransportSession.KeyLength];
        if (!Convert.TryFromBase64String(base64Key, buffer, out var written) ||
            written != UdpTransportSession.KeyLength)
        {
            CryptographicOperations.ZeroMemory(buffer);
            return false;
        }

        key = new UdpTransportKey(buffer, keyId.Value);
        return true;
    }

    /// <summary>Overwrites the key bytes (call when the connection is gone).</summary>
    public void Clear()
    {
        CryptographicOperations.ZeroMemory(Key);
    }
}
