using System;
using System.Collections.Concurrent;
using System.Net;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     Limits how fast one IP address may open new connections (token bucket: a burst of <see cref="Burst" />, then one
///     per <see cref="RefillInterval" />). Refused connections are closed before the TLS handshake, so a connection
///     flood from one address costs at most one handshake per <see cref="RefillInterval" /> once the burst is used up
///     (the limits on concurrent and unfinished connections apply on top).
/// </summary>
public class ConnectionRateLimiter
{
    /// <summary>New connections one address may open at once - enough for everybody behind one NAT at a LAN party.</summary>
    public const int Burst = ServerSync.MaxConnectionsPerAddress;

    public static readonly TimeSpan RefillInterval = TimeSpan.FromSeconds(1);

    // a refused address is logged at most once per this interval
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

    // idle (full) buckets are pruned once the table gets this big; far beyond it the table is reset
    private const int PruneThreshold = 4096;

    private readonly ConcurrentDictionary<IPAddress, Bucket> _buckets = new();

    /// <summary>Takes a token for a new connection from <paramref name="address" />.</summary>
    /// <param name="logRefusal">True for the first refusal of an address in a while (log it, but not every one).</param>
    /// <returns>false if the connection must be refused</returns>
    public bool TryAcquire(IPAddress address, DateTime nowUtc, out bool logRefusal)
    {
        logRefusal = false;
        if (address == null) return false;

        if (_buckets.Count > PruneThreshold) Prune(nowUtc);

        var bucket = _buckets.GetOrAdd(Normalise(address), _ => new Bucket(nowUtc));
        lock (bucket)
        {
            bucket.Refill(nowUtc);

            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                return true;
            }

            if (nowUtc - bucket.LastLoggedUtc >= LogInterval)
            {
                bucket.LastLoggedUtc = nowUtc;
                logRefusal = true;
            }

            return false;
        }
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var pair in _buckets)
            lock (pair.Value)
            {
                pair.Value.Refill(nowUtc);
                if (pair.Value.Tokens >= Burst) _buckets.TryRemove(pair.Key, out _);
            }

        // many busy addresses at once (a distributed flood): bound the memory, the limits start over
        if (_buckets.Count > PruneThreshold * 4) _buckets.Clear();
    }

    private static IPAddress Normalise(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private sealed class Bucket
    {
        public DateTime LastLoggedUtc = DateTime.MinValue;
        public DateTime LastRefillUtc;
        public double Tokens = Burst;

        public Bucket(DateTime nowUtc)
        {
            LastRefillUtc = nowUtc;
        }

        public void Refill(DateTime nowUtc)
        {
            if (nowUtc <= LastRefillUtc) return;

            Tokens = Math.Min(Burst, Tokens + (nowUtc - LastRefillUtc) / RefillInterval);
            LastRefillUtc = nowUtc;
        }
    }
}
