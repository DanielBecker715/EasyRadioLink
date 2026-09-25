using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings.Setting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Audio;

[TestClass]
public class RadioEffectRulesTests
{
    [TestMethod]
    public void HfStaticReachesUpToThirtyMegahertz()
    {
        Assert.IsTrue(RadioEffectRules.IsHfNoise(7100000), "40 m band");
        Assert.IsTrue(RadioEffectRules.IsHfNoise(26965000), "CB channel 1");
        Assert.IsTrue(RadioEffectRules.IsHfNoise(27405000), "CB channel 40");
        Assert.IsTrue(RadioEffectRules.IsHfNoise(30000000));
        Assert.IsFalse(RadioEffectRules.IsHfNoise(30001000));
        Assert.IsFalse(RadioEffectRules.IsHfNoise(124800000));
        Assert.IsFalse(RadioEffectRules.IsHfNoise(446006250));
    }

    [TestMethod]
    public void FrequencyNoiseGainIsFiniteAndNeverPositive()
    {
        Assert.AreEqual(0, RadioEffectRules.FrequencyNoiseGainDb(1e6), 1e-9);
        Assert.AreEqual(0, RadioEffectRules.FrequencyNoiseGainDb(0), 1e-9, "0 Hz must not produce +infinity");
        Assert.AreEqual(0, RadioEffectRules.FrequencyNoiseGainDb(-5), 1e-9);
        Assert.AreEqual(0, RadioEffectRules.FrequencyNoiseGainDb(double.NaN), 1e-9);
        Assert.AreEqual(0, RadioEffectRules.FrequencyNoiseGainDb(100000), 1e-9, "below 1 MHz is clamped");
        Assert.AreEqual(-17.0, RadioEffectRules.FrequencyNoiseGainDb(30e6), 0.1);

        var previous = double.MaxValue;
        foreach (var frequency in new[] { 1e6, 7.1e6, 27.185e6, 124.8e6, 251e6, 446e6 })
        {
            var gain = RadioEffectRules.FrequencyNoiseGainDb(frequency);
            Assert.IsLessThan(previous, gain, "higher frequencies get less static");
            previous = gain;
        }
    }

    [TestMethod]
    public void DigitalIsTheOnlyCleanPath()
    {
        Assert.IsTrue(RadioEffectRules.IsCleanPath(Modulation.DIGITAL));
        Assert.IsFalse(RadioEffectRules.IsCleanPath(Modulation.AM));
        Assert.IsFalse(RadioEffectRules.IsCleanPath(Modulation.FM));
    }

    [TestMethod]
    public void FmToneOnlyOnFm()
    {
        Assert.IsTrue(RadioEffectRules.HasFmTone(Modulation.FM));
        Assert.IsFalse(RadioEffectRules.HasFmTone(Modulation.AM));
        Assert.IsFalse(RadioEffectRules.HasFmTone(Modulation.DIGITAL));
        Assert.IsFalse(RadioEffectRules.HasFmTone(Modulation.DISABLED));
    }

    [TestMethod]
    public void SquelchTailOnlyForAmAndFmWithRadioEffects()
    {
        Assert.IsTrue(RadioEffectRules.HasSquelchTail(Modulation.AM, false));
        Assert.IsTrue(RadioEffectRules.HasSquelchTail(Modulation.FM, false));
        Assert.IsFalse(RadioEffectRules.HasSquelchTail(Modulation.DIGITAL, false));
        Assert.IsFalse(RadioEffectRules.HasSquelchTail(Modulation.DISABLED, false));
        Assert.IsFalse(RadioEffectRules.HasSquelchTail(Modulation.AM, true), "clean frequency");
        Assert.IsFalse(RadioEffectRules.HasSquelchTail(Modulation.FM, true), "clean frequency");
    }

    [TestMethod]
    public void OnlyFmWithRadioEffectsTakesPartInFmCapture()
    {
        Assert.IsTrue(RadioEffectRules.TakesPartInFmCapture(Modulation.FM, false));
        Assert.IsFalse(RadioEffectRules.TakesPartInFmCapture(Modulation.FM, true));
        Assert.IsFalse(RadioEffectRules.TakesPartInFmCapture(Modulation.AM, false));
        Assert.IsFalse(RadioEffectRules.TakesPartInFmCapture(Modulation.DIGITAL, false));
    }

    [TestMethod]
    public void CleanFrequenciesPlayWithoutRadioEffects()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var settings = new SyncedServerSettings();
            settings.Decode(new Dictionary<string, string>
            {
                { ServerSettingsKeys.CLEAN_FREQUENCIES.ToString(), "27.405, 446.19375" }
            }, false);

