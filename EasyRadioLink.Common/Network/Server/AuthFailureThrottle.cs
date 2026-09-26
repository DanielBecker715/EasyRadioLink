using System;
using System.Collections.Concurrent;
using System.Net;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     Slows down password guessing: after <see cref="MaxFailures" /> wrong passwords from one IP address within
///     <see cref="FailureWindow" />, connections from that address are refused for <see cref="LockoutDuration" />.
///     A correct password resets the counter of the address.
/// </summary>
public class AuthFailureThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    // entries are pruned once the table gets this big, so a flood of addresses can't grow it forever
    private const int PruneThreshold = 1000;

    private readonly ConcurrentDictionary<IPAddress, Entry> _entries = new();

    /// <summary>True if connections from <paramref name="address" /> are currently refused.</summary>
    public bool IsLockedOut(IPAddress address, DateTime nowUtc)
    {
        if (address == null || !_entries.TryGetValue(Normalise(address), out var entry)) return false;

        lock (entry)
        {
            return entry.LockedUntilUtc > nowUtc;
        }
    }

    /// <summary>Counts a wrong password from <paramref name="address" />. Returns true if the address is now locked out.</summary>
    public bool RecordFailure(IPAddress address, DateTime nowUtc)
    {
        if (address == null) return false;

        if (_entries.Count > PruneThreshold) Prune(nowUtc);

        var entry = _entries.GetOrAdd(Normalise(address), _ => new Entry());
        lock (entry)
        {
            if (nowUtc - entry.FirstFailureUtc > FailureWindow)
            {
                entry.FirstFailureUtc = nowUtc;
                entry.Failures = 0;
            }

            entry.Failures++;
            if (entry.Failures < MaxFailures) return false;

            entry.LockedUntilUtc = nowUtc + LockoutDuration;
            entry.Failures = 0;
            entry.FirstFailureUtc = nowUtc;
            return true;
        }
    }

    /// <summary>A correct password: forget the failures of <paramref name="address" />.</summary>
    public void RecordSuccess(IPAddress address)
    {
        if (address != null) _entries.TryRemove(Normalise(address), out _);
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var pair in _entries)
            lock (pair.Value)
            {
                if (pair.Value.LockedUntilUtc <= nowUtc && nowUtc - pair.Value.FirstFailureUtc > FailureWindow)
                    _entries.TryRemove(pair.Key, out _);
            }
    }

    private static IPAddress Normalise(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private sealed class Entry
    {
        public int Failures;
        public DateTime FirstFailureUtc;
        public DateTime LockedUntilUtc;
    }
}
