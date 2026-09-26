using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace EasyRadioLink.Common.Network.Crypto;

/// <summary>
///     The end-to-end voice key pair of one client: ECDH on NIST P-256. The client app creates one when it starts and
///     keeps it in memory only (never written to disk, a new one on every start). Only the public half leaves the
///     process: as base64 of its DER SubjectPublicKeyInfo in the client list (<c>ClientInfo.E2EPublicKey</c>).
///     <see cref="E2EVoiceCrypto" /> uses it to wrap and unwrap the per-transmission voice keys.
///     <para>
///         The raw ECDH secret with a peer depends only on the two key pairs, so it is computed once per peer public
///         key and kept (at most <see cref="MaxCachedPeers" />, the least recently used one is dropped first; all are
///         cleared on <see cref="Dispose" />): an ECDH costs about a millisecond on Windows, HKDF a microsecond. The
///         secrets never leave this object - only wrap keys derived from them (HKDF with the transmission id as salt)
///         do.
///     </para>
///     <para>
///         The key pair also carries the app run's memory of received transmissions
///         (<see cref="ReceivedTransmissions" />): transmission keys are wrapped for this key pair, so the replay
///         protection lives exactly as long as it.
///     </para>
/// </summary>
public sealed class E2EKeyPair : IDisposable
{
    /// <summary>Length of the DER SubjectPublicKeyInfo of an uncompressed P-256 point.</summary>
    public const int PublicKeyLength = 91;

    /// <summary>Longest base64 text of a valid public key (checked before decoding anything).</summary>
    public const int MaxPublicKeyTextLength = (PublicKeyLength + 2) / 3 * 4;

    /// <summary>Peers whose ECDH secret is kept; beyond that the least recently used one is dropped.</summary>
    public const int MaxCachedPeers = 4096;

    // SEQUENCE { SEQUENCE { OID id-ecPublicKey, OID prime256v1 }, BIT STRING { 0x04 X Y } }: everything up to the
    // uncompressed point marker 0x04 is the same for every P-256 key
    private static readonly byte[] SpkiPrefix =
    [
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
        0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04
    ];

    private readonly ECDiffieHellman _key;
    private readonly object _lock = new();
    private readonly int _maxCachedPeers;

    // peer public key (base64) -> raw ECDH secret with it; _recency orders them, most recently used first
    private readonly Dictionary<string, LinkedListNode<SharedSecret>> _sharedSecrets = new(StringComparer.Ordinal);
    private readonly LinkedList<SharedSecret> _recency = new();
    private bool _disposed;

    private E2EKeyPair(ECDiffieHellman key, int maxCachedPeers = MaxCachedPeers)
    {
        _key = key;
        _maxCachedPeers = maxCachedPeers;

        var spki = key.ExportSubjectPublicKeyInfo();
        if (!HasP256Layout(spki))
        {
            key.Dispose();
            throw new CryptographicException("The platform exported an unexpected P-256 public key format");
        }

        PublicKey = Convert.ToBase64String(spki);
    }

    /// <summary>Base64 of the DER SubjectPublicKeyInfo (<see cref="PublicKeyLength" /> bytes).</summary>
    public string PublicKey { get; }

    /// <summary>
    ///     The transmissions whose keys were accepted with this key pair (replay protection across reconnects, see
    ///     <see cref="TransmissionReplayGuard" />).
    /// </summary>
    public TransmissionReplayGuard ReceivedTransmissions { get; } = new();

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;

