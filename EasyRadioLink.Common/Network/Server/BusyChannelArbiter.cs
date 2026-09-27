using System;
using System.Diagnostics;
using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     Busy channel lockout ("one speaker per frequency", server setting <c>BUSY_CHANNEL_LOCKOUT</c>): the server
///     decides who talks. The first station that transmits on a channel - a frequency (within
///     <see cref="RadioBase.FreqCloseEnough" />) with a modulation - holds it; voice packets of every other station on
///     that channel are dropped (for all listeners) until the holder has been silent for <see cref="HangTimeMilliseconds" />.
///     Then the next station to transmit takes the channel over. Radio check (echo) frequencies are ordinary channels.
///     <list type="bullet">
///         <item>
///             One entry per sender (the channel of the first frequency of its last accepted packet), removed once the
///             holder has been silent for the hang time or has left - so the table never holds more than the active
///             senders and is capped at <see cref="MaxChannels" />.
///         </item>
///         <item>
///             Thread-safe (one lock, the voice packets are routed on thread-pool tasks); no allocations per packet
///             once the table has grown to the number of simultaneous senders.
///         </item>
///     </list>
///     The clock is passed in (<see cref="NowMilliseconds" />), so the decisions are pure and testable.
/// </summary>
public sealed class BusyChannelArbiter
{
    /// <summary>
    ///     The clock of the busy channel lockout, on the server and in the client: milliseconds of the high-resolution
    ///     monotonic clock (<see cref="Stopwatch" />). <see cref="Environment.TickCount64" /> moves in steps of ~15.6 ms
    ///     on Windows: the hang time would be anything from ~285 to ~315 ms, and a client could see the channel free a
    ///     tick before the server does (the first frame of a VOX transmission lost).
    /// </summary>
    public static long NowMilliseconds => (long)Stopwatch.GetElapsedTime(0).TotalMilliseconds;

    /// <summary>
    ///     A channel stays busy this long after the holder's last voice packet (a client sends one every 40 ms; this
    ///     bridges network jitter and short gaps). The clients use the same time for their busy check.
    /// </summary>
    public const int HangTimeMilliseconds = 300;

    /// <summary>Upper bound of the table (more than the server accepts connections).</summary>
    public const int MaxChannels = 1024;

    private readonly object _lock = new();

    // null = every holder counts; otherwise false for a holder that has left (its channel is free at once)
    private readonly Func<string, bool> _isHolderConnected;

    private Channel[] _channels = new Channel[8];
    private int _count;
    private long _drops;

    /// <param name="isHolderConnected">
    ///     Optional: false for a client id that is no longer connected. Only asked when a packet collides with that
    ///     holder's channel.
    /// </param>
    public BusyChannelArbiter(Func<string, bool> isHolderConnected = null)
    {
        _isHolderConnected = isHolderConnected;
    }

    /// <summary>Voice packets dropped so far because their channel was busy.</summary>
    public long Drops
    {
        get
        {
            lock (_lock)
            {
                return _drops;
            }
        }
    }

    /// <summary>Channels held right now (entries whose holder transmitted within the hang time).</summary>
    internal int ActiveChannels(long nowMilliseconds)
    {
        lock (_lock)
        {
            var active = 0;
            for (var i = 0; i < _count; i++)
                if (!IsExpired(_channels[i], nowMilliseconds))
                    active++;

            return active;
        }
    }

    /// <summary>Entries in the table (active or not yet pruned).</summary>
    internal int TableSize
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>
    ///     Decides whether a voice packet of <paramref name="senderGuid" /> on <paramref name="frequencies" /> /
    ///     <paramref name="modulations" /> may be forwarded. False (counted in <see cref="Drops" />) if one of its
    ///     frequencies is a channel another station holds; otherwise the sender takes (or keeps) the channel of its
    ///     first frequency.
    /// </summary>
    public bool Allow(string senderGuid, double[] frequencies, byte[] modulations, long nowMilliseconds)
    {
        if (senderGuid == null || frequencies == null || modulations == null) return true;

        var frequencyCount = Math.Min(frequencies.Length, modulations.Length);
        if (frequencyCount == 0) return true;

        lock (_lock)
        {
            var own = -1;

            // backwards: RemoveAt moves the last entry (already visited) into the removed slot
            for (var i = _count - 1; i >= 0; i--)
            {
                ref var channel = ref _channels[i];

                if (string.Equals(channel.Holder, senderGuid, StringComparison.Ordinal))
                {
                    own = i;
                    continue;
                }

                if (IsExpired(channel, nowMilliseconds))
                {
                    own = RemoveAt(i, own);
                    continue;
                }

                if (!Collides(channel, frequencies, modulations, frequencyCount)) continue;

                // the holder left in the middle of its transmission: the channel is free
                if (_isHolderConnected != null && !_isHolderConnected(channel.Holder))
                {
                    own = RemoveAt(i, own);
                    continue;
                }

                _drops++;
                return false;
            }

            if (own >= 0)
            {
                ref var held = ref _channels[own];
                held.Frequency = frequencies[0];
                held.Modulation = modulations[0];
                held.LastMilliseconds = Math.Max(held.LastMilliseconds, nowMilliseconds);
                return true;
            }

            // full (can't happen with the server's connection limits): forwarded, but not held
            if (_count >= MaxChannels) return true;

            if (_count == _channels.Length)
                Array.Resize(ref _channels, Math.Min(_channels.Length * 2, MaxChannels));

            _channels[_count++] = new Channel
            {
                Holder = senderGuid,
                Frequency = frequencies[0],
                Modulation = modulations[0],
                LastMilliseconds = nowMilliseconds
            };

            return true;
        }
    }

    /// <summary>Forgets every channel (e.g. the setting was switched off).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_channels, 0, _count);
            _count = 0;
        }
    }

    private static bool IsExpired(in Channel channel, long nowMilliseconds)
    {
        // a packet routed a little out of order (older clock value) still counts as recent
        return nowMilliseconds - channel.LastMilliseconds >= HangTimeMilliseconds;
    }

    private static bool Collides(in Channel channel, double[] frequencies, byte[] modulations, int frequencyCount)
    {
        for (var f = 0; f < frequencyCount; f++)
            if (modulations[f] == channel.Modulation && RadioBase.FreqCloseEnough(frequencies[f], channel.Frequency))
                return true;

        return false;
    }

    /// <summary>Removes entry <paramref name="index" /> (the last entry takes its place); returns where <paramref name="own" /> is now.</summary>
    private int RemoveAt(int index, int own)
    {
        var last = _count - 1;
        _channels[index] = _channels[last];
        _channels[last] = default;
        _count = last;

        return own == last ? index : own;
    }

    private struct Channel
    {
        public string Holder;
        public double Frequency;
        public byte Modulation;
        public long LastMilliseconds;
    }
}
