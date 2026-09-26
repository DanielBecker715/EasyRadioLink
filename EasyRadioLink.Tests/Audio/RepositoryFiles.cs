using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Audio;

/// <summary>Locates the shipped client assets in the source tree (the tests run from the test project's bin folder).</summary>
internal static class RepositoryFiles
{
    private static readonly Lazy<string> Root = new(FindRoot);

    public static string ClientFolder => Path.Combine(Root.Value, "EasyRadioLink.Client");

    public static string RadioModelsFolder => Path.Combine(ClientFolder, "RadioModels");

    public static string AudioEffectsFolder => Path.Combine(ClientFolder, "AudioEffects");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EasyRadioLink.sln"))) return directory.FullName;

            directory = directory.Parent;
        }

        Assert.Fail($"EasyRadioLink.sln not found above {AppContext.BaseDirectory}");
        return null;
    }
}
