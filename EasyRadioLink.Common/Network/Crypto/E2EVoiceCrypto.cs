using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EasyRadioLink.Common.Models;

namespace EasyRadioLink.Common.Network.Crypto;

/// <summary>
///     End-to-end voice encryption (protocol 1.1). Every transmission (PTT press on one frequency) gets a random
///     <see cref="TransmissionKeyLength" /> byte key K and a random <see cref="TxIdLength" /> byte id. Only listeners
///     that can hear the frequency receive K, wrapped for each of them:
///     <code>
///     shared  = ECDH(sender private key, recipient public key)                  (P-256, raw x coordinate)
///     wrapKey = HKDF-SHA256(ikm = shared, salt = TxId,
///                           info = "EasyRadioLink E2E v1" | sender id | recipient id)   (ids: 22 ASCII bytes)
///     wrapped = AES-256-GCM(wrapKey, nonce = 12 zero bytes, K) = 32 B ciphertext | 16 B tag
///     </code>
///     A zero nonce is safe: every wrap key is used for exactly one plaintext (K of that TxId).
///     <para>
///         The audio segment of a voice packet (<see cref="UDPVoicePacket.AudioPart1Bytes" />) is
///         <c>TxId (8 B) | AES-256-GCM(K, nonce, AAD, Opus frame) | tag (16 B)</c> with
///         nonce = 4 zero bytes | packet number (u64 big-endian) and
///         AAD = sender id (22 ASCII bytes) | per frequency: frequency (f64 little-endian, as on the wire) | modulation
///         byte. The server routes by the clear frequency segment but can neither read the audio nor move it to another
///         frequency or sender without breaking the tag.
///     </para>
/// </summary>
public static class E2EVoiceCrypto
{
    public const int TransmissionKeyLength = 32;
    public const int TxIdLength = 8;
    public const int TagLength = 16;
    public const int NonceLength = 12;

    /// <summary>Wrapped K: ciphertext + tag.</summary>
    public const int WrappedKeyLength = TransmissionKeyLength + TagLength;

    /// <summary>Bytes the E2E layer adds to an Opus frame (TxId + tag).</summary>
    public const int AudioOverhead = TxIdLength + TagLength;

    /// <summary>Largest Opus frame that is encrypted (a 40 ms voice frame is a few hundred bytes at most).</summary>
    public const int MaxOpusFrameLength = 3000;

    private const int GuidLength = 22;

    private static readonly byte[] WrapInfoLabel = "EasyRadioLink E2E v1"u8.ToArray();

    // --- transmission ids -------------------------------------------------------------------------------------------

    public static byte[] NewTxId()
    {
        return RandomNumberGenerator.GetBytes(TxIdLength);
    }

