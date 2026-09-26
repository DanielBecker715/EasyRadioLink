using System.Collections.Generic;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>The client's one radio: band plan, and what goes over the wire.</summary>
[TestClass]
public class RadioTests
{
    private static void AssertRadio(Radio radio, double frequencyHz, Modulation modulation, string model)
    {
        Assert.AreEqual(frequencyHz, radio.freq);
        Assert.AreEqual(modulation, radio.modulation);
        Assert.AreEqual(model, radio.model);
        Assert.IsTrue(radio.IsEnabled);
    }

    [TestMethod]
    public void CreateAppliesTheBandPlan()
    {
        AssertRadio(Radio.Create(27_185_000), 27_185_000, Modulation.AM, "cb");
        AssertRadio(Radio.Create(446_193_750), 446_193_750, Modulation.FM, "walkie");
        AssertRadio(Radio.Create(121_500_000), 121_500_000, Modulation.AM, "airband");
        AssertRadio(Radio.Create(243_000_000), 243_000_000, Modulation.AM, "tactical");
        AssertRadio(Radio.Create(950_000_000), 950_000_000, Modulation.DIGITAL, "digital");
        AssertRadio(Radio.Create(1_500_000), 1_500_000, Modulation.AM, "vintage");

        Assert.AreEqual("CB", Radio.Create(27_405_000).Band.Label);
        Assert.AreEqual("PMR", Radio.Create(446_193_750).Band.Label);
    }

    [TestMethod]
    public void CreateClampsAndNormalisesTheFrequency()
    {
        AssertRadio(Radio.Create(0), BandPlan.MinFrequency, Modulation.AM, "vintage");
        AssertRadio(Radio.Create(-5), BandPlan.MinFrequency, Modulation.AM, "vintage");
        AssertRadio(Radio.Create(5e9), BandPlan.MaxFrequency, Modulation.DIGITAL, "digital");
        AssertRadio(Radio.Create(double.NaN), BandPlan.DefaultFrequency, Modulation.AM, "cb");
        AssertRadio(Radio.Create(27_185_000.4), 27_185_000, Modulation.AM, "cb");

        // next to a modulation edge: the whole kHz (restoring a remembered frequency)
        AssertRadio(Radio.Create(29_999_600), 30_000_000, Modulation.FM, "tactical");
        AssertRadio(Radio.Create(29_999_400), 29_999_000, Modulation.AM, "hf");
    }

    [TestMethod]
    public void CreateClampsTheVolume()
    {
        Assert.AreEqual(1.0f, Radio.Create(27_185_000).volume);
        Assert.AreEqual(0.4f, Radio.Create(27_185_000, 0.4f).volume);
        Assert.AreEqual(1.0f, Radio.Create(27_185_000, 3f).volume);
        Assert.AreEqual(0f, Radio.Create(27_185_000, -1f).volume);
        Assert.AreEqual(1.0f, Radio.Create(27_185_000, float.NaN).volume);
        Assert.AreEqual(1.0f, Radio.Create(27_185_000, float.PositiveInfinity).volume);
    }

    [TestMethod]
    public void ApplyBandPlanFollowsTheFrequency()
    {
        var radio = Radio.Create(27_185_000);

        radio.freq = 100_000_000;
        radio.ApplyBandPlan();
        AssertRadio(radio, 100_000_000, Modulation.FM, "walkie");

        radio.freq = 118_000_000;
        radio.ApplyBandPlan();
        AssertRadio(radio, 118_000_000, Modulation.AM, "airband");

        radio.freq = 7_100_000;
        radio.ApplyBandPlan();
        AssertRadio(radio, 7_100_000, Modulation.AM, "hf");

        // a new (switched off) radio is switched on by it
        var off = new Radio { freq = 446_006_250 };
        Assert.IsFalse(off.IsEnabled);
        off.ApplyBandPlan();
        AssertRadio(off, 446_006_250, Modulation.FM, "walkie");
    }

    [TestMethod]
    public void EqualsComparesOnlyWhatIsSent()
    {
        var radio = Radio.Create(27_185_000, 0.5f);

        var clone = radio.DeepClone();
        Assert.AreNotSame(radio, clone);
        Assert.AreEqual(radio, clone);

        // the volume is local
        clone.volume = 0.1f;
        Assert.AreEqual(radio, clone);
        Assert.AreEqual(0.5f, radio.volume);

        // within the receive tolerance
        clone.freq = 27_185_400;
        Assert.AreEqual(radio, clone);

        clone.freq = 27_186_000;
        Assert.AreNotEqual(radio, clone);
    }

