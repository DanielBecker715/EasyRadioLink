using System;
using System.Collections.Generic;
using EasyRadioLink.Common.Models;

namespace EasyRadioLink.Common.Network.Crypto;

/// <summary>A voice frame ready to play: decrypted, or scrambled because its key never arrived.</summary>
public readonly struct ReceivedVoiceFrame
{
    public ReceivedVoiceFrame(UDPVoicePacket packet, byte[] opus)
    {
        Packet = packet;
        Opus = opus;
    }

    public UDPVoicePacket Packet { get; }

    /// <summary>The decrypted Opus frame; null = no key: play the scrambled effect for the length of one frame.</summary>
    public byte[] Opus { get; }

    public bool Scrambled => Opus == null;
}

/// <summary>
///     Receive side of the end-to-end voice encryption: decrypts voice packets with the keys in <see cref="Keys" />.
///     A packet whose key is not known yet is held for up to <see cref="HoldTime" /> (the key travels over TCP and may
///     arrive a little after the first packets); if the key does not arrive in time the packet comes out
///     <see cref="ReceivedVoiceFrame.Scrambled" />, and so do later packets of that transmission until its key
///     arrives. Replayed, forged and malformed packets are dropped. Thread-safe; one thread calls
///     <see cref="Receive" /> and <see cref="Poll" /> (every few milliseconds).
/// </summary>
public sealed class E2EVoiceReceiver : IDisposable
{
    /// <summary>How long a packet waits for its key.</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(400);

    /// <summary>Most packets held at once (a sender sends 25 per second); beyond this the oldest one is scrambled.</summary>
    public const int MaxHeldPackets = 128;

    // transmissions whose key did not arrive in time: their packets are scrambled straight away (no second wait)
    private static readonly TimeSpan KeylessMemory = TimeSpan.FromSeconds(30);
    private const int MaxKeylessTransmissions = 1024;

    private readonly object _lock = new();
    private readonly Dictionary<(string Sender, ulong TxId), DateTime> _keyless = new();
    private bool _disposed;
    private List<Held> _held = new();

    public VoiceKeyCache Keys { get; } = new();

