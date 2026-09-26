using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyRadioLink.Common.Network.Crypto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

/// <summary>Trust-on-first-use pins of server identities (known-servers.json).</summary>
[TestClass]
public class KnownServersStoreTests
{
    private static readonly string FingerprintA = ServerIdentity.FormatFingerprint(SHA256.HashData("server A"u8));
    private static readonly string FingerprintB = ServerIdentity.FormatFingerprint(SHA256.HashData("server B"u8));

    private string _directory;

    private string FilePath => Path.Combine(_directory, KnownServersStore.FileName);

    [TestInitialize]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "erl-known-servers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void DeleteDirectory()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [TestMethod]
    public void FirstUseThenMatch()
    {
        var store = new KnownServersStore(FilePath);
        var key = KnownServersStore.ServerKey("radio.example.org", 5010);

        Assert.AreEqual(ServerPinStatus.FirstUse, store.Check(key, FingerprintA, out var pinned));
        Assert.IsNull(pinned);
        Assert.IsFalse(File.Exists(FilePath), "checking alone never pins");

        store.Trust(key, FingerprintA);

        Assert.AreEqual(ServerPinStatus.Match, store.Check(key, FingerprintA, out pinned));
        Assert.AreEqual(FingerprintA, pinned);

        // any notation of the same fingerprint matches
        Assert.AreEqual(ServerPinStatus.Match, store.Check(key, FingerprintA.Replace(":", "").ToLowerInvariant(), out _));
    }

    [TestMethod]
    public void MismatchKeepsThePinUntilTheUserTrustsTheNewIdentity()
    {
        var store = new KnownServersStore(FilePath);
        var key = KnownServersStore.ServerKey("radio.example.org", 5010);
        store.Trust(key, FingerprintA);

        Assert.AreEqual(ServerPinStatus.Mismatch, store.Check(key, FingerprintB, out var pinned));
        Assert.AreEqual(FingerprintA, pinned);
        Assert.AreEqual(ServerPinStatus.Mismatch, store.Check(key, "garbage", out _), "an invalid fingerprint never matches");
        Assert.AreEqual(ServerPinStatus.Mismatch, store.Check(key, null, out _));

        // a mismatch alone changes nothing
        Assert.AreEqual(FingerprintA, store.GetPinned(key));

        // "Connect anyway and trust the new identity"
        store.Trust(key, FingerprintB);
        Assert.AreEqual(ServerPinStatus.Match, store.Check(key, FingerprintB, out _));
        Assert.AreEqual(ServerPinStatus.Mismatch, store.Check(key, FingerprintA, out _));
    }

    [TestMethod]
    public void PinsArePerHostAndPort()
    {
        var store = new KnownServersStore(FilePath);
        store.Trust(KnownServersStore.ServerKey("radio.example.org", 5010), FingerprintA);

        Assert.AreEqual(ServerPinStatus.FirstUse,
            store.Check(KnownServersStore.ServerKey("radio.example.org", 5011), FingerprintB, out _));
        Assert.AreEqual(ServerPinStatus.FirstUse,
            store.Check(KnownServersStore.ServerKey("other.example.org", 5010), FingerprintB, out _));
        Assert.AreEqual(ServerPinStatus.Match,
            store.Check(KnownServersStore.ServerKey("RADIO.Example.org.", 5010), FingerprintA, out _));
    }

    [TestMethod]
    public void ServerKeyNormalisation()
    {
        Assert.AreEqual("radio.example.org:5010", KnownServersStore.ServerKey(" Radio.Example.ORG ", 5010));
        Assert.AreEqual("127.0.0.1:5010", KnownServersStore.ServerKey("127.0.0.1", 5010));
        Assert.AreEqual("[::1]:5010", KnownServersStore.ServerKey("::1", 5010));
        Assert.AreEqual("[::1]:5010", KnownServersStore.ServerKey("[::1]", 5010));

        Assert.ThrowsExactly<ArgumentException>(() => KnownServersStore.ServerKey(" ", 5010));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => KnownServersStore.ServerKey("a", 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => KnownServersStore.ServerKey("a", 65536));
    }

    [TestMethod]
    public void PinsSurviveARestartAndAreSharedBetweenInstances()
    {
        var key = KnownServersStore.ServerKey("radio.example.org", 5010);
        new KnownServersStore(FilePath).Trust(key, FingerprintA);

        var other = new KnownServersStore(FilePath);
        Assert.AreEqual(ServerPinStatus.Match, other.Check(key, FingerprintA, out _));

        var json = File.ReadAllText(FilePath);
        StringAssert.Contains(json, "\"radio.example.org:5010\"");
        StringAssert.Contains(json, FingerprintA);
        Assert.IsFalse(File.Exists(FilePath + ".tmp"));
    }

