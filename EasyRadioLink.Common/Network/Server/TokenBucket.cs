using System;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     A token bucket: up to <see cref="Burst" /> at once, refilled with <see cref="PerSecond" /> tokens per second.
///     Starts full. Not thread-safe (the owner serialises the calls).
/// </summary>
public sealed class TokenBucket
{
    private DateTime _lastRefillUtc;
    private bool _started;
    private double _tokens;

    public TokenBucket(int burst, double perSecond)
    {
        if (burst < 1) throw new ArgumentOutOfRangeException(nameof(burst));
        if (perSecond <= 0) throw new ArgumentOutOfRangeException(nameof(perSecond));

        Burst = burst;
        PerSecond = perSecond;
        _tokens = burst;
    }

    public int Burst { get; }
    public double PerSecond { get; }

    /// <summary>Takes one token; false if the bucket is empty.</summary>
    public bool TryTake(DateTime nowUtc)
    {
        if (!_started || nowUtc < _lastRefillUtc)
        {
            // first use, or the clock went back: no refill for the time in between
            _started = true;
        }
        else
        {
            _tokens = Math.Min(Burst, _tokens + (nowUtc - _lastRefillUtc).TotalSeconds * PerSecond);
        }

        _lastRefillUtc = nowUtc;

        if (_tokens < 1) return false;

        _tokens -= 1;
        return true;
    }
}
