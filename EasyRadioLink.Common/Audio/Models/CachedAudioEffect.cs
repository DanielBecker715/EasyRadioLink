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
        RADIO_TRANS_START = 0, // prefix: RADIO_TRANS_START*.wav (user selectable)
        RADIO_TRANS_END = 1, // prefix: RADIO_TRANS_END*.wav (user selectable)
        ENCRYPTION_TX = 2, // ENCRYPTION_TX.wav - played on TX start of an encrypted radio
        ENCRYPTION_RX = 3, // ENCRYPTION_RX.wav - played on RX start of a decryptable encrypted transmission
        NATO_TONE = 4, // NATO_TONE.wav - looping FM tone (user-visible label "FM tone")
        SQUELCH_TAIL_AM = 5, // SQUELCH_TAIL_AM.wav - noise source for the AM squelch tail
        SQUELCH_TAIL_FM = 6, // SQUELCH_TAIL_FM.wav - noise source for the FM squelch tail
        BACKGROUND = 7 // Background\<name>.wav - background sounds (jet, prop, helicopter, ...)
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

    /// <summary>Friendly name for the UI, e.g. "Default" or "Alternate".</summary>
    public string DisplayName { get; }

    public bool Loaded { get; }

    public AudioEffectTypes AudioEffectType { get; }

    public float[] AudioEffectFloat { get; set; }

    /// <summary>
    ///     "RADIO_TRANS_START.wav" -> "Default", "RADIO_TRANS_START_ALTERNATE.wav" -> "Alternate",
    ///     "RADIO_TRANS_END_ROGER_BEEP.wav" -> "Roger Beep", "jet.wav" -> "Jet".
    /// </summary>
    internal static string BuildDisplayName(AudioEffectTypes audioEffect, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var prefix = audioEffect.ToString();

        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name.Substring(prefix.Length);

        var words = name.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word.ToLowerInvariant()));

        name = string.Join(" ", words);

        return name.Length == 0 ? "Default" : name;
    }

    /** Needed for list view ***/
    public override string ToString()
    {
        return Text;
    }
}
