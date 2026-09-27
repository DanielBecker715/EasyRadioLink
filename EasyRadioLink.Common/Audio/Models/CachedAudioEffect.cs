using System;
using System.Globalization;
using System.IO;
using System.Linq;
using EasyRadioLink.Common.Audio.Utility;
using EasyRadioLink.Common.Helpers;
using NAudio.Wave;
using NLog;

namespace EasyRadioLink.Common.Audio.Models;

/// <summary>
///     A sound effect loaded from <c>&lt;ProgramDirectory&gt;\AudioEffects</c>.
///     Files must be 16-bit PCM, 48000 Hz, mono - anything else is ignored (<see cref="Loaded" /> = false).
/// </summary>
public class CachedAudioEffect
{
    /// <summary>
    ///     Effect types. The member names are file names / file-name prefixes in the AudioEffects folder
    ///     (<c>&lt;NAME&gt;.wav</c>) - renaming a member requires renaming the WAV file. Numeric values are not persisted.
    /// </summary>
    public enum AudioEffectTypes
    {
        RADIO_TRANS_START = 0, // prefix: RADIO_TRANS_START*.wav (user selectable), plus SharedToneNames
        RADIO_TRANS_END = 1, // prefix: RADIO_TRANS_END*.wav (user selectable), plus SharedToneNames
        // 2 and 3 were the encryption tones (removed)
        NATO_TONE = 4, // NATO_TONE.wav - looping FM tone (user-visible label "FM tone")
        SQUELCH_TAIL_AM = 5, // SQUELCH_TAIL_AM.wav - noise source for the AM squelch tail
        SQUELCH_TAIL_FM = 6, // SQUELCH_TAIL_FM.wav - noise source for the FM squelch tail
        BACKGROUND = 7, // Background\<name>.wav - background sounds (jet, prop, helicopter, ...)
        BUSY_TONE = 8 // BUSY_TONE.wav - played locally when push-to-talk is refused on a busy channel
    }

    public const string AudioEffectsFolderName = "AudioEffects";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly WaveFormat RequiredFormat = new(Constants.OUTPUT_SAMPLE_RATE, 16, 1);

    /// <summary>Loads <c>AudioEffects\&lt;audioEffect&gt;.wav</c>.</summary>
    public CachedAudioEffect(AudioEffectTypes audioEffect) : this(audioEffect,
        audioEffect + ".wav",
        Path.Combine(AudioEffectsFolder, audioEffect + ".wav"))
    {
    }

    public CachedAudioEffect(AudioEffectTypes audioEffect, string fileName, string path)
    {
        FileName = fileName;
        AudioEffectType = audioEffect;
        DisplayName = BuildDisplayName(audioEffect, fileName);

        AudioEffectFloat = null;

        try
        {
            if (File.Exists(path))
                using (var reader = new WaveFileReader(path))
                {
                    if (reader.WaveFormat.BitsPerSample == RequiredFormat.BitsPerSample &&
                        reader.WaveFormat.SampleRate == RequiredFormat.SampleRate &&
                        reader.WaveFormat.Channels == 1)
                    {
                        var tmpBytes = new byte[reader.Length];
                        var read = reader.Read(tmpBytes, 0, tmpBytes.Length);
                        if (read != tmpBytes.Length) Array.Resize(ref tmpBytes, read - read % 2);

                        //convert to short  - 16 - then to float 32
                        var tmpShort = ConversionHelpers.ByteArrayToShortArray(tmpBytes);

                        //now to float
                        AudioEffectFloat = ConversionHelpers.ShortPCM16ArrayToFloat32Array(tmpShort);

                        if (AudioEffectFloat.Length > 0)
                        {
                            RMS = VolumeConversionHelper.CalculateRMS(AudioEffectFloat, 0, AudioEffectFloat.Length);
                            Loaded = true;
                            Logger.Info($"Read Effect {audioEffect} from {path} Successfully - Format {reader.WaveFormat}");
                        }
                        else
                        {
                            AudioEffectFloat = null;
                            Logger.Info($"Effect {audioEffect} from {path} contains no audio");
                        }
                    }
                    else
                    {
                        Logger.Info(
                            $"Unable to read Effect {audioEffect} from {path} Successfully - {reader.WaveFormat} is not {RequiredFormat} !");
                    }
                }
            else
                Logger.Info($"Unable to find file for effect {audioEffect} at {path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Unable to load file for effect {audioEffect} from {path}");
        }
    }

    /// <summary><c>&lt;ProgramDirectory&gt;\AudioEffects</c> - never the working directory.</summary>
    public static string AudioEffectsFolder => Path.Combine(AppPaths.ProgramDirectory, AudioEffectsFolderName);

    /// <summary>RMS level of the effect in dBFS.</summary>
    public double RMS { get; }

    /** Needed for list view ***/
    public string Text => DisplayName;

