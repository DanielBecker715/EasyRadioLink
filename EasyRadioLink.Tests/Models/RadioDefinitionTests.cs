using System.Collections.Generic;
using System.Text.Json;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings.Setting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Models;

[TestClass]
public class RadioDefinitionTests
{
    private static RadioDefinition Cb()
    {
        return new RadioDefinition
        {
            name = "CB", model = "cb", modulation = Modulation.AM, freq = 27185000, freqMin = 26965000,
            freqMax = 27405000
        };
    }

    [TestMethod]
    public void NormaliseAlwaysReturnsElevenEntriesWithReservedSlotZero()
    {
        var empty = RadioDefinition.Normalise(null);
        Assert.HasCount(Constants.MAX_RADIOS, empty);
        foreach (var radio in empty) Assert.AreEqual(Modulation.DISABLED, radio.modulation);
        Assert.AreEqual("Reserved", empty[0].name);
        Assert.AreEqual("Radio 10", empty[10].name);

        var eleven = new List<RadioDefinition>();
        for (var i = 0; i < Constants.MAX_RADIOS; i++) eleven.Add(Cb());

        var result = RadioDefinition.Normalise(eleven);
        Assert.HasCount(Constants.MAX_RADIOS, result);
        Assert.AreEqual(Modulation.DISABLED, result[0].modulation, "slot 0 is always disabled");
        Assert.AreEqual(Modulation.AM, result[1].modulation);
    }

    [TestMethod]
    public void UserRadiosOnlyAreShiftedToSlotOne()
    {
        var result = RadioDefinition.Normalise(new List<RadioDefinition> { Cb(), Cb() });

        Assert.HasCount(Constants.MAX_RADIOS, result);
        Assert.AreEqual(Modulation.DISABLED, result[0].modulation);
        Assert.AreEqual("CB", result[1].name);
        Assert.AreEqual("CB", result[2].name);
        Assert.AreEqual(Modulation.DISABLED, result[3].modulation);
    }

    [TestMethod]
    public void ListStartingWithDisabledSlotKeepsItsIndices()
    {
        var result = RadioDefinition.Normalise(new List<RadioDefinition>
            { new() { name = "Reserved", modulation = Modulation.DISABLED }, Cb() });

        Assert.AreEqual("CB", result[1].name);
        Assert.AreEqual(Modulation.DISABLED, result[2].modulation);
    }

    [TestMethod]
    public void TooManyEntriesAreTrimmed()
    {
        var list = new List<RadioDefinition> { new() { modulation = Modulation.DISABLED } };
        for (var i = 0; i < 15; i++) list.Add(Cb());

        Assert.HasCount(Constants.MAX_RADIOS, RadioDefinition.Normalise(list));
    }

    [TestMethod]
    public void UnknownModulationsBecomeDisabled()
    {
        var list = new List<RadioDefinition> { new() { modulation = Modulation.DISABLED } };
        foreach (var value in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 99 })
            list.Add(new RadioDefinition { modulation = (Modulation)value, freq = 100000000 });

        var result = RadioDefinition.Normalise(list);

