using System.Collections.Generic;
using EasyRadioLink.Client.Audio.Managers;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Decision = EasyRadioLink.Client.Audio.Managers.BusyChannelLockout.Decision;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>
///     The client side of the busy channel lockout: busy detection, one decision per push-to-talk press, the lost
///     race, VOX and the BUSY indicator (<see cref="BusyChannelLockout" />). Times are milliseconds of a monotonic
///     clock; a mic frame is 40 ms.
/// </summary>
[TestClass]
public class BusyChannelLockoutTests
{
    private const double Cb19 = 27185000;
    private const double Cb20 = 27205000;
    private const string Own = "Me____________________";
    private const string Other = "Other_________________";
    private const int Hang = BusyChannelLockout.HangTimeMilliseconds;

    private static Decision Ptt(BusyChannelLockout lockout, long now, out bool tone, double frequency = Cb19,
        bool enabled = true)
    {
        return lockout.Decide(enabled, true, false, frequency, Modulation.AM, now, out tone);
    }

    private static Decision Vox(BusyChannelLockout lockout, long now, out bool tone)
    {
        return lockout.Decide(true, false, true, Cb19, Modulation.AM, now, out tone);
    }

    private static void Release(BusyChannelLockout lockout, long now)
    {
        Assert.AreEqual(Decision.Idle, lockout.Decide(true, false, false, Cb19, Modulation.AM, now, out var tone));
        Assert.IsFalse(tone);
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
    public void TheChannelIsBusyForTheHangTimeAfterAnotherStationWasHeard()
    {
        var lockout = new BusyChannelLockout();
        Assert.IsFalse(lockout.IsChannelBusy(Cb19, Modulation.AM, 0), "nothing heard yet");

        lockout.OnVoiceHeard(Cb19, Modulation.AM, 1000);

        Assert.IsTrue(lockout.IsChannelBusy(Cb19, Modulation.AM, 1000));
        Assert.IsTrue(lockout.IsChannelBusy(Cb19, Modulation.AM, 1000 + Hang - 1));
        Assert.IsFalse(lockout.IsChannelBusy(Cb19, Modulation.AM, 1000 + Hang), "free 300 ms after the last packet");

        // only that channel: within 500 Hz and the same modulation
        Assert.IsTrue(lockout.IsChannelBusy(Cb19 + 400, Modulation.AM, 1100));
        Assert.IsFalse(lockout.IsChannelBusy(Cb20, Modulation.AM, 1100));
        Assert.IsFalse(lockout.IsChannelBusy(Cb19, Modulation.FM, 1100));
    }

    [TestMethod]
    public void OnAFreeChannelPushToTalkTransmits()
    {
        var lockout = new BusyChannelLockout();

        for (long now = 0; now < 1000; now += 40)
        {
            Assert.AreEqual(Decision.Transmit, Ptt(lockout, now, out var tone));
            Assert.IsFalse(tone);
        }

        Release(lockout, 1000);
        Assert.IsFalse(lockout.IsIndicatorLit(Cb19, Modulation.AM, 1000));
    }

    [TestMethod]
    public void APressOnABusyChannelIsRefusedUntilItIsReleased()
    {
        var lockout = new BusyChannelLockout();

        // another station talks from 0 to 1000 ms
        for (long heard = 0; heard <= 1000; heard += 40) lockout.OnVoiceHeard(Cb19, Modulation.AM, heard);

        Assert.AreEqual(Decision.Blocked, Ptt(lockout, 500, out var tone), "pressed while the channel is busy");
        Assert.IsTrue(tone, "busy tone");
        Assert.IsTrue(lockout.IsPressRefused);

        // held on: nothing is transmitted for the whole press - even after the channel is free - and one tone only
        var tones = 0;
        for (long now = 540; now < 3000; now += 40)
        {
            Assert.AreEqual(Decision.Blocked, Ptt(lockout, now, out tone), $"{now} ms");
            if (tone) tones++;
        }

        Assert.AreEqual(0, tones, "the busy tone plays once per refused press");

        // release and press again: the channel is free now
        Release(lockout, 3000);
        Assert.IsFalse(lockout.IsPressRefused);
        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 3040, out tone));
        Assert.IsFalse(tone);
    }

    [TestMethod]
    public void AShortReleaseAndANewPressWhileStillBusyIsRefusedAgain()
    {
        var lockout = new BusyChannelLockout();
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 0);

        Assert.AreEqual(Decision.Blocked, Ptt(lockout, 10, out var first));
        Release(lockout, 50);

        lockout.OnVoiceHeard(Cb19, Modulation.AM, 80);
        Assert.AreEqual(Decision.Blocked, Ptt(lockout, 90, out var second));
        Assert.IsTrue(first && second, "a busy tone for every refused press");
    }

    [TestMethod]
    public void TheHangTimeDecidesWhetherAPressIsRefused()
    {
        var lockout = new BusyChannelLockout();
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 1000);

        Assert.AreEqual(Decision.Blocked, Ptt(lockout, 1000 + Hang - 1, out _));
        Release(lockout, 1000 + Hang);

        var free = new BusyChannelLockout();
        free.OnVoiceHeard(Cb19, Modulation.AM, 1000);
        Assert.AreEqual(Decision.Transmit, Ptt(free, 1000 + Hang, out _), "300 ms after the last packet");
    }

    [TestMethod]
    public void AnotherFrequencyIsNotBusy()
    {
        var lockout = new BusyChannelLockout();
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 0);

        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 10, out var tone, Cb20));
        Assert.IsFalse(tone);
    }

    [TestMethod]
    public void WithTheLockoutOffEveryPressTransmits()
    {
        var lockout = new BusyChannelLockout();
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 0);

        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 10, out var tone, enabled: false));
        Assert.IsFalse(tone);

        // voice of another station while transmitting is not a lost race either
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 200);
        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 240, out tone, enabled: false));
        Assert.IsFalse(tone);
        Assert.AreEqual(Decision.Transmit,
            lockout.Decide(false, false, true, Cb19, Modulation.AM, 280, out tone), "VOX as well");
        Assert.IsFalse(tone);
    }

    [TestMethod]
    public void VoiceOfAnotherStationWhileTransmittingIsALostRace()
    {
        var lockout = new BusyChannelLockout();

        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 0, out _));
        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 40, out _));

        // the server gave the channel to the other station: its voice arrives
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 70);

        Assert.AreEqual(Decision.Blocked, Ptt(lockout, 80, out var tone), "transmission stops");
        Assert.IsTrue(tone, "busy tone once");
        Assert.IsTrue(lockout.IsIndicatorLit(Cb19, Modulation.AM, 80), "BUSY flashes");

        // for the rest of the press - also once the other station is done
        for (long now = 120; now < 2000; now += 40)
        {
            Assert.AreEqual(Decision.Blocked, Ptt(lockout, now, out tone), $"{now} ms");
            Assert.IsFalse(tone, $"{now} ms");
        }

        Release(lockout, 2000);
        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 2040, out tone), "a new press on the free channel");
        Assert.IsFalse(tone);
    }

    [TestMethod]
    public void VoiceThatWasOnItsWayBeforeTheKeyUpIsNotALostRace()
    {
        var lockout = new BusyChannelLockout();

        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 1000, out _));

        // handled a few ms after the transmission started: it left the other station before our key-up
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 1000 + BusyChannelLockout.RaceGraceMilliseconds - 1);
        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 1080, out var tone));
        Assert.IsFalse(tone);

        // more voice of that station later on: the server gave it the channel after all
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 1100);
        Assert.AreEqual(Decision.Blocked, Ptt(lockout, 1120, out tone));
        Assert.IsTrue(tone);
    }

    [TestMethod]
    public void VoiceOnAnotherChannelWhileTransmittingIsNoRace()
    {
        var lockout = new BusyChannelLockout();

        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 0, out _));
        lockout.OnVoiceHeard(Cb20, Modulation.AM, 200);
        lockout.OnVoiceHeard(Cb19, Modulation.FM, 210);

        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 240, out var tone));
        Assert.IsFalse(tone);
    }

    [TestMethod]
    public void OnlyVoiceOfAnotherStationOnTheOwnChannelCounts()
    {
        // voice of another station on the radio's channel
        Assert.IsTrue(BusyChannelLockout.IsOtherStationOnChannel(Packet(Other, Cb19, Modulation.AM), Own, Cb19,
            Modulation.AM));
        Assert.IsTrue(BusyChannelLockout.IsOtherStationOnChannel(Packet(Other, Cb19 + 300, Modulation.AM), Own, Cb19,
            Modulation.AM));

        // the own radio check echo is never a lost race or a busy channel
        Assert.IsFalse(BusyChannelLockout.IsOtherStationOnChannel(Packet(Own, Cb19, Modulation.AM), Own, Cb19,
            Modulation.AM));
        var echo = Packet(Own, Cb19, Modulation.AM);
        echo.Guid = Other;
        Assert.IsFalse(BusyChannelLockout.IsOtherStationOnChannel(echo, Own, Cb19, Modulation.AM),
            "the original sender counts");

        // another channel
        Assert.IsFalse(BusyChannelLockout.IsOtherStationOnChannel(Packet(Other, Cb20, Modulation.AM), Own, Cb19,
            Modulation.AM));
        Assert.IsFalse(BusyChannelLockout.IsOtherStationOnChannel(Packet(Other, Cb19, Modulation.FM), Own, Cb19,
            Modulation.AM));

        // one of several frequencies of the packet is enough
        var twoFrequencies = new UDPVoicePacket
        {
            Guid = Other, OriginalClientGuid = Other, Frequencies = new[] { Cb20, Cb19 },
            Modulations = new[] { (byte)Modulation.AM, (byte)Modulation.AM }, Encryptions = new byte[] { 0, 0 }
        };
        Assert.IsTrue(BusyChannelLockout.IsOtherStationOnChannel(twoFrequencies, Own, Cb19, Modulation.AM));
        Assert.IsFalse(BusyChannelLockout.IsOtherStationOnChannel(null, Own, Cb19, Modulation.AM));
    }

    [TestMethod]
    public void TheOwnRadioCheckEchoDoesNotStopTheTransmission()
    {
        // as UDPClientAudioProcessor does it: only voice of another station reaches OnVoiceHeard
        var lockout = new BusyChannelLockout();
        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 0, out _));

        for (long now = 100; now < 1000; now += 40)
        {
            if (BusyChannelLockout.IsOtherStationOnChannel(Packet(Own, Cb19, Modulation.AM), Own, Cb19, Modulation.AM))
                lockout.OnVoiceHeard(Cb19, Modulation.AM, now);

            Assert.AreEqual(Decision.Transmit, Ptt(lockout, now, out var tone));
            Assert.IsFalse(tone);
        }
    }

    [TestMethod]
    public void VoxDoesNotTransmitWhileTheChannelIsBusyAndBeepsAtMostOncePerSecond()
    {
        var lockout = new BusyChannelLockout();
        var toneTimes = new List<long>();

        // another station talks for 2.5 s; VOX hears speech all the time (40 ms frames)
        for (long now = 0; now < 2500; now += 40)
        {
            lockout.OnVoiceHeard(Cb19, Modulation.AM, now);
            Assert.AreEqual(Decision.Blocked, Vox(lockout, now + 1, out var tone), $"{now} ms");
            if (tone) toneTimes.Add(now + 1);
        }

        CollectionAssert.AreEqual(new List<long> { 1, 1001, 2001 }, toneTimes, "one busy tone per second at most");

        // the channel is free again: VOX transmits (no need to stop talking first)
        Assert.AreEqual(Decision.Transmit, Vox(lockout, 2480 + Hang, out var last), "300 ms after the last packet");
        Assert.IsFalse(last);
    }

    [TestMethod]
    public void VoxStopsWhenItLosesTheRace()
    {
        var lockout = new BusyChannelLockout();

        Assert.AreEqual(Decision.Transmit, Vox(lockout, 0, out _));
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 100);

        Assert.AreEqual(Decision.Blocked, Vox(lockout, 120, out var tone));
        Assert.IsTrue(tone);
        Assert.AreEqual(Decision.Blocked, Vox(lockout, 160, out tone), "still busy");
        Assert.IsFalse(tone, "no second tone within a second");
    }

    [TestMethod]
    public void TheIndicatorFlashesAfterARefusedPressAndIsLitWhileBusy()
    {
        var lockout = new BusyChannelLockout();
        Assert.IsFalse(lockout.IsIndicatorLit(Cb19, Modulation.AM, 0));

        lockout.OnVoiceHeard(Cb19, Modulation.AM, 1000);
        Assert.IsTrue(lockout.IsIndicatorLit(Cb19, Modulation.AM, 1000), "lit while busy");
        Assert.IsFalse(lockout.IsIndicatorLit(Cb20, Modulation.AM, 1000), "only for the radio's channel");

        Assert.AreEqual(Decision.Blocked, Ptt(lockout, 1100, out _));

        // flashing: on, off, on ... for RefusedFlashMilliseconds after the refusal
        const int period = BusyChannelLockout.FlashPeriodMilliseconds;
        Assert.IsTrue(lockout.IsIndicatorLit(Cb19, Modulation.AM, 1100));
        Assert.IsFalse(lockout.IsIndicatorLit(Cb19, Modulation.AM, 1100 + period));
        Assert.IsTrue(lockout.IsIndicatorLit(Cb19, Modulation.AM, 1100 + 2 * period));
        Assert.IsFalse(lockout.IsIndicatorLit(Cb19, Modulation.AM, 1100 + 3 * period));

        // afterwards: lit only while the channel is busy
        var after = 1100 + BusyChannelLockout.RefusedFlashMilliseconds;
        Assert.IsFalse(lockout.IsIndicatorLit(Cb19, Modulation.AM, after), "the other station is quiet");
        lockout.OnVoiceHeard(Cb19, Modulation.AM, after);
        Assert.IsTrue(lockout.IsIndicatorLit(Cb19, Modulation.AM, after + 1));
    }

    [TestMethod]
    public void AKeyUpOnABusyChannelDoesNotBlockTheReceiverBeforeItsFirstFrame()
    {
        var lockout = new BusyChannelLockout();
        Assert.IsFalse(lockout.WillRefuseKeyUp(Cb19, Modulation.AM, 0), "free channel: half-duplex as usual");

        // push-to-talk pressed while another station talks: until the next mic frame refuses the press, its voice
        // must still be heard (half-duplex would drop it otherwise)
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 1000);
        Assert.IsTrue(lockout.WillRefuseKeyUp(Cb19, Modulation.AM, 1010));
        Assert.IsFalse(lockout.WillRefuseKeyUp(Cb20, Modulation.AM, 1010), "only on the busy channel");
        Assert.IsFalse(lockout.WillRefuseKeyUp(Cb19, Modulation.AM, 1000 + Hang), "free again after the hang time");

        // transmitting (e.g. the voice arrived during a race): half-duplex until the lost race stops the transmission
        var transmitting = new BusyChannelLockout();
        Assert.AreEqual(Decision.Transmit, Ptt(transmitting, 0, out _));
        transmitting.OnVoiceHeard(Cb19, Modulation.AM, 100);
        Assert.IsFalse(transmitting.WillRefuseKeyUp(Cb19, Modulation.AM, 110));
        Assert.AreEqual(Decision.Blocked, Ptt(transmitting, 120, out _));
        Assert.IsTrue(transmitting.WillRefuseKeyUp(Cb19, Modulation.AM, 130), "stopped: receiving again");
    }

    [TestMethod]
    public void ARefusedPressStaysRefusedWhileNothingCanBeSent()
    {
        var lockout = new BusyChannelLockout();
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 0);
        Assert.AreEqual(Decision.Blocked, Ptt(lockout, 10, out var tone));
        Assert.IsTrue(tone);

        // still pressed, but the voice link drops out for a moment (or the radio is off): the press goes on
        lockout.KeyedWithoutTransmitter();
        lockout.KeyedWithoutTransmitter();
        Assert.IsTrue(lockout.IsPressRefused);

        // back, the channel is free by now - but it is the same press: still refused, no second tone
        Assert.AreEqual(Decision.Blocked, Ptt(lockout, 1000, out tone));
        Assert.IsFalse(tone);

        Release(lockout, 1040);
        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 1080, out _), "released and pressed again");

        // a transmitting press that pauses transmits nothing meanwhile: voice heard during the pause is no lost race
        // once it can send again (and the channel is free by then)
        lockout.KeyedWithoutTransmitter();
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 1200);
        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 1200 + Hang, out tone));
        Assert.IsFalse(tone);
    }

    [TestMethod]
    public void ResetForgetsTheChannelAndThePress()
    {
        var lockout = new BusyChannelLockout();
        lockout.OnVoiceHeard(Cb19, Modulation.AM, 0);
        Assert.AreEqual(Decision.Blocked, Ptt(lockout, 10, out _));

        lockout.Reset();

        Assert.IsFalse(lockout.IsPressRefused);
        Assert.IsFalse(lockout.IsChannelBusy(Cb19, Modulation.AM, 20));
        Assert.IsFalse(lockout.IsIndicatorLit(Cb19, Modulation.AM, 20));
        Assert.AreEqual(Decision.Transmit, Ptt(lockout, 20, out _));
    }
}
