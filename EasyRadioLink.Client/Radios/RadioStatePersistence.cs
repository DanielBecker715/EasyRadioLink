using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings;
using NLog;

namespace EasyRadioLink.Client.Radios;

/// <summary>
///     Remembers the user's tuning between sessions in <c>radio-state.json</c> (configuration directory,
///     <c>%AppData%\EasyRadioLink</c> or <c>-cfg=</c>).
///     <para>
///         Saved per radio: freq, model, guardEnabled, enc, encKey, volume, channel, simul - plus the selected radio and
///         the global simultaneous transmission toggle. Entries are keyed by slot + radio name + modulation, so the
///         tuning of different layouts (local file vs. a server's layout) is kept side by side.
///     </para>
///     A saved entry is only re-applied when the slot still has a radio with the same name and modulation and the saved
///     frequency is inside the radio's range.
/// </summary>
public static class RadioStatePersistence
{
    public const string FileName = "radio-state.json";

    // upper bound for remembered radios (all layouts together)
    private const int MaxSavedRadios = 100;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static string FilePath => Path.Combine(GlobalSettingsStore.Path, FileName);

    /// <summary>Stores the tuning of every enabled user radio (merged with what is already saved).</summary>
    public static void Save(PlayerRadioInfo info)
    {
        if (info == null) return;

        lock (FileLock)
        {
            try
            {
                var state = ReadFile() ?? new RadioStateFile();
                var now = DateTime.UtcNow;

                var current = new List<SavedRadio>();
                for (var slot = PlayerRadioInfo.FirstUserRadio; slot < info.radios.Length; slot++)
                {
                    var radio = info.radios[slot];
                    if (radio == null || !radio.IsEnabled) continue;

                    current.Add(new SavedRadio
                    {
                        Slot = slot,
                        Name = radio.name ?? "",
                        Modulation = (int)radio.modulation,
                        Freq = radio.freq,
                        Model = radio.model ?? "",
                        GuardEnabled = radio.guardEnabled,
                        Enc = radio.enc,
                        EncKey = radio.encKey,
                        Volume = radio.volume,
                        Channel = radio.channel,
                        Simul = radio.simul,
                        SavedUtc = now
                    });
                }

                if (current.Count == 0) return;

                // newest first, replacing older entries of the same radio
                var merged = new List<SavedRadio>(current);
                if (state.Radios != null)
                    merged.AddRange(state.Radios.Where(old => old != null &&
                                                              !current.Any(c => c.IsSameRadio(old))));

                state.Version = RadioStateFile.CurrentVersion;
                state.Selected = info.selected;
                state.SimultaneousTransmission = info.simultaneousTransmission;
                state.Radios = merged.Take(MaxSavedRadios).ToList();

                var path = FilePath;
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(tempPath, path, true);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to save the radio state to {FilePath}");
            }
        }
    }

    /// <summary>
    ///     Re-applies saved tuning to <paramref name="info" /> (whose radios were just loaded from a layout).
    /// </summary>
    /// <returns>The slots that got their saved tuning back.</returns>
    public static HashSet<int> Apply(PlayerRadioInfo info)
    {
        var restored = new HashSet<int>();
        if (info == null) return restored;

        RadioStateFile state;
        lock (FileLock)
        {
            state = ReadFile();
        }

        if (state?.Radios == null) return restored;

        for (var slot = PlayerRadioInfo.FirstUserRadio; slot < info.radios.Length; slot++)
        {
            var radio = info.radios[slot];
            if (radio == null || !radio.IsEnabled) continue;

            var saved = state.Radios.FirstOrDefault(s => s != null
                                                         && s.Slot == slot
                                                         && s.Modulation == (int)radio.modulation
                                                         && string.Equals(s.Name ?? "", radio.name ?? "",
                                                             StringComparison.Ordinal));
            if (saved == null) continue;

            // the saved frequency must still fit the radio (the layout may have changed)
            if (!double.IsFinite(saved.Freq) || saved.Freq < radio.freqMin - 0.5 || saved.Freq > radio.freqMax + 0.5)
                continue;

            radio.freq = Math.Clamp(saved.Freq, radio.freqMin, radio.freqMax);
            radio.model = saved.Model ?? radio.model;
            radio.guardEnabled = saved.GuardEnabled;
            radio.enc = radio.encCapable && saved.Enc;
            radio.encKey = Math.Clamp(saved.EncKey, RadioDefinition.MinEncryptionKey, RadioDefinition.MaxEncryptionKey);
            radio.volume = float.IsFinite(saved.Volume) ? Math.Clamp(saved.Volume, 0f, 1f) : 1f;
            radio.channel = saved.Channel > 0 ? saved.Channel : -1;
            radio.simul = saved.Simul && !radio.rxOnly;

            restored.Add(slot);
        }

        if (restored.Count > 0)
        {
            if (state.Selected >= PlayerRadioInfo.FirstUserRadio && state.Selected < info.radios.Length &&
                info.radios[state.Selected]?.IsEnabled == true)
                info.selected = state.Selected;

            info.simultaneousTransmission = state.SimultaneousTransmission;

            if (!info.simultaneousTransmission)
                foreach (var radio in info.radios)
                    if (radio != null)
                        radio.simul = false;

            Logger.Info($"Restored the saved tuning of {restored.Count} radio(s)");
        }

        return restored;
    }

    private static RadioStateFile ReadFile()
    {
        var path = FilePath;
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<RadioStateFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Unable to read the saved radio state {path} - ignored");
            return null;
        }
    }

    private sealed class RadioStateFile
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public short Selected { get; set; } = PlayerRadioInfo.FirstUserRadio;
        public bool SimultaneousTransmission { get; set; }
        public List<SavedRadio> Radios { get; set; } = new();
    }

    private sealed class SavedRadio
    {
        public int Slot { get; set; }
        public string Name { get; set; } = "";

        // numeric Modulation value (wire contract)
        public int Modulation { get; set; }

        public double Freq { get; set; }
        public string Model { get; set; } = "";
        public bool GuardEnabled { get; set; } = true;
        public bool Enc { get; set; }
        public byte EncKey { get; set; } = RadioDefinition.MinEncryptionKey;
        public float Volume { get; set; } = 1.0f;
        public int Channel { get; set; } = -1;
        public bool Simul { get; set; }
        public DateTime SavedUtc { get; set; }

        public bool IsSameRadio(SavedRadio other)
        {
            return other != null
                   && Slot == other.Slot
                   && Modulation == other.Modulation
                   && string.Equals(Name ?? "", other.Name ?? "", StringComparison.Ordinal);
        }
    }
}
