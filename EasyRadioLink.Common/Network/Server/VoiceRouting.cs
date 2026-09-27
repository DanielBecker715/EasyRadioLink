using System;
using System.Collections.Generic;
using System.Net;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     The server's voice routing decision (pure, no I/O):
///     <list type="bullet">
///         <item>a muted sender is dropped,</item>
///         <item>
///             while the busy channel lockout is on, a packet on a channel another station holds is dropped
///             (<see cref="PassesBusyChannelLockout" />, checked by the <c>UDPVoiceRouter</c> before routing),
///         </item>
///         <item>the sender gets its own transmission back only on a radio check (test) frequency,</item>
///         <item>every other client receives the packet if one of its radios <see cref="CanReceive" /> it.</item>
///     </list>
///     Clients without a known UDP endpoint are skipped. The encryption byte of the packet (always 0 since 1.1, set by
///     older clients) does not stop delivery: a listener on the right frequency still gets the packet.
///     The rate limits (<see cref="AllowVoicePacket" />, <see cref="UdpAuthFailureBudget" /> for datagrams that are not
///     from the client's authenticated endpoint, <see cref="IsFromAuthenticatedEndpoint" />) are applied by the
///     <c>UDPVoiceRouter</c> before routing.
/// </summary>
public static class VoiceRouting
{
    // the server does not track blocked (transmitting) radios, it forwards to all radios that can hear
    private static readonly List<int> NoBlockedRadios = new();

