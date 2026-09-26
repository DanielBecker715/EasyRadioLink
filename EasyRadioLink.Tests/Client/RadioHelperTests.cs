using EasyRadioLink.Client.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>Rotary style tuning (profile setting RotaryStyleIncrement): the digit rolls over without carrying.</summary>
[TestClass]
public class RadioHelperTests
{
    [TestMethod]
    public void RotaryStepChangesOneDigit()
    {
        Assert.AreEqual(27_186_000d, RadioHelper.RotaryStep(27_185_000, 1_000));
        Assert.AreEqual(27_184_000d, RadioHelper.RotaryStep(27_185_000, -1_000));
        Assert.AreEqual(27_195_000d, RadioHelper.RotaryStep(27_185_000, 10_000));
        Assert.AreEqual(27_285_000d, RadioHelper.RotaryStep(27_185_000, 100_000));
        Assert.AreEqual(28_185_000d, RadioHelper.RotaryStep(27_185_000, 1_000_000));
        Assert.AreEqual(17_185_000d, RadioHelper.RotaryStep(27_185_000, -10_000_000));
        Assert.AreEqual(127_185_000d, RadioHelper.RotaryStep(27_185_000, 100_000_000));
    }

    [TestMethod]
    public void RotaryStepRollsOverWithoutCarrying()
    {
        // 9 -> 0 going up, 0 -> 9 going down; the next digit stays
        Assert.AreEqual(27_180_000d, RadioHelper.RotaryStep(27_189_000, 1_000));
        Assert.AreEqual(27_189_000d, RadioHelper.RotaryStep(27_180_000, -1_000));
        Assert.AreEqual(27_105_000d, RadioHelper.RotaryStep(27_195_000, 10_000));
        Assert.AreEqual(27_195_000d, RadioHelper.RotaryStep(27_105_000, -10_000));
        Assert.AreEqual(27_085_000d, RadioHelper.RotaryStep(27_985_000, 100_000));
        Assert.AreEqual(20_185_000d, RadioHelper.RotaryStep(29_185_000, 1_000_000));
        Assert.AreEqual(7_000_000d, RadioHelper.RotaryStep(97_000_000, 10_000_000));
        Assert.AreEqual(46_000_000d, RadioHelper.RotaryStep(946_000_000, 100_000_000));
        Assert.AreEqual(946_000_000d, RadioHelper.RotaryStep(46_000_000, -100_000_000));
    }

    [TestMethod]
    public void RotaryStepKeepsTheDigitsBelowTheStep()
    {
        // PMR446 channel 8: the 750 Hz below the kHz digit stay
        Assert.AreEqual(446_194_750d, RadioHelper.RotaryStep(446_193_750, 1_000));
        Assert.AreEqual(446_190_750d, RadioHelper.RotaryStep(446_199_750, 1_000));
        Assert.AreEqual(446_199_750d, RadioHelper.RotaryStep(446_190_750, -1_000));
        Assert.AreEqual(446_103_750d, RadioHelper.RotaryStep(446_193_750, 10_000));
    }

    [TestMethod]
    public void RotaryStepToleratesFloatingPointNoise()
    {
        // a frequency a hair below the kHz grid still counts as ...189 kHz, so 1 kHz up rolls over to ...180 kHz
        Assert.AreEqual(27_180_000d, RadioHelper.RotaryStep(27_189_000 - 1e-7, 1_000), 1e-6);
        Assert.AreEqual(27_188_000d, RadioHelper.RotaryStep(27_189_000 - 1e-7, -1_000), 1e-6);
    }
}
