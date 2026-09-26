using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Network.Crypto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

/// <summary>UDP hop encryption (AES-256-GCM per client, replay window) - protocol 1.1.</summary>
[TestClass]
public class UdpTransportTests
{
    private const string Alice = "ufYS_WlLVkmFPjqCgxz6GA";
    private const string Bob = "abcdefghijklmnopqrstuv";

    private static readonly byte[] VoiceBody = VoicePacket(Alice, 7).EncodePacket();

    private static UDPVoicePacket VoicePacket(string guid, ulong packetNumber)
    {
        var guidBytes = Encoding.ASCII.GetBytes(guid);
        return new UDPVoicePacket
        {
            GuidBytes = guidBytes,
            OriginalClientGuidBytes = guidBytes,
            AudioPart1Bytes = Enumerable.Range(0, 60).Select(i => (byte)i).ToArray(),
            AudioPart1Length = 60,
            Frequencies = [27185000d],
            Modulations = [0],
            Encryptions = [0],
            PacketNumber = packetNumber
        };
    }

    /// <summary>A client session and the matching server session (same key).</summary>
    private sealed class SessionPair : IDisposable
    {
        public SessionPair()
        {
            var key = UdpTransportSession.GenerateKey();
            Client = new UdpTransportSession(Alice, key, 42, false);
            Server = new UdpTransportSession(Alice, key, 42, true);
        }

        public UdpTransportSession Client { get; }
        public UdpTransportSession Server { get; }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }

    [TestMethod]
    public void DatagramLayoutIsGuidCounterCiphertextTag()
    {
        using var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);

        var datagram = client.Seal(VoiceBody);

        Assert.HasCount(UdpDatagram.Overhead + VoiceBody.Length, datagram);
        Assert.AreEqual(Alice, Encoding.ASCII.GetString(datagram, 0, UdpDatagram.GuidLength));
        Assert.AreEqual(1UL, BinaryPrimitives.ReadUInt64BigEndian(datagram.AsSpan(UdpDatagram.GuidLength, 8)));
        Assert.AreEqual(Alice, UdpDatagram.ReadClientGuid(datagram));

        // the body is not readable on the wire
        Assert.IsFalse(datagram.AsSpan(UdpDatagram.HeaderLength, VoiceBody.Length).SequenceEqual(VoiceBody));

