using System.IO;
using System.Linq;
using EasyRadioLink.Client.GameIntegration.Msfs;
using EasyRadioLink.Common.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>The client ships SimConnect.dll, so the MSFS integration works without installing anything.</summary>
[TestClass]
public class SimConnectLibraryTests
{
    [TestMethod]
    public void SimConnectIsLookedForNextToTheProgramFirst()
    {
        Assert.AreEqual(Path.Combine(AppPaths.ProgramDirectory, SimConnectLibrary.FileName),
            SimConnectLibrary.CandidatePaths().First());
    }

    [TestMethod]
    public void TheShippedSimConnectLoadsWithEveryFunctionTheIntegrationUses()
    {
        // copied next to the client (and so next to the tests) by the client project
        var shipped = Path.Combine(AppPaths.ProgramDirectory, SimConnectLibrary.FileName);
        Assert.IsTrue(File.Exists(shipped), shipped);

        // binding fails (and TryLoad returns null) if an export is missing
        var library = SimConnectLibrary.TryLoad();

        Assert.IsNotNull(library);
        Assert.AreEqual(shipped, library.Path);
    }
}
