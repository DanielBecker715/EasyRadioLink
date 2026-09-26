using System;
using System.Collections.Generic;
using System.Threading;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>Why a connection was refused or closed before (or instead of) the login.</summary>
public enum RefusedConnectionReason
{
    /// <summary>The address is in banned.txt.</summary>
    Banned,

    /// <summary>Too many wrong passwords from the address.</summary>
    LockedOut,

    /// <summary>Too many connections or unfinished handshakes from the address.</summary>
    ConnectionLimit,

    /// <summary>The server has the maximum number of connections.</summary>
    ServerFull,

    /// <summary>Neither TLS nor an EasyRadioLink 1.0 client (scanners, other protocols).</summary>
    NotTls,

    /// <summary>An unencrypted EasyRadioLink 1.0 client (answered VERSION_MISMATCH).</summary>
    Version10Client,

    /// <summary>TLS handshake and login not finished within the handshake timeout.</summary>
    HandshakeTimeout
}

/// <summary>
///     Refused and unfinished connections are logged at debug level each - a connection flood must not flood the log -
///     and counted here; <see cref="TakeSummary" /> gives one line with the counts per <see cref="Interval" />
///     (called from the server's housekeeping timer). Thread-safe.
/// </summary>
public sealed class ConnectionLogSummary
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private static readonly RefusedConnectionReason[] Reasons = Enum.GetValues<RefusedConnectionReason>();

    private readonly int[] _counts = new int[Reasons.Length];
    private readonly object _lock = new();
    private DateTime _periodStartUtc;

    public ConnectionLogSummary(DateTime nowUtc)
    {
        _periodStartUtc = nowUtc;
    }

    public void Record(RefusedConnectionReason reason)
    {
        Interlocked.Increment(ref _counts[(int)reason]);
    }

    /// <summary>
    ///     The summary of the connections counted since the last one, once <see cref="Interval" /> has passed (or
    ///     <paramref name="force" />); null if it is not due or nothing was counted. <paramref name="important" /> is
    ///     true if the server was full (worth a warning).
    /// </summary>
    public string TakeSummary(DateTime nowUtc, out bool important, bool force = false)
    {
        important = false;

        lock (_lock)
        {
            if (!force && nowUtc - _periodStartUtc < Interval && nowUtc >= _periodStartUtc) return null;

            var counts = new int[Reasons.Length];
            var total = 0;
            for (var i = 0; i < counts.Length; i++)
            {
                counts[i] = Interlocked.Exchange(ref _counts[i], 0);
                total += counts[i];
            }

            var since = _periodStartUtc;
            _periodStartUtc = nowUtc;
            if (total == 0) return null;

            important = counts[(int)RefusedConnectionReason.ServerFull] > 0;

            var parts = new List<string>();
            void Add(RefusedConnectionReason reason, string text)
            {
                var count = counts[(int)reason];
                if (count > 0) parts.Add($"{count} {text}");
            }

            Add(RefusedConnectionReason.ServerFull, $"refused - server full ({ServerSync.MaxConnections} connections)");
            Add(RefusedConnectionReason.ConnectionLimit, "refused - too many connections or unfinished handshakes from one address");
            Add(RefusedConnectionReason.Banned, "refused - banned address");
            Add(RefusedConnectionReason.LockedOut, "refused - address locked out after wrong passwords");
            Add(RefusedConnectionReason.HandshakeTimeout,
                $"closed - no TLS handshake and login within {ServerSync.HandshakeTimeout.TotalSeconds:0} s");
            Add(RefusedConnectionReason.NotTls, "closed - not TLS (not an EasyRadioLink 1.1 client)");
            Add(RefusedConnectionReason.Version10Client, "closed - unencrypted EasyRadioLink 1.0 client (answered VERSION_MISMATCH)");

            return $"Connections since {since:u}: {string.Join("; ", parts)} (each one is logged at debug level)";
        }
    }
}
