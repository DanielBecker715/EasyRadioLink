using System;
using System.Collections.Generic;
using System.Net;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     CPU protection of the UDP receive loop against forged datagrams: how many datagrams per second may fail
///     authentication (AES-GCM tag) before the server stops even trying to decrypt more of them. The budget is kept per
///     <b>source endpoint</b> (IP address and port), per source IP address and in total - never per client id, so forged
///     datagrams in a victim's name (e.g. from another host behind the same NAT, which passes the IP address check) use
///     up only the forger's own budget.
///     <para>
///         The router does not consult the budget for datagrams from a client's last authenticated endpoint
///         (<c>ClientInfo.VoipPort</c>): they are always decrypted, so nobody can mute a client by flooding. The budget
///         only covers datagrams from other endpoints (a client's very first datagram, a NAT rebinding - and forgeries).
///     </para>
///     Fixed one-second windows; the tables hold only endpoints that failed in the current second (at most
///     <see cref="MaxFailuresTotal" /> entries). Not thread-safe: used by the single UDP receive loop.
/// </summary>
internal sealed class UdpAuthFailureBudget
{
    /// <summary>Failed authentications per second and source endpoint (IP address and port).</summary>
    public const int MaxFailuresPerEndpoint = 50;

    /// <summary>Failed authentications per second and source IP address (all its ports together).</summary>
    public const int MaxFailuresPerAddress = 250;

    /// <summary>Failed authentications per second in total (a few milliseconds of CPU at most).</summary>
    public const int MaxFailuresTotal = 5000;

    private readonly Dictionary<IPAddress, int> _perAddress = new();
    private readonly Dictionary<IPEndPoint, int> _perEndpoint = new();
    private int _total;
    private bool _windowStarted;
    private long _windowStartTicks;

    /// <summary>Failed authentications counted in the current second (tests).</summary>
    internal int FailuresInWindow => _total;

    /// <summary>
    ///     False if a datagram from <paramref name="source" /> must be dropped without decrypting it: the endpoint, its
    ///     IP address or the server as a whole used up its budget of failed authentications in the current second.
    /// </summary>
    public bool AllowAttempt(IPEndPoint source, long nowTicks)
    {
        if (source?.Address == null) return false;

        StartWindowIfDue(nowTicks);

        if (_total >= MaxFailuresTotal) return false;
        if (_perAddress.TryGetValue(source.Address, out var fromAddress) && fromAddress >= MaxFailuresPerAddress)
            return false;

        return !_perEndpoint.TryGetValue(source, out var fromEndpoint) || fromEndpoint < MaxFailuresPerEndpoint;
    }

    /// <summary>Counts a datagram from <paramref name="source" /> whose authentication failed.</summary>
    public void RecordFailure(IPEndPoint source, long nowTicks)
    {
        if (source?.Address == null) return;

        StartWindowIfDue(nowTicks);

        _total++;
        _perAddress[source.Address] = _perAddress.GetValueOrDefault(source.Address) + 1;
        _perEndpoint[source] = _perEndpoint.GetValueOrDefault(source) + 1;
    }

    private void StartWindowIfDue(long nowTicks)
    {
        // a new second (or the clock went back)
        if (_windowStarted && nowTicks >= _windowStartTicks && nowTicks - _windowStartTicks < TimeSpan.TicksPerSecond)
            return;

        _windowStarted = true;
        _windowStartTicks = nowTicks;
        _total = 0;
        if (_perEndpoint.Count > 0) _perEndpoint.Clear();
        if (_perAddress.Count > 0) _perAddress.Clear();
    }
}
