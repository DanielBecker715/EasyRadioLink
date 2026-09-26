using System.IO;
using EasyRadioLink.Common.Network.Server;
using EasyRadioLink.Common.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Settings;

[TestClass]
public class ServerFilesTests
{
    private static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), "erl-server-data");

    [TestMethod]
    public void PresetLinesUseTheSameRulesAsClientPresetFiles()
    {
        var channels = ServerChannelPresetHelper.ParsePresetLines(new[]
        {
            "Channel 19|27.185",
            "Kanal 9|27,065", // German Windows: a single comma is a decimal separator
            " 446.00625 ",
            "",
            "no frequency here",
            "Broken|27.1.85",
            "Negative|-5"
        });

        Assert.HasCount(3, channels);

        Assert.AreEqual("Channel 19", channels[0].Name);
        Assert.AreEqual(27.185, channels[0].Frequency, 1e-9);

        Assert.AreEqual("Kanal 9", channels[1].Name);
        Assert.AreEqual(27.065, channels[1].Frequency, 1e-9);

        Assert.AreEqual("446.00625", channels[2].Name);
        Assert.AreEqual(446.00625, channels[2].Frequency, 1e-9);
    }

    [TestMethod]
    public void ClientExportDefaultsToTheConfigFolder()
    {
        var expected = Path.Combine(DataDirectory, "clients-list.json");

        Assert.AreEqual(expected, ServerState.ResolveExportFilePath(null, DataDirectory));
        Assert.AreEqual(expected, ServerState.ResolveExportFilePath("", DataDirectory));
        Assert.AreEqual(expected, ServerState.ResolveExportFilePath("clients-list.json", DataDirectory));
    }

    [TestMethod]
    public void RelativeClientExportPathIsRelativeToTheConfigFolder()
    {
        Assert.AreEqual(Path.Combine(DataDirectory, "export.json"),
            ServerState.ResolveExportFilePath("export.json", DataDirectory));
        Assert.AreEqual(Path.Combine(DataDirectory, "web", "clients.json"),
            ServerState.ResolveExportFilePath(Path.Combine("web", "clients.json"), DataDirectory));
    }

    [TestMethod]
    public void AbsoluteClientExportPathIsKept()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "somewhere", "clients.json");

        Assert.AreEqual(absolute, ServerState.ResolveExportFilePath(absolute, DataDirectory));
        Assert.AreEqual(absolute, ServerState.ResolveExportFilePath("\"" + absolute + "\"", DataDirectory));
        Assert.AreEqual(absolute, ServerState.ResolveExportFilePath(new System.Uri(absolute).AbsoluteUri, DataDirectory));
    }

    [TestMethod]
    public void UnusableClientExportPathFallsBackToTheDefault()
    {
        var expected = Path.Combine(DataDirectory, "clients-list.json");

        // a folder instead of a file
        Assert.AreEqual(expected,
            ServerState.ResolveExportFilePath(Path.GetTempPath(), DataDirectory));
        Assert.AreEqual(expected, ServerState.ResolveExportFilePath("file://", DataDirectory));
    }
}