    [TestMethod]
    public void TrustRejectsInvalidFingerprints()
    {
        var store = new KnownServersStore(FilePath);
        var key = KnownServersStore.ServerKey("radio.example.org", 5010);

        Assert.ThrowsExactly<ArgumentException>(() => store.Trust(key, "AB:CD"));
        Assert.ThrowsExactly<ArgumentException>(() => store.Trust(key, null));
        Assert.ThrowsExactly<ArgumentException>(() => store.Trust(null, FingerprintA));
        Assert.IsNull(store.GetPinned(key));
    }

    [TestMethod]
    [DataRow("{ this is not json", DisplayName = "not JSON")]
    [DataRow("{\"radio.example.org:5010\": \"AB:CD\"", DisplayName = "truncated")]
    [DataRow("[\"radio.example.org:5010\"]", DisplayName = "not an object")]
    [DataRow("null", DisplayName = "null")]
    [DataRow("", DisplayName = "empty")]
    public void DamagedFileMeansNoIdentityCanBeCheckedAndIsNeverOverwritten(string damaged)
    {
        File.WriteAllText(FilePath, damaged);
        var store = new KnownServersStore(FilePath);
        var key = KnownServersStore.ServerKey("radio.example.org", 5010);

        // never "first use" (that would pin whatever an attacker presents) - for every server
        Assert.AreEqual(ServerPinStatus.StoreUnreadable, store.Check(key, FingerprintA, out var pinned, out var problem));
        Assert.IsNull(pinned);
        StringAssert.Contains(problem, "damaged");
        Assert.AreEqual(ServerPinStatus.StoreUnreadable,
            store.Check(KnownServersStore.ServerKey("other.example.org", 5010), FingerprintB, out _));
        Assert.IsNull(store.GetPinned(key));

        // nothing writes it: the other pins it may still contain are not lost
        Assert.ThrowsExactly<KnownServersStoreException>(() => store.Trust(key, FingerprintA));
        Assert.ThrowsExactly<KnownServersStoreException>(() => store.Forget(key));
        Assert.AreEqual(ServerPinStatus.StoreUnreadable, store.TrustFirstUse(key, FingerprintA, out _));
        Assert.AreEqual(damaged, File.ReadAllText(FilePath), "the damaged file is kept as it is");
        Assert.IsFalse(File.Exists(FilePath + ".tmp"));

        // repaired (here: deleted by the user) - first use again
        File.Delete(FilePath);
        Assert.AreEqual(ServerPinStatus.FirstUse, store.Check(key, FingerprintA, out _));
    }

    [TestMethod]
    public void AnInvalidEntryMakesThatServerUnknownAndTheRestIsKeptVerbatim()
    {
        const string json = """
                            {
                              "bad:2": "12:34",
                              "number:3": 42,
                              "": "not a key",
                              "good:1": "FINGERPRINT_A",
                              "object:4": { "nested": [1, 2] }
                            }
                            """;
        File.WriteAllText(FilePath, json.Replace("FINGERPRINT_A", FingerprintA));
        var store = new KnownServersStore(FilePath);

        Assert.AreEqual(ServerPinStatus.Match, store.Check("good:1", FingerprintA, out _));
        Assert.AreEqual(ServerPinStatus.Unknown, store.Check("bad:2", FingerprintB, out var pinned, out var problem),
            "an unreadable pin is never a silent first use");
        Assert.IsNull(pinned);
        StringAssert.Contains(problem, "bad:2");
        Assert.AreEqual(ServerPinStatus.Unknown, store.Check("number:3", FingerprintB, out _));
        Assert.AreEqual(ServerPinStatus.Unknown, store.TrustFirstUse("bad:2", FingerprintB, out _), "not pinned silently");
        Assert.IsNull(store.GetPinned("bad:2"));
        Assert.AreEqual(ServerPinStatus.FirstUse, store.Check("new:5", FingerprintB, out _));

        // the user checked the fingerprint and trusts it: that entry is replaced, everything else stays as it was
        store.Trust("bad:2", FingerprintB);
        Assert.AreEqual(ServerPinStatus.Match, store.Check("bad:2", FingerprintB, out _));
        Assert.AreEqual(ServerPinStatus.Match, store.Check("good:1", FingerprintA, out _));

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        var root = written.RootElement;
        Assert.AreEqual(FingerprintB, root.GetProperty("bad:2").GetString());
        Assert.AreEqual(42, root.GetProperty("number:3").GetInt32());
        Assert.AreEqual("not a key", root.GetProperty("").GetString());
        Assert.AreEqual(2, root.GetProperty("object:4").GetProperty("nested").GetArrayLength());
        Assert.AreEqual(5, root.EnumerateObject().Count());
    }

    [TestMethod]
    public void AServerListedTwiceIsUnknown()
    {
        File.WriteAllText(FilePath, $"{{ \"dup:1\": \"{FingerprintA}\", \"dup:1\": \"{FingerprintA}\" }}");
        var store = new KnownServersStore(FilePath);

        Assert.AreEqual(ServerPinStatus.Unknown, store.Check("dup:1", FingerprintA, out _));

        store.Trust("dup:1", FingerprintA);
        Assert.AreEqual(ServerPinStatus.Match, store.Check("dup:1", FingerprintA, out _));
        Assert.AreEqual(1, JsonDocument.Parse(File.ReadAllText(FilePath)).RootElement.EnumerateObject().Count());
    }