        // counters increase per datagram
        Assert.AreEqual(2UL, BinaryPrimitives.ReadUInt64BigEndian(client.Seal(VoiceBody).AsSpan(UdpDatagram.GuidLength, 8)));
    }

    [TestMethod]
    public void RoundTripsInBothDirections()
    {
        using var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);

        Assert.AreEqual(UdpOpenResult.Ok, server.Open(client.Seal(VoiceBody), out var up));
        CollectionAssert.AreEqual(VoiceBody, up);

        Assert.AreEqual(UdpOpenResult.Ok, client.Open(server.Seal(VoiceBody), out var down));
        CollectionAssert.AreEqual(VoiceBody, down);

        Assert.AreEqual(UdpOpenResult.Ok, server.Open(client.Seal(UdpDatagram.PingBody), out var ping));
        Assert.IsTrue(UdpDatagram.IsPing(ping));
        Assert.IsFalse(UdpDatagram.IsPing(VoiceBody));
    }

    [TestMethod]
    public void DirectionsCannotBeMixedUp()
    {
        using var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);

        // a client -> server datagram reflected back to the client (or to the server as if it came from the server)
        var up = client.Seal(VoiceBody);
        Assert.AreEqual(UdpOpenResult.AuthenticationFailed, client.Open(up, out var body));
        Assert.IsNull(body);

        var down = server.Seal(VoiceBody);
        Assert.AreEqual(UdpOpenResult.AuthenticationFailed, server.Open(down, out _));
    }

    [TestMethod]
    public void WrongKeyFails()
    {
        using var client = new UdpTransportSession(Alice, UdpTransportSession.GenerateKey(), 1, false);
        using var server = new UdpTransportSession(Alice, UdpTransportSession.GenerateKey(), 2, true);

        Assert.AreEqual(UdpOpenResult.AuthenticationFailed, server.Open(client.Seal(VoiceBody), out var body));
        Assert.IsNull(body);
        Assert.AreEqual(1, server.AuthenticationFailures);
    }

    [TestMethod]
    public void EveryTamperedByteFails()
    {
        using var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);

        var datagram = client.Seal(VoiceBody);

        // client id bytes are part of the AAD, but a changed id is rejected even before any crypto
        for (var i = 0; i < datagram.Length; i++)
        {
            var tampered = (byte[])datagram.Clone();
            tampered[i] ^= 0x01;

            var result = server.Open(tampered, out var body);
            Assert.AreNotEqual(UdpOpenResult.Ok, result, $"byte {i} was changed");
            Assert.IsNull(body);
        }

        // the untouched datagram still works: failed attempts did not move the replay window
        Assert.AreEqual(UdpOpenResult.Ok, server.Open(datagram, out _));
    }

    [TestMethod]
    public void TruncatedOrOversizedDatagramsAreMalformed()
    {
        using var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);

        var datagram = client.Seal(VoiceBody);

        Assert.AreEqual(UdpOpenResult.Malformed, server.Open(datagram.AsSpan(0, UdpDatagram.MinLength - 1), out _));
        Assert.AreEqual(UdpOpenResult.Malformed, server.Open(new byte[UdpDatagram.MaxLength + 1], out _));
        Assert.AreEqual(UdpOpenResult.Malformed, server.Open(Array.Empty<byte>(), out _));
        Assert.AreEqual(UdpOpenResult.AuthenticationFailed, server.Open(datagram.AsSpan(0, datagram.Length - 1), out _));

        Assert.ThrowsExactly<ArgumentException>(() => client.Seal(new byte[UdpDatagram.MaxBodyLength + 1]));
    }

    [TestMethod]
    public void ADatagramForAnotherClientIsRejected()
    {
        var key = UdpTransportSession.GenerateKey();
        using var bob = new UdpTransportSession(Bob, key, 1, false);
        using var serverForAlice = new UdpTransportSession(Alice, key, 1, true);

        // even with the same key, Bob's client id is not Alice's
        Assert.AreEqual(UdpOpenResult.Malformed, serverForAlice.Open(bob.Seal(VoiceBody), out _));
    }

    [TestMethod]
    public void ReplayedDatagramIsDropped()
    {
        using var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);

        var datagram = client.Seal(VoiceBody);
        Assert.AreEqual(UdpOpenResult.Ok, server.Open(datagram, out _));
        Assert.AreEqual(UdpOpenResult.Replayed, server.Open(datagram, out var body));
        Assert.IsNull(body);
        Assert.AreEqual(1, server.ReplaysRejected);
    }

    [TestMethod]
    public void ReorderedDatagramsWithinTheWindowAreAccepted()
    {
        using var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);

        var datagrams = Enumerable.Range(0, ReplayWindow.Size).Select(_ => client.Seal(VoiceBody)).ToArray();

        // newest first, then all older ones (63 below the newest is the oldest still accepted)
        Assert.AreEqual(UdpOpenResult.Ok, server.Open(datagrams[^1], out _));
        for (var i = datagrams.Length - 2; i >= 0; i--)
            Assert.AreEqual(UdpOpenResult.Ok, server.Open(datagrams[i], out _), $"datagram {i + 1}");

        // and none of them twice
        foreach (var datagram in datagrams) Assert.AreEqual(UdpOpenResult.Replayed, server.Open(datagram, out _));
    }

    [TestMethod]
    public void DatagramOlderThanTheWindowIsDropped()
    {
        using var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);

        var old = client.Seal(VoiceBody); // counter 1
        for (var i = 0; i < ReplayWindow.Size - 1; i++) client.Seal(VoiceBody); // 2..64
        var newest = client.Seal(VoiceBody); // 65

        Assert.AreEqual(UdpOpenResult.Ok, server.Open(newest, out _));
        Assert.AreEqual(UdpOpenResult.Replayed, server.Open(old, out _), "64 below the newest is outside the window");
    }

    [TestMethod]
    public void ReplayWindowRules()
    {
        var window = new ReplayWindow();

        Assert.IsFalse(window.IsAcceptable(0), "counter 0 is never valid");
        Assert.IsTrue(window.IsAcceptable(1));
        window.Accept(1);
        Assert.IsFalse(window.IsAcceptable(1));

        window.Accept(100);
        Assert.AreEqual(100UL, window.Highest);
        Assert.IsTrue(window.IsAcceptable(37), "63 below the highest");
        Assert.IsFalse(window.IsAcceptable(36), "64 below the highest");
        Assert.IsTrue(window.IsAcceptable(99));
        window.Accept(99);
        Assert.IsFalse(window.IsAcceptable(99));
        Assert.IsTrue(window.IsAcceptable(98));

        // a jump far ahead clears the window
        window.Accept(10_000);
        Assert.IsFalse(window.IsAcceptable(100));
        Assert.IsTrue(window.IsAcceptable(9_999));

        // near the end of the counter space
        window.Accept(ulong.MaxValue);
        Assert.IsFalse(window.IsAcceptable(ulong.MaxValue));
        Assert.IsTrue(window.IsAcceptable(ulong.MaxValue - 1));
    }

    [TestMethod]
    public void SealingStopsWhenTheCounterIsUsedUp()
    {
        using var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);

        client.SetNextCounterForTests(ulong.MaxValue - 1);

        var secondToLast = client.Seal(VoiceBody);
        var last = client.Seal(VoiceBody);
        Assert.IsNotNull(secondToLast);
        Assert.IsNotNull(last);
        Assert.AreEqual(ulong.MaxValue, BinaryPrimitives.ReadUInt64BigEndian(last.AsSpan(UdpDatagram.GuidLength, 8)));

        // no wrap to 0/1: a nonce is never reused - the sender stops (fail closed)
        Assert.IsNull(client.Seal(VoiceBody));
        Assert.IsNull(client.Seal(UdpDatagram.PingBody));

        Assert.AreEqual(UdpOpenResult.Ok, server.Open(secondToLast, out _));
        Assert.AreEqual(UdpOpenResult.Ok, server.Open(last, out _));
    }

    [TestMethod]
    public void CounterZeroIsRejected()
    {
        using var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);

        var datagram = client.Seal(VoiceBody);
        BinaryPrimitives.WriteUInt64BigEndian(datagram.AsSpan(UdpDatagram.GuidLength, 8), 0);

        Assert.AreEqual(UdpOpenResult.Replayed, server.Open(datagram, out _));
    }

    [TestMethod]
    public void ClosedSessionNeitherSealsNorOpens()
    {
        var pair = new SessionPair();
        var (client, server) = (pair.Client, pair.Server);
        var datagram = client.Seal(VoiceBody);

        client.Dispose();
        server.Dispose();
        server.Dispose();

        Assert.IsNull(client.Seal(VoiceBody));
        Assert.AreEqual(UdpOpenResult.Closed, server.Open(datagram, out _));
    }

    [TestMethod]
    public void RejectsInvalidKeysAndIds()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new UdpTransportSession(Alice, new byte[16], 1, true));
        Assert.ThrowsExactly<ArgumentException>(() => new UdpTransportSession("short", new byte[32], 1, true));
        Assert.ThrowsExactly<ArgumentException>(() => new UdpTransportSession(null, new byte[32], 1, true));
        Assert.ThrowsExactly<ArgumentException>(
            () => new UdpTransportSession("ufYS_WlLVkmFPjqCgxz6Gä", new byte[32], 1, true));
    }

    [TestMethod]
    public void KeysAreRandom()
    {
        CollectionAssert.AreNotEqual(UdpTransportSession.GenerateKey(), UdpTransportSession.GenerateKey());
        Assert.HasCount(UdpTransportSession.KeyLength, UdpTransportSession.GenerateKey());
    }

    [TestMethod]
    public void TransportKeyParsing()
    {
        var key = UdpTransportSession.GenerateKey();

        Assert.IsTrue(UdpTransportKey.TryParse(Convert.ToBase64String(key), 5, out var parsed));
        CollectionAssert.AreEqual(key, parsed.Key);
        Assert.AreEqual(5u, parsed.KeyId);

        parsed.Clear();
        Assert.IsTrue(parsed.Key.All(b => b == 0));

        Assert.IsFalse(UdpTransportKey.TryParse(Convert.ToBase64String(key), null, out _), "key id missing");
        Assert.IsFalse(UdpTransportKey.TryParse(Convert.ToBase64String(new byte[16]), 1, out _));
        Assert.IsFalse(UdpTransportKey.TryParse(Convert.ToBase64String(new byte[33]), 1, out _));
        Assert.IsFalse(UdpTransportKey.TryParse("not base64!", 1, out _));
        Assert.IsFalse(UdpTransportKey.TryParse(null, 1, out _));
    }

    [TestMethod]
    public void StrictVoicePacketDecoding()
    {
        Assert.IsTrue(UDPVoicePacket.TryDecode(VoiceBody, true, out var packet));
        Assert.AreEqual(Alice, packet.Guid);
        Assert.AreEqual(Alice, packet.OriginalClientGuid);
        Assert.AreEqual(7UL, packet.PacketNumber);
        Assert.AreEqual(27185000d, packet.Frequencies.Single());
        CollectionAssert.AreEqual(VoicePacket(Alice, 7).AudioPart1Bytes, packet.AudioPart1Bytes);

        Assert.IsTrue(UDPVoicePacket.TryDecode(VoiceBody, false, out packet));
        Assert.IsNull(packet.AudioPart1Bytes);

        // wrong total length, inconsistent segment lengths, no frequency, truncated
        Assert.IsFalse(UDPVoicePacket.TryDecode(VoiceBody.Concat(new byte[] { 0 }).ToArray(), false, out _));
        Assert.IsFalse(UDPVoicePacket.TryDecode(VoiceBody.AsSpan(0, VoiceBody.Length - 1), false, out _));

        var badAudioLength = (byte[])VoiceBody.Clone();
        badAudioLength[2]++;
        Assert.IsFalse(UDPVoicePacket.TryDecode(badAudioLength, false, out _));

        var badFrequencyLength = (byte[])VoiceBody.Clone();
        badFrequencyLength[4] = 0;
        Assert.IsFalse(UDPVoicePacket.TryDecode(badFrequencyLength, false, out _));

        var notANumber = (byte[])VoiceBody.Clone();
        BitConverter.GetBytes(double.NaN).CopyTo(notANumber, UDPVoicePacket.PacketHeaderLength + 60);
        Assert.IsFalse(UDPVoicePacket.TryDecode(notANumber, false, out _));

        var badGuid = (byte[])VoiceBody.Clone();
        badGuid[^1] = 0;
        Assert.IsFalse(UDPVoicePacket.TryDecode(badGuid, false, out _));

        Assert.IsFalse(UDPVoicePacket.TryDecode(Array.Empty<byte>(), false, out _));
        Assert.IsFalse(UDPVoicePacket.TryDecode(UdpDatagram.PingBody, false, out _));
    }
}