    /// <summary>True if one of <paramref name="receiver" />'s radios is tuned to one of the packet's frequencies.</summary>
    public static bool CanReceive(PlayerRadioInfoBase receiver, UDPVoicePacket packet)
    {
        if (receiver == null || packet?.Frequencies == null || packet.Modulations == null) return false;

        for (var i = 0; i < packet.Frequencies.Length; i++)
        {
            if (i >= packet.Modulations.Length) break;

            var encryption = packet.Encryptions != null && i < packet.Encryptions.Length ? packet.Encryptions[i] : (byte)0;

            if (receiver.CanHearTransmission(packet.Frequencies[i],
                    (Modulation)packet.Modulations[i],
                    encryption,
                    NoBlockedRadios,
                    out _,
                    out _) != null)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     True if one of <paramref name="receiver" />'s radios hears <paramref name="frequency" /> with
    ///     <paramref name="modulation" /> - the same rule as for a voice packet. Used for the end-to-end voice keys: the
    ///     sender wraps its key for these clients and the server forwards a key only to them.
    /// </summary>
    public static bool CanReceive(PlayerRadioInfoBase receiver, double frequency, Modulation modulation)
    {
        if (receiver == null || !double.IsFinite(frequency)) return false;

        return receiver.CanHearTransmission(frequency, modulation, 0, NoBlockedRadios, out _, out _) != null;
    }

    /// <summary>True if one of the packet's frequencies is a radio check (echo) frequency.</summary>
    public static bool IsOnTestFrequency(UDPVoicePacket packet, IReadOnlyList<double> testFrequencies)
    {
        if (packet?.Frequencies == null || testFrequencies == null) return false;

        foreach (var frequency in packet.Frequencies)
        foreach (var testFrequency in testFrequencies)
            if (RadioBase.FreqCloseEnough(testFrequency, frequency))
                return true;

        return false;
    }

    /// <summary>
    ///     True if a UDP datagram received from <paramref name="source" /> may act for <paramref name="client" />: it must
    ///     come from the IP address the client's TCP connection authenticated from
    ///     (<see cref="ClientInfo.SessionAddress" />). Knowing another user's client id is not enough to receive or send
    ///     voice as that user.
    /// </summary>
    public static bool IsFromClientAddress(ClientInfo client, IPEndPoint source)
    {
        var expected = client?.SessionAddress;
        if (expected == null || source?.Address == null) return false;

        return Normalise(expected).Equals(Normalise(source.Address));
    }

    private static IPAddress Normalise(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>UDP endpoints that must receive <paramref name="packet" /> sent by <paramref name="sender" />.</summary>
    public static HashSet<IPEndPoint> SelectRecipients(IEnumerable<ClientInfo> clients,
        ClientInfo sender,
        UDPVoicePacket packet,
        IReadOnlyList<double> testFrequencies)
    {
        var recipients = new HashSet<IPEndPoint>();
        foreach (var client in SelectRecipientClients(clients, sender, packet, testFrequencies))
            recipients.Add(client.VoipPort);

        return recipients;
    }

    /// <summary>
    ///     Clients (with a known UDP endpoint) that must receive <paramref name="packet" /> sent by
    ///     <paramref name="sender" /> - the server encrypts the packet for each of them with its own key.
    /// </summary>
    public static List<ClientInfo> SelectRecipientClients(IEnumerable<ClientInfo> clients,
        ClientInfo sender,
        UDPVoicePacket packet,
        IReadOnlyList<double> testFrequencies)
    {
        var recipients = new List<ClientInfo>();

        if (clients == null || sender == null || packet == null || sender.Muted) return recipients;

        foreach (var client in clients)
        {
            if (client?.VoipPort == null) continue;

            if (ReferenceEquals(client, sender) || client.ClientGuid == sender.ClientGuid)
            {
                // echo back to the sender on a test frequency ("radio check")
                if (IsOnTestFrequency(packet, testFrequencies)) recipients.Add(client);

                continue;
            }

            if (CanReceive(client.RadioInfo, packet)) recipients.Add(client);
        }

        return recipients;
    }

    /// <summary>
    ///     The busy channel lockout (one speaker per frequency): true if <paramref name="packet" /> of
    ///     <paramref name="sender" /> may be forwarded - always when the lockout is off (<paramref name="enabled" />,
    ///     server setting BUSY_CHANNEL_LOCKOUT), otherwise only if no other station holds one of its channels
    ///     (<see cref="BusyChannelArbiter.Allow" />; the sender then holds the channel).
    /// </summary>
    public static bool PassesBusyChannelLockout(BusyChannelArbiter arbiter, bool enabled, ClientInfo sender,
        UDPVoicePacket packet, long nowMilliseconds)
    {
        if (!enabled || arbiter == null || sender == null || packet == null) return true;

        return arbiter.Allow(sender.ClientGuid, packet.Frequencies, packet.Modulations, nowMilliseconds);
    }

    /// <summary>A client sends 25 voice packets per second (40 ms frames); more than this is dropped.</summary>
    internal const int MaxVoicePacketsPerSecond = 100;

    /// <summary>
    ///     Per-sender rate limit for voice packets (a sender floods only itself, not every listener). Called from the
    ///     single UDP receive loop only.
    /// </summary>
    public static bool AllowVoicePacket(ClientInfo sender, long nowTicks)
    {
        if (nowTicks - sender.VoiceWindowStartTicks >= TimeSpan.TicksPerSecond)
        {
            sender.VoiceWindowStartTicks = nowTicks;
            sender.VoicePacketsInWindow = 0;
        }

        return ++sender.VoicePacketsInWindow <= MaxVoicePacketsPerSecond;
    }

    /// <summary>
    ///     True if <paramref name="source" /> is the endpoint <paramref name="client" /> last sent an authenticated
    ///     datagram from (<see cref="ClientInfo.VoipPort" />). Datagrams from there are always decrypted: the budget of
    ///     failed authentications (<see cref="UdpAuthFailureBudget" />) only applies to other endpoints, so forged
    ///     datagrams in a client's name from another port can't mute it.
    /// </summary>
    public static bool IsFromAuthenticatedEndpoint(ClientInfo client, IPEndPoint source)
    {
        var known = client?.VoipPort;
        return known != null && source != null && known.Port == source.Port &&
               Normalise(known.Address).Equals(Normalise(source.Address));
    }
}
