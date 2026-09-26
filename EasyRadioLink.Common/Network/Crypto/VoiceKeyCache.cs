using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace EasyRadioLink.Common.Network.Crypto;

public enum VoiceDecryptResult
{
    Ok,

    /// <summary>No key (yet) for this sender and transmission.</summary>
    NoKey,

    /// <summary>Packet number already played or too old for the replay window.</summary>
    Replayed,

    /// <summary>The tag did not verify: wrong key, other sender/frequency/modulation (AAD) or tampered.</summary>
    AuthenticationFailed,

    /// <summary>The audio segment is too short or too long.</summary>
    Malformed
}

/// <summary>
///     Receiver side transmission keys, per (sender, TxId). A key stays for <see cref="Lifetime" /> after it was last
///     used; the cache holds at most <see cref="MaxKeysPerSender" /> keys per sender and <see cref="MaxSenders" />
///     senders (the least recently used ones go first). Each key has a replay window over the packet numbers
///     (<see cref="TransmissionReplayState" />; for keys from VOICE_KEY messages the one of the app run's
///     <see cref="TransmissionReplayGuard" />, so frames already played stay refused after a reconnect).
///     Keys never leave the cache: they live in <see cref="AesGcm" /> instances that are disposed when the key is
///     removed. Thread-safe.
/// </summary>
public sealed class VoiceKeyCache : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaxKeysPerSender = 64;
    public const int MaxSenders = 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, SenderKeys> _senders = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Number of cached keys (all senders).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                var count = 0;
                foreach (var sender in _senders.Values) count += sender.Keys.Count;
                return count;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            ClearLocked();
        }
    }

    /// <summary>
    ///     Adds the key of a transmission. False if it is invalid, the cache is disposed or the transmission already has
    ///     a key (the first key of a transmission stays).
    /// </summary>
    /// <param name="replayState">
    ///     The replay state of this transmission from <see cref="TransmissionReplayGuard.Register" />; null = a new one
    ///     for this cache only (the sender's own transmission).
    /// </param>
    public bool TryAdd(string senderGuid, ulong txId, ReadOnlySpan<byte> key, DateTime nowUtc,
        TransmissionReplayState replayState = null)
    {
        if (senderGuid == null || key.Length != E2EVoiceCrypto.TransmissionKeyLength) return false;

        lock (_lock)
        {
            if (_disposed) return false;

            if (_senders.TryGetValue(senderGuid, out var sender))
            {
                sender.RemoveExpired(nowUtc);
            }
            else
            {
                if (_senders.Count >= MaxSenders) MakeRoomForSender(nowUtc);

                sender = new SenderKeys();
                _senders[senderGuid] = sender;
            }

            if (sender.Keys.ContainsKey(txId)) return false;

            if (sender.Keys.Count >= MaxKeysPerSender) sender.RemoveLeastRecentlyUsed();

            sender.Keys[txId] = new Entry(new AesGcm(key, E2EVoiceCrypto.TagLength), nowUtc,
                replayState ?? new TransmissionReplayState(nowUtc));
            sender.LastUsedUtc = nowUtc;
            return true;
        }
    }

    /// <summary>True if a (not expired) key of this transmission is cached.</summary>
    public bool Contains(string senderGuid, ulong txId, DateTime nowUtc)
    {
        lock (_lock)
        {
            return TryGetEntry(senderGuid, txId, nowUtc, out _, out _);
        }
    }

    /// <summary>
    ///     Checks the replay window, then decrypts an audio segment (<see cref="E2EVoiceCrypto" /> layout). Only a
    ///     successful decryption moves the replay window and keeps the key alive.
    /// </summary>
    public VoiceDecryptResult TryDecrypt(string senderGuid, ulong txId, ulong packetNumber, ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> segment, DateTime nowUtc, out byte[] opus)
    {
        opus = null;
        if (!E2EVoiceCrypto.HasValidSegmentLength(segment.Length)) return VoiceDecryptResult.Malformed;

        lock (_lock)
        {
            if (!TryGetEntry(senderGuid, txId, nowUtc, out var sender, out var entry)) return VoiceDecryptResult.NoKey;

            // check, decrypt and accept in one step: the replay state may be shared with the cache of an earlier
            // connection
            var replay = entry.Replay;
            lock (replay.Sync)
            {
                if (!replay.Window.IsAcceptable(packetNumber)) return VoiceDecryptResult.Replayed;

                if (!E2EVoiceCrypto.TryDecryptFrame(entry.Cipher, segment, packetNumber, aad, out opus))
                    return VoiceDecryptResult.AuthenticationFailed;

                replay.Window.Accept(packetNumber);
            }

            replay.Touch(nowUtc);
            entry.LastUsedUtc = nowUtc;
            sender.LastUsedUtc = nowUtc;
            return VoiceDecryptResult.Ok;
        }
    }

    /// <summary>Forgets every key of a sender (e.g. it disconnected or changed its public key).</summary>
    public void RemoveSender(string senderGuid)
    {
        if (senderGuid == null) return;

        lock (_lock)
        {
            if (_senders.Remove(senderGuid, out var sender)) sender.Dispose();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            ClearLocked();
        }
    }

    private void ClearLocked()
    {
        foreach (var sender in _senders.Values) sender.Dispose();
        _senders.Clear();
    }

    private bool TryGetEntry(string senderGuid, ulong txId, DateTime nowUtc, out SenderKeys sender, out Entry entry)
    {
        entry = null;
        sender = null;
        if (_disposed || senderGuid == null || !_senders.TryGetValue(senderGuid, out sender) ||
            !sender.Keys.TryGetValue(txId, out entry))
            return false;

        if (!entry.IsExpired(nowUtc)) return true;

        sender.Keys.Remove(txId);
        entry.Dispose();
        entry = null;
        return false;
    }

    private void MakeRoomForSender(DateTime nowUtc)
    {
        var empty = new List<string>();
        string oldest = null;
        var oldestUsed = DateTime.MaxValue;

        foreach (var (guid, sender) in _senders)
        {
            sender.RemoveExpired(nowUtc);
            if (sender.Keys.Count == 0)
            {
                empty.Add(guid);
            }
            else if (sender.LastUsedUtc < oldestUsed)
            {
                oldestUsed = sender.LastUsedUtc;
                oldest = guid;
            }
        }

        foreach (var guid in empty)
            if (_senders.Remove(guid, out var sender))
                sender.Dispose();

        if (_senders.Count >= MaxSenders && oldest != null && _senders.Remove(oldest, out var evicted))
            evicted.Dispose();
    }

    private sealed class Entry : IDisposable
    {
        public Entry(AesGcm cipher, DateTime nowUtc, TransmissionReplayState replay)
        {
            Cipher = cipher;
            LastUsedUtc = nowUtc;
            Replay = replay;
        }

        public AesGcm Cipher { get; }
        public TransmissionReplayState Replay { get; }
        public DateTime LastUsedUtc { get; set; }

        public void Dispose()
        {
            Cipher.Dispose();
        }

        public bool IsExpired(DateTime nowUtc)
        {
            return nowUtc - LastUsedUtc >= Lifetime;
        }
    }

    private sealed class SenderKeys : IDisposable
    {
        public Dictionary<ulong, Entry> Keys { get; } = new();
        public DateTime LastUsedUtc { get; set; }

        public void Dispose()
        {
            foreach (var entry in Keys.Values) entry.Dispose();
            Keys.Clear();
        }

        public void RemoveExpired(DateTime nowUtc)
        {
            List<ulong> expired = null;
            foreach (var (txId, entry) in Keys)
                if (entry.IsExpired(nowUtc))
                    (expired ??= new List<ulong>()).Add(txId);

            if (expired == null) return;

            foreach (var txId in expired)
                if (Keys.Remove(txId, out var entry))
                    entry.Dispose();
        }

        public void RemoveLeastRecentlyUsed()
        {
            ulong oldestTxId = 0;
            Entry oldest = null;
            foreach (var (txId, entry) in Keys)
                if (oldest == null || entry.LastUsedUtc < oldest.LastUsedUtc)
                {
                    oldest = entry;
                    oldestTxId = txId;
                }

            if (oldest == null) return;

            Keys.Remove(oldestTxId);
            oldest.Dispose();
        }
    }
}
