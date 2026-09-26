using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Client;
using EasyRadioLink.Common.Network.Crypto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

/// <summary>End-to-end voice encryption: key pairs, key wrapping, frame encryption, key cache, hold buffer, session.</summary>
[TestClass]
public class VoiceE2ETests
{
    private const double Cb19 = 27185000;
    private const double Pmr = 446006250;
    private const double RadioCheck = 27405000;

    private static readonly string AliceGuid = "Alice".PadRight(22, '_');
    private static readonly string BobGuid = "Bob".PadRight(22, '_');
    private static readonly string CarolGuid = "Carol".PadRight(22, '_');
    private static readonly string EveGuid = "Eve".PadRight(22, '_');

    private static readonly byte[] TxIdBytes = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly DateTime T0 = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private static E2EKeyPair _alice;
    private static E2EKeyPair _bob;
    private static E2EKeyPair _eve;

    [ClassInitialize]
    public static void CreateKeys(TestContext _)
    {
        _alice = E2EKeyPair.Create();
        _bob = E2EKeyPair.Create();
        _eve = E2EKeyPair.Create();
    }

    [ClassCleanup]
    public static void DisposeKeys()
    {
        _alice?.Dispose();
        _bob?.Dispose();
        _eve?.Dispose();
    }

    private static PlayerRadioInfoBase Tuned(double frequency, Modulation modulation)
    {
        var info = new PlayerRadioInfoBase();
        info.radios[1] = new RadioBase { freq = frequency, modulation = modulation };
        return info;
    }

    private static ClientInfo Client(string guid, string publicKey, double frequency, Modulation modulation)
    {
        return new ClientInfo
            { ClientGuid = guid, Name = guid[..3], RadioInfo = Tuned(frequency, modulation), E2EPublicKey = publicKey };
    }

    private static byte[] Opus(int length = 120, int seed = 1)
    {
        var opus = new byte[length];
        new Random(seed).NextBytes(opus);
        return opus;
    }

    private static byte[] Aad(string guid, double frequency, Modulation modulation)
    {
        return E2EVoiceCrypto.BuildAad(Encoding.ASCII.GetBytes(guid), [frequency], [(byte)modulation]);
    }

    /// <summary>What a receiver gets from the network: the packet encoded and strictly decoded again.</summary>
    private static UDPVoicePacket OverTheWire(UDPVoicePacket packet)
    {
        Assert.IsTrue(UDPVoicePacket.TryDecode(packet.EncodePacket(), true, out var decoded));
        return decoded;
    }

    private static UDPVoicePacket Packet(VoiceTransmission transmission, byte[] opus, ulong packetNumber,
        double frequency = Cb19, Modulation modulation = Modulation.AM)
    {
        var guidBytes = Encoding.ASCII.GetBytes(transmission.SenderGuid);
        var segment = transmission.EncryptFrame(opus, packetNumber, Aad(transmission.SenderGuid, frequency, modulation));
        return OverTheWire(new UDPVoicePacket
        {
            GuidBytes = guidBytes,
            OriginalClientGuidBytes = guidBytes,
            AudioPart1Bytes = segment,
            AudioPart1Length = (ushort)segment.Length,
            Frequencies = [frequency],
            Modulations = [(byte)modulation],
            Encryptions = [0],
            PacketNumber = packetNumber
        });
    }

    // --- key pair ---------------------------------------------------------------------------------------------------

    [TestMethod]
    public void PublicKeyIsAP256SubjectPublicKeyInfo()
    {
        var spki = Convert.FromBase64String(_alice.PublicKey);
        Assert.AreEqual(E2EKeyPair.PublicKeyLength, spki.Length);
        Assert.IsTrue(E2EKeyPair.IsValidPublicKey(_alice.PublicKey));
        Assert.AreNotEqual(_alice.PublicKey, _bob.PublicKey, "every key pair is new");

        using var imported = E2EKeyPair.ImportPublicKey(_alice.PublicKey);
        Assert.IsNotNull(imported);
        Assert.AreEqual(256, imported.KeySize);
        Assert.AreEqual("1.2.840.10045.3.1.7", imported.ExportParameters(false).Curve.Oid.Value);
    }

