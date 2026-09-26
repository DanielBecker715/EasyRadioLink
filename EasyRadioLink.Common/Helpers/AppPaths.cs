using System;
using System.IO;
using NLog;

namespace EasyRadioLink.Common.Helpers;

/// <summary>
///     Well-known file locations of the client.
///     <para>
///         User data lives in <see cref="UserDataDirectory" /> (<c>%AppData%\EasyRadioLink</c>). The settings
///         (global.cfg, profiles), favourites, the radio state and the client logs are in the settings folder
///         <c>GlobalSettingsStore.Path</c>, which the <c>-cfg=&lt;directory&gt;</c> argument moves; custom radio models
///         and recordings always use the locations below. Built-in data (radio models, audio effects) is resolved
///         relative to <see cref="ProgramDirectory" />, never the working directory.
///     </para>
///     <para>
///         The server does NOT use these locations: server.cfg defaults to the server's program folder, and banned.txt,
///         logs, the transmission log and the client export live next to server.cfg
///         (see <c>ServerSettingsStore.ConfigDirectory</c>).
///     </para>
///     All directory properties create the directory on first access (errors are logged, not thrown).
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "EasyRadioLink";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Directory of the running executable (with trailing separator, as returned by the runtime).</summary>
    public static string ProgramDirectory => AppContext.BaseDirectory;

    /// <summary><c>%AppData%\EasyRadioLink</c> - created on demand.</summary>
    public static string UserDataDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                // no roaming profile (e.g. service account) - fall back to a folder next to the executable
                appData = ProgramDirectory;

            return EnsureDirectory(Path.Combine(appData, AppFolderName));
        }
    }

    /// <summary><c>%AppData%\EasyRadioLink\RadioModels</c> - user overrides of the built-in radio models.</summary>
    public static string CustomRadioModelsDirectory => EnsureDirectory(Path.Combine(UserDataDirectory, "RadioModels"));

    /// <summary><c>Documents\EasyRadioLink\Recordings</c>.</summary>
    public static string RecordingsDirectory
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                return EnsureDirectory(Path.Combine(UserDataDirectory, "Recordings"));

            return EnsureDirectory(Path.Combine(documents, AppFolderName, "Recordings"));
        }
    }

    /// <summary>Full path of a built-in file shipped next to the executable, e.g. <c>GetProgramFile("whitelist.txt")</c>.</summary>
    public static string GetProgramFile(params string[] relativePathParts)
    {
        if (relativePathParts == null || relativePathParts.Length == 0) return ProgramDirectory;

        var parts = new string[relativePathParts.Length + 1];
        parts[0] = ProgramDirectory;
        Array.Copy(relativePathParts, 0, parts, 1, relativePathParts.Length);
        return Path.Combine(parts);
    }

    /// <summary>Full path of a file in <see cref="UserDataDirectory" />, e.g. <c>GetUserDataFile("radio-state.json")</c>.</summary>
    public static string GetUserDataFile(string fileName)
    {
        return Path.Combine(UserDataDirectory, fileName);
    }

    private static string EnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Unable to create directory {directory}");
        }

        return directory;
    }
}
