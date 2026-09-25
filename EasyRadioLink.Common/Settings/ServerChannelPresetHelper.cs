using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
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

    // One channel per line: "Name|MHz" or just "MHz" (invariant culture, e.g. "Channel 19|27.185").
    private List<ServerPresetChannel> ReadFrequenciesFromFile(string filePath)
    {
        var channels = new List<ServerPresetChannel>();
        var lines = File.ReadAllLines(filePath);

        if (lines?.Length > 0)
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    try
                    {
                        var split = trimmed.Split('|');

                        var name = "";
                        double frequency = 0;
                        if (split.Length >= 2)
                        {
                            name = split[0].Trim();
                            frequency = double.Parse(split[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            name = trimmed;
                            frequency = double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
                        }

                        //assume its in MHz - will transform client side to save some bytes 
                        channels.Add(new ServerPresetChannel
                        {
                            Name = name,
                            Frequency = frequency
                        });
                    }
                    catch (Exception)
                    {
                        Logger.Log(LogLevel.Info, "Error parsing frequency  " + trimmed);
                    }
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