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
///         <item>the sender gets its own transmission back only on a radio check (test) frequency,</item>
///         <item>every other client receives the packet if one of its radios <see cref="CanReceive" /> it.</item>
///     </list>
///     Clients without a known UDP endpoint are skipped. The encryption byte of the packet (always 0 since 1.1, set by
///     older clients) does not stop delivery: a listener on the right frequency still gets the packet.
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

        if (clients == null || sender == null || packet == null || sender.Muted) return recipients;

        foreach (var client in clients)
        {
            var endpoint = client?.VoipPort;
            if (endpoint == null) continue;

            if (ReferenceEquals(client, sender) || client.ClientGuid == sender.ClientGuid)
            {
                // echo back to the sender on a test frequency ("radio check")
                if (IsOnTestFrequency(packet, testFrequencies)) recipients.Add(endpoint);

                continue;
            }

            if (CanReceive(client.RadioInfo, packet)) recipients.Add(endpoint);
        }

        return recipients;
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
}
