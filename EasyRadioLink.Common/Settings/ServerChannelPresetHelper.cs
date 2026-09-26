using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;
using NLog;

namespace EasyRadioLink.Common.Settings;

public partial class ServerChannelPresetHelper
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly string _presetsFolder;

    public ServerChannelPresetHelper(string workingDirectory)
    {
        _presetsFolder = Path.Combine(workingDirectory, "Presets");
    }

    public ConcurrentDictionary<string, List<ServerPresetChannel>> Presets { get; } = new();

    public void LoadPresets()
    {
        Presets.Clear();

        FindRadioFiles();
    }

    private void FindRadioFiles()
    {
        try
        {
            if (Directory.Exists(_presetsFolder) == false) return;

            var files = Directory.EnumerateFiles(_presetsFolder);

            foreach (var fileAndPath in files)
                if (Path.GetExtension(fileAndPath).ToLowerInvariant() == ".txt")
                {
                    var name = Path.GetFileNameWithoutExtension(fileAndPath);

                    name = NormaliseString(name);

                    var presets = ReadFrequenciesFromFile(fileAndPath);

                    if (presets.Count > 0) Presets[name] = presets;
                }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error loading Server Presets");
        }
    }

    private List<ServerPresetChannel> ReadFrequenciesFromFile(string filePath)
    {
        return ParsePresetLines(File.ReadAllLines(filePath));
    }

    /// <summary>
    ///     One channel per line: "Name|MHz" or just "MHz", e.g. "Channel 19|27.185". Same rules as the client's preset
    ///     files (<see cref="RadioCalculator.TryParseMHz" />): a dot is the decimal separator, a single comma is accepted
    ///     too ("27,185"). Invalid lines are skipped.
    /// </summary>
    internal static List<ServerPresetChannel> ParsePresetLines(IEnumerable<string> lines)
    {
        var channels = new List<ServerPresetChannel>();
        if (lines == null) return channels;

        foreach (var line in lines)
        {
            var trimmed = line?.Trim() ?? "";
            if (trimmed.Length == 0) continue;

            var split = trimmed.Split('|');
            var name = split.Length >= 2 ? split[0].Trim() : trimmed;
            var frequencyText = split.Length >= 2 ? split[1] : trimmed;

            if (!RadioCalculator.TryParseMHz(frequencyText, out var frequencyHz))
            {
                Logger.Log(LogLevel.Info, "Error parsing frequency  " + trimmed);
                continue;
            }

            //in MHz - will transform client side to save some bytes
            channels.Add(new ServerPresetChannel
            {
                Name = name,
                Frequency = frequencyHz / RadioCalculator.MHz
            });
        }

        return channels;
    }

    private string NormaliseString(string str)
    {
        //only allow alphanumeric, remove all spaces etc
        return NormaliseRegex().Replace(str, "").ToLower();
    }

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NormaliseRegex();
}