using System.Globalization;
using EasyRadioLink.Client.UI.ClientWindow.RadioPanel;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>The frequency display and the direct entry of the radio window.</summary>
[TestClass]
public class RadioPanelWindowTests
{
    private CultureInfo _culture;

    [TestInitialize]
    public void UseGermanCulture()
    {
        // the owner runs a German Windows: the display must still show "027.185"
        _culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
    }

    [TestCleanup]
    public void RestoreCulture()
    {
        CultureInfo.CurrentCulture = _culture;
    }

    private static void AssertDisplay(double frequencyHz, string expected, int expectedSubKiloHertz)
    {
        Assert.AreEqual(expected, RadioPanelWindow.FormatFrequency(frequencyHz, out var subKiloHertz),
            $"{frequencyHz} Hz");
        Assert.AreEqual(expectedSubKiloHertz, subKiloHertz, $"{frequencyHz} Hz");
    }

    [TestMethod]
    public void FormatFrequencyShowsMegaHertzWithThreeDecimals()
    {
        AssertDisplay(27_185_000, "027.185", 0);
        AssertDisplay(446_193_750, "446.193", 750);
        AssertDisplay(446_006_250, "446.006", 250);
        AssertDisplay(121_500_000, "121.500", 0);
        AssertDisplay(1_000_000, "001.000", 0);
        AssertDisplay(999_999_000, "999.999", 0);
        AssertDisplay(27_185_010, "027.185", 10);
    }

    [TestMethod]
    public void FormatFrequencyRoundsToWholeHertz()
    {
        AssertDisplay(27_185_004.6, "027.185", 5);
        AssertDisplay(27_184_999.9999, "027.185", 0);
        AssertDisplay(29_999_999.6, "030.000", 0);
    }

    [TestMethod]
    public void FormatFrequencyShowsZeroForInvalidFrequencies()
    {
        AssertDisplay(0, "000.000", 0);
        AssertDisplay(-27_185_000, "000.000", 0);
    }

    [TestMethod]
    public void EntryAcceptsInvariantAndGermanDecimals()
    {
        Assert.IsTrue(RadioPanelWindow.TryParseEntry("27.185", out var hz));
        Assert.AreEqual(27_185_000d, hz);

        Assert.IsTrue(RadioPanelWindow.TryParseEntry("27,185", out hz));
        Assert.AreEqual(27_185_000d, hz);

        Assert.IsTrue(RadioPanelWindow.TryParseEntry(" 446.19375 ", out hz));
        Assert.AreEqual(446_193_750d, hz);

        Assert.IsTrue(RadioPanelWindow.TryParseEntry("446,00625", out hz));
        Assert.AreEqual(446_006_250d, hz);

        Assert.IsFalse(RadioPanelWindow.TryParseEntry("", out _));
        Assert.IsFalse(RadioPanelWindow.TryParseEntry("abc", out _));
        Assert.IsFalse(RadioPanelWindow.TryParseEntry("0", out _));
        Assert.IsFalse(RadioPanelWindow.TryParseEntry("-27.185", out _));
    }

    [TestMethod]
    public void EntryIsRoundedTo10Hertz()
    {
        // the display and the entry box show 10 Hz (5 decimals) - finer input would be invisible
        Assert.IsTrue(RadioPanelWindow.TryParseEntry("27.185005", out var hz));
        Assert.AreEqual(27_185_010d, hz);

        Assert.IsTrue(RadioPanelWindow.TryParseEntry("27.185004", out hz));
        Assert.AreEqual(27_185_000d, hz);

        Assert.IsTrue(RadioPanelWindow.TryParseEntry("27.185001", out hz));
        Assert.AreEqual(27_185_000d, hz);

        Assert.IsTrue(RadioPanelWindow.TryParseEntry("446.193756", out hz));
        Assert.AreEqual(446_193_760d, hz);
    }

    [TestMethod]
    public void EnterOnAnUnchangedEntryDoesNotMoveTheRadio()
    {
        // what the window does: type -> tune (RadioHelper.SetFrequency: clamp + normalise) -> the entry box shows the
        // tuned frequency again (RadioCalculator.FormatMHz) -> Enter tunes to the same frequency
        foreach (var typed in new[]
                 {
                     "27.185005", "27.185004", "446.19375", "446.193756", "145.12345", "121.5", "29.99955", "29.99945",
                     "107.9996", "899.99949", "1", "0.5", "999.999", "1234", "27,18"
                 })
        {
            Assert.IsTrue(RadioPanelWindow.TryParseEntry(typed, out var typedHz), typed);
            var tuned = BandPlan.Normalise(typedHz);

            var shown = RadioCalculator.FormatMHz(tuned);
            Assert.IsTrue(RadioPanelWindow.TryParseEntry(shown, out var enteredAgain), shown);

            Assert.AreEqual(tuned, BandPlan.Normalise(enteredAgain), $"typed {typed}, shown {shown}");
        }
    }
}
