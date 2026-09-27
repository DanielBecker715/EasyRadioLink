using System.IO;
using EasyRadioLink.Client.GameIntegration;
using EasyRadioLink.Common.Tests.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>The background sound chosen for the aircraft flown in the simulator.</summary>
[TestClass]
public class AircraftBackgroundSoundTests
{
    [TestMethod]
    public void HelicoptersGetTheHelicopterSound()
    {
        Assert.AreEqual("helicopter",
            AircraftBackgroundSound.ForAircraft("Helicopter", AircraftBackgroundSound.EngineTypeHeloTurbine));
        // piston helicopters (e.g. Robinson R22) are helicopters too
        Assert.AreEqual("helicopter",
            AircraftBackgroundSound.ForAircraft("Helicopter", AircraftBackgroundSound.EngineTypePiston));
        Assert.AreEqual("helicopter",
            AircraftBackgroundSound.ForAircraft("", AircraftBackgroundSound.EngineTypeHeloTurbine));
    }

    [TestMethod]
    public void AirplanesGetJetOrPropByTheirEngines()
    {
        Assert.AreEqual("jet", AircraftBackgroundSound.ForAircraft("Airplane", AircraftBackgroundSound.EngineTypeJet));
        Assert.AreEqual("prop",
            AircraftBackgroundSound.ForAircraft("Airplane", AircraftBackgroundSound.EngineTypePiston));
        Assert.AreEqual("prop",
            AircraftBackgroundSound.ForAircraft("airplane ", AircraftBackgroundSound.EngineTypeTurboprop));
    }

    [TestMethod]
    public void GlidersAndUnknownAircraftKeepTheProfileSound()
    {
        Assert.IsNull(AircraftBackgroundSound.ForAircraft("Airplane", AircraftBackgroundSound.EngineTypeNone));
        Assert.IsNull(AircraftBackgroundSound.ForAircraft("Airplane", AircraftBackgroundSound.EngineTypeUnsupported));
        Assert.IsNull(AircraftBackgroundSound.ForAircraft("Boat", AircraftBackgroundSound.EngineTypePiston));
    }

    [TestMethod]
    public void EverySoundIsAShippedBackgroundSound()
    {
        foreach (var sound in new[]
                 {
                     AircraftBackgroundSound.Helicopter, AircraftBackgroundSound.Jet, AircraftBackgroundSound.Prop
                 })
            Assert.IsTrue(File.Exists(Path.Combine(RepositoryFiles.AudioEffectsFolder, "Background", sound + ".wav")),
                sound);
    }
}
