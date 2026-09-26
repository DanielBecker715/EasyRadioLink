using System;
using System.Collections.Generic;
using System.Linq;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Tests.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Models;

/// <summary>The band plan decides modulation, radio model and label from the frequency alone.</summary>
[TestClass]
public class BandPlanTests
{
    // label, modulation, model, first and last frequency on the 1 kHz grid in MHz (both inclusive)
    private static readonly (string label, Modulation modulation, string model, double first, double last)[] Expected =
    {
        ("MW", Modulation.AM, "vintage", 1.000, 2.999),
        ("HF", Modulation.AM, "hf", 3.000, 26.964),
        ("CB", Modulation.AM, "cb", 26.965, 27.405),
        ("HF", Modulation.AM, "hf", 27.406, 29.999),
        ("VHF", Modulation.FM, "tactical", 30.000, 87.999),
        ("FM", Modulation.FM, "walkie", 88.000, 107.999),
        ("AIR", Modulation.AM, "airband", 108.000, 136.999),
        ("VHF", Modulation.FM, "walkie", 137.000, 224.999),
        ("UHF", Modulation.AM, "tactical", 225.000, 399.999),
        ("UHF", Modulation.FM, "walkie", 400.000, 445.999),
        ("PMR", Modulation.FM, "walkie", 446.000, 446.199),
        ("UHF", Modulation.FM, "walkie", 446.200, 899.999),
        ("DIG", Modulation.DIGITAL, "digital", 900.000, 999.999)
    };

    private static double Hz(double mhz)
    {
        // the way user input is converted (RadioCalculator.TryParseMHz) - may carry floating point noise before rounding
        return Math.Round(mhz * 1e6);
    }

    private static void AssertBand(double mhz, string label, Modulation modulation, string model)
    {
        var band = BandPlan.GetBand(mhz * 1e6);
        Assert.AreEqual(label, band.Label, $"{mhz} MHz");
        Assert.AreEqual(modulation, band.Modulation, $"{mhz} MHz");
        Assert.AreEqual(model, band.Model, $"{mhz} MHz");
    }

    [TestMethod]
    public void EveryBandMatchesTheSpecification()
    {
        Assert.HasCount(Expected.Length, BandPlan.Bands);

        for (var i = 0; i < Expected.Length; i++)
        {
            var e = Expected[i];
            var band = BandPlan.Bands[i];

            Assert.AreEqual(e.label, band.Label, $"band {i}");
            Assert.AreEqual(e.modulation, band.Modulation, $"band {i}");
            Assert.AreEqual(e.model, band.Model, $"band {i}");
            Assert.AreEqual(Hz(e.first), band.FirstFrequency, $"band {i}");
            Assert.AreEqual(Hz(e.last), band.LastFrequency, $"band {i}");
        }
    }

    [TestMethod]
    public void EveryBandIsFoundAtItsEdgesAndInItsMiddle()
    {
        foreach (var e in Expected)
        {
            AssertBand(e.first, e.label, e.modulation, e.model);
            AssertBand(e.last, e.label, e.modulation, e.model);
            AssertBand((e.first + e.last) / 2, e.label, e.modulation, e.model);

            // within the receive tolerance of the first / last kHz, including floating point noise
            AssertBand(e.first - 0.000499, e.label, e.modulation, e.model);
            AssertBand(e.last + 0.000499, e.label, e.modulation, e.model);
            Assert.AreEqual(e.label, BandPlan.GetBand(e.first * 1e6 - 1e-7).Label, $"{e.first} MHz - noise");
            Assert.AreEqual(e.label, BandPlan.GetBand(e.last * 1e6 + 1e-7).Label, $"{e.last} MHz + noise");
        }
    }

