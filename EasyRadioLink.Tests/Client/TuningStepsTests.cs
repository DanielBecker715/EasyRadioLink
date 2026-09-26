using System.Collections.Generic;
using System.Globalization;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Client.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>The STEP button of the radio window: 1 kHz .. 100 MHz, one decade per display digit.</summary>
[TestClass]
public class TuningStepsTests
{
    [TestMethod]
    public void AllStepsAreTheDecadesFrom1KHzTo100MHz()
    {
        CollectionAssert.AreEqual(new[] { 1_000, 10_000, 100_000, 1_000_000, 10_000_000, 100_000_000 },
            new List<int>(TuningSteps.All));
        Assert.AreEqual(1_000, RadioState.Default.Step, "the default step is 1 kHz");

        foreach (var step in TuningSteps.All) Assert.IsTrue(TuningSteps.IsValid(step), step.ToString());

        Assert.IsFalse(TuningSteps.IsValid(0));
        Assert.IsFalse(TuningSteps.IsValid(-1_000));
        Assert.IsFalse(TuningSteps.IsValid(5_000));
        Assert.IsFalse(TuningSteps.IsValid(1_000_000_000));
    }

    [TestMethod]
    public void NextCyclesThroughTheStepsAndStartsAgainAt1KHz()
    {
        Assert.AreEqual(10_000, TuningSteps.Next(1_000));
        Assert.AreEqual(100_000, TuningSteps.Next(10_000));
        Assert.AreEqual(1_000_000, TuningSteps.Next(100_000));
        Assert.AreEqual(10_000_000, TuningSteps.Next(1_000_000));
        Assert.AreEqual(100_000_000, TuningSteps.Next(10_000_000));
        Assert.AreEqual(1_000, TuningSteps.Next(100_000_000));

        // an invalid step (e.g. from a hand-edited radio-state.json) starts at the default
        Assert.AreEqual(TuningSteps.Default, TuningSteps.Next(0));
        Assert.AreEqual(TuningSteps.Default, TuningSteps.Next(12_345));
        Assert.AreEqual(TuningSteps.Default, TuningSteps.Next(-1_000));
    }

    [TestMethod]
    public void DigitIndexMarksTheDigitTheStepChanges()
    {
        // display "000.000" (6 digits): 0 = hundreds of MHz ... 5 = kHz
        Assert.AreEqual(5, TuningSteps.DigitIndex(1_000));
        Assert.AreEqual(4, TuningSteps.DigitIndex(10_000));
        Assert.AreEqual(3, TuningSteps.DigitIndex(100_000));
        Assert.AreEqual(2, TuningSteps.DigitIndex(1_000_000));
        Assert.AreEqual(1, TuningSteps.DigitIndex(10_000_000));
        Assert.AreEqual(0, TuningSteps.DigitIndex(100_000_000));

        // invalid: the last digit (like the default step)
        Assert.AreEqual(5, TuningSteps.DigitIndex(0));
        Assert.AreEqual(5, TuningSteps.DigitIndex(12_345));
    }

    [TestMethod]
    public void LabelNamesTheStep()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // no thousands separators or decimal commas on a German Windows
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.AreEqual("1 kHz", TuningSteps.Label(1_000));
            Assert.AreEqual("10 kHz", TuningSteps.Label(10_000));
            Assert.AreEqual("100 kHz", TuningSteps.Label(100_000));
            Assert.AreEqual("1 MHz", TuningSteps.Label(1_000_000));
            Assert.AreEqual("10 MHz", TuningSteps.Label(10_000_000));
            Assert.AreEqual("100 MHz", TuningSteps.Label(100_000_000));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