    [TestMethod]
    public void PublicKeyFormatCheckRejectsEverythingElse()
    {
        var spki = Convert.FromBase64String(_alice.PublicKey);

        Assert.IsFalse(E2EKeyPair.IsValidPublicKey(null));
        Assert.IsFalse(E2EKeyPair.IsValidPublicKey(""));
        Assert.IsFalse(E2EKeyPair.IsValidPublicKey("not base64!"));
        Assert.IsFalse(E2EKeyPair.IsValidPublicKey(Convert.ToBase64String(spki[..90])));
        Assert.IsFalse(E2EKeyPair.IsValidPublicKey(Convert.ToBase64String([.. spki, 0])));
        Assert.IsFalse(E2EKeyPair.IsValidPublicKey(Convert.ToBase64String(RandomNumberGenerator.GetBytes(91))));
        Assert.IsFalse(E2EKeyPair.IsValidPublicKey(" " + _alice.PublicKey), "no whitespace variants");

        // compressed point marker instead of 0x04
        var compressed = (byte[])spki.Clone();
        compressed[26] = 0x02;
        Assert.IsFalse(E2EKeyPair.IsValidPublicKey(Convert.ToBase64String(compressed)));

        // another curve
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        Assert.IsFalse(E2EKeyPair.IsValidPublicKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
        Assert.IsNull(E2EKeyPair.ImportPublicKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
    }

    [TestMethod]
    public void PointsOffTheCurveAreNotImported()
    {
        var spki = Convert.FromBase64String(_alice.PublicKey);
        spki[^1] ^= 0x01; // y no longer matches x

        var tampered = Convert.ToBase64String(spki);
        Assert.IsTrue(E2EKeyPair.IsValidPublicKey(tampered), "the server only checks the format");
        Assert.IsNull(E2EKeyPair.ImportPublicKey(tampered), "clients refuse points that are not on P-256");
    }

    // --- key wrapping -----------------------------------------------------------------------------------------------

    [TestMethod]
    public void WrappedKeyOpensOnlyForTheRightRecipient()
    {
        var key = RandomNumberGenerator.GetBytes(E2EVoiceCrypto.TransmissionKeyLength);

        var wrapped = E2EVoiceCrypto.WrapKey(_alice, AliceGuid, _bob.PublicKey, BobGuid, TxIdBytes, key);

        Assert.AreEqual(E2EVoiceCrypto.WrappedKeyLength, wrapped.Length);
        Assert.IsFalse(wrapped.AsSpan().IndexOf(key.AsSpan(0, 8)) >= 0, "the wrapped key does not contain K");

        Assert.IsTrue(E2EVoiceCrypto.TryUnwrapKey(_bob, BobGuid, _alice.PublicKey, AliceGuid, TxIdBytes, wrapped, out var unwrapped));
        CollectionAssert.AreEqual(key, unwrapped);

        // Eve can't open it - not with her key, not claiming to be Bob
        Assert.IsFalse(E2EVoiceCrypto.TryUnwrapKey(_eve, EveGuid, _alice.PublicKey, AliceGuid, TxIdBytes, wrapped, out _));
        Assert.IsFalse(E2EVoiceCrypto.TryUnwrapKey(_eve, BobGuid, _alice.PublicKey, AliceGuid, TxIdBytes, wrapped, out _));

        // bound to the sender's key, the ids and the transmission
        Assert.IsFalse(E2EVoiceCrypto.TryUnwrapKey(_bob, BobGuid, _eve.PublicKey, AliceGuid, TxIdBytes, wrapped, out _));
        Assert.IsFalse(E2EVoiceCrypto.TryUnwrapKey(_bob, BobGuid, _alice.PublicKey, EveGuid, TxIdBytes, wrapped, out _));
        Assert.IsFalse(E2EVoiceCrypto.TryUnwrapKey(_bob, EveGuid, _alice.PublicKey, AliceGuid, TxIdBytes, wrapped, out _));
        Assert.IsFalse(E2EVoiceCrypto.TryUnwrapKey(_bob, BobGuid, _alice.PublicKey, AliceGuid, [1, 2, 3, 4, 5, 6, 7, 9],
            wrapped, out _));

        for (var i = 0; i < wrapped.Length; i++)
        {
            var tampered = (byte[])wrapped.Clone();
            tampered[i] ^= 0x40;
            Assert.IsFalse(E2EVoiceCrypto.TryUnwrapKey(_bob, BobGuid, _alice.PublicKey, AliceGuid, TxIdBytes, tampered,
                out var result), $"byte {i}");
            Assert.IsNull(result);
        }

        // base64 form (as in VOICE_KEY)
        Assert.IsTrue(E2EVoiceCrypto.TryUnwrapKey(_bob, BobGuid, _alice.PublicKey, AliceGuid, TxIdBytes,
            Convert.ToBase64String(wrapped), out var fromBase64));
        CollectionAssert.AreEqual(key, fromBase64);
        Assert.IsFalse(E2EVoiceCrypto.TryUnwrapKey(_bob, BobGuid, _alice.PublicKey, AliceGuid, TxIdBytes,
            Convert.ToBase64String(wrapped[..47]), out _));
    }

    [TestMethod]
    public void WrapMatchesTheSpecification()
    {
        // recomputed from the BCL primitives exactly as DESIGN-crypto section 3 states it
        using var aliceRaw = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var alice = E2EKeyPair.FromKeyForTests(ECDiffieHellman.Create(aliceRaw.ExportParameters(true)));
        using var bobRaw = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var bobPublicKey = Convert.ToBase64String(bobRaw.ExportSubjectPublicKeyInfo());
        var key = RandomNumberGenerator.GetBytes(32);

        var wrapped = E2EVoiceCrypto.WrapKey(alice, AliceGuid, bobPublicKey, BobGuid, TxIdBytes, key);

        using var alicePublic = aliceRaw.PublicKey;
        var shared = bobRaw.DeriveRawSecretAgreement(alicePublic);
        var info = Encoding.ASCII.GetBytes("EasyRadioLink E2E v1" + AliceGuid + BobGuid);
        var wrapKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, TxIdBytes, info);
        var expected = new byte[48];
        using (var aes = new AesGcm(wrapKey, 16))
        {
            aes.Encrypt(new byte[12], key, expected.AsSpan(0, 32), expected.AsSpan(32, 16));
        }

        CollectionAssert.AreEqual(expected, wrapped);
        Assert.AreEqual(1, alice.CachedPeerCount);

        // the same again from the cached ECDH secret
        CollectionAssert.AreEqual(expected, E2EVoiceCrypto.WrapKey(alice, AliceGuid, bobPublicKey, BobGuid, TxIdBytes, key));
        Assert.AreEqual(1, alice.CachedPeerCount);
    }

    [TestMethod]
    public void FrameEncryptionMatchesTheSpecification()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.FM);
        var opus = Opus();
        const ulong packetNumber = 0x0102030405060708;

        var segment = transmission.EncryptFrame(opus, packetNumber, Aad(AliceGuid, Cb19, Modulation.FM));

        // nonce = 4 zero bytes | packet number (big-endian); AAD = sender id | frequency (f64 LE) | modulation
        var nonce = new byte[] { 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var aad = Encoding.ASCII.GetBytes(AliceGuid).Concat(BitConverter.GetBytes(Cb19)).Concat(new byte[] { 1 }).ToArray();
        var expected = new byte[opus.Length + 16];
        using (var aes = new AesGcm(transmission.CopyKeyForTests(), 16))
        {
            aes.Encrypt(nonce, opus, expected.AsSpan(0, opus.Length), expected.AsSpan(opus.Length, 16), aad);
        }

        CollectionAssert.AreEqual(Convert.FromBase64String(transmission.TxIdBase64).Concat(expected).ToArray(), segment);
    }

    [TestMethod]
    public void EcdhSecretIsComputedOncePerPeerAndForgottenOnDispose()
    {
        var keys = E2EKeyPair.Create();

        Assert.IsTrue(keys.TryPrepare(_bob.PublicKey));
        Assert.IsTrue(keys.TryPrepare(_bob.PublicKey));
        Assert.IsTrue(keys.TryPrepare(_eve.PublicKey));
        Assert.AreEqual(2, keys.CachedPeerCount);

        Assert.IsFalse(keys.TryPrepare(OffCurveKey()));
        Assert.IsFalse(keys.TryPrepare("AAAA"));
        Assert.IsFalse(keys.TryPrepare(null));
        Assert.IsNull(E2EVoiceCrypto.WrapKey(keys, AliceGuid, OffCurveKey(), EveGuid, TxIdBytes, new byte[32]));
        Assert.AreEqual(2, keys.CachedPeerCount, "unusable keys are not kept");

        keys.Dispose();
        Assert.AreEqual(0, keys.CachedPeerCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => keys.TryPrepare(_bob.PublicKey));
    }

    [TestMethod]
    public void EcdhCacheDropsOnlyTheLeastRecentlyUsedPeer()
    {
        using var keys = E2EKeyPair.CreateForTests(3);
        using var carol = E2EKeyPair.Create();

        Assert.IsTrue(keys.TryPrepare(_alice.PublicKey));
        Assert.IsTrue(keys.TryPrepare(_bob.PublicKey));
        Assert.IsTrue(keys.TryPrepare(_eve.PublicKey));

        // Alice is used again (a wrap for her): Bob is now the least recently used peer
        Assert.IsNotNull(E2EVoiceCrypto.WrapKey(keys, CarolGuid, _alice.PublicKey, AliceGuid, TxIdBytes, new byte[32]));

        // full: one new peer costs exactly one entry - not the whole cache
        Assert.IsTrue(keys.TryPrepare(carol.PublicKey));
        Assert.AreEqual(3, keys.CachedPeerCount);
        Assert.IsFalse(keys.HasCachedPeer(_bob.PublicKey));
        Assert.IsTrue(keys.HasCachedPeer(_alice.PublicKey));
        Assert.IsTrue(keys.HasCachedPeer(_eve.PublicKey));
        Assert.IsTrue(keys.HasCachedPeer(carol.PublicKey));

        // an evicted peer is simply prepared again
        Assert.IsTrue(keys.TryPrepare(_bob.PublicKey));
        Assert.IsFalse(keys.HasCachedPeer(_eve.PublicKey));
        Assert.AreEqual(3, keys.CachedPeerCount);
    }

    // --- frames -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public void FrameLayoutIsTxIdCiphertextTag()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        var opus = Opus();

        var segment = transmission.EncryptFrame(opus, 5, Aad(AliceGuid, Cb19, Modulation.AM));

        Assert.AreEqual(opus.Length + E2EVoiceCrypto.AudioOverhead, segment.Length);
        Assert.AreEqual(transmission.TxId, E2EVoiceCrypto.TxIdToUInt64(segment));
        CollectionAssert.AreEqual(Convert.FromBase64String(transmission.TxIdBase64), segment[..8]);
        Assert.IsFalse(segment.AsSpan(8, opus.Length).SequenceEqual(opus), "the audio is encrypted");