    [TestMethod]
    public void BandsAreGaplessAndCoverTheTuningRange()
    {
        Assert.AreEqual(BandPlan.MinFrequency, BandPlan.Bands[0].FirstFrequency);
        Assert.AreEqual(BandPlan.MaxFrequency, BandPlan.Bands[^1].LastFrequency);

        for (var i = 1; i < BandPlan.Bands.Count; i++)
        {
            var previous = BandPlan.Bands[i - 1];
            var band = BandPlan.Bands[i];

            Assert.AreEqual(previous.LastFrequency + 1000, band.FirstFrequency, $"{band.Label} follows {previous.Label}");
            Assert.AreEqual(previous.UpperEdge, band.LowerEdge, $"no gap before {band.Label}");
            Assert.IsLessThan(band.LastFrequency, band.FirstFrequency, band.Label);
        }

        // every kHz step at a band boundary belongs to exactly one band
        foreach (var band in BandPlan.Bands)
        {
            Assert.AreEqual(1, BandPlan.Bands.Count(b => b.Contains(band.FirstFrequency)), band.Label);
            Assert.AreEqual(1, BandPlan.Bands.Count(b => b.Contains(band.LastFrequency)), band.Label);
        }
    }

    [TestMethod]
    public void BoundaryFrequencies()
    {
        // CB channel 1 / 40 and the neighbouring kHz
        AssertBand(26.964, "HF", Modulation.AM, "hf");
        AssertBand(26.965, "CB", Modulation.AM, "cb");
        AssertBand(27.405, "CB", Modulation.AM, "cb");
        AssertBand(27.406, "HF", Modulation.AM, "hf");

        AssertBand(2.999, "MW", Modulation.AM, "vintage");
        AssertBand(3.000, "HF", Modulation.AM, "hf");
        AssertBand(29.999, "HF", Modulation.AM, "hf");
        AssertBand(30.000, "VHF", Modulation.FM, "tactical");
        AssertBand(87.999, "VHF", Modulation.FM, "tactical");
        AssertBand(88.000, "FM", Modulation.FM, "walkie");
        AssertBand(107.999, "FM", Modulation.FM, "walkie");
        AssertBand(108.000, "AIR", Modulation.AM, "airband");
        AssertBand(136.999, "AIR", Modulation.AM, "airband");
        AssertBand(137.000, "VHF", Modulation.FM, "walkie");
        AssertBand(224.999, "VHF", Modulation.FM, "walkie");
        AssertBand(225.000, "UHF", Modulation.AM, "tactical");
        AssertBand(399.999, "UHF", Modulation.AM, "tactical");
        AssertBand(400.000, "UHF", Modulation.FM, "walkie");
        AssertBand(445.999, "UHF", Modulation.FM, "walkie");
        AssertBand(446.000, "PMR", Modulation.FM, "walkie");
        AssertBand(446.199, "PMR", Modulation.FM, "walkie");
        AssertBand(446.200, "UHF", Modulation.FM, "walkie");
        AssertBand(899.999, "UHF", Modulation.FM, "walkie");
        AssertBand(900.000, "DIG", Modulation.DIGITAL, "digital");
        AssertBand(999.999, "DIG", Modulation.DIGITAL, "digital");
    }

    [TestMethod]
    public void WellKnownFrequencies()
    {
        // radio check (echo) defaults of the server
        AssertBand(27.405, "CB", Modulation.AM, "cb");
        AssertBand(446.19375, "PMR", Modulation.FM, "walkie");

        // CB channel 19 (default), PMR446 channel 1, the airband emergency frequency, UHF guard
        AssertBand(27.185, "CB", Modulation.AM, "cb");
        AssertBand(446.00625, "PMR", Modulation.FM, "walkie");
        AssertBand(121.5, "AIR", Modulation.AM, "airband");
        AssertBand(243.0, "UHF", Modulation.AM, "tactical");
        AssertBand(950, "DIG", Modulation.DIGITAL, "digital");
        AssertBand(100.0, "FM", Modulation.FM, "walkie");
        AssertBand(7.1, "HF", Modulation.AM, "hf");
        AssertBand(1.0, "MW", Modulation.AM, "vintage");

        // exactly the values the client computes from typed text
        Assert.AreEqual("CB", BandPlan.GetBand(Hz(27.405)).Label);
        Assert.AreEqual("PMR", BandPlan.GetBand(Hz(446.19375)).Label);
        Assert.AreEqual("AIR", BandPlan.GetBand(Hz(121.5)).Label);
        Assert.AreEqual("DIG", BandPlan.GetBand(Hz(950)).Label);
    }

    [TestMethod]
    public void DefaultFrequencyIsCbChannel19()
    {
        Assert.AreEqual("27.185", RadioCalculator.FormatMHz(BandPlan.DefaultFrequency));
        Assert.AreEqual("CB", BandPlan.GetBand(BandPlan.DefaultFrequency).Label);
        Assert.IsTrue(BandPlan.IsInRange(BandPlan.DefaultFrequency));
    }

