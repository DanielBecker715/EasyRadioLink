using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Settings;
using NLog;

namespace EasyRadioLink.Common.Audio.Providers;

/// <summary>
///     Loads and caches the sound effects shipped in <c>&lt;ProgramDirectory&gt;\AudioEffects</c>:
///     start/end clicks (user selectable by file-name prefix), the FM tone, the squelch tail noise
///     sources and the background sounds in <c>AudioEffects\Background</c>.
/// </summary>
public class CachedAudioEffectProvider
{
    public const string BackgroundFolderName = "Background";

    // background names travel over the network (PlayerRadioInfo.ambient.abType) - same rules as radio model keys
    private const int MaxBackgroundNameLength = 32;
    private const int MaxBackgroundSounds = 64;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly Lazy<CachedAudioEffectProvider> _instance =
        new(() => new CachedAudioEffectProvider(), LazyThreadSafetyMode.ExecutionAndPublication);

    // Whitelist of background sounds: only files found in AudioEffects\Background with a valid name are ever loaded,
    // so a remote abType can never be used to open arbitrary paths.
    private IReadOnlyDictionary<string, CachedAudioEffect> _backgroundEffects =
        new Dictionary<string, CachedAudioEffect>();

    private CachedAudioEffectProvider()
    {
        LoadEffects();
    }

    public static CachedAudioEffectProvider Instance => _instance.Value;

    /// <summary><c>&lt;ProgramDirectory&gt;\AudioEffects\Background</c>.</summary>
    public static string BackgroundFolder => Path.Combine(CachedAudioEffect.AudioEffectsFolder, BackgroundFolderName);

    public List<CachedAudioEffect> RadioTransmissionStart { get; private set; }
    public List<CachedAudioEffect> RadioTransmissionEnd { get; private set; }

    public CachedAudioEffect SelectedRadioTransmissionStartEffect =>
        FindSelectedEffect(RadioTransmissionStart, ProfileSettingsKeys.RadioTransmissionStartSelection);

    public CachedAudioEffect SelectedRadioTransmissionEndEffect =>
        FindSelectedEffect(RadioTransmissionEnd, ProfileSettingsKeys.RadioTransmissionEndSelection);

    /// <summary>NATO_TONE.wav - looping FM tone.</summary>
    public CachedAudioEffect NATOTone { get; private set; }

    /// <summary>SQUELCH_TAIL_AM.wav - noise source of the squelch tail for AM.</summary>
    public CachedAudioEffect SquelchTailAM { get; private set; }

    /// <summary>SQUELCH_TAIL_FM.wav - noise source of the squelch tail for FM.</summary>
    public CachedAudioEffect SquelchTailFM { get; private set; }

    /// <summary>
    ///     Names (lower case, e.g. "helicopter", "jet", "prop") of the background sounds found in
    ///     <c>AudioEffects\Background</c>, sorted. "" (none) is not part of the list.
    /// </summary>
    public IReadOnlyList<string> AvailableBackgroundSounds { get; private set; } = Array.Empty<string>();

    private void LoadEffects()
    {
        LoadRadioStartAndEndEffects();

        NATOTone = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.NATO_TONE);