            Assert.IsTrue(RadioEffectRules.PlayWithoutRadioEffects(false, 27405000, settings));
            Assert.IsTrue(RadioEffectRules.PlayWithoutRadioEffects(false, 27405000 + 499, settings), "within 500 Hz");
            Assert.IsTrue(RadioEffectRules.PlayWithoutRadioEffects(false, 446193750, settings));
            Assert.IsFalse(RadioEffectRules.PlayWithoutRadioEffects(false, 27405000 + 1000, settings));
            Assert.IsFalse(RadioEffectRules.PlayWithoutRadioEffects(false, 27185000, settings));
            Assert.IsFalse(RadioEffectRules.PlayWithoutRadioEffects(false, 27405, settings), "MHz must not be read as kHz");

            Assert.IsTrue(RadioEffectRules.PlayWithoutRadioEffects(true, 27185000, settings), "flagged by the caller");
            Assert.IsFalse(RadioEffectRules.PlayWithoutRadioEffects(false, 27405000, null));

            // no clean frequencies configured (default)
            Assert.IsFalse(RadioEffectRules.PlayWithoutRadioEffects(false, 27405000, new SyncedServerSettings()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestMethod]
    public void SquelchTailFadesOutToSilence()
    {
        var noise = Enumerable.Range(0, 30000).Select(i => i % 2 == 0 ? 0.5f : -0.5f).ToArray();
        var tail = new float[SquelchTail.Samples];

        var written = SquelchTail.Render(noise, 1000, tail, 0.8f);

        Assert.AreEqual(Constants.OUTPUT_SAMPLE_RATE * SquelchTail.DurationMs / 1000, written);
        Assert.AreEqual(0f, tail[0], "fades in - no click");
        Assert.IsLessThan(0.001f, Math.Abs(tail[written - 1]), "fades out to silence");
        Assert.IsTrue(tail.All(sample => Math.Abs(sample) <= 0.5f * 0.8f + 1e-6f));

        // louder at the start than at the end
        var start = tail.Skip(100).Take(1000).Average(Math.Abs);
        var end = tail.Skip(written - 1100).Take(1000).Average(Math.Abs);
        Assert.IsGreaterThan(end * 10, start);
    }

    [TestMethod]
    public void SquelchTailHandlesShortSourcesAndBadInput()
    {
        var noise = new float[] { 1, -1, 1, -1, 1, -1 };
        var tail = new float[SquelchTail.Samples];

        Assert.AreEqual(noise.Length, SquelchTail.Render(noise, 0, tail, 1f));
        Assert.AreEqual(noise.Length, SquelchTail.Render(noise, 99, tail, 1f), "out of range offset starts at 0");
        Assert.AreEqual(0, SquelchTail.Render(Array.Empty<float>(), 0, tail, 1f));

        SquelchTail.Render(noise, 0, tail, 5f);
        Assert.IsTrue(tail.All(sample => Math.Abs(sample) <= 1f), "gain is clamped to 1");

        SquelchTail.Render(noise, 0, tail, -1f);
        Assert.IsTrue(tail.Take(noise.Length).All(sample => sample == 0f), "negative gain is silence");
    }

    private static TransmissionSegment Segment(string guid, Modulation modulation = Modulation.FM,
        bool noAudioEffects = false)
    {
        return new TransmissionSegment(new DeJitteredTransmission
        {
            OriginalClientGuid = guid,
            Modulation = modulation,
            NoAudioEffects = noAudioEffects,
            Volume = 1f,
            PCMMonoAudio = new float[] { 0.1f, 0.2f },
            PCMAudioLength = 2
        });
    }

    [TestMethod]
    public void FirstFmStationKeepsTheChannel()
    {
        var first = Segment("first");
        var second = Segment("second");

        Assert.IsNull(ClientEffectsPipeline.SelectCapturedFmSegment(null, null));
        Assert.IsNull(ClientEffectsPipeline.SelectCapturedFmSegment(new List<TransmissionSegment>(), "first"));

        // nobody holds the channel: the first transmission captures it
        Assert.AreSame(first, ClientEffectsPipeline.SelectCapturedFmSegment(new[] { first, second }, null));

        // the holder keeps it even if it is not first in the list
        Assert.AreSame(second, ClientEffectsPipeline.SelectCapturedFmSegment(new[] { first, second }, "second"));

        // the holder stopped transmitting: the next one captures it
        Assert.AreSame(first, ClientEffectsPipeline.SelectCapturedFmSegment(new[] { first }, "second"));
    }

    [TestMethod]
    public void TransmissionSegmentCarriesVolumeAndFlags()
    {
        var segment = Segment("guid", Modulation.AM, true);

        Assert.AreEqual(Modulation.AM, segment.Modulation);
        Assert.IsTrue(segment.NoAudioEffects);
        Assert.AreEqual(1f, segment.Volume);
        CollectionAssert.AreEqual(new[] { 0.1f, 0.2f }, segment.Audio);
    }
}