    [TestMethod]
    public void OutOfRangeFrequenciesUseTheNearestBand()
    {
        Assert.AreEqual("MW", BandPlan.GetBand(0).Label);
        Assert.AreEqual("MW", BandPlan.GetBand(-5e6).Label);
        Assert.AreEqual("MW", BandPlan.GetBand(double.NegativeInfinity).Label);
        Assert.AreEqual("DIG", BandPlan.GetBand(1_000_000_000).Label);
        Assert.AreEqual("DIG", BandPlan.GetBand(double.PositiveInfinity).Label);
        Assert.AreEqual("CB", BandPlan.GetBand(double.NaN).Label, "NaN is treated as the default frequency");
    }

    [TestMethod]
    public void ClampKeepsFrequenciesInTheTuningRange()
    {
        Assert.AreEqual(1_000_000d, BandPlan.Clamp(0));
        Assert.AreEqual(1_000_000d, BandPlan.Clamp(-27185000));
        Assert.AreEqual(1_000_000d, BandPlan.Clamp(999_999));
        Assert.AreEqual(1_000_000d, BandPlan.Clamp(double.NegativeInfinity));
        Assert.AreEqual(999_999_000d, BandPlan.Clamp(1_000_000_000));
        Assert.AreEqual(999_999_000d, BandPlan.Clamp(999_999_001));
        Assert.AreEqual(999_999_000d, BandPlan.Clamp(double.PositiveInfinity));
        Assert.AreEqual(BandPlan.DefaultFrequency, BandPlan.Clamp(double.NaN));

        // inside the range: unchanged apart from rounding to whole Hz
        Assert.AreEqual(27185000d, BandPlan.Clamp(27185000));
        Assert.AreEqual(446193750d, BandPlan.Clamp(446.19375 * 1e6));
        Assert.AreEqual(1_000_000d, BandPlan.Clamp(1_000_000));
        Assert.AreEqual(999_999_000d, BandPlan.Clamp(999_999_000));
        Assert.AreEqual(27185000d, BandPlan.Clamp(27185000.4));
    }

    [TestMethod]
    public void IsInRange()
    {
        Assert.IsTrue(BandPlan.IsInRange(1_000_000));
        Assert.IsTrue(BandPlan.IsInRange(999_999_000));
        Assert.IsTrue(BandPlan.IsInRange(446193750));
        Assert.IsFalse(BandPlan.IsInRange(999_000));
        Assert.IsFalse(BandPlan.IsInRange(1_000_000_000));
        Assert.IsFalse(BandPlan.IsInRange(double.NaN));
        Assert.IsFalse(BandPlan.IsInRange(double.PositiveInfinity));
    }

    [TestMethod]
    public void UsersOnTheSameFrequencyAlwaysMatch()
    {
        // sender and receiver derive the modulation from their own frequency - within the receive tolerance
        // (RadioBase.FreqCloseEnough) the modulation must be the same, otherwise they could not hear each other
        foreach (var band in BandPlan.Bands)
        foreach (var frequency in new[] { band.FirstFrequency, band.LastFrequency })
        foreach (var offset in new[] { -499d, -250d, 0d, 250d, 499d })
        {
            var sender = BandPlan.GetBand(frequency);
            var receiver = BandPlan.GetBand(frequency + offset);

            Assert.IsTrue(RadioBase.FreqCloseEnough(frequency, frequency + offset));
            Assert.AreEqual(sender, receiver, $"{frequency} Hz vs {frequency + offset} Hz");
        }
    }

    [TestMethod]
    public void ModulationEdgesAreWhereTheModulationChanges()
    {
        // 30 (AM/FM), 108 (FM/AM), 137 (AM/FM), 225 (FM/AM), 400 (AM/FM) and 900 MHz (FM/DIGITAL)
        CollectionAssert.AreEqual(
            new[] { 29_999_500d, 107_999_500d, 136_999_500d, 224_999_500d, 399_999_500d, 899_999_500d },
            BandPlan.ModulationEdges.ToArray());

        foreach (var edge in BandPlan.ModulationEdges)
            Assert.AreNotEqual(BandPlan.GetBand(edge - 1).Modulation, BandPlan.GetBand(edge).Modulation, $"{edge} Hz");
    }