    [TestMethod]
    public void AFileThatCantBeReadMeansNoIdentityCanBeChecked()
    {
        var store = new KnownServersStore(FilePath);
        var key = KnownServersStore.ServerKey("radio.example.org", 5010);
        store.Trust(key, FingerprintA);

        // another program holds the file exclusively (Windows sharing): the check fails closed instead of throwing
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.AreEqual(ServerPinStatus.StoreUnreadable, store.Check(key, FingerprintB, out _, out var problem));
            StringAssert.Contains(problem, "can't be read");
        }

        Assert.AreEqual(ServerPinStatus.Match, store.Check(key, FingerprintA, out _));
    }

    [TestMethod]
    public void TrustFirstUsePinsOnlyWhileThereIsNoEntry()
    {
        var store = new KnownServersStore(FilePath);
        var key = KnownServersStore.ServerKey("radio.example.org", 5010);

        Assert.AreEqual(ServerPinStatus.FirstUse, store.TrustFirstUse(key, FingerprintA, out var pinned));
        Assert.IsNull(pinned);
        Assert.AreEqual(FingerprintA, store.GetPinned(key));

        // another instance pinned it meanwhile: the same identity is fine, another one is a mismatch (nothing written)
        Assert.AreEqual(ServerPinStatus.Match, store.TrustFirstUse(key, FingerprintA, out _));
        Assert.AreEqual(ServerPinStatus.Mismatch, store.TrustFirstUse(key, FingerprintB, out pinned));
        Assert.AreEqual(FingerprintA, pinned);
        Assert.AreEqual(FingerprintA, store.GetPinned(key));
        Assert.ThrowsExactly<ArgumentException>(() => store.TrustFirstUse(key, "AB", out _));
    }

    [TestMethod]
    public void ChangesWaitForTheLockOfAnotherInstance()
    {
        var store = new KnownServersStore(FilePath);
        var key = KnownServersStore.ServerKey("radio.example.org", 5010);
        store.Trust(key, FingerprintA);

        // another client instance holds the lock file for a moment: the change waits instead of losing an update
        var lockFile = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var trust = Task.Run(() => store.Trust(KnownServersStore.ServerKey("other.example.org", 5010), FingerprintB));
        Thread.Sleep(300);
        Assert.IsFalse(trust.IsCompleted, "waits for the lock");
        lockFile.Dispose();

        Assert.IsTrue(trust.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(FingerprintA, store.GetPinned(key));
        Assert.AreEqual(FingerprintB, store.GetPinned(KnownServersStore.ServerKey("other.example.org", 5010)));
    }

    [TestMethod]
    public void ConcurrentChangesAreNotLost()
    {
        var keys = Enumerable.Range(1, 40).Select(i => KnownServersStore.ServerKey($"server{i}.example.org", 5010)).ToArray();

        // separate instances, as in separate client processes (they share the lock file and the file)
        Parallel.ForEach(keys, key => new KnownServersStore(FilePath).Trust(key, FingerprintA));

        var store = new KnownServersStore(FilePath);
        foreach (var key in keys) Assert.AreEqual(FingerprintA, store.GetPinned(key), key);
    }

    [TestMethod]
    public void ForgetRemovesAPin()
    {
        var store = new KnownServersStore(FilePath);
        var key = KnownServersStore.ServerKey("radio.example.org", 5010);
        store.Trust(key, FingerprintA);

        Assert.IsTrue(store.Forget(key));
        Assert.IsFalse(store.Forget(key));
        Assert.AreEqual(ServerPinStatus.FirstUse, store.Check(key, FingerprintB, out _));
    }

    [TestMethod]
    public void FingerprintFormatting()
    {
        var hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var formatted = ServerIdentity.FormatFingerprint(hash);

        Assert.AreEqual("00:01:02:03:04:05:06:07:08:09:0A:0B:0C:0D:0E:0F:10:11:12:13:14:15:16:17:18:19:1A:1B:1C:1D:1E:1F",
            formatted);
        Assert.IsTrue(ServerIdentity.TryParseFingerprint(formatted.Replace(':', ' ').ToLowerInvariant(), out var parsed));
        CollectionAssert.AreEqual(hash, parsed);
        Assert.IsTrue(ServerIdentity.FingerprintsEqual(formatted, Convert.ToHexString(hash)));

        Assert.IsFalse(ServerIdentity.TryParseFingerprint(formatted[..^3], out _), "31 bytes");
        Assert.IsFalse(ServerIdentity.TryParseFingerprint(formatted + ":20", out _), "33 bytes");
        Assert.IsFalse(ServerIdentity.TryParseFingerprint(formatted.Replace("1F", "1G"), out _));
        Assert.IsNull(ServerIdentity.NormaliseFingerprint(""));
        Assert.IsFalse(ServerIdentity.FingerprintsEqual(null, null));
    }
}