    [TestMethod]
    public void ToRadioBaseNeverEncryptsAndHasNoGuardFrequency()
    {
        var wire = Radio.Create(446_193_750).ToRadioBase();

        Assert.AreEqual(446_193_750d, wire.freq);
        Assert.AreEqual(Modulation.FM, wire.modulation);
        Assert.AreEqual("walkie", wire.Model);
        Assert.IsFalse(wire.enc);
        Assert.AreEqual((byte)0, wire.encKey);
        Assert.AreEqual(0d, wire.secFreq);
    }

    [TestMethod]
    public void OnlySlot1IsSentAndOnlyWithTheBandPlan()
    {
        var info = new PlayerRadioInfo();
        info.radios[PlayerRadioInfo.RadioId] = Radio.Create(121_500_000);
        info.ambient = new Ambient { abType = "rain", vol = 0.3f };

        // whatever ends up in the other slots is never sent
        info.radios[0] = Radio.Create(27_185_000);
        info.radios[2] = Radio.Create(446_006_250);
        info.radios[10] = Radio.Create(950_000_000);

        var wire = info.ConvertToRadioBase();

        // the wire format keeps 11 slots: 0 reserved, 1 = the radio, 2..10 unused
        Assert.HasCount(Constants.MAX_RADIOS, wire.radios);

        for (var slot = 0; slot < wire.radios.Length; slot++)
        {
            var radio = wire.radios[slot];
            Assert.IsNotNull(radio, $"slot {slot}");
            Assert.IsFalse(radio.enc, $"slot {slot}");
            Assert.AreEqual((byte)0, radio.encKey, $"slot {slot}");
            Assert.AreEqual(0d, radio.secFreq, $"slot {slot}");

            if (slot != 1)
                Assert.AreEqual(Modulation.DISABLED, radio.modulation, $"slot {slot}");
        }

        var sent = wire.radios[1];
        Assert.AreEqual(121_500_000d, sent.freq);
        Assert.AreEqual(Modulation.AM, sent.modulation);
        Assert.AreEqual("airband", sent.Model);

        Assert.AreEqual("rain", wire.ambient.abType);
        Assert.AreEqual(0.3f, wire.ambient.vol);
        Assert.AreNotSame(info.ambient, wire.ambient);
    }

    [TestMethod]
    public void AllSlotsAreDisabledBeforeTheRadioIsSwitchedOn()
    {
        var wire = new PlayerRadioInfo().ConvertToRadioBase();

        Assert.HasCount(Constants.MAX_RADIOS, wire.radios);
        foreach (var radio in wire.radios)
        {
            Assert.AreEqual(Modulation.DISABLED, radio.modulation);
            Assert.IsFalse(radio.enc);
            Assert.AreEqual((byte)0, radio.encKey);
            Assert.AreEqual(0d, radio.secFreq);
        }
    }

    [TestMethod]
    public void TheRadioHearsItsFrequencyAndModulation()
    {
        var info = new PlayerRadioInfo();
        var radio = Radio.Create(27_185_000);
        info.radios[PlayerRadioInfo.RadioId] = radio;

        var heard = info.CanHearTransmission(27_185_400, Modulation.AM, 0, new List<int>(), out var state,
            out var decryptable);
        Assert.AreSame(radio, heard);
        Assert.IsNotNull(state);
        Assert.AreEqual(PlayerRadioInfo.RadioId, state.ReceivedOn);
        Assert.IsTrue(decryptable);

        // another modulation, another frequency, or half-duplex while transmitting: nothing
        Assert.IsNull(info.CanHearTransmission(27_185_000, Modulation.FM, 0, null, out state, out _));
        Assert.IsNull(state);
        Assert.IsNull(info.CanHearTransmission(27_186_000, Modulation.AM, 0, null, out _, out _));
        Assert.IsNull(info.CanHearTransmission(27_185_000, Modulation.AM, 0,
            new List<int> { PlayerRadioInfo.RadioId }, out _, out _));
    }
}