        SquelchTailAM = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.SQUELCH_TAIL_AM);
        SquelchTailFM = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.SQUELCH_TAIL_FM);

        LoadBackgroundEffects();
    }

    private void LoadRadioStartAndEndEffects()
    {
        var start = new List<CachedAudioEffect>();
        var end = new List<CachedAudioEffect>();

        var sourceFolder = CachedAudioEffect.AudioEffectsFolder;
        try
        {
            if (Directory.Exists(sourceFolder))
            {
                var startPrefix = CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START.ToString();
                var endPrefix = CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END.ToString();

                foreach (var effectPath in Directory.EnumerateFiles(sourceFolder, "*.wav")
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var effect = Path.GetFileName(effectPath);

                    if (effect.StartsWith(startPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var audioEffect = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START,
                            effect, effectPath);

                        if (audioEffect.Loaded) start.Add(audioEffect);
                    }
                    else if (effect.StartsWith(endPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var audioEffect = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END,
                            effect, effectPath);

                        if (audioEffect.Loaded) end.Add(audioEffect);
                    }
                }
            }
            else
            {
                Logger.Error($"Audio effects folder {sourceFolder} is missing");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Unable to list the audio effects in {sourceFolder}");
        }

        // the default (un-suffixed) effect first, then the alternatives
        start = start.OrderBy(effect => effect.DisplayName == "Default" ? 0 : 1)
            .ThenBy(effect => effect.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        end = end.OrderBy(effect => effect.DisplayName == "Default" ? 0 : 1)
            .ThenBy(effect => effect.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        //IF the audio folder is missing - to avoid a crash, init with a blank one
        if (start.Count == 0) start.Add(new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START));
        if (end.Count == 0) end.Add(new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END));

        RadioTransmissionStart = start;
        RadioTransmissionEnd = end;
    }

    private void LoadBackgroundEffects()
    {
        var effects = new Dictionary<string, CachedAudioEffect>();
        var folder = BackgroundFolder;

        try
        {
            if (Directory.Exists(folder))
                foreach (var path in Directory.EnumerateFiles(folder, "*.wav")
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var rawName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    var name = NormaliseBackgroundName(rawName);

                    if (name.Length == 0 || name != rawName)
                    {
                        Logger.Warn(
                            $"Ignoring background sound {path} - file names must be letters and digits only (max {MaxBackgroundNameLength})");
                        continue;
                    }

                    if (effects.ContainsKey(name)) continue;

                    if (effects.Count >= MaxBackgroundSounds)
                    {
                        Logger.Warn($"Too many background sounds in {folder} - ignoring {path}");
                        break;
                    }

                    var effect = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.BACKGROUND,
                        Path.GetFileName(path), path);

                    if (effect.Loaded) effects[name] = effect;
                }
            else
                Logger.Warn($"Background sound folder {folder} is missing");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Unable to load the background sounds from {folder}");
        }

        _backgroundEffects = effects;
        AvailableBackgroundSounds = effects.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Normalises a background sound name the way it is compared: trimmed, lower case, letters and digits only,
    ///     max 32 characters. Returns "" for null/empty (= no background sound).
    /// </summary>
    public static string NormaliseBackgroundName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var normalised = new string(name.Trim().ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9')
            .ToArray());

        return normalised.Length > MaxBackgroundNameLength
            ? normalised.Substring(0, MaxBackgroundNameLength)
            : normalised;
    }

    /// <summary>True if <paramref name="name" /> is one of <see cref="AvailableBackgroundSounds" />.</summary>
    public bool IsBackgroundSoundAvailable(string name)
    {
        return GetBackgroundEffect(name) != null;
    }

    /// <summary>
    ///     The loaded background sound for <paramref name="name" /> (e.g. the remote <c>ambient.abType</c>), or null if
    ///     the name is empty or not in the whitelist of <see cref="AvailableBackgroundSounds" />.
    /// </summary>
    public CachedAudioEffect GetBackgroundEffect(string name)
    {
        var normalised = NormaliseBackgroundName(name);
        if (normalised.Length == 0) return null;

        return _backgroundEffects.TryGetValue(normalised, out var effect) ? effect : null;
    }

    private static CachedAudioEffect FindSelectedEffect(List<CachedAudioEffect> effects, ProfileSettingsKeys key)
    {
        var selectedTone = GlobalSettingsStore.Instance.ProfileSettingsStore.GetClientSettingString(key) ?? string.Empty;

        foreach (var effect in effects)
            if (string.Equals(effect.FileName, selectedTone, StringComparison.OrdinalIgnoreCase))
                return effect;

        return effects[0];
    }
}
