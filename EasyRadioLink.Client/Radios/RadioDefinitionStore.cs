using System;
using System.Collections.Generic;
using System.IO;
using EasyRadioLink.Common;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using NLog;

namespace EasyRadioLink.Client.Radios;

/// <summary>Where the radio layout of a session came from.</summary>
public enum RadioLayoutSource
{
    // SERVER_RADIO_PRESET sent by the server (and allowed by the profile setting AllowServerRadioPreset)
    Server,

    // radios-custom.json in the user's configuration directory
    Custom,

    // radios.json shipped next to the executable
    BuiltIn,

    // nothing could be loaded - every radio is disabled
    None
}

/// <summary>A validated radio layout: exactly <see cref="Constants.MAX_RADIOS" /> entries, slot 0 disabled.</summary>
public sealed class RadioLayout
{
    public RadioLayout(List<RadioDefinition> definitions, RadioLayoutSource source, string sourceDescription,
        string serverPresetJson = "")
    {
        Definitions = definitions;
        Source = source;
        SourceDescription = sourceDescription;
        ServerPresetJson = serverPresetJson ?? "";
    }

    public IReadOnlyList<RadioDefinition> Definitions { get; }

    public RadioLayoutSource Source { get; }

    // file path or "server" - for logs
    public string SourceDescription { get; }

    // raw SERVER_RADIO_PRESET the layout was built from ("" unless Source == Server)
    public string ServerPresetJson { get; }
}

/// <summary>
///     Loads the radio layout, first hit wins:
///     <list type="number">
///         <item>the server's radio layout, if the server sends one and the profile allows it (AllowServerRadioPreset),</item>
///         <item><c>radios-custom.json</c> in the configuration directory (<c>%AppData%\EasyRadioLink</c> or <c>-cfg=</c>),</item>
///         <item><c>radios.json</c> next to the executable,</item>
///         <item>all radios disabled.</item>
///     </list>
///     Files are read with tolerant JSON options and validated with <see cref="RadioDefinition.Normalise" />
///     (11 entries, slot 0 disabled, frequencies clamped, encKey 1..252, unknown modulations disabled).
/// </summary>
public static class RadioDefinitionStore
{
    public const string RadiosFileName = "radios.json";
    public const string CustomRadiosFileName = "radios-custom.json";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Shipped default layout (read only).</summary>
    public static string BuiltInRadiosFilePath => AppPaths.GetProgramFile(RadiosFileName);

    /// <summary>User override of the default layout.</summary>
    public static string CustomRadiosFilePath => Path.Combine(GlobalSettingsStore.Path, CustomRadiosFileName);

    public static RadioLayout Load(SyncedServerSettings serverSettings, bool allowServerPreset)
    {
        if (allowServerPreset && serverSettings != null)
        {
            var serverPreset = serverSettings.ServerRadioPreset;
            if (serverPreset != null && serverPreset.Count == Constants.MAX_RADIOS)
            {
                Logger.Info("Using the radio layout of the server");
                return new RadioLayout(RadioDefinition.Normalise(serverPreset), RadioLayoutSource.Server, "server",
                    serverSettings.ServerRadioPresetJson);
            }
        }

        var custom = LoadFile(CustomRadiosFilePath);
        if (custom != null) return new RadioLayout(custom, RadioLayoutSource.Custom, CustomRadiosFilePath);

        var builtIn = LoadFile(BuiltInRadiosFilePath);
        if (builtIn != null) return new RadioLayout(builtIn, RadioLayoutSource.BuiltIn, BuiltInRadiosFilePath);

        Logger.Warn(
            $"No radio layout found ({CustomRadiosFilePath}, {BuiltInRadiosFilePath}) - all radios are disabled");

        return new RadioLayout(RadioDefinition.Normalise(Array.Empty<RadioDefinition>()), RadioLayoutSource.None,
            "none");
    }

    /// <summary>Reads and validates a layout file. Returns null if the file is missing, empty or invalid.</summary>
    public static List<RadioDefinition> LoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            var definitions = RadioDefinition.ParseList(File.ReadAllText(path));

            if (definitions.Count == 0)
            {
                Logger.Warn($"Radio layout {path} contains no radios - ignored");
                return null;
            }

            if (definitions.Count != Constants.MAX_RADIOS && definitions.Count > Constants.RADIO_COUNT)
                Logger.Warn(
                    $"Radio layout {path} has {definitions.Count} entries, expected {Constants.MAX_RADIOS} - extra entries are ignored");

            Logger.Info($"Loaded radio layout {path}");
            return RadioDefinition.Normalise(definitions);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to read radio layout {path} - ignored");
            return null;
        }
    }
}