    [TestMethod]
    public void NormaliseRoundsToTheWholeKiloHertzNearModulationEdges()
    {
        foreach (var edge in BandPlan.ModulationEdges)
        {
            var lastKHz = edge - 500; // e.g. 29.999 MHz
            var firstKHz = edge + 500; // e.g. 30.000 MHz

            // closer than 1 kHz to the edge: the whole kHz, on either side
            Assert.AreEqual(lastKHz, BandPlan.Normalise(edge - 999), $"{edge} - 999 Hz");
            Assert.AreEqual(lastKHz, BandPlan.Normalise(edge - 600), $"{edge} - 600 Hz");
            Assert.AreEqual(lastKHz, BandPlan.Normalise(edge - 100), $"{edge} - 100 Hz");
            Assert.AreEqual(lastKHz, BandPlan.Normalise(edge - 1), $"{edge} - 1 Hz");
            Assert.AreEqual(firstKHz, BandPlan.Normalise(edge), $"{edge} Hz");
            Assert.AreEqual(firstKHz, BandPlan.Normalise(edge + 1), $"{edge} + 1 Hz");
            Assert.AreEqual(firstKHz, BandPlan.Normalise(edge + 100), $"{edge} + 100 Hz");
            Assert.AreEqual(firstKHz, BandPlan.Normalise(edge + 999), $"{edge} + 999 Hz");

            // on the kHz grid: unchanged
            Assert.AreEqual(lastKHz, BandPlan.Normalise(lastKHz));
            Assert.AreEqual(firstKHz, BandPlan.Normalise(firstKHz));

            // 1 kHz or more away from the edge: unchanged
            Assert.AreEqual(edge - 1000, BandPlan.Normalise(edge - 1000), $"{edge} - 1000 Hz");
            Assert.AreEqual(edge - 1250, BandPlan.Normalise(edge - 1250), $"{edge} - 1250 Hz");
            Assert.AreEqual(edge + 1000, BandPlan.Normalise(edge + 1000), $"{edge} + 1000 Hz");
            Assert.AreEqual(edge + 1250, BandPlan.Normalise(edge + 1250), $"{edge} + 1250 Hz");
        }

        // 29.9996 MHz is FM, 29.9994 MHz AM - after normalising 30.000 (FM) and 29.999 MHz (AM)
        Assert.AreEqual(30_000_000d, BandPlan.Normalise(29_999_600));
        Assert.AreEqual(29_999_000d, BandPlan.Normalise(29_999_400));
    }

    [TestMethod]
    public void NormaliseLeavesOtherFrequenciesAlone()
    {
        // edges where only the label or the sound changes (CB, PMR) are not rounded
        Assert.AreEqual(27_405_400d, BandPlan.Normalise(27_405_400));
        Assert.AreEqual(26_964_600d, BandPlan.Normalise(26_964_600));
        Assert.AreEqual(446_199_750d, BandPlan.Normalise(446_199_750));
        Assert.AreEqual(445_999_750d, BandPlan.Normalise(445_999_750));
        Assert.AreEqual(87_999_600d, BandPlan.Normalise(87_999_600));

        // well-known frequencies
        Assert.AreEqual(446_193_750d, BandPlan.Normalise(446_193_750));
        Assert.AreEqual(446_006_250d, BandPlan.Normalise(446_006_250));
        Assert.AreEqual(27_185_000d, BandPlan.Normalise(27_185_000));
        Assert.AreEqual(121_500_000d, BandPlan.Normalise(121_500_000));
        Assert.AreEqual(145_123_450d, BandPlan.Normalise(145_123_450));

        // like Clamp outside the tuning range
        Assert.AreEqual(BandPlan.MinFrequency, BandPlan.Normalise(0));
        Assert.AreEqual(BandPlan.MaxFrequency, BandPlan.Normalise(2e9));
        Assert.AreEqual(BandPlan.MaxFrequency, BandPlan.Normalise(double.PositiveInfinity));
        Assert.AreEqual(BandPlan.DefaultFrequency, BandPlan.Normalise(double.NaN));
        Assert.AreEqual(27_185_000d, BandPlan.Normalise(27_185_000.4));

        // idempotent
        foreach (var band in BandPlan.Bands)
        foreach (var frequency in new[] { band.FirstFrequency, band.LastFrequency, band.LowerEdge, band.UpperEdge })
            Assert.AreEqual(BandPlan.Normalise(frequency), BandPlan.Normalise(BandPlan.Normalise(frequency)));
    }