    public static ulong TxIdToUInt64(ReadOnlySpan<byte> txId)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(txId);
    }

    /// <summary>Parses the base64 TxId of a VOICE_KEY message: exactly <see cref="TxIdLength" /> bytes.</summary>
    public static bool TryParseTxId(string base64, out ulong txId)
    {
        txId = 0;
        if (string.IsNullOrEmpty(base64) || base64.Length > 16) return false;

        Span<byte> buffer = stackalloc byte[12];
        if (!Convert.TryFromBase64String(base64, buffer, out var written) || written != TxIdLength) return false;

        txId = TxIdToUInt64(buffer);
        return true;
    }

    /// <summary>Base64 of exactly <see cref="WrappedKeyLength" /> bytes.</summary>
    public static bool IsValidWrappedKey(string base64)
    {
        return TryDecodeWrappedKey(base64, out _);
    }

    private static bool TryDecodeWrappedKey(string base64, out byte[] wrapped)
    {
        wrapped = null;
        if (string.IsNullOrEmpty(base64) || base64.Length > 64) return false;

        var buffer = new byte[WrappedKeyLength + 3];
        if (!Convert.TryFromBase64String(base64, buffer, out var written) || written != WrappedKeyLength) return false;

        wrapped = buffer.AsSpan(0, WrappedKeyLength).ToArray();
        return true;
    }

    // --- key wrapping -----------------------------------------------------------------------------------------------

    /// <summary>
    ///     Wraps <paramref name="transmissionKey" /> for one recipient (see the class description).
    /// </summary>
    /// <returns>
    ///     The <see cref="WrappedKeyLength" /> byte wrapped key, or null if <paramref name="recipientPublicKey" /> can't
    ///     be used (bad format, not on the curve).
    /// </returns>
    public static byte[] WrapKey(E2EKeyPair sender, string senderGuid, string recipientPublicKey, string recipientGuid,
        ReadOnlySpan<byte> txId, ReadOnlySpan<byte> transmissionKey)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (txId.Length != TxIdLength) throw new ArgumentException("Invalid transmission id", nameof(txId));
        if (transmissionKey.Length != TransmissionKeyLength)
            throw new ArgumentException("Invalid transmission key", nameof(transmissionKey));

        Span<byte> wrapKey = stackalloc byte[TransmissionKeyLength];
        try
        {
            if (!TryDeriveWrapKey(sender, recipientPublicKey, txId, senderGuid, recipientGuid, wrapKey)) return null;

            var wrapped = new byte[WrappedKeyLength];
            using var aes = new AesGcm(wrapKey, TagLength);
            Span<byte> nonce = stackalloc byte[NonceLength];
            nonce.Clear();
            aes.Encrypt(nonce, transmissionKey, wrapped.AsSpan(0, TransmissionKeyLength),
                wrapped.AsSpan(TransmissionKeyLength, TagLength));
            return wrapped;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    /// <summary>
    ///     Unwraps a transmission key addressed to <paramref name="recipient" />. False (nothing returned) if the blob
    ///     was not made by the holder of <paramref name="senderPublicKey" /> for exactly this sender, recipient and
    ///     TxId, was changed on the way, or the sender's key can't be used.
    /// </summary>
    public static bool TryUnwrapKey(E2EKeyPair recipient, string recipientGuid, string senderPublicKey,
        string senderGuid, ReadOnlySpan<byte> txId, ReadOnlySpan<byte> wrapped, out byte[] transmissionKey)
    {
        transmissionKey = null;
        if (recipient == null || txId.Length != TxIdLength || wrapped.Length != WrappedKeyLength ||
            !IsAsciiId(senderGuid) || !IsAsciiId(recipientGuid))
            return false;

        Span<byte> wrapKey = stackalloc byte[TransmissionKeyLength];
        var key = new byte[TransmissionKeyLength];
        try
        {
            if (!TryDeriveWrapKey(recipient, senderPublicKey, txId, senderGuid, recipientGuid, wrapKey)) return false;

            using var aes = new AesGcm(wrapKey, TagLength);
            Span<byte> nonce = stackalloc byte[NonceLength];
            nonce.Clear();
            aes.Decrypt(nonce, wrapped[..TransmissionKeyLength], wrapped.Slice(TransmissionKeyLength, TagLength), key);

            transmissionKey = key;
            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    /// <summary>Base64 variant of <see cref="TryUnwrapKey(E2EKeyPair,string,string,string,ReadOnlySpan{byte},ReadOnlySpan{byte},out byte[])" />.</summary>
    public static bool TryUnwrapKey(E2EKeyPair recipient, string recipientGuid, string senderPublicKey,
        string senderGuid, ReadOnlySpan<byte> txId, string wrappedBase64, out byte[] transmissionKey)
    {
        transmissionKey = null;
        return TryDecodeWrappedKey(wrappedBase64, out var wrapped) &&
               TryUnwrapKey(recipient, recipientGuid, senderPublicKey, senderGuid, txId, wrapped, out transmissionKey);
    }

    /// <summary>
    ///     wrapKey = HKDF-SHA256(ECDH(own key, peer key), salt = TxId, info = label | sender id | recipient id). The
    ///     same on both sides: the sender uses its private key with the recipient's public key and vice versa.
    /// </summary>
    private static bool TryDeriveWrapKey(E2EKeyPair own, string peerPublicKey, ReadOnlySpan<byte> txId,
        string senderGuid, string recipientGuid, Span<byte> wrapKey)
    {
        if (!IsAsciiId(senderGuid)) throw new ArgumentException("Invalid sender id", nameof(senderGuid));
        if (!IsAsciiId(recipientGuid)) throw new ArgumentException("Invalid recipient id", nameof(recipientGuid));

        Span<byte> info = stackalloc byte[WrapInfoLabel.Length + 2 * GuidLength];
        WrapInfoLabel.CopyTo(info);
        Encoding.ASCII.GetBytes(senderGuid, info.Slice(WrapInfoLabel.Length, GuidLength));
        Encoding.ASCII.GetBytes(recipientGuid, info.Slice(WrapInfoLabel.Length + GuidLength, GuidLength));

        return own.TryDeriveKey(peerPublicKey, txId, info, wrapKey);
    }

    private static bool IsAsciiId(string id)
    {
        return UdpTransportSession.IsValidClientGuid(id);
    }

    // --- voice frames -----------------------------------------------------------------------------------------------

    /// <summary>
    ///     Additional authenticated data of a voice frame: sender id | per frequency (f64 little-endian | modulation).
    ///     Built from exactly the values that travel in the packet's clear segments.
    /// </summary>
    public static byte[] BuildAad(ReadOnlySpan<byte> senderGuidAscii, double[] frequencies, byte[] modulations)
    {
        if (senderGuidAscii.Length != GuidLength) throw new ArgumentException("Invalid sender id", nameof(senderGuidAscii));
        if (frequencies == null || modulations == null || modulations.Length < frequencies.Length)
            throw new ArgumentException("One modulation per frequency is required");

        var aad = new byte[GuidLength + frequencies.Length * (sizeof(double) + 1)];
        senderGuidAscii.CopyTo(aad);

        var offset = GuidLength;
        for (var i = 0; i < frequencies.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(aad.AsSpan(offset), frequencies[i]);
            aad[offset + sizeof(double)] = modulations[i];
            offset += sizeof(double) + 1;
        }

        return aad;
    }

    /// <summary>The AAD of a received (decoded) packet: its sender id, frequencies and modulations.</summary>
    public static byte[] BuildAad(UDPVoicePacket packet)
    {
        return BuildAad(packet.GuidBytes, packet.Frequencies, packet.Modulations);
    }

    private static void WriteFrameNonce(Span<byte> nonce, ulong packetNumber)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], packetNumber);
    }

    /// <summary>
    ///     Encrypts one Opus frame into an audio segment <c>TxId | ciphertext | tag</c>. <paramref name="cipher" /> must
    ///     hold the transmission key; the caller serialises access to it.
    /// </summary>
    internal static byte[] EncryptFrame(AesGcm cipher, ReadOnlySpan<byte> txId, ulong packetNumber,
        ReadOnlySpan<byte> aad, ReadOnlySpan<byte> opus)
    {
        if (opus.IsEmpty || opus.Length > MaxOpusFrameLength)
            throw new ArgumentException("Invalid Opus frame length", nameof(opus));

        var segment = new byte[AudioOverhead + opus.Length];
        txId.CopyTo(segment);

        Span<byte> nonce = stackalloc byte[NonceLength];
        WriteFrameNonce(nonce, packetNumber);

        cipher.Encrypt(nonce, opus, segment.AsSpan(TxIdLength, opus.Length),
            segment.AsSpan(TxIdLength + opus.Length, TagLength), aad);

        return segment;
    }

    /// <summary>Decrypts an audio segment; false if the tag does not verify (wrong key, other AAD or tampered).</summary>
    internal static bool TryDecryptFrame(AesGcm cipher, ReadOnlySpan<byte> segment, ulong packetNumber,
        ReadOnlySpan<byte> aad, out byte[] opus)
    {
        opus = null;
        if (!HasValidSegmentLength(segment.Length)) return false;

        var length = segment.Length - AudioOverhead;
        var plaintext = new byte[length];

        Span<byte> nonce = stackalloc byte[NonceLength];
        WriteFrameNonce(nonce, packetNumber);

        try
        {
            cipher.Decrypt(nonce, segment.Slice(TxIdLength, length), segment.Slice(TxIdLength + length, TagLength),
                plaintext, aad);
        }
        catch (CryptographicException)
        {
            return false;
        }

        opus = plaintext;
        return true;
    }

    /// <summary>True if an audio segment of this length can hold TxId + a non-empty frame + tag.</summary>
    public static bool HasValidSegmentLength(int length)
    {
        return length > AudioOverhead && length <= AudioOverhead + MaxOpusFrameLength;
    }
}
