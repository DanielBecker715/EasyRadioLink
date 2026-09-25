using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Models;

/// <summary>Modulation values are on the wire (UDP byte, TCP JSON int, radio files) and must never change.</summary>
[TestClass]
public class ModulationTests
{
    [TestMethod]
    public void NumericValuesAreStable()
    {
        var expected = new Dictionary<Modulation, int>
        {
            { Modulation.AM, 0 },
            { Modulation.FM, 1 },
            { Modulation.DISABLED, 3 },
            { Modulation.DIGITAL, 5 }
        };

        foreach (var (modulation, value) in expected)
            Assert.AreEqual(value, (int)modulation, modulation.ToString());
    }

    [TestMethod]
    public void OnlyTheStandaloneModulationsExist()
    {
        CollectionAssert.AreEquivalent(new[] { "AM", "FM", "DISABLED", "DIGITAL" }, Enum.GetNames<Modulation>());

        foreach (var retired in new[] { 2, 4, 6, 7 })
            Assert.IsFalse(Enum.IsDefined(typeof(Modulation), retired), $"{retired} is retired and must not be reused");
    }

    [TestMethod]
    public void RadioLayoutsSupportExactlyTheDefinedModulations()
    {
        foreach (var modulation in Enum.GetValues<Modulation>())
            Assert.IsTrue(RadioDefinition.IsSupportedModulation(modulation), modulation.ToString());

        foreach (var retired in new[] { 2, 4, 6, 7, 8, 255 })
            Assert.IsFalse(RadioDefinition.IsSupportedModulation((Modulation)retired), retired.ToString());
    }

    [TestMethod]
    public void ModulationIsSerialisedAsNumberInRadioJson()
    {
        var json = JsonSerializer.Serialize(new RadioBase { modulation = Modulation.DIGITAL, freq = 100000000 },
            new JsonSerializerOptions { IncludeFields = true });

        StringAssert.Contains(json, "\"modulation\":5");

        var radio = JsonSerializer.Deserialize<RadioBase>("{\"modulation\":1,\"freq\":446006250}",
            new JsonSerializerOptions { IncludeFields = true });
        Assert.AreEqual(Modulation.FM, radio.modulation);
    }

    [TestMethod]
    public void DigitalModulationSurvivesTheVoicePacket()
    {
        var packet = new UDPVoicePacket
        {
            GuidBytes = Encoding.ASCII.GetBytes("ufYS_WlLVkmFPjqCgxz6GA"),
            OriginalClientGuidBytes = Encoding.ASCII.GetBytes("ufYS_WlLVkmFPjqCgxz6GA"),
            AudioPart1Bytes = new byte[] { 1, 2, 3 },
            AudioPart1Length = 3,
            Frequencies = new[] { 100000000d, 27185000d },
            Modulations = new[] { (byte)Modulation.DIGITAL, (byte)Modulation.AM },
            Encryptions = new byte[] { 7, 0 },
            PacketNumber = 1
        };

        var decoded = UDPVoicePacket.DecodeVoicePacket(packet.EncodePacket());

        Assert.IsNotNull(decoded);
        CollectionAssert.AreEqual(new[] { (byte)5, (byte)0 }, decoded.Modulations);
        Assert.AreEqual(Modulation.DIGITAL, (Modulation)decoded.Modulations.First());
    }
}