    [TestMethod]
    public void OffGridSendersNearModulationEdgesMatchAfterNormalisation()
    {
        foreach (var edge in BandPlan.ModulationEdges)
        {
            // the problem: 200 Hz apart - close enough to hear each other - but on different modulations
            Assert.IsTrue(RadioBase.FreqCloseEnough(edge - 100, edge + 100));
            Assert.AreNotEqual(BandPlan.GetBand(edge - 100).Modulation, BandPlan.GetBand(edge + 100).Modulation);

            // every frequency the direct entry can produce (10 Hz grid) within 2.5 kHz of the edge on both sides, plus
            // off-grid values (hotkeys / older clients / hand-edited files)
            var frequencies = new List<double>();
            for (var offset = -2500; offset <= 2500; offset += 10) frequencies.Add(edge + offset);
            foreach (var offset in new[] { 1, 3, 499, 501, 999, 1001, 1499, 1501 })
            {
                frequencies.Add(edge - offset);
                frequencies.Add(edge + offset);
            }

            // what each client tunes to and the modulation it uses there
            var tuned = frequencies.Select(BandPlan.Normalise).ToArray();
            var modulations = tuned.Select(f => BandPlan.GetBand(f).Modulation).ToArray();

            var pairs = 0;
            for (var sender = 0; sender < tuned.Length; sender++)
            for (var receiver = 0; receiver < tuned.Length; receiver++)
            {
                if (!RadioBase.FreqCloseEnough(tuned[sender], tuned[receiver])) continue;

                pairs++;
                if (modulations[sender] != modulations[receiver])
                    Assert.Fail(
                        $"{frequencies[sender]} Hz -> {tuned[sender]} Hz ({modulations[sender]}) and " +
                        $"{frequencies[receiver]} Hz -> {tuned[receiver]} Hz ({modulations[receiver]}) " +
                        "are close enough to hear each other but use different modulations");
            }

            Assert.IsGreaterThan(frequencies.Count, pairs, "senders and receivers within the receive tolerance");

            // off-grid senders right next to the edge meet receivers on the whole kHz of their side
            foreach (var offset in new[] { 1d, 10d, 100d, 250d, 499d, 500d, 750d, 999d })
            {
                var lastKHz = BandPlan.Normalise(edge - 500);
                var firstKHz = BandPlan.Normalise(edge + 500);

                Assert.AreEqual(lastKHz, BandPlan.Normalise(edge - offset), $"{edge} - {offset} Hz");
                Assert.AreEqual(BandPlan.GetBand(lastKHz).Modulation,
                    BandPlan.GetBand(BandPlan.Normalise(edge - offset)).Modulation);

                Assert.AreEqual(firstKHz, BandPlan.Normalise(edge + offset - 1), $"{edge} + {offset - 1} Hz");
                Assert.AreEqual(BandPlan.GetBand(firstKHz).Modulation,
                    BandPlan.GetBand(BandPlan.Normalise(edge + offset - 1)).Modulation);
            }
        }
    }

    [TestMethod]
    public void EveryBandModelIsShipped()
    {
        var models = RadioModelFactory.FromFolders(RepositoryFiles.RadioModelsFolder);

        foreach (var band in BandPlan.Bands)
            Assert.IsNotNull(models.GetModelInfo(band.Model), $"{band.Label}: radio model {band.Model} is missing");

        // "standard" stays the fallback for unknown model keys
        Assert.IsNotNull(models.GetModelInfo("standard"));
        Assert.AreEqual("standard", models.ResolveModelKey("nosuchmodel"));
    }

    [TestMethod]
    public void ModelKeysAreValidNetworkModelNames()
    {
        foreach (var band in BandPlan.Bands)
        {
            // RadioBase.Model keeps lower case letters and digits only
            var radio = new RadioBase { Model = band.Model };
            Assert.AreEqual(band.Model, radio.Model, band.Label);
        }
    }
}
