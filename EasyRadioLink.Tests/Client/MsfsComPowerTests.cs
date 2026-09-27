using EasyRadioLink.Client.GameIntegration.Msfs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>SimConnect COM STATUS: when the radio is dead (checked in the sim: battery / avionics off = 2).</summary>
[TestClass]
public class MsfsComPowerTests
{
    [TestMethod]
    public void NoElectricityOrAFailureSwitchesTheRadioOff()
    {
        Assert.IsTrue(MsfsIntegration.IsComDead(2), "no electricity");
        Assert.IsTrue(MsfsIntegration.IsComDead(3), "failed");
    }

    [TestMethod]
    public void AWorkingRadioOrOneTheAircraftDoesNotHaveStaysOn()
    {
        Assert.IsFalse(MsfsIntegration.IsComDead(0), "OK");
        Assert.IsFalse(MsfsIntegration.IsComDead(1), "does not exist");
        Assert.IsFalse(MsfsIntegration.IsComDead(-1), "invalid");
    }
}
