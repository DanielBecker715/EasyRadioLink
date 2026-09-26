using System;
using System.Collections.Generic;
using System.Threading;

namespace EasyRadioLink.Common.Network.Crypto;

/// <summary>
///     Replay state of one received transmission (sender public key + TxId): the packet numbers already played
///     (<see cref="ReplayWindow" />) and when it was last active. Shared by every key cache that holds the key of this
///     transmission during the app run, so a reconnect does not start the replay window over. Thread-safe: callers hold
///     <see cref="Sync" /> around <see cref="Window" /> (check, decrypt, accept).
/// </summary>
public sealed class TransmissionReplayState
{
    private long _lastActivityTicks;

    internal TransmissionReplayState(DateTime nowUtc)
    {
        _lastActivityTicks = nowUtc.Ticks;
    }

    /// <summary>Lock for <see cref="Window" />.</summary>
    internal object Sync { get; } = new();

    /// <summary>Packet numbers already played. Only under <see cref="Sync" />.</summary>
    internal ReplayWindow Window { get; } = new();

    /// <summary>When a key of this transmission was first accepted or a frame of it was last played.</summary>
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    /// <summary>A frame was played (never moves backwards).</summary>
    internal void Touch(DateTime nowUtc)
    {
        var ticks = nowUtc.Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _lastActivityTicks);
            if (ticks <= current) return;
        } while (Interlocked.CompareExchange(ref _lastActivityTicks, ticks, current) != current);
    }
}

/// <summary>
///     Receiver side memory of the transmissions whose keys this client accepted, per sender public key, for the whole
///     app run (it belongs to the client's <see cref="E2EKeyPair" />, which lives as long as the app): the last
///     <see cref="MaxTransmissionsPerSender" /> transmissions of at most <see cref="MaxSenders" /> senders.
///     <para>
///         Without it a malicious server could record a transmission (its VOICE_KEY and voice packets) and play it to
///         a client again after a reconnect, when a fresh key cache would accept it. With it:
///     </para>
///     <list type="bullet">
///         <item>
///             frames already played are never played again - the packet number window of a transmission is kept
///             across connections (<see cref="TransmissionReplayState" />);
///         </item>
///         <item>
///             a key of a known transmission is accepted again only while the transmission is recent (activity within
///             <see cref="MaxIdleTime" />): that is the sender wrapping its running transmission key again for a
///             listener that reconnected or tuned in again. An older one is a replay and is refused.
///         </item>
///     </list>
///     Thread-safe.
/// </summary>
public sealed class TransmissionReplayGuard
{
    public const int MaxTransmissionsPerSender = 256;
    public const int MaxSenders = 256;

    /// <summary>A known transmission without activity for this long is over: its key is not accepted again.</summary>
    public static readonly TimeSpan MaxIdleTime = VoiceKeyCache.Lifetime;

    private readonly object _lock = new();
    private readonly Dictionary<string, SenderLog> _senders = new(StringComparer.Ordinal);

    /// <summary>Transmissions remembered (all senders, tests).</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                var count = 0;
                foreach (var sender in _senders.Values) count += sender.Transmissions.Count;
                return count;
            }
        }
    }

    /// <summary>
    ///     A key of transmission <paramref name="txId" /> from <paramref name="senderPublicKey" /> authenticated. Returns
    ///     the replay state its key cache entry must use, or null if the key must be refused: the transmission is known
    ///     and was not active for <see cref="MaxIdleTime" /> (an old transmission played again).
    /// </summary>
    public TransmissionReplayState Register(string senderPublicKey, ulong txId, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(senderPublicKey)) return null;

        lock (_lock)
        {
            if (_senders.TryGetValue(senderPublicKey, out var sender))
            {
                if (sender.Transmissions.TryGetValue(txId, out var known))
                    return nowUtc - known.LastActivityUtc > MaxIdleTime ? null : known;
            }
            else
            {
                if (_senders.Count >= MaxSenders) EvictLeastRecentlyActiveSender();

                sender = new SenderLog();
                _senders[senderPublicKey] = sender;
            }

            if (sender.Transmissions.Count >= MaxTransmissionsPerSender) sender.EvictLeastRecentlyActive();

            var state = new TransmissionReplayState(nowUtc);
            sender.Transmissions[txId] = state;
            return state;
        }
    }

    private void EvictLeastRecentlyActiveSender()
    {
        string oldest = null;
        var oldestActivity = DateTime.MaxValue;
        foreach (var (publicKey, sender) in _senders)
        {
            var activity = sender.LastActivityUtc;
            if (activity < oldestActivity)
            {
                oldestActivity = activity;
                oldest = publicKey;
            }
        }

        if (oldest != null) _senders.Remove(oldest);
    }

    private sealed class SenderLog
    {
        public Dictionary<ulong, TransmissionReplayState> Transmissions { get; } = new();

        public DateTime LastActivityUtc
        {
            get
            {
                var latest = DateTime.MinValue;
                foreach (var state in Transmissions.Values)
                    if (state.LastActivityUtc > latest)
                        latest = state.LastActivityUtc;

                return latest;
            }
        }

        public void EvictLeastRecentlyActive()
        {
            ulong oldestTxId = 0;
            var oldestActivity = DateTime.MaxValue;
            var found = false;
            foreach (var (txId, state) in Transmissions)
                if (!found || state.LastActivityUtc < oldestActivity)
                {
                    found = true;
                    oldestActivity = state.LastActivityUtc;
                    oldestTxId = txId;
                }

            if (found) Transmissions.Remove(oldestTxId);
        }
    }
}
