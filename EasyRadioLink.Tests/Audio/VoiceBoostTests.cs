using System;
using System.Linq;
using EasyRadioLink.Common.Audio.Utility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Audio;

/// <summary>"Boost my voice" (<see cref="VoiceBoost" />).</summary>
[TestClass]
public class VoiceBoostTests
{
    private static float[] Ramp()
    {
        // -1 .. +1 in small steps
        return Enumerable.Range(-2000, 4001).Select(i => i / 2000f).ToArray();
    }

    [TestMethod]
    public void OffChangesNothing()
    {
        foreach (var boost in new[] { 0f, -1f, float.NaN, float.NegativeInfinity })
        {
            var audio = Ramp();
            VoiceBoost.Apply(audio, boost);
            CollectionAssert.AreEqual(Ramp(), audio, $"boost {boost}");
            Assert.AreEqual(1f, VoiceBoost.GainFor(boost));
        }
    }

    [TestMethod]
    public void QuietVoiceIsRaisedByTheBoost()
    {
        foreach (var (boost, db) in new[] { (1f, 10.0), (0.5f, 5.0), (5f, 10.0) })
        {
            var audio = new[] { 0.01f, -0.02f, 0.1f };
            VoiceBoost.Apply(audio, boost);
            Assert.AreEqual(0.01 * Math.Pow(10, db / 20), audio[0], 1e-6, $"boost {boost}");
            Assert.AreEqual(-0.02 * Math.Pow(10, db / 20), audio[1], 1e-6, $"boost {boost}");
            Assert.AreEqual(0.1 * Math.Pow(10, db / 20), audio[2], 1e-6, $"boost {boost}");
        }
    }

    [TestMethod]
    public void PeaksAreRoundedOffSmoothly()
    {
        var audio = Ramp();
        VoiceBoost.Apply(audio, 1f);

        // never above the ceiling, symmetric, monotonic, and no jump anywhere (in particular at the knee)
        Assert.IsLessThanOrEqualTo(VoiceBoost.LimiterCeiling, audio.Max(Math.Abs));
        for (var i = 0; i < audio.Length; i++) Assert.AreEqual(-audio[audio.Length - 1 - i], audio[i], 1e-6);

        var gainStep = VoiceBoost.GainFor(1f) / 2000f;
        for (var i = 1; i < audio.Length; i++)
        {
            Assert.IsGreaterThanOrEqualTo(audio[i - 1], audio[i], $"monotonic at {i}");
            Assert.IsLessThanOrEqualTo(gainStep * 1.0001f, audio[i] - audio[i - 1], $"no jump at {i}");
        }

        // a full scale peak ends up close to the ceiling
        Assert.IsGreaterThan(VoiceBoost.LimiterCeiling - 0.01f, audio[^1]);
    }
}
