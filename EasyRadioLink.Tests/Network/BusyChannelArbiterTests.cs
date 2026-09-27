using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Server;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

/// <summary>
///     The server side of the busy channel lockout ("one speaker per frequency"): <see cref="BusyChannelArbiter" />,
///     <see cref="VoiceRouting.PassesBusyChannelLockout" /> and the BUSY_CHANNEL_LOCKOUT setting.
/// </summary>
[TestClass]
public class BusyChannelArbiterTests
{
    private const double Cb19 = 27185000;
    private const double RadioCheck = 27405000;
    private const double Pmr1 = 446006250;
    private const int Hang = BusyChannelArbiter.HangTimeMilliseconds;

    private const string Alice = "Alice_________________";
    private const string Bob = "Bob___________________";
    private const string Carol = "Carol_________________";

    private static bool Send(BusyChannelArbiter arbiter, string sender, double frequency, Modulation modulation,
        long now)
    {
        return arbiter.Allow(sender, new[] { frequency }, new[] { (byte)modulation }, now);
    }

    private static UDPVoicePacket Packet(string sender, double frequency, Modulation modulation)
    {
        return new UDPVoicePacket
        {
            Guid = sender,
            OriginalClientGuid = sender,
            Frequencies = new[] { frequency },
            Modulations = new[] { (byte)modulation },
            Encryptions = new byte[] { 0 }
        };
    }

    [TestMethod]
    public void TheClockOfTheLockoutIsMonotonicAndFine()
    {
        // not Environment.TickCount64 (steps of ~15.6 ms on Windows): the hang time must be 300 ms, not 285 - 315 ms
        var previous = BusyChannelArbiter.NowMilliseconds;
        var steps = new List<long>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (steps.Count < 20 && DateTime.UtcNow < deadline)
        {
            var now = BusyChannelArbiter.NowMilliseconds;
            Assert.IsGreaterThanOrEqualTo(previous, now, "monotonic");
            if (now != previous) steps.Add(now - previous);
            previous = now;
        }

        // a thread that is descheduled sees bigger steps, never smaller ones
        Assert.HasCount(20, steps);
        Assert.IsLessThanOrEqualTo(2L, steps.Min(), "1 ms steps (" + string.Join(", ", steps) + ")");
    }

