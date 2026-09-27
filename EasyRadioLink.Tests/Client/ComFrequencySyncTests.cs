using System;
using EasyRadioLink.Client.GameIntegration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>Keeping COM1 of the simulator and the radio on the same frequency, in both directions.</summary>
[TestClass]
public class ComFrequencySyncTests
{
    private const double Unicom = 122_800_000;
    private const double Tower = 118_700_000;
    private const double Ground = 121_900_000;
    private const double Cb = 27_185_000;

    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime After(double seconds)
    {
        return Start.AddSeconds(seconds);
    }

    [TestMethod]
    public void FirstContactSendsACOMFrequencyOfTheAppToTheCockpit()
    {
        var sync = new ComFrequencySync();

        Assert.AreEqual(new ComSyncAction(ComSyncDirection.AppToSim, Unicom), sync.Update(Tower, Unicom, Start));
    }

    [TestMethod]
    public void FirstContactTunesTheAppToTheCockpitIfTheAppIsNotOnACOMFrequency()
    {
        var sync = new ComFrequencySync();

        Assert.AreEqual(new ComSyncAction(ComSyncDirection.SimToApp, Tower), sync.Update(Tower, Cb, Start));
    }

    [TestMethod]
    public void NothingHappensWhileBothRadiosAgree()
    {
        var sync = new ComFrequencySync();

        Assert.AreEqual(ComSyncAction.None, sync.Update(Tower, Tower, Start));
        Assert.AreEqual(ComSyncAction.None, sync.Update(Tower, Tower, After(5)));
        // within the receive tolerance
        Assert.AreEqual(ComSyncAction.None, sync.Update(Tower + 200, Tower, After(6)));
    }

    [TestMethod]
    public void TuningTheCockpitTunesTheApp()
    {
        var sync = new ComFrequencySync();
        sync.Update(Tower, Tower, Start);

        Assert.AreEqual(new ComSyncAction(ComSyncDirection.SimToApp, Ground), sync.Update(Ground, Tower, After(1)));
        Assert.AreEqual(ComSyncAction.None, sync.Update(Ground, Ground, After(1.1)));
    }

    [TestMethod]
    public void TuningTheAppTunesTheCockpit()
    {
        var sync = new ComFrequencySync();
        sync.Update(Tower, Tower, Start);

        Assert.AreEqual(new ComSyncAction(ComSyncDirection.AppToSim, Ground), sync.Update(Tower, Ground, After(1)));
        Assert.AreEqual(ComSyncAction.None, sync.Update(Ground, Ground, After(1.2)));
        Assert.AreEqual(ComSyncAction.None, sync.Update(Ground, Ground, After(5)));
    }

    [TestMethod]
    public void TheOldCockpitFrequencyReportedRightAfterTuningDoesNotTuneTheAppBack()
    {
        var sync = new ComFrequencySync();
        sync.Update(Tower, Tower, Start);

        sync.Update(Tower, Ground, After(1));

        // the sim takes a moment to apply the event and still reports Tower
        Assert.AreEqual(ComSyncAction.None, sync.Update(Tower, Ground, After(1.1)));
        Assert.AreEqual(ComSyncAction.None, sync.Update(Tower, Ground, After(1.5)));
        Assert.AreEqual(ComSyncAction.None, sync.Update(Ground, Ground, After(1.7)));
        Assert.AreEqual(ComSyncAction.None, sync.Update(Ground, Ground, After(10)));
    }

    [TestMethod]
    public void FastTuningInTheAppSendsEveryFrequencyAndTheLastOneWins()
    {
        var sync = new ComFrequencySync();
        sync.Update(Tower, Tower, Start);

        Assert.AreEqual(new ComSyncAction(ComSyncDirection.AppToSim, 118_705_000),
            sync.Update(Tower, 118_705_000, After(0.1)));
        Assert.AreEqual(new ComSyncAction(ComSyncDirection.AppToSim, 118_710_000),
            sync.Update(Tower, 118_710_000, After(0.2)));
        // the sim reports the first step late
        Assert.AreEqual(ComSyncAction.None, sync.Update(118_705_000, 118_710_000, After(0.3)));
        Assert.AreEqual(ComSyncAction.None, sync.Update(118_710_000, 118_710_000, After(0.4)));
        Assert.AreEqual(ComSyncAction.None, sync.Update(118_710_000, 118_710_000, After(3)));
    }

    [TestMethod]
    public void AFrequencyTheCockpitRoundedIsCopiedBackToTheApp()
    {
        var sync = new ComFrequencySync();
        sync.Update(Tower, Tower, Start);

        // 25 kHz radio: the sim makes 118.710 118.700
        sync.Update(Tower, 118_710_000, After(1));
        Assert.AreEqual(ComSyncAction.None, sync.Update(Tower, 118_710_000, After(1.5)));

        var settled = After(1) + ComFrequencySync.EchoHoldOff + TimeSpan.FromMilliseconds(100);
        Assert.AreEqual(new ComSyncAction(ComSyncDirection.SimToApp, Tower), sync.Update(Tower, 118_710_000, settled));
    }

    [TestMethod]
    public void AnAppFrequencyOutsideTheCOMBandIsNotSentAndTheCockpitCanTakeOverAgain()
    {
        var sync = new ComFrequencySync();
        sync.Update(Tower, Tower, Start);

        Assert.AreEqual(ComSyncAction.None, sync.Update(Tower, Cb, After(1)));
        Assert.AreEqual(ComSyncAction.None, sync.Update(Tower, Cb, After(5)));

        Assert.AreEqual(new ComSyncAction(ComSyncDirection.SimToApp, Ground), sync.Update(Ground, Cb, After(6)));
    }

    [TestMethod]
    public void AfterResetTheNextUpdateIsAFirstContactAgain()
    {
        var sync = new ComFrequencySync();
        sync.Update(Tower, Tower, Start);

        sync.Reset();

        Assert.AreEqual(new ComSyncAction(ComSyncDirection.AppToSim, Unicom), sync.Update(Tower, Unicom, After(1)));
    }

    [TestMethod]
    public void TheCOMBandIs118To136_99MHz()
    {
        Assert.IsTrue(ComFrequencySync.IsComFrequency(118_000_000));
        Assert.IsTrue(ComFrequencySync.IsComFrequency(Unicom));
        Assert.IsTrue(ComFrequencySync.IsComFrequency(136_990_000));

        Assert.IsFalse(ComFrequencySync.IsComFrequency(117_975_000), "NAV");
        Assert.IsFalse(ComFrequencySync.IsComFrequency(137_000_000));
        Assert.IsFalse(ComFrequencySync.IsComFrequency(Cb));
        Assert.IsFalse(ComFrequencySync.IsComFrequency(double.NaN));
    }
}
