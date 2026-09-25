using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Settings;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Client.Settings.RadioChannels;

/// <summary>
///     Preset channels from text files in the presets folder (global setting <c>LastPresetsFolder</c>, default
///     <see cref="AppPaths.PresetsDirectory" />). The file of a radio is <c>&lt;normalised radio name&gt;.txt</c>; each
///     line is <c>Name|MHz</c> or just <c>MHz</c> (invariant culture, e.g. <c>Channel 19|27.185</c>).
/// </summary>
public partial class FilePresetChannelsStore : IPresetChannelsStore
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;

    /// <summary>The configured presets folder, or the default one if none is set or it no longer exists.</summary>
    public static string PresetsFolder
    {
        get
        {
            var folder = _globalSettings.GetClientSetting(GlobalSettingsKeys.LastPresetsFolder).RawValue;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) folder = AppPaths.PresetsDirectory;

            return folder;
        }
    }

    public IEnumerable<PresetChannel> LoadFromStore(string radioName)
    {
        var file = FindRadioFile(NormaliseString(radioName));

        if (file != null) return ReadFrequenciesFromFile(file);

        return new List<PresetChannel>();
    }

    public string CreatePresetFile(string radioName)
    {
        var normalisedName = NormaliseString(radioName);
        if (normalisedName.Length == 0) return null;

        var file = FindRadioFile(normalisedName);

        if (file == null)
        {
            var path = Path.ChangeExtension(Path.Combine(PresetsFolder, normalisedName), "txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Create(path).Dispose();
                Logger.Info($"Created radio file {path} ");
                return path;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Error creating radio file {path} ");
            }

            return null;
        }

        return file;
    }

    private static List<PresetChannel> ReadFrequenciesFromFile(string filePath)
    {
        var channels = new List<PresetChannel>();

        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Unable to read preset file {filePath}");
            return channels;
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var split = trimmed.Split('|');

            var name = split.Length >= 2 ? split[0].Trim() : trimmed;
            var frequencyText = split.Length >= 2 ? split[1] : trimmed;

            // invariant culture: "27.185" is always 27.185 MHz (a single ',' is accepted as decimal separator)
            if (RadioCalculator.TryParseMHz(frequencyText, out var frequencyHz))
                channels.Add(new PresetChannel
                {
                    Text = name,
                    Value = frequencyHz
                });
            else
                Logger.Info("Error parsing frequency  " + trimmed);
        }

        return channels;
    }

    private static string FindRadioFile(string radioName)
    {
        if (string.IsNullOrEmpty(radioName)) return null;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(PresetsFolder, "*.txt");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Unable to list the presets folder {PresetsFolder}");
            return null;
        }

        // an exact name match wins ("radio1.txt" must not be used for "Radio 10" if "radio10.txt" exists),
        // otherwise the file name may be a prefix of the radio name ("uhf.txt" for "UHF Tactical")
        string prefixMatch = null;
        foreach (var fileAndPath in files)
            if (Path.GetExtension(fileAndPath).ToLowerInvariant() == ".txt")
            {
                var name = NormaliseString(Path.GetFileNameWithoutExtension(fileAndPath));

                if (name.Length == 0) continue;

                if (name == radioName) return fileAndPath;

                if (prefixMatch == null && radioName.StartsWith(name)) prefixMatch = fileAndPath;
            }

        return prefixMatch;
    }

    public static string NormaliseString(string str)
    {
        //only allow alphanumeric, remove all spaces etc
        return NormaliseRegex().Replace(str ?? "", "").ToLowerInvariant();
    }

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NormaliseRegex();
}