    /** Needed for list view ***/
    public object Value => this;

    /// <summary>File name (e.g. <c>RADIO_TRANS_START_ALTERNATE.wav</c>) - this is what the profile settings store.</summary>
    public string FileName { get; }

    /// <summary>Friendly name for the UI, e.g. "Click", "Soft click" or "Roger beep".</summary>
    public string DisplayName { get; }

    public bool Loaded { get; }

    public AudioEffectTypes AudioEffectType { get; }

    public float[] AudioEffectFloat { get; set; }

    /// <summary>
    ///     Names of the shipped start/end sounds by file-name suffix (after <c>RADIO_TRANS_START</c> /
    ///     <c>RADIO_TRANS_END</c>), in the order the settings list them. Other files get a name from their suffix.
    /// </summary>
    internal static readonly (string Suffix, string Name)[] BuiltInToneNames =
    {
        ("", "Click"),
        ("ALTERNATE", "Soft click"),
        ("CHIRP", "Chirp"),
        ("KEY_UP_BEEP", "Key-up beep"),
        ("ROGER_BEEP", "Roger beep"),
        ("DOUBLE_BEEP", "Double beep"),
        ("THREE_TONE_BEEP", "Three-tone beep")
    };

    /// <summary>
    ///     Sounds without a RADIO_TRANS_START / RADIO_TRANS_END prefix that are selectable as start AND as end sounds
    ///     (file name in AudioEffects, name), listed after <see cref="BuiltInToneNames" />.
    /// </summary>
    internal static readonly (string FileName, string Name)[] SharedToneNames =
    {
        (FancyReleaseFile, "Fancy Release"),
        (AlmostFancyFile, "Almost Fancy")
    };

    /// <summary>Default start sound (push-to-talk pressed, someone starts talking).</summary>
    public const string FancyReleaseFile = "FancyRelease.wav";

    /// <summary>Default end sound (push-to-talk released, someone stops talking).</summary>
    public const string AlmostFancyFile = "AlmostFancy.wav";

    /// <summary>True for a file of <see cref="SharedToneNames" /> (start and end sound).</summary>
    internal static bool IsSharedTone(string fileName)
    {
        return SharedToneNames.Any(tone => string.Equals(tone.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     "RADIO_TRANS_START.wav" -> "Click", "RADIO_TRANS_START_ALTERNATE.wav" -> "Soft click",
    ///     "RADIO_TRANS_END_ROGER_BEEP.wav" -> "Roger beep" (shipped sounds, see <see cref="BuiltInToneNames" />),
    ///     "RADIO_TRANS_END_MY_TONE.wav" -> "My Tone" (own files), "jet.wav" -> "Jet".
    /// </summary>
    internal static string BuildDisplayName(AudioEffectTypes audioEffect, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var prefix = audioEffect.ToString();

        if (audioEffect is AudioEffectTypes.RADIO_TRANS_START or AudioEffectTypes.RADIO_TRANS_END)
            foreach (var (sharedFileName, sharedName) in SharedToneNames)
                if (string.Equals(Path.GetFileName(fileName ?? string.Empty), sharedFileName,
                        StringComparison.OrdinalIgnoreCase))
                    return sharedName;

        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(prefix.Length);

            if (audioEffect is AudioEffectTypes.RADIO_TRANS_START or AudioEffectTypes.RADIO_TRANS_END)
            {
                var suffix = name.Trim('_', '-', ' ');
                foreach (var (builtInSuffix, builtInName) in BuiltInToneNames)
                    if (string.Equals(suffix, builtInSuffix, StringComparison.OrdinalIgnoreCase))
                        return builtInName;
            }
        }

        var words = name.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word.ToLowerInvariant()));

        name = string.Join(" ", words);

        return name.Length == 0 ? "Default" : name;
    }

    /// <summary>
    ///     Position of a start/end sound in the settings lists: the shipped sounds in the order of
    ///     <see cref="BuiltInToneNames" />, then <see cref="SharedToneNames" />, then everything else.
    /// </summary>
    internal static int ToneSortOrder(AudioEffectTypes audioEffect, string fileName)
    {
        for (var i = 0; i < SharedToneNames.Length; i++)
            if (string.Equals(Path.GetFileName(fileName ?? string.Empty), SharedToneNames[i].FileName,
                    StringComparison.OrdinalIgnoreCase))
                return BuiltInToneNames.Length + i;

        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var prefix = audioEffect.ToString();

        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = name.Substring(prefix.Length).Trim('_', '-', ' ');
            for (var i = 0; i < BuiltInToneNames.Length; i++)
                if (string.Equals(suffix, BuiltInToneNames[i].Suffix, StringComparison.OrdinalIgnoreCase))
                    return i;
        }

        return BuiltInToneNames.Length + SharedToneNames.Length;
    }

    /** Needed for list view ***/
    public override string ToString()
    {
        return Text;
    }
}