        Assert.AreEqual(Modulation.AM, result[1].modulation);
        Assert.AreEqual(Modulation.FM, result[2].modulation);
        Assert.AreEqual(Modulation.DISABLED, result[3].modulation); // 2 (retired value)
        Assert.AreEqual(Modulation.DISABLED, result[4].modulation);
        Assert.AreEqual(Modulation.DISABLED, result[5].modulation); // 4
        Assert.AreEqual(5, (int)result[6].modulation); // DIGITAL
        Assert.AreEqual(Modulation.DISABLED, result[7].modulation); // 6
        Assert.AreEqual(Modulation.DISABLED, result[8].modulation); // 7
        Assert.AreEqual(Modulation.DISABLED, result[9].modulation); // 99
    }

    [TestMethod]
    public void FrequenciesAreClampedIntoTheirRange()
    {
        var tooHigh = Cb();
        tooHigh.freq = 28000000;
        var swapped = Cb();
        swapped.freqMin = 27405000;
        swapped.freqMax = 26965000;
        swapped.freq = 1;
        var fixedFreq = new RadioDefinition { name = "Fixed", modulation = Modulation.FM, freq = 446006250 };

        var result = RadioDefinition.Normalise(new List<RadioDefinition> { tooHigh, swapped, fixedFreq });

        Assert.AreEqual(27405000d, result[1].freq);
        Assert.AreEqual(26965000d, result[2].freqMin);
        Assert.AreEqual(27405000d, result[2].freqMax);
        Assert.AreEqual(26965000d, result[2].freq);
        Assert.AreEqual(446006250d, result[3].freq);
        Assert.AreEqual(446006250d, result[3].freqMin);
        Assert.AreEqual(446006250d, result[3].freqMax);
    }

    [TestMethod]
    public void EncryptionIsValidated()
    {
        var noKey = Cb();
        noKey.encCapable = true;
        noKey.encKey = 0;
        var bigKey = Cb();
        bigKey.encCapable = true;
        bigKey.encKey = 255;
        var notCapable = Cb();
        notCapable.enc = true;
        notCapable.encKey = 7;

        var result = RadioDefinition.Normalise(new List<RadioDefinition> { noKey, bigKey, notCapable });

        Assert.AreEqual((byte)1, result[1].encKey);
        Assert.AreEqual((byte)252, result[2].encKey);
        Assert.IsFalse(result[3].enc);
    }

    [TestMethod]
    public void NormaliseDoesNotModifyTheInput()
    {
        var input = Cb();
        input.freq = 1;
        input.name = "  CB  ";

        RadioDefinition.Normalise(new List<RadioDefinition> { input });

        Assert.AreEqual(1d, input.freq);
        Assert.AreEqual("  CB  ", input.name);
    }

    [TestMethod]
    public void ParseListIsTolerant()
    {
        var radios = RadioDefinition.ParseList("""
            [
              // comment
              { "Name": "CB", "MODULATION": 0, "freq": 27185000, "guardFreq": 0, "encCapable": false, "channel": 19, "unknownField": 1 },
              { "name": "Airband", "modulation": 0, "freq": 124800000, "guardFreq": 121500000, "encCapable": true, },
            ]
            """);

        Assert.HasCount(2, radios);
        Assert.AreEqual("CB", radios[0].name);
        Assert.AreEqual(Modulation.AM, radios[0].modulation);
        Assert.AreEqual(19, radios[0].channel);
        Assert.AreEqual(121500000d, radios[1].guardFreq);
        Assert.IsTrue(radios[1].encCapable);

        Assert.IsEmpty(RadioDefinition.ParseList(""));
        Assert.ThrowsExactly<JsonException>(() => RadioDefinition.ParseList("{ not json"));
    }

    [TestMethod]
    public void ServerRadioPresetIsValidatedOnTheClient()
    {
        var settings = new SyncedServerSettings();

        settings.Decode(new Dictionary<string, string>
        {
            { ServerSettingsKeys.SERVER_RADIO_PRESET.ToString(), "[{\"name\":\"CB\",\"modulation\":0,\"freq\":27185000}]" }
        });

        Assert.HasCount(Constants.MAX_RADIOS, settings.ServerRadioPreset);
        Assert.AreEqual("CB", settings.ServerRadioPreset[1].name);
        Assert.AreNotEqual("", settings.ServerRadioPresetJson);

        settings.Decode(new Dictionary<string, string> { { ServerSettingsKeys.SERVER_RADIO_PRESET.ToString(), "[]" } });
        Assert.IsEmpty(settings.ServerRadioPreset);
        Assert.AreEqual("", settings.ServerRadioPresetJson);

        settings.Decode(new Dictionary<string, string>
            { { ServerSettingsKeys.SERVER_RADIO_PRESET.ToString(), "garbage" } });
        Assert.IsEmpty(settings.ServerRadioPreset);
    }
}
