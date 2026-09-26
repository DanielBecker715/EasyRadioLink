using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings;
using NLog;

namespace EasyRadioLink.Client.Radios;

/// <summary>What is remembered of the radio between sessions.</summary>
/// <param name="Frequency">Last frequency in Hz (inside the tuning range of the <see cref="BandPlan" />).</param>
/// <param name="Volume">Last receive volume 0..1.</param>
/// <param name="Step">Tuning step of the radio window in Hz (one of <see cref="TuningSteps.All" />).</param>
public sealed record RadioState(double Frequency, float Volume, int Step)
{
    public static RadioState Default { get; } = new(BandPlan.DefaultFrequency, 1.0f, TuningSteps.Default);
}

/// <summary>
///     Remembers the radio between sessions in <c>radio-state.json</c> (configuration directory,
///     <c>%AppData%\EasyRadioLink</c> or <c>-cfg=</c>): frequency, volume and the tuning step of the radio window.
///     Modulation and radio model are not stored - they always follow from the frequency (<see cref="BandPlan" />).
///     <para>
///         A file of version 1 (several radios) is migrated: the last selected radio's frequency and volume are kept.
///         The frequency is <see cref="BandPlan.Normalise" />d; invalid values are replaced by the defaults
///         (27.185 MHz, full volume, 1 kHz steps).
///     </para>
/// </summary>
public static class RadioStatePersistence
{
    public const string FileName = "radio-state.json";

    private const int CurrentVersion = 2;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FilePath => Path.Combine(GlobalSettingsStore.Path, FileName);

    /// <summary>The remembered state, or <see cref="RadioState.Default" /> values for anything missing / invalid.</summary>
    public static RadioState Load()
    {
        lock (FileLock)
        {
            return ToState(ReadFile());
        }
    }

    /// <summary>Remembers frequency and volume (the tuning step is kept).</summary>
    public static void SaveTuning(double frequency, float volume)
    {
        Update(state => state with { Frequency = frequency, Volume = volume });
    }

    /// <summary>Remembers the tuning step of the radio window (frequency and volume are kept).</summary>
    public static void SaveStep(int step)
    {
        Update(state => state with { Step = step });
    }

    private static void Update(Func<RadioState, RadioState> change)
    {
        lock (FileLock)
        {
            try
            {
                var state = Sanitise(change(ToState(ReadFile())));

                var file = new RadioStateFile
                {
                    Version = CurrentVersion,
                    Frequency = state.Frequency,
                    Volume = state.Volume,
                    Step = state.Step
                };

                var path = FilePath;
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(file, JsonOptions));
                File.Move(tempPath, path, true);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to save the radio state to {FilePath}");
            }
        }
    }

    private static RadioState ToState(RadioStateFile file)
    {
        if (file == null) return RadioState.Default;

        var frequency = file.Frequency;
        var volume = file.Volume;

        if (frequency == null && file.Radios != null)
        {
            // version 1: one entry per radio - continue with the radio that was selected last
            var radios = file.Radios.Where(radio => radio != null).ToList();
            var saved = radios.FirstOrDefault(radio => radio.Slot == file.Selected) ?? radios.FirstOrDefault();

            if (saved != null)
            {
                frequency = saved.Freq;
                volume = saved.Volume;
                Logger.Info("Migrated the radio state of a previous version");
            }
        }

        return Sanitise(new RadioState(frequency ?? RadioState.Default.Frequency,
            volume ?? RadioState.Default.Volume, file.Step ?? RadioState.Default.Step));
    }

    private static RadioState Sanitise(RadioState state)
    {
        var frequency = double.IsFinite(state.Frequency) && state.Frequency > 0
            ? BandPlan.Normalise(state.Frequency)
            : RadioState.Default.Frequency;

        var volume = float.IsFinite(state.Volume) ? Math.Clamp(state.Volume, 0f, 1f) : RadioState.Default.Volume;

        var step = TuningSteps.IsValid(state.Step) ? state.Step : RadioState.Default.Step;

        return new RadioState(frequency, volume, step);
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
        public int Version { get; set; }

        // version 2
        public double? Frequency { get; set; }
        public float? Volume { get; set; }
        public int? Step { get; set; }

        // version 1 (read only, never written)
        public short? Selected { get; set; }
        public List<SavedRadioV1> Radios { get; set; }
    }

    private sealed class SavedRadioV1
    {
        public int Slot { get; set; }
        public double Freq { get; set; }
        public float Volume { get; set; } = 1.0f;
    }
}