            _disposed = true;
            ClearSharedSecrets();
            _key.Dispose();
        }
    }

    /// <summary>A new random key pair.</summary>
    public static E2EKeyPair Create()
    {
        return new E2EKeyPair(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
    }

    /// <summary>Test hook: a new key pair that keeps at most <paramref name="maxCachedPeers" /> ECDH secrets.</summary>
    internal static E2EKeyPair CreateForTests(int maxCachedPeers)
    {
        return new E2EKeyPair(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), maxCachedPeers);
    }

    /// <summary>Test hook: a key pair around a known P-256 key (takes ownership) - for spec conformance tests.</summary>
    internal static E2EKeyPair FromKeyForTests(ECDiffieHellman key)
    {
        return new E2EKeyPair(key);
    }

    /// <summary>
    ///     Format check only (the server's check; no curve arithmetic): base64 of exactly
    ///     <see cref="PublicKeyLength" /> bytes with the fixed P-256 SubjectPublicKeyInfo header and an uncompressed point.
    /// </summary>
    public static bool IsValidPublicKey(string base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > MaxPublicKeyTextLength) return false;

        Span<byte> buffer = stackalloc byte[PublicKeyLength + 3];
        return Convert.TryFromBase64String(base64, buffer, out var written)
               && written == PublicKeyLength
               && HasP256Layout(buffer[..written]);
    }

    /// <summary>
    ///     Imports a peer's public key for ECDH. Null unless it passes <see cref="IsValidPublicKey" /> and the platform
    ///     accepts the point (it must lie on the curve). The caller disposes the result.
    /// </summary>
    public static ECDiffieHellman ImportPublicKey(string base64)
    {
        if (!IsValidPublicKey(base64)) return null;

        var spki = Convert.FromBase64String(base64);
        ECDiffieHellman key = null;
        try
        {
            key = ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);

            if (read != spki.Length || key.KeySize != 256)
            {
                key.Dispose();
                return null;
            }

            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or ArgumentException)
        {
            // e.g. a point that is not on the curve (Windows CNG reports it as PlatformNotSupportedException)
            key?.Dispose();
            return null;
        }
    }

    /// <summary>
    ///     Computes (once) and keeps the ECDH secret with <paramref name="peerPublicKey" />, so that a later
    ///     <see cref="TryDeriveKey" /> is cheap. False if the key is unusable (bad format, not on the curve).
    /// </summary>
    public bool TryPrepare(string peerPublicKey)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sharedSecrets.TryGetValue(peerPublicKey ?? "", out var cached))
            {
                MarkUsed(cached);
                return true;
            }
        }

        // the expensive part (import + ECDH) outside the lock; a racing duplicate computes the same secret
        using var peer = ImportPublicKey(peerPublicKey);
        if (peer == null) return false;

        byte[] secret;
        try
        {
            using var peerKey = peer.PublicKey;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                secret = _key.DeriveRawSecretAgreement(peerKey);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return false;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                CryptographicOperations.ZeroMemory(secret);
                throw new ObjectDisposedException(nameof(E2EKeyPair));
            }

            if (_sharedSecrets.TryGetValue(peerPublicKey, out var existing))
            {
                // a racing duplicate computed the same secret
                CryptographicOperations.ZeroMemory(secret);
                MarkUsed(existing);
                return true;
            }

            // full: only the least recently used secret goes (a flood of new peers can't evict everybody at once)
            if (_sharedSecrets.Count >= _maxCachedPeers) EvictLeastRecentlyUsed();

            _sharedSecrets[peerPublicKey] = _recency.AddFirst(new SharedSecret(peerPublicKey, secret));
            return true;
        }
    }

    /// <summary>
    ///     HKDF-SHA256(ikm = ECDH secret with <paramref name="peerPublicKey" />, <paramref name="salt" />,
    ///     <paramref name="info" />) into <paramref name="output" />. False if the peer key is unusable.
    /// </summary>
    internal bool TryDeriveKey(string peerPublicKey, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, Span<byte> output)
    {
        if (!TryPrepare(peerPublicKey)) return false;

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // evicted in the meantime (cache full): prepared again on the next call
            if (!_sharedSecrets.TryGetValue(peerPublicKey, out var node)) return false;

            MarkUsed(node);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, node.Value.Secret, output, salt, info);
            return true;
        }
    }

    /// <summary>Number of peers whose ECDH secret is kept (tests).</summary>
    internal int CachedPeerCount
    {
        get
        {
            lock (_lock)
            {
                return _sharedSecrets.Count;
            }
        }
    }

    /// <summary>True if the ECDH secret with <paramref name="peerPublicKey" /> is kept (tests).</summary>
    internal bool HasCachedPeer(string peerPublicKey)
    {
        lock (_lock)
        {
            return _sharedSecrets.ContainsKey(peerPublicKey ?? "");
        }
    }

    private void MarkUsed(LinkedListNode<SharedSecret> node)
    {
        if (node.List == null || ReferenceEquals(_recency.First, node)) return;

        _recency.Remove(node);
        _recency.AddFirst(node);
    }

    private void EvictLeastRecentlyUsed()
    {
        var oldest = _recency.Last;
        if (oldest == null) return;

        _recency.RemoveLast();
        _sharedSecrets.Remove(oldest.Value.PeerPublicKey);
        CryptographicOperations.ZeroMemory(oldest.Value.Secret);
    }

    private void ClearSharedSecrets()
    {
        foreach (var node in _recency) CryptographicOperations.ZeroMemory(node.Secret);
        _recency.Clear();
        _sharedSecrets.Clear();
    }

    private static bool HasP256Layout(ReadOnlySpan<byte> spki)
    {
        return spki.Length == PublicKeyLength && spki.StartsWith(SpkiPrefix);
    }

    private sealed record SharedSecret(string PeerPublicKey, byte[] Secret);
}