    [TestMethod]
    public void TheFirstSpeakerHoldsTheChannelAndTheOthersAreDropped()
    {
        var arbiter = new BusyChannelArbiter();

        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 1000), "Alice takes 27.185 AM");
        Assert.IsFalse(Send(arbiter, Bob, Cb19, Modulation.AM, 1010), "Bob's packet on the busy channel is dropped");
        Assert.IsTrue(Send(arbiter, Carol, Pmr1, Modulation.FM, 1020), "Carol on another frequency is not affected");
        Assert.IsFalse(Send(arbiter, Bob, Cb19, Modulation.AM, 1050));

        Assert.AreEqual(2, arbiter.Drops);
        Assert.AreEqual(2, arbiter.ActiveChannels(1050), "27.185 AM (Alice) and 446.00625 FM (Carol)");
    }

    [TestMethod]
    public void TheHoldersOwnPacketsAlwaysPass()
    {
        var arbiter = new BusyChannelArbiter();

        // Alice talks for three seconds (a frame every 40 ms), Bob keeps trying in between
        for (long now = 0; now < 3000; now += 40)
        {
            Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, now), $"Alice at {now} ms");
            Assert.IsFalse(Send(arbiter, Bob, Cb19, Modulation.AM, now + 5), $"Bob at {now + 5} ms");
        }

        // a gap inside the transmission (network jitter) shorter than the hang time: still Alice's channel
        Assert.IsFalse(Send(arbiter, Bob, Cb19, Modulation.AM, 2960 + Hang - 1));
        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 2960 + Hang - 1));
    }

    [TestMethod]
    public void AnotherStationTakesOverAfterTheHangTime()
    {
        var arbiter = new BusyChannelArbiter();
        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 1000));

        Assert.IsFalse(Send(arbiter, Bob, Cb19, Modulation.AM, 1000 + Hang - 1), "still within the hang time");
        Assert.IsTrue(Send(arbiter, Bob, Cb19, Modulation.AM, 1000 + Hang), "Alice silent for 300 ms: Bob takes over");

        // now Bob holds the channel and Alice is locked out
        Assert.IsFalse(Send(arbiter, Alice, Cb19, Modulation.AM, 1000 + Hang + 40));
        Assert.IsTrue(Send(arbiter, Bob, Cb19, Modulation.AM, 1000 + Hang + 40));
    }

    [TestMethod]
    public void ADifferentModulationOnTheSameFrequencyIsAnotherChannel()
    {
        var arbiter = new BusyChannelArbiter();

        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 0));
        Assert.IsTrue(Send(arbiter, Bob, Cb19, Modulation.FM, 10), "27.185 FM is not 27.185 AM");
        Assert.IsTrue(Send(arbiter, Carol, Cb19, Modulation.DIGITAL, 20));
        Assert.IsFalse(Send(arbiter, Carol, Cb19, Modulation.AM, 30));
        Assert.AreEqual(3, arbiter.ActiveChannels(30));
    }

    [TestMethod]
    public void TheChannelIsEveryFrequencyARadioWouldHear()
    {
        var arbiter = new BusyChannelArbiter();
        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 0));

        // within RadioBase.FreqCloseEnough (500 Hz) the radios hear each other - the same channel
        Assert.IsFalse(Send(arbiter, Bob, Cb19 + 400, Modulation.AM, 10));
        Assert.IsFalse(Send(arbiter, Bob, Cb19 - 499, Modulation.AM, 10));
        Assert.IsTrue(Send(arbiter, Carol, Cb19 + 10000, Modulation.AM, 10), "CB channel 20 is free");
    }

    [TestMethod]
    public void RadioCheckFrequenciesAreLockedOutLikeAnyOtherFrequency()
    {
        var arbiter = new BusyChannelArbiter();

        Assert.IsTrue(Send(arbiter, Alice, RadioCheck, Modulation.AM, 0));
        Assert.IsFalse(Send(arbiter, Bob, RadioCheck, Modulation.AM, 20), "Bob's radio check waits for Alice's");
        Assert.IsTrue(Send(arbiter, Bob, RadioCheck, Modulation.AM, Hang + 1));
    }

    [TestMethod]
    public void APacketOnSeveralFrequenciesIsDroppedIfOneOfThemIsBusy()
    {
        var arbiter = new BusyChannelArbiter();
        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 0));

        var bothFrequencies = new[] { Pmr1, Cb19 };
        var modulations = new[] { (byte)Modulation.FM, (byte)Modulation.AM };
        Assert.IsFalse(arbiter.Allow(Bob, bothFrequencies, modulations, 10), "27.185 AM is busy");

        // the free first frequency alone is fine - and Bob holds it now
        Assert.IsTrue(Send(arbiter, Bob, Pmr1, Modulation.FM, 20));
        Assert.IsFalse(Send(arbiter, Carol, Pmr1, Modulation.FM, 30));
    }

    [TestMethod]
    public void EverySenderHoldsOneChannelAtMost()
    {
        var arbiter = new BusyChannelArbiter();

        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 0));
        Assert.AreEqual(1, arbiter.TableSize);

        // Alice retunes while talking: 27.185 is free at once, she holds 446.00625 FM now
        Assert.IsTrue(Send(arbiter, Alice, Pmr1, Modulation.FM, 40));
        Assert.AreEqual(1, arbiter.TableSize);
        Assert.IsTrue(Send(arbiter, Bob, Cb19, Modulation.AM, 50));
        Assert.IsFalse(Send(arbiter, Carol, Pmr1, Modulation.FM, 60));
        Assert.AreEqual(2, arbiter.TableSize);
    }

    [TestMethod]
    public void SilentChannelsArePrunedFromTheTable()
    {
        var arbiter = new BusyChannelArbiter();

        // 50 stations on 50 frequencies
        for (var i = 0; i < 50; i++)
            Assert.IsTrue(Send(arbiter, $"Station{i:D2}".PadRight(22, '_'), 400000000 + i * 12500d, Modulation.FM, i));

        Assert.AreEqual(50, arbiter.TableSize);
        Assert.AreEqual(50, arbiter.ActiveChannels(49));
        Assert.AreEqual(0, arbiter.ActiveChannels(49 + Hang), "all silent for the hang time");

        // the next packet prunes the expired entries
        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 49 + Hang));
        Assert.AreEqual(1, arbiter.TableSize);
        Assert.AreEqual(1, arbiter.ActiveChannels(49 + Hang));
    }

    [TestMethod]
    public void TheTableIsBounded()
    {
        var arbiter = new BusyChannelArbiter();

        for (var i = 0; i < BusyChannelArbiter.MaxChannels; i++)
            Assert.IsTrue(arbiter.Allow("S" + i.ToString("D21"), new[] { 1000000 + i * 1000d },
                new[] { (byte)Modulation.AM }, 0));

        Assert.AreEqual(BusyChannelArbiter.MaxChannels, arbiter.TableSize);

        // full: forwarded, but not held (never happens with the server's connection limit)
        Assert.IsTrue(Send(arbiter, Alice, 999000000, Modulation.FM, 1));
        Assert.AreEqual(BusyChannelArbiter.MaxChannels, arbiter.TableSize);
        Assert.IsTrue(Send(arbiter, Bob, 999000000, Modulation.FM, 2));
    }

    [TestMethod]
    public void AHolderWhoLeftFreesTheChannelAtOnce()
    {
        var connected = new HashSet<string> { Alice, Bob };
        var arbiter = new BusyChannelArbiter(connected.Contains);

        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 0));
        Assert.IsFalse(Send(arbiter, Bob, Cb19, Modulation.AM, 10));

        connected.Remove(Alice); // disconnected in the middle of the transmission
        Assert.IsTrue(Send(arbiter, Bob, Cb19, Modulation.AM, 20), "no need to wait for the hang time");
        Assert.AreEqual(1, arbiter.TableSize, "Alice's entry is gone");
        Assert.IsFalse(Send(arbiter, Carol, Cb19, Modulation.AM, 30), "Bob holds the channel now");
    }

    [TestMethod]
    public void PacketsRoutedSlightlyOutOfOrderDoNotShortenTheHangTime()
    {
        var arbiter = new BusyChannelArbiter();

        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 1000));
        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 990), "an older clock value of a parallel task");
        Assert.IsFalse(Send(arbiter, Bob, Cb19, Modulation.AM, 995));
        Assert.IsFalse(Send(arbiter, Bob, Cb19, Modulation.AM, 1000 + Hang - 1), "the newest packet counts");
    }

    [TestMethod]
    public void ClearForgetsEveryChannel()
    {
        var arbiter = new BusyChannelArbiter();
        Assert.IsTrue(Send(arbiter, Alice, Cb19, Modulation.AM, 0));

        arbiter.Clear();

        Assert.AreEqual(0, arbiter.TableSize);
        Assert.IsTrue(Send(arbiter, Bob, Cb19, Modulation.AM, 10));
    }

    [TestMethod]
    public void InvalidInputIsNotLockedOut()
    {
        var arbiter = new BusyChannelArbiter();

        Assert.IsTrue(arbiter.Allow(null, new[] { Cb19 }, new[] { (byte)Modulation.AM }, 0));
        Assert.IsTrue(arbiter.Allow(Alice, Array.Empty<double>(), Array.Empty<byte>(), 0));
        Assert.IsTrue(arbiter.Allow(Alice, null, null, 0));
        Assert.AreEqual(0, arbiter.TableSize);
    }

    [TestMethod]
    public void TheHoldersPacketsDoNotAllocate()
    {
        var arbiter = new BusyChannelArbiter(_ => true);
        var aliceFrequencies = new[] { Cb19 };
        var bobFrequencies = new[] { Cb19 };
        var modulations = new[] { (byte)Modulation.AM };
        var others = new[] { Pmr1 };
        var fm = new[] { (byte)Modulation.FM };

        // warm up (JIT) and let a few stations hold channels
        for (var i = 0; i < 200; i++)
        {
            arbiter.Allow(Alice, aliceFrequencies, modulations, i);
            arbiter.Allow(Bob, bobFrequencies, modulations, i);
            arbiter.Allow(Carol, others, fm, i);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 200; i < 2200; i++)
        {
            arbiter.Allow(Alice, aliceFrequencies, modulations, i);
            arbiter.Allow(Bob, bobFrequencies, modulations, i);
            arbiter.Allow(Carol, others, fm, i);
        }

        Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before, "no allocations per packet");
    }

    // --- the router's decision ------------------------------------------------------------------------------------

    [TestMethod]
    public void WithTheSettingOffEveryPacketPasses()
    {
        var arbiter = new BusyChannelArbiter();
        var alice = new ClientInfo { ClientGuid = Alice };
        var bob = new ClientInfo { ClientGuid = Bob };

        Assert.IsTrue(VoiceRouting.PassesBusyChannelLockout(arbiter, false, alice, Packet(Alice, Cb19, Modulation.AM), 0));
        Assert.IsTrue(VoiceRouting.PassesBusyChannelLockout(arbiter, false, bob, Packet(Bob, Cb19, Modulation.AM), 10),
            "both delivered, as before 1.3");
        Assert.AreEqual(0, arbiter.TableSize, "the table is not even used");

        Assert.IsTrue(VoiceRouting.PassesBusyChannelLockout(arbiter, true, alice, Packet(Alice, Cb19, Modulation.AM), 20));
        Assert.IsFalse(VoiceRouting.PassesBusyChannelLockout(arbiter, true, bob, Packet(Bob, Cb19, Modulation.AM), 30));
    }

    // --- the setting ---------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheSettingIsOnByDefaultAndSentToTheClients()
    {
        Assert.AreEqual("true", DefaultServerSettings.Defaults[nameof(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT)]);
        CollectionAssert.Contains(new List<ServerSettingsKeys>(DefaultServerSettings.BroadcastKeys),
            ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT);

        var directory = Path.Combine(Path.GetTempPath(), "erl-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ServerSettingsStore(Path.Combine(directory, "server.cfg"));
            Assert.IsTrue(store.GetGeneralSetting(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT).BoolValue);
            Assert.AreEqual("true", store.ToDictionary()[nameof(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT)]);

            store.SetGeneralSetting(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT, false);
            var reloaded = new ServerSettingsStore(Path.Combine(directory, "server.cfg"));
            Assert.IsFalse(reloaded.GetGeneralSetting(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT).BoolValue,
                "saved to server.cfg");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // best effort (temp folder)
            }
        }
    }

    [TestMethod]
    public void ClientsSeeTheLockoutOnlyIfTheServerSendsIt()
    {
        var settings = new SyncedServerSettings();

        // not connected / a server before 1.3: it does not send the setting and locks nothing out
        Assert.IsFalse(settings.BusyChannelLockout);
        settings.Decode(new Dictionary<string, string> { { nameof(ServerSettingsKeys.SHOW_TUNED_COUNT), "true" } },
            false);
        Assert.IsFalse(settings.BusyChannelLockout, "older server");
        Assert.IsTrue(settings.GetSettingAsBool(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT), "the default is still on");

        settings.Decode(new Dictionary<string, string> { { nameof(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT), "True" } },
            false);
        Assert.IsTrue(settings.BusyChannelLockout);

        settings.Decode(new Dictionary<string, string> { { nameof(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT), "False" } },
            false);
        Assert.IsFalse(settings.BusyChannelLockout, "switched off in the server window");

        settings.Decode(new Dictionary<string, string> { { nameof(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT), "true" } },
            false);
        settings.Reset();
        Assert.IsFalse(settings.BusyChannelLockout, "forgotten on disconnect");
    }
}