        // a new transmission has a new id and key
        using var next = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        Assert.AreNotEqual(transmission.TxId, next.TxId);
        CollectionAssert.AreNotEqual(transmission.CopyKeyForTests(), next.CopyKeyForTests());
    }

    [TestMethod]
    public void FrameDecryptsOnlyWithItsSenderFrequencyModulationAndNumber()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        var opus = Opus();
        var aad = Aad(AliceGuid, Cb19, Modulation.AM);
        var segment = transmission.EncryptFrame(opus, 5, aad);

        using var cipher = new AesGcm(transmission.CopyKeyForTests(), E2EVoiceCrypto.TagLength);

        Assert.IsTrue(E2EVoiceCrypto.TryDecryptFrame(cipher, segment, 5, aad, out var plain));
        CollectionAssert.AreEqual(opus, plain);

        // AAD binding: moved to another frequency / modulation / sender by the server -> no audio
        Assert.IsFalse(E2EVoiceCrypto.TryDecryptFrame(cipher, segment, 5, Aad(AliceGuid, Cb19 + 1000, Modulation.AM), out _));
        Assert.IsFalse(E2EVoiceCrypto.TryDecryptFrame(cipher, segment, 5, Aad(AliceGuid, Cb19, Modulation.FM), out _));
        Assert.IsFalse(E2EVoiceCrypto.TryDecryptFrame(cipher, segment, 5, Aad(BobGuid, Cb19, Modulation.AM), out _));
        Assert.IsFalse(E2EVoiceCrypto.TryDecryptFrame(cipher, segment, 6, aad, out _), "other packet number (nonce)");

        for (var i = E2EVoiceCrypto.TxIdLength; i < segment.Length; i++)
        {
            var tampered = (byte[])segment.Clone();
            tampered[i] ^= 0x01;
            Assert.IsFalse(E2EVoiceCrypto.TryDecryptFrame(cipher, tampered, 5, aad, out _), $"byte {i}");
        }

        using var wrongKey = new AesGcm(RandomNumberGenerator.GetBytes(32), E2EVoiceCrypto.TagLength);
        Assert.IsFalse(E2EVoiceCrypto.TryDecryptFrame(wrongKey, segment, 5, aad, out _));

        Assert.IsFalse(E2EVoiceCrypto.TryDecryptFrame(cipher, segment[..E2EVoiceCrypto.AudioOverhead], 5, aad, out _));
    }

    [TestMethod]
    public void EndedTransmissionEncryptsNothing()
    {
        var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        transmission.Retire();

        Assert.IsTrue(transmission.IsDisposed);
        Assert.IsNull(transmission.EncryptFrame(Opus(), 1, Aad(AliceGuid, Cb19, Modulation.AM)));
        Assert.IsNull(transmission.CopyKeyForTests());
    }

    [TestMethod]
    public void RetiredTransmissionKeepsItsKeyForRunningWraps()
    {
        var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        var lease = transmission.AcquireLease();

        transmission.Retire();
        Assert.IsFalse(transmission.IsDisposed, "a key wrap is still running");

        var messages = transmission.WrapFor(_alice, [new VoiceKeyRecipient(BobGuid, _bob.PublicKey)]);
        Assert.HasCount(1, messages);

        lease.Dispose();
        lease.Dispose(); // twice is harmless
        Assert.IsTrue(transmission.IsDisposed);
        Assert.IsEmpty(transmission.WrapFor(_alice, [new VoiceKeyRecipient(BobGuid, _bob.PublicKey)]));
        Assert.IsNull(transmission.AcquireLease());
    }

    // --- recipients -------------------------------------------------------------------------------------------------

    [TestMethod]
    public void KeyIsWrappedForListenersOnTheFrequencyOnly()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);

        var bob = Client(BobGuid, _bob.PublicKey, Cb19 + 200, Modulation.AM); // within the 500 Hz tolerance
        var clients = new List<ClientInfo>
        {
            Client(AliceGuid, _alice.PublicKey, Cb19, Modulation.AM), // the sender itself
            bob,
            Client(CarolGuid, _bob.PublicKey, Cb19, Modulation.FM), // other modulation
            Client(EveGuid, _eve.PublicKey, Pmr, Modulation.FM), // other frequency
            Client("NoKey".PadRight(22, '_'), null, Cb19, Modulation.AM),
            Client("BadKey".PadRight(22, '_'), "AAAA", Cb19, Modulation.AM),
            null
        };

        var recipients = transmission.TakeNewRecipients(clients);
        CollectionAssert.AreEqual(new[] { BobGuid }, recipients.Select(r => r.ClientGuid).ToArray());

        Assert.IsEmpty(transmission.TakeNewRecipients(clients), "Bob has the key already");

        // Eve tunes in during the transmission: she is a newcomer
        clients[3].RadioInfo = Tuned(Cb19, Modulation.AM);
        CollectionAssert.AreEqual(new[] { EveGuid }, transmission.TakeNewRecipients(clients).Select(r => r.ClientGuid).ToArray());

        // Bob's id now belongs to another key (reconnected / taken over): he needs the key again
        bob.E2EPublicKey = _eve.PublicKey;
        var again = transmission.TakeNewRecipients(clients);
        Assert.HasCount(1, again);
        Assert.AreEqual(_eve.PublicKey, again[0].PublicKey);
    }

    [TestMethod]
    public void ListenersThatComeBackGetTheKeyAgain()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        var bob = Client(BobGuid, _bob.PublicKey, Cb19, Modulation.AM);
        var clients = new List<ClientInfo> { bob };

        string[] Take()
        {
            return transmission.TakeNewRecipients(clients).Select(r => r.ClientGuid).ToArray();
        }

        CollectionAssert.AreEqual(new[] { BobGuid }, Take());
        Assert.IsEmpty(Take(), "covered");

        // Bob's knob sweeps away and back: he needs the key again (the server dropped keys meanwhile, if any)
        bob.RadioInfo = Tuned(Cb19 + 5000, Modulation.AM);
        Assert.IsEmpty(Take());
        bob.RadioInfo = Tuned(Cb19, Modulation.AM);
        CollectionAssert.AreEqual(new[] { BobGuid }, Take(), "tuned back");

        // the sender's view was a moment behind the server's: the key it wrapped was dropped by the server (Bob was
        // elsewhere by then). Bob's next radio update - the same frequency, but a new announcement - brings it again.
        bob.RadioInfo = Tuned(Cb19, Modulation.AM);
        CollectionAssert.AreEqual(new[] { BobGuid }, Take(), "new radio state from the server");
        Assert.IsEmpty(Take(), "nothing new");

        // Bob leaves the client list and comes back (reconnect, same key pair and even the same radio object)
        clients.Clear();
        Assert.IsEmpty(Take());
        clients.Add(bob);
        CollectionAssert.AreEqual(new[] { BobGuid }, Take(), "back after a disconnect");

        // CLIENT_DISCONNECT and the new announcement came in between two frames (id taken over)
        transmission.ForgetListener(BobGuid);
        CollectionAssert.AreEqual(new[] { BobGuid }, Take(), "forgotten");

        // wrapping again is harmless: the same wrap key, K and zero nonce give the identical blob
        var first = transmission.WrapFor(_alice, [new VoiceKeyRecipient(BobGuid, _bob.PublicKey)]).Single();
        var second = transmission.WrapFor(_alice, [new VoiceKeyRecipient(BobGuid, _bob.PublicKey)]).Single();
        Assert.AreEqual(first.Keys[BobGuid], second.Keys[BobGuid]);
    }

    [TestMethod]
    public void VoiceKeyMessagesCarryOneWrappedKeyPerListener()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        var messages = transmission.WrapFor(_alice,
        [
            new VoiceKeyRecipient(BobGuid, _bob.PublicKey),
            new VoiceKeyRecipient(EveGuid, "AAAA") // unusable key: left out
        ]);

        Assert.HasCount(1, messages);
        var message = messages[0];
        Assert.IsTrue(message.IsValid(out var txId, out _));
        Assert.AreEqual(transmission.TxId, txId);
        Assert.AreEqual(AliceGuid, message.SenderGuid);
        Assert.AreEqual(Cb19, message.Frequency);
        Assert.AreEqual(Modulation.AM, message.Modulation);
        CollectionAssert.AreEqual(new[] { BobGuid }, message.Keys.Keys.ToArray());

        Assert.IsTrue(E2EVoiceCrypto.TryUnwrapKey(_bob, BobGuid, _alice.PublicKey, AliceGuid,
            Convert.FromBase64String(message.TxId), message.Keys[BobGuid], out var key));
        CollectionAssert.AreEqual(transmission.CopyKeyForTests(), key);
        StringAssert.DoesNotMatch(message.Keys[BobGuid],
            new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(Convert.ToBase64String(key))));
    }

    private static string OffCurveKey()
    {
        var spki = Convert.FromBase64String(_eve.PublicKey);
        spki[^1] ^= 0x01;
        return Convert.ToBase64String(spki);
    }

    [TestMethod]
    public void HostilePublicKeysCostNobodyElseTheirKey()
    {
        // a format-valid key that is not on the curve passes the server's check; the sender must skip it quietly
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        var messages = transmission.WrapFor(_alice,
        [
            new VoiceKeyRecipient(EveGuid, OffCurveKey()),
            new VoiceKeyRecipient(BobGuid, _bob.PublicKey)
        ]);

        Assert.HasCount(1, messages);
        CollectionAssert.AreEqual(new[] { BobGuid }, messages[0].Keys.Keys.ToArray());

        // ... and a receiver ignores a voice key from a sender with such a key (no exception into the TCP loop)
        var clients = new ConcurrentDictionary<string, ClientInfo>
        {
            [EveGuid] = Client(EveGuid, OffCurveKey(), Cb19, Modulation.AM),
            [BobGuid] = Client(BobGuid, _bob.PublicKey, Cb19, Modulation.AM)
        };
        using var bob = new Peer(BobGuid, _bob, clients);
        var fromEve = messages[0];
        fromEve.SenderGuid = EveGuid;
        Assert.IsFalse(bob.Session.HandleVoiceKey(fromEve, T0));
        Assert.AreEqual(0, bob.Session.Receiver.Keys.Count);
    }

    [TestMethod]
    public void MoreThanAThousandListenersAreSplitOverSeveralMessages()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        var recipients = Enumerable.Range(0, VoiceKeyMessage.MaxKeys + 1)
            .Select(i => new VoiceKeyRecipient(i.ToString("D22"), _bob.PublicKey)).ToList();

        var messages = transmission.WrapFor(_alice, recipients);

        Assert.HasCount(2, messages);
        Assert.AreEqual(VoiceKeyMessage.MaxKeys, messages[0].Keys.Count);
        Assert.AreEqual(1, messages[1].Keys.Count);
        Assert.IsTrue(messages.All(m => m.IsValid(out _, out _)));
    }

    // --- VOICE_KEY format -------------------------------------------------------------------------------------------

    private static VoiceKeyMessage ValidVoiceKey()
    {
        return new VoiceKeyMessage
        {
            SenderGuid = AliceGuid,
            TxId = Convert.ToBase64String(TxIdBytes),
            Frequency = Cb19,
            Modulation = Modulation.AM,
            Keys = new Dictionary<string, string> { [BobGuid] = Convert.ToBase64String(new byte[48]) }
        };
    }

    [TestMethod]
    public void VoiceKeyFormatIsCheckedStrictly()
    {
        Assert.IsTrue(ValidVoiceKey().IsValid(out var txId, out var reason), reason);
        Assert.AreEqual(0x0102030405060708UL, txId);

        var invalid = new List<Action<VoiceKeyMessage>>
        {
            m => m.SenderGuid = null,
            m => m.SenderGuid = "short",
            m => m.SenderGuid = "Alice?".PadRight(22, '_'),
            m => m.TxId = null,
            m => m.TxId = Convert.ToBase64String(new byte[7]),
            m => m.TxId = Convert.ToBase64String(new byte[9]),
            m => m.TxId = "not base64",
            m => m.Frequency = double.NaN,
            m => m.Frequency = double.PositiveInfinity,
            m => m.Frequency = 0,
            m => m.Frequency = -1,
            m => m.Modulation = Modulation.DISABLED,
            m => m.Modulation = (Modulation)99,
            m => m.Keys = null,
            m => m.Keys = new Dictionary<string, string>(),
            m => m.Keys["x"] = Convert.ToBase64String(new byte[48]),
            m => m.Keys[BobGuid] = Convert.ToBase64String(new byte[47]),
            m => m.Keys[BobGuid] = Convert.ToBase64String(new byte[49]),
            m => m.Keys[BobGuid] = null,
            m => m.Keys[BobGuid] = "not base64",
            m =>
            {
                for (var i = 0; i < VoiceKeyMessage.MaxKeys; i++) m.Keys[i.ToString("D22")] = m.Keys[BobGuid];
            }
        };

        for (var i = 0; i < invalid.Count; i++)
        {
            var message = ValidVoiceKey();
            invalid[i](message);
            Assert.IsFalse(message.IsValid(out _, out reason), $"case {i}");
            Assert.IsNotNull(reason);
        }

        // exactly the maximum is fine
        var full = ValidVoiceKey();
        for (var i = 1; i < VoiceKeyMessage.MaxKeys; i++) full.Keys[i.ToString("D22")] = full.Keys[BobGuid];
        Assert.IsTrue(full.IsValid(out _, out _));
    }

    // --- key cache --------------------------------------------------------------------------------------------------

    [TestMethod]
    public void CachedKeyDecryptsAndRejectsReplays()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        using var cache = new VoiceKeyCache();
        var aad = Aad(AliceGuid, Cb19, Modulation.AM);

        Assert.AreEqual(VoiceDecryptResult.NoKey,
            cache.TryDecrypt(AliceGuid, transmission.TxId, 1, aad, transmission.EncryptFrame(Opus(), 1, aad), T0, out _));

        Assert.IsTrue(transmission.AddTo(cache, T0));
        Assert.IsFalse(transmission.AddTo(cache, T0), "the first key of a transmission stays");

        var frames = Enumerable.Range(1, 100).Select(n => transmission.EncryptFrame(Opus(seed: n), (ulong)n, aad)).ToArray();

        Assert.AreEqual(VoiceDecryptResult.Ok, cache.TryDecrypt(AliceGuid, transmission.TxId, 10, aad, frames[9], T0, out var opus));
        CollectionAssert.AreEqual(Opus(seed: 10), opus);

        Assert.AreEqual(VoiceDecryptResult.Replayed, cache.TryDecrypt(AliceGuid, transmission.TxId, 10, aad, frames[9], T0, out _));
        Assert.AreEqual(VoiceDecryptResult.Ok, cache.TryDecrypt(AliceGuid, transmission.TxId, 3, aad, frames[2], T0, out _), "late but new");
        Assert.AreEqual(VoiceDecryptResult.Ok, cache.TryDecrypt(AliceGuid, transmission.TxId, 100, aad, frames[99], T0, out _));
        Assert.AreEqual(VoiceDecryptResult.Replayed, cache.TryDecrypt(AliceGuid, transmission.TxId, 20, aad, frames[19], T0, out _), "older than the window");

        // a forged frame does not move the window
        var forged = (byte[])frames[50].Clone();
        forged[^1] ^= 1;
        Assert.AreEqual(VoiceDecryptResult.AuthenticationFailed, cache.TryDecrypt(AliceGuid, transmission.TxId, 51, aad, forged, T0, out _));
        Assert.AreEqual(VoiceDecryptResult.Ok, cache.TryDecrypt(AliceGuid, transmission.TxId, 51, aad, frames[50], T0, out _));

        // keys are per sender
        Assert.AreEqual(VoiceDecryptResult.NoKey, cache.TryDecrypt(BobGuid, transmission.TxId, 52, aad, frames[51], T0, out _));
        Assert.AreEqual(VoiceDecryptResult.Malformed, cache.TryDecrypt(AliceGuid, transmission.TxId, 52, aad, new byte[24], T0, out _));
    }

    [TestMethod]
    public void CachedKeysExpireFiveMinutesAfterTheirLastUse()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        using var cache = new VoiceKeyCache();
        var aad = Aad(AliceGuid, Cb19, Modulation.AM);
        transmission.AddTo(cache, T0);

        // used after 4 minutes: alive for another 5
        Assert.AreEqual(VoiceDecryptResult.Ok, cache.TryDecrypt(AliceGuid, transmission.TxId, 1, aad,
            transmission.EncryptFrame(Opus(), 1, aad), T0.AddMinutes(4), out _));
        Assert.IsTrue(cache.Contains(AliceGuid, transmission.TxId, T0.AddMinutes(8.9)));
        Assert.IsFalse(cache.Contains(AliceGuid, transmission.TxId, T0.AddMinutes(9)));
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public void CacheIsBoundedPerSenderAndInTotal()
    {
        using var cache = new VoiceKeyCache();
        var key = new byte[32];

        for (ulong txId = 1; txId <= VoiceKeyCache.MaxKeysPerSender + 6; txId++)
            Assert.IsTrue(cache.TryAdd(AliceGuid, txId, key, T0.AddSeconds(txId)));

        Assert.AreEqual(VoiceKeyCache.MaxKeysPerSender, cache.Count);
        Assert.IsFalse(cache.Contains(AliceGuid, 1, T0.AddSeconds(100)), "the least recently used key went first");
        Assert.IsTrue(cache.Contains(AliceGuid, VoiceKeyCache.MaxKeysPerSender + 6, T0.AddSeconds(100)));

        // 10 senders more than allowed (1 ms apart, nothing expires): the least recently used senders go first -
        // Alice (last used at 70 s), then senders 0..9
        var start = T0.AddSeconds(200);
        for (var sender = 0; sender < VoiceKeyCache.MaxSenders + 10; sender++)
            Assert.IsTrue(cache.TryAdd(sender.ToString("D22"), 1, key, start.AddMilliseconds(sender)));

        var now = start.AddSeconds(10);
        Assert.AreEqual(VoiceKeyCache.MaxSenders, cache.Count);
        Assert.IsFalse(cache.Contains(AliceGuid, VoiceKeyCache.MaxKeysPerSender + 6, now));
        Assert.IsFalse(cache.Contains(9.ToString("D22"), 1, now));
        Assert.IsTrue(cache.Contains(10.ToString("D22"), 1, now));

        var newest = (VoiceKeyCache.MaxSenders + 9).ToString("D22");
        Assert.IsTrue(cache.Contains(newest, 1, now));
        cache.RemoveSender(newest);
        Assert.IsFalse(cache.Contains(newest, 1, now));

        cache.Clear();
        Assert.AreEqual(0, cache.Count);
        Assert.IsFalse(cache.TryAdd(AliceGuid, 1, new byte[31], T0), "wrong key length");

        cache.Dispose();
        Assert.IsFalse(cache.TryAdd(AliceGuid, 1, key, T0), "closed");
    }

    // --- receiver (hold buffer) -------------------------------------------------------------------------------------

    [TestMethod]
    public void PacketWithKnownKeyIsPlayedAtOnce()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        using var receiver = new E2EVoiceReceiver();
        transmission.AddTo(receiver.Keys, T0);

        var frames = new List<ReceivedVoiceFrame>();
        receiver.Receive(Packet(transmission, Opus(), 1), T0, frames);

        Assert.HasCount(1, frames);
        Assert.IsFalse(frames[0].Scrambled);
        CollectionAssert.AreEqual(Opus(), frames[0].Opus);
        Assert.AreEqual(1UL, frames[0].Packet.PacketNumber);
    }

    [TestMethod]
    public void PacketsWaitForTheirKeyAndComeOutInOrder()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        using var receiver = new E2EVoiceReceiver();
        var frames = new List<ReceivedVoiceFrame>();

        for (var n = 1; n <= 3; n++)
            receiver.Receive(Packet(transmission, Opus(seed: n), (ulong)n), T0.AddMilliseconds(40 * n), frames);

        Assert.IsEmpty(frames);
        Assert.AreEqual(3, receiver.HeldCount);

        receiver.Poll(T0.AddMilliseconds(200), frames);
        Assert.IsEmpty(frames, "the key is not there yet");

        transmission.AddTo(receiver.Keys, T0.AddMilliseconds(250)); // VOICE_KEY arrives
        receiver.Poll(T0.AddMilliseconds(260), frames);

        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, frames.Select(f => f.Packet.PacketNumber).ToArray());
        Assert.IsTrue(frames.All(f => !f.Scrambled));
        CollectionAssert.AreEqual(Opus(seed: 2), frames[1].Opus);
        Assert.AreEqual(0, receiver.HeldCount);
    }

    [TestMethod]
    public void WithoutKeyThePacketsAreScrambledAfterTheHoldTime()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        using var receiver = new E2EVoiceReceiver();
        var frames = new List<ReceivedVoiceFrame>();

        receiver.Receive(Packet(transmission, Opus(), 1), T0, frames);
        receiver.Receive(Packet(transmission, Opus(), 2), T0.AddMilliseconds(40), frames);

        receiver.Poll(T0.AddMilliseconds(399), frames);
        Assert.IsEmpty(frames);

        receiver.Poll(T0 + E2EVoiceReceiver.HoldTime, frames);
        Assert.HasCount(2, frames, "the first packet expired - the rest of that transmission goes out scrambled too");
        Assert.IsTrue(frames.All(f => f.Scrambled && f.Opus == null));

        // later packets of that transmission: scrambled straight away (no second wait)
        frames.Clear();
        receiver.Receive(Packet(transmission, Opus(), 3), T0.AddMilliseconds(500), frames);
        Assert.HasCount(1, frames);
        Assert.IsTrue(frames[0].Scrambled);

        // the key arrives late (tuned in mid-transmission): clear audio from now on
        frames.Clear();
        transmission.AddTo(receiver.Keys, T0.AddMilliseconds(600));
        receiver.Receive(Packet(transmission, Opus(seed: 4), 4), T0.AddMilliseconds(620), frames);
        Assert.HasCount(1, frames);
        CollectionAssert.AreEqual(Opus(seed: 4), frames[0].Opus);
    }

    [TestMethod]
    public void ForgedReplayedAndMalformedPacketsAreNeverPlayed()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        using var receiver = new E2EVoiceReceiver();
        transmission.AddTo(receiver.Keys, T0);
        var frames = new List<ReceivedVoiceFrame>();

        var packet = Packet(transmission, Opus(), 1);
        receiver.Receive(packet, T0, frames);
        receiver.Receive(packet, T0, frames); // replay
        Assert.HasCount(1, frames);

        frames.Clear();

        // the server moved Alice's packet to another frequency
        var moved = Packet(transmission, Opus(), 2);
        moved.Frequencies[0] = Pmr;
        moved.Modulations[0] = (byte)Modulation.FM;
        receiver.Receive(OverTheWire(moved), T0, frames);

        // tampered audio
        var tampered = Packet(transmission, Opus(), 3);
        tampered.AudioPart1Bytes[20] ^= 1;
        receiver.Receive(tampered, T0, frames);

        // legacy encryption byte, mixed ids, audio too short for TxId + tag
        var legacy = Packet(transmission, Opus(), 4);
        legacy.Encryptions[0] = 1;
        receiver.Receive(legacy, T0, frames);
        var mixed = Packet(transmission, Opus(), 5);
        mixed.OriginalClientGuid = BobGuid;
        receiver.Receive(mixed, T0, frames);
        var shortAudio = Packet(transmission, Opus(), 6);
        shortAudio.AudioPart1Bytes = shortAudio.AudioPart1Bytes[..24];
        receiver.Receive(shortAudio, T0, frames);
        receiver.Receive(null, T0, frames);

        receiver.Poll(T0.AddSeconds(1), frames);
        Assert.IsEmpty(frames);
        Assert.AreEqual(0, receiver.HeldCount);
    }

    [TestMethod]
    public void HoldBufferIsBounded()
    {
        using var transmission = new VoiceTransmission(AliceGuid, Cb19, Modulation.AM);
        using var receiver = new E2EVoiceReceiver();
        var frames = new List<ReceivedVoiceFrame>();

        for (var n = 1; n <= E2EVoiceReceiver.MaxHeldPackets + 5; n++)
            receiver.Receive(Packet(transmission, Opus(), (ulong)n), T0, frames);

        Assert.AreEqual(E2EVoiceReceiver.MaxHeldPackets, receiver.HeldCount);
        Assert.HasCount(5, frames);
        Assert.IsTrue(frames.All(f => f.Scrambled));
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3, 4, 5 }, frames.Select(f => f.Packet.PacketNumber).ToArray());

        receiver.RemoveSender(AliceGuid);
        Assert.AreEqual(0, receiver.HeldCount);
    }

    // --- session (client side, both directions) ---------------------------------------------------------------------

    private sealed class Peer : IDisposable
    {
        public Peer(string guid, E2EKeyPair keys, ConcurrentDictionary<string, ClientInfo> clients)
        {
            Session = new E2EVoiceSession(guid, keys, clients, message =>
            {
                Sent.Enqueue(message);
                return System.Threading.Tasks.Task.CompletedTask;
            }, frequency => RadioBase.FreqCloseEnough(frequency, RadioCheck));
        }

        public E2EVoiceSession Session { get; }
        public ConcurrentQueue<NetworkMessage> Sent { get; } = new();

        public void Dispose()
        {
            Session.Dispose();
        }

        public VoiceKeyMessage NextKeyMessage()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (Sent.TryDequeue(out var message))
                {
                    Assert.AreEqual(NetworkMessage.MessageType.VOICE_KEY, message.MsgType);
                    return message.VoiceKey;
                }

                Thread.Sleep(5);
            }

            Assert.Fail("no VOICE_KEY was sent");
            return null;
        }

        public void ExpectNoKeyMessage()
        {
            Thread.Sleep(150);
            Assert.IsTrue(Sent.IsEmpty, "no VOICE_KEY expected");
        }
    }

    /// <summary>What the server forwards: only the recipient's own entry.</summary>
    private static VoiceKeyMessage ForwardTo(VoiceKeyMessage message, string recipient)
    {
        return new VoiceKeyMessage
        {
            SenderGuid = message.SenderGuid,
            TxId = message.TxId,
            Frequency = message.Frequency,
            Modulation = message.Modulation,
            Keys = new Dictionary<string, string> { [recipient] = message.Keys[recipient] }
        };
    }

    [TestMethod]
    public void SessionsExchangeKeysAndVoiceEndToEnd()
    {
        var clients = new ConcurrentDictionary<string, ClientInfo>
        {
            [AliceGuid] = Client(AliceGuid, _alice.PublicKey, Cb19, Modulation.AM),
            [BobGuid] = Client(BobGuid, _bob.PublicKey, Cb19, Modulation.AM),
            [EveGuid] = Client(EveGuid, _eve.PublicKey, Pmr, Modulation.FM)
        };

        using var alice = new Peer(AliceGuid, _alice, clients);
        using var bob = new Peer(BobGuid, _bob, clients);
        using var eve = new Peer(EveGuid, _eve, clients);

        // PTT: the first frame starts a transmission and sends its key to Bob only
        var packet = alice.Session.CreateVoicePacket(Opus(seed: 1), 1, Cb19, Modulation.AM, T0);
        Assert.IsNotNull(packet);
        Assert.AreEqual(120 + E2EVoiceCrypto.AudioOverhead, packet.AudioPart1Length);

        var voiceKey = alice.NextKeyMessage();
        CollectionAssert.AreEqual(new[] { BobGuid }, voiceKey.Keys.Keys.ToArray());
        Assert.AreEqual(alice.Session.CurrentTransmission.TxIdBase64, voiceKey.TxId);

        Assert.IsTrue(bob.Session.HandleVoiceKey(ForwardTo(voiceKey, BobGuid), T0));
        Assert.IsFalse(bob.Session.HandleVoiceKey(ForwardTo(voiceKey, BobGuid), T0), "already known");
        Assert.IsFalse(eve.Session.HandleVoiceKey(ForwardTo(voiceKey, BobGuid), T0), "not addressed to Eve");

        // Bob's wrapped key relabelled for Eve does not open for her
        var stolen = ForwardTo(voiceKey, BobGuid);
        stolen.Keys = new Dictionary<string, string> { [EveGuid] = voiceKey.Keys[BobGuid] };
        Assert.IsFalse(eve.Session.HandleVoiceKey(stolen, T0));

        var frames = new List<ReceivedVoiceFrame>();
        bob.Session.Receiver.Receive(OverTheWire(packet), T0, frames);
        Assert.HasCount(1, frames);
        CollectionAssert.AreEqual(Opus(seed: 1), frames[0].Opus, "Bob hears Alice");

        frames.Clear();
        eve.Session.Receiver.Receive(OverTheWire(packet), T0, frames);
        eve.Session.Receiver.Poll(T0.AddSeconds(1), frames);
        Assert.IsTrue(frames.All(f => f.Scrambled), "Eve (if she got the packet at all) only hears noise");

        // 27.185 is no radio check frequency: the server never echoes it, so Alice does not keep K for herself
        Assert.IsFalse(alice.Session.Receiver.Keys.Contains(AliceGuid, alice.Session.CurrentTransmission.TxId, T0));

        // next frame of the same transmission: no new key message
        Assert.IsNotNull(alice.Session.CreateVoicePacket(Opus(seed: 2), 2, Cb19, Modulation.AM, T0.AddMilliseconds(40)));
        alice.ExpectNoKeyMessage();

        // Eve tunes in during the transmission. Key messages are spaced: not before 125 ms after the last one.
        clients[EveGuid].RadioInfo = Tuned(Cb19, Modulation.AM);
        alice.Session.CreateVoicePacket(Opus(seed: 3), 3, Cb19, Modulation.AM, T0.AddMilliseconds(80));
        alice.ExpectNoKeyMessage();
        var packet4 = alice.Session.CreateVoicePacket(Opus(seed: 4), 4, Cb19, Modulation.AM, T0.AddMilliseconds(125));
        var newcomerKey = alice.NextKeyMessage();
        CollectionAssert.AreEqual(new[] { EveGuid }, newcomerKey.Keys.Keys.ToArray());
        Assert.AreEqual(voiceKey.TxId, newcomerKey.TxId, "same transmission");

        Assert.IsTrue(eve.Session.HandleVoiceKey(ForwardTo(newcomerKey, EveGuid), T0));
        frames.Clear();
        eve.Session.Receiver.Receive(OverTheWire(packet4), T0.AddMilliseconds(130), frames);
        Assert.HasCount(1, frames);
        CollectionAssert.AreEqual(Opus(seed: 4), frames[0].Opus, "Eve hears the rest of the transmission");

        // PTT released and pressed again: a new transmission with a new key for both listeners
        var oldTxId = alice.Session.CurrentTransmission.TxId;
        alice.Session.EndTransmission();
        Assert.IsNull(alice.Session.CurrentTransmission);
        alice.Session.CreateVoicePacket(Opus(seed: 5), 5, Cb19, Modulation.AM, T0.AddSeconds(1));
        var secondKey = alice.NextKeyMessage();
        CollectionAssert.AreEquivalent(new[] { BobGuid, EveGuid }, secondKey.Keys.Keys.ToArray());
        Assert.AreNotEqual(oldTxId, alice.Session.CurrentTransmission.TxId);

        // tuning while transmitting starts a new transmission as well
        var beforeTune = alice.Session.CurrentTransmission.TxId;
        alice.Session.CreateVoicePacket(Opus(seed: 6), 6, Cb19 + 1000, Modulation.AM, T0.AddSeconds(2));
        Assert.AreNotEqual(beforeTune, alice.Session.CurrentTransmission.TxId);

        // unknown senders and closed sessions
        var unknown = ForwardTo(voiceKey, BobGuid);
        unknown.SenderGuid = CarolGuid;
        Assert.IsFalse(bob.Session.HandleVoiceKey(unknown, T0));

        alice.Session.Dispose();
        Assert.IsNull(alice.Session.CreateVoicePacket(Opus(), 7, Cb19, Modulation.AM, T0.AddSeconds(3)), "fail closed");
        Assert.IsNull(alice.Session.CurrentTransmission);
        Assert.AreEqual(0, alice.Session.Receiver.Keys.Count, "every key is gone with the connection");

        frames.Clear();
        alice.Session.Receiver.Receive(OverTheWire(packet), T0, frames);
        alice.Session.Receiver.Poll(T0.AddSeconds(1), frames);
        Assert.IsEmpty(frames);
        Assert.AreEqual(0, alice.Session.Receiver.HeldCount);
        Assert.IsFalse(alice.Session.HandleVoiceKey(ForwardTo(secondKey, BobGuid), T0));
    }

    [TestMethod]
    public void OwnKeyIsKeptOnlyOnRadioCheckFrequencies()
    {
        var clients = new ConcurrentDictionary<string, ClientInfo>
        {
            [AliceGuid] = Client(AliceGuid, _alice.PublicKey, RadioCheck, Modulation.AM)
        };
        using var alice = new Peer(AliceGuid, _alice, clients);

        // radio check: Alice decrypts her own echo with the key she knows
        var echo = alice.Session.CreateVoicePacket(Opus(seed: 1), 1, RadioCheck, Modulation.AM, T0);
        var frames = new List<ReceivedVoiceFrame>();
        alice.Session.Receiver.Receive(OverTheWire(echo), T0, frames);
        Assert.HasCount(1, frames);
        CollectionAssert.AreEqual(Opus(seed: 1), frames[0].Opus);

        // any other frequency: K stays out of her receive cache
        alice.Session.CreateVoicePacket(Opus(seed: 2), 2, Cb19, Modulation.AM, T0.AddSeconds(1));
        Assert.IsFalse(alice.Session.Receiver.Keys.Contains(AliceGuid, alice.Session.CurrentTransmission.TxId, T0));
        Assert.AreEqual(1, alice.Session.Receiver.Keys.Count, "only the radio check transmission");

        // a broken frequency check fails closed (no echo, no K in the cache)
        using var broken = new E2EVoiceSession(AliceGuid, _alice, clients, _ => System.Threading.Tasks.Task.CompletedTask,
            _ => throw new InvalidOperationException("settings unavailable"));
        Assert.IsNotNull(broken.CreateVoicePacket(Opus(), 1, RadioCheck, Modulation.AM, T0));
        Assert.AreEqual(0, broken.Receiver.Keys.Count);
    }

    [TestMethod]
    public void ListenerReconnectingDuringATransmissionGetsTheKeyAgain()
    {
        var clients = new ConcurrentDictionary<string, ClientInfo>
        {
            [AliceGuid] = Client(AliceGuid, _alice.PublicKey, Cb19, Modulation.AM),
            [BobGuid] = Client(BobGuid, _bob.PublicKey, Cb19, Modulation.AM)
        };
        using var alice = new Peer(AliceGuid, _alice, clients);
        var bob = new Peer(BobGuid, _bob, clients);

        var packet1 = alice.Session.CreateVoicePacket(Opus(seed: 1), 1, Cb19, Modulation.AM, T0);
        var key1 = alice.NextKeyMessage();
        Assert.IsTrue(bob.Session.HandleVoiceKey(ForwardTo(key1, BobGuid), T0));
        var frames = new List<ReceivedVoiceFrame>();
        bob.Session.Receiver.Receive(OverTheWire(packet1), T0, frames);
        Assert.HasCount(1, frames);

        // Bob's connection drops: CLIENT_DISCONNECT
        bob.Dispose();
        clients.TryRemove(BobGuid, out _);
        alice.Session.ForgetClient(BobGuid);
        alice.Session.CreateVoicePacket(Opus(seed: 2), 2, Cb19, Modulation.AM, T0.AddMilliseconds(200));
        alice.ExpectNoKeyMessage();

        // ... and he is back in the same app run (same key pair) while Alice still talks: he gets K again
        clients[BobGuid] = Client(BobGuid, _bob.PublicKey, Cb19, Modulation.AM);
        var packet3 = alice.Session.CreateVoicePacket(Opus(seed: 3), 3, Cb19, Modulation.AM, T0.AddMilliseconds(400));
        var key2 = alice.NextKeyMessage();
        CollectionAssert.AreEqual(new[] { BobGuid }, key2.Keys.Keys.ToArray());
        Assert.AreEqual(key1.TxId, key2.TxId, "the same transmission");
        Assert.AreEqual(key1.Keys[BobGuid], key2.Keys[BobGuid], "wrapped again: the identical blob");

        using var bob2 = new Peer(BobGuid, _bob, clients);
        Assert.IsTrue(bob2.Session.HandleVoiceKey(ForwardTo(key2, BobGuid), T0.AddMilliseconds(400)));
        frames.Clear();
        bob2.Session.Receiver.Receive(OverTheWire(packet3), T0.AddMilliseconds(400), frames);
        Assert.HasCount(1, frames);
        CollectionAssert.AreEqual(Opus(seed: 3), frames[0].Opus, "Bob hears the rest of the transmission");

        // the id is taken over between two frames (CLIENT_DISCONNECT, then the new connection's announcement with the
        // same radio): forgetting is enough to wrap K again
        alice.Session.ForgetClient(BobGuid);
        alice.Session.CreateVoicePacket(Opus(seed: 4), 4, Cb19, Modulation.AM, T0.AddMilliseconds(600));
        CollectionAssert.AreEqual(new[] { BobGuid }, alice.NextKeyMessage().Keys.Keys.ToArray());
    }

    [TestMethod]
    public void AnOldTransmissionCanNotBeReplayedAfterAReconnect()
    {
        using var bobKeys = E2EKeyPair.Create(); // this app run's key pair
        var clients = new ConcurrentDictionary<string, ClientInfo>
        {
            [AliceGuid] = Client(AliceGuid, _alice.PublicKey, Cb19, Modulation.AM),
            [BobGuid] = Client(BobGuid, bobKeys.PublicKey, Cb19, Modulation.AM)
        };
        using var alice = new Peer(AliceGuid, _alice, clients);

        var packets = new List<UDPVoicePacket>();
        for (var n = 1; n <= 3; n++)
            packets.Add(OverTheWire(alice.Session.CreateVoicePacket(Opus(seed: n), (ulong)n, Cb19, Modulation.AM,
                T0.AddMilliseconds(40 * n))));
        var voiceKey = ForwardTo(alice.NextKeyMessage(), BobGuid);

        var frames = new List<ReceivedVoiceFrame>();
        using (var bob = new Peer(BobGuid, bobKeys, clients))
        {
            Assert.IsTrue(bob.Session.HandleVoiceKey(voiceKey, T0));
            foreach (var packet in packets) bob.Session.Receiver.Receive(packet, T0.AddMilliseconds(200), frames);
            Assert.HasCount(3, frames);
        }

        // a new connection in the same app run: a fresh key cache - but the server can't play the recording again
        using (var bob = new Peer(BobGuid, bobKeys, clients))
        {
            // the key itself is accepted (the running transmission, wrapped again for a returning listener, looks
            // exactly like this) ...
            Assert.IsTrue(bob.Session.HandleVoiceKey(voiceKey, T0.AddSeconds(10)));

            // ... but frames that were played already are not played again
            frames.Clear();
            foreach (var packet in packets) bob.Session.Receiver.Receive(packet, T0.AddSeconds(10), frames);
            bob.Session.Receiver.Poll(T0.AddSeconds(11), frames);
            Assert.IsEmpty(frames);
            Assert.AreEqual(0, bob.Session.Receiver.HeldCount);

            // the rest of the transmission is
            var next = OverTheWire(alice.Session.CreateVoicePacket(Opus(seed: 4), 4, Cb19, Modulation.AM,
                T0.AddSeconds(10)));
            bob.Session.Receiver.Receive(next, T0.AddSeconds(10), frames);
            Assert.HasCount(1, frames);
            CollectionAssert.AreEqual(Opus(seed: 4), frames[0].Opus);
        }

        // once the transmission is over (no activity for 5 minutes) its key is not accepted any more
        using (var bob = new Peer(BobGuid, bobKeys, clients))
        {
            var late = T0.AddSeconds(10) + TransmissionReplayGuard.MaxIdleTime + TimeSpan.FromSeconds(1);
            Assert.IsFalse(bob.Session.HandleVoiceKey(voiceKey, late));
            Assert.AreEqual(0, bob.Session.Receiver.Keys.Count);
        }
    }

    [TestMethod]
    public void ReplayGuardIsBoundedPerSenderAndInTotal()
    {
        var guard = new TransmissionReplayGuard();

        var first = guard.Register(_alice.PublicKey, 1, T0);
        Assert.IsNotNull(first);
        Assert.AreSame(first, guard.Register(_alice.PublicKey, 1, T0.AddMinutes(4)), "known and recent: the same state");
        Assert.IsNull(guard.Register(_alice.PublicKey, 1, T0 + TransmissionReplayGuard.MaxIdleTime + TimeSpan.FromTicks(1)),
            "known and over");
        Assert.IsNull(guard.Register(null, 1, T0));

        // played frames keep it alive
        first.Touch(T0.AddMinutes(4));
        Assert.AreSame(first, guard.Register(_alice.PublicKey, 1, T0.AddMinutes(8)));

        for (ulong txId = 2; txId <= TransmissionReplayGuard.MaxTransmissionsPerSender + 10; txId++)
            Assert.IsNotNull(guard.Register(_alice.PublicKey, txId, T0.AddMinutes(8).AddMilliseconds(txId)));
        Assert.AreEqual(TransmissionReplayGuard.MaxTransmissionsPerSender, guard.Count);

        for (var sender = 0; sender < TransmissionReplayGuard.MaxSenders + 5; sender++)
            Assert.IsNotNull(guard.Register("sender" + sender, 1, T0.AddMinutes(9).AddMilliseconds(sender)));
        Assert.IsLessThanOrEqualTo(TransmissionReplayGuard.MaxTransmissionsPerSender + TransmissionReplayGuard.MaxSenders,
            guard.Count);
        Assert.IsNotNull(guard.Register("sender" + (TransmissionReplayGuard.MaxSenders + 4), 1, T0.AddMinutes(9)));
    }

    [TestMethod]
    public void EcdhWithKnownClientsIsPreparedInTheBackground()
    {
        using var keys = E2EKeyPair.Create();
        var clients = new ConcurrentDictionary<string, ClientInfo>
        {
            [AliceGuid] = Client(AliceGuid, keys.PublicKey, Cb19, Modulation.AM), // itself
            [BobGuid] = Client(BobGuid, _bob.PublicKey, Pmr, Modulation.FM), // any frequency
            [CarolGuid] = Client(CarolGuid, null, Cb19, Modulation.AM),
            [EveGuid] = Client(EveGuid, OffCurveKey(), Cb19, Modulation.AM)
        };
        using var session = new E2EVoiceSession(AliceGuid, keys, clients, _ => System.Threading.Tasks.Task.CompletedTask);

        session.PrepareKeys(clients.Values);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (keys.CachedPeerCount == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Thread.Sleep(100);
        Assert.AreEqual(1, keys.CachedPeerCount, "only Bob's key is usable");
    }

    [TestMethod]
    public void ForgottenSenderNeedsANewKey()
    {
        var clients = new ConcurrentDictionary<string, ClientInfo>
        {
            [AliceGuid] = Client(AliceGuid, _alice.PublicKey, Cb19, Modulation.AM),
            [BobGuid] = Client(BobGuid, _bob.PublicKey, Cb19, Modulation.AM)
        };

        using var alice = new Peer(AliceGuid, _alice, clients);
        using var bob = new Peer(BobGuid, _bob, clients);

        var packet = alice.Session.CreateVoicePacket(Opus(), 1, Cb19, Modulation.AM, T0);
        Assert.IsTrue(bob.Session.HandleVoiceKey(ForwardTo(alice.NextKeyMessage(), BobGuid), T0));

        bob.Session.ForgetClient(AliceGuid); // Alice left

        var frames = new List<ReceivedVoiceFrame>();
        bob.Session.Receiver.Receive(OverTheWire(packet), T0, frames);
        Assert.IsEmpty(frames);
        Assert.AreEqual(1, bob.Session.Receiver.HeldCount);
    }
}
