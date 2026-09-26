using System.IO;
using EasyRadioLink.Common.Network.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Settings;

[TestClass]
public class ServerFilesTests
{
    private static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), "erl-server-data");

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