    /// <summary>Packets currently waiting for their key.</summary>
    public int HeldCount
    {
        get
        {
            lock (_lock)
            {
                return _held.Count;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _held.Clear();
            _keyless.Clear();
        }

        Keys.Dispose();
    }

    /// <summary>
    ///     Handles one received packet (decoded, hop-authenticated). Adds a frame to <paramref name="output" /> if it
    ///     can be played now; holds it if its key may still arrive; drops it if it is invalid, replayed or forged.
    /// </summary>
    public void Receive(UDPVoicePacket packet, DateTime nowUtc, ICollection<ReceivedVoiceFrame> output)
    {
        if (!TryPrepare(packet, out var txId, out var aad)) return;

        lock (_lock)
        {
            if (_disposed) return;

            var result = Keys.TryDecrypt(packet.Guid, txId, packet.PacketNumber, aad, packet.AudioPart1Bytes, nowUtc,
                out var opus);

            switch (result)
            {
                case VoiceDecryptResult.Ok:
                    output.Add(new ReceivedVoiceFrame(packet, opus));
                    break;

                case VoiceDecryptResult.NoKey when _keyless.ContainsKey((packet.Guid, txId)):
                    _keyless[(packet.Guid, txId)] = nowUtc;
                    output.Add(new ReceivedVoiceFrame(packet, null));
                    break;

                case VoiceDecryptResult.NoKey:
                    if (_held.Count >= MaxHeldPackets)
                    {
                        output.Add(new ReceivedVoiceFrame(_held[0].Packet, null));
                        _held.RemoveAt(0);
                    }

                    _held.Add(new Held(packet, txId, aad, nowUtc));
                    break;

                // replayed, forged or tampered: never played
            }
        }
    }

    /// <summary>
    ///     Releases held packets (in arrival order): decrypted once their key has arrived, scrambled once they waited
    ///     <see cref="HoldTime" />.
    /// </summary>
    public void Poll(DateTime nowUtc, ICollection<ReceivedVoiceFrame> output)
    {
        lock (_lock)
        {
            if (_disposed) return;

            if (_held.Count > 0)
            {
                var stillHeld = new List<Held>();
                foreach (var held in _held)
                {
                    var packet = held.Packet;
                    var result = Keys.TryDecrypt(packet.Guid, held.TxId, packet.PacketNumber, held.Aad,
                        packet.AudioPart1Bytes, nowUtc, out var opus);

                    if (result == VoiceDecryptResult.Ok)
                    {
                        output.Add(new ReceivedVoiceFrame(packet, opus));
                    }
                    else if (result == VoiceDecryptResult.NoKey)
                    {
                        var transmission = (packet.Guid, held.TxId);
                        if (_keyless.ContainsKey(transmission) || nowUtc - held.ReceivedUtc >= HoldTime)
                        {
                            MarkKeyless(transmission, nowUtc);
                            output.Add(new ReceivedVoiceFrame(packet, null));
                        }
                        else
                        {
                            stillHeld.Add(held);
                        }
                    }
                }

                _held = stillHeld;
            }

            if (_keyless.Count > 0) ForgetOldKeylessTransmissions(nowUtc);
        }
    }

    /// <summary>Drops everything held or remembered for a sender (it disconnected).</summary>
    public void RemoveSender(string senderGuid)
    {
        lock (_lock)
        {
            _held.RemoveAll(held => held.Packet.Guid == senderGuid);

            List<(string, ulong)> forget = null;
            foreach (var transmission in _keyless.Keys)
                if (transmission.Sender == senderGuid)
                    (forget ??= new List<(string, ulong)>()).Add(transmission);

            if (forget != null)
                foreach (var transmission in forget)
                    _keyless.Remove(transmission);
        }

        Keys.RemoveSender(senderGuid);
    }

    private void MarkKeyless((string, ulong) transmission, DateTime nowUtc)
    {
        if (!_keyless.ContainsKey(transmission) && _keyless.Count >= MaxKeylessTransmissions)
        {
            (string, ulong) oldest = default;
            var oldestSeen = DateTime.MaxValue;
            foreach (var (key, seen) in _keyless)
                if (seen < oldestSeen)
                {
                    oldestSeen = seen;
                    oldest = key;
                }

            _keyless.Remove(oldest);
        }

        _keyless[transmission] = nowUtc;
    }

    private void ForgetOldKeylessTransmissions(DateTime nowUtc)
    {
        List<(string, ulong)> old = null;
        foreach (var (transmission, seen) in _keyless)
            if (nowUtc - seen > KeylessMemory)
                (old ??= new List<(string, ulong)>()).Add(transmission);

        if (old == null) return;

        foreach (var transmission in old) _keyless.Remove(transmission);
    }

    /// <summary>
    ///     Checks the parts of a packet the E2E layer relies on: one sender id in both id fields, no legacy encryption
    ///     byte, an audio segment with TxId + frame + tag.
    /// </summary>
    private static bool TryPrepare(UDPVoicePacket packet, out ulong txId, out byte[] aad)
    {
        txId = 0;
        aad = null;

        if (packet?.AudioPart1Bytes == null || packet.GuidBytes == null || packet.Guid == null ||
            packet.Frequencies == null || packet.Modulations == null ||
            !E2EVoiceCrypto.HasValidSegmentLength(packet.AudioPart1Bytes.Length) ||
            packet.GuidBytes.Length != UdpDatagram.GuidLength ||
            packet.Guid != packet.OriginalClientGuid ||
            packet.Modulations.Length != packet.Frequencies.Length)
            return false;

        if (packet.Encryptions != null)
            foreach (var encryption in packet.Encryptions)
                if (encryption != 0)
                    return false;

        txId = E2EVoiceCrypto.TxIdToUInt64(packet.AudioPart1Bytes);
        aad = E2EVoiceCrypto.BuildAad(packet);
        return true;
    }

    private sealed record Held(UDPVoicePacket Packet, ulong TxId, byte[] Aad, DateTime ReceivedUtc);
}
