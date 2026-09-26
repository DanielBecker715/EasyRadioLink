using System;
using System.IO;
using System.Linq;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpConfig;

namespace EasyRadioLink.Common.Tests.Audio;

/// <summary>The shipped sound effects and the audio settings keys.</summary>
[TestClass]
public class AudioAssetTests
{
    // played by System.Media.SoundPlayer (UI), not by the radio pipeline - any format is fine
    private static readonly string[] UiSounds = { "beep-connected.wav", "beep-disconnected.wav" };

    [TestMethod]
    public void EveryPipelineEffectHasTheRequiredFormat()
    {
        var files = Directory.GetFiles(RepositoryFiles.AudioEffectsFolder, "*.wav", SearchOption.AllDirectories)
            .Where(file => !UiSounds.Contains(Path.GetFileName(file)))
            .ToList();
        Assert.IsNotEmpty(files);

        foreach (var file in files)
        {
            var effect = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.BACKGROUND, Path.GetFileName(file),
                file);
            Assert.IsTrue(effect.Loaded, $"{file} must be 16-bit PCM, 48000 Hz, mono");
            Assert.IsTrue(double.IsFinite(effect.RMS), file);
        }
    }

    [TestMethod]
    public void EveryEffectTypeHasItsFile()
    {
        foreach (var type in Enum.GetValues<CachedAudioEffect.AudioEffectTypes>())
        {
            if (type == CachedAudioEffect.AudioEffectTypes.BACKGROUND) continue;

            var file = Path.Combine(RepositoryFiles.AudioEffectsFolder, type + ".wav");
            Assert.IsTrue(File.Exists(file), $"{type}: {file} is missing (file names must match the enum names)");
        }

        foreach (var name in new[] { "jet", "prop", "helicopter" })
            Assert.IsTrue(File.Exists(Path.Combine(RepositoryFiles.AudioEffectsFolder,
                CachedAudioEffectProvider.BackgroundFolderName, name + ".wav")), name);
    }

    [TestMethod]
    public void RemovedEffectsAreNotShipped()
    {
        var names = Directory.GetFiles(RepositoryFiles.AudioEffectsFolder, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToList();

        foreach (var name in names)
        {
            Assert.IsFalse(name.StartsWith("INTERCOM", StringComparison.OrdinalIgnoreCase), name);
            Assert.IsFalse(name.StartsWith("MIDS", StringComparison.OrdinalIgnoreCase), name);
            Assert.IsFalse(name.StartsWith("HAVEQUICK", StringComparison.OrdinalIgnoreCase), name);
            Assert.IsFalse(name.StartsWith("KY_58", StringComparison.OrdinalIgnoreCase), name);
            Assert.IsFalse(name.Contains("Apache", StringComparison.OrdinalIgnoreCase), name);
            Assert.IsFalse(name.StartsWith("ENCRYPTION", StringComparison.OrdinalIgnoreCase), name);
        }

        Assert.IsFalse(Directory.Exists(Path.Combine(RepositoryFiles.AudioEffectsFolder, "Ambient")));
        Assert.IsFalse(Directory.Exists(Path.Combine(RepositoryFiles.AudioEffectsFolder, "Original")));
    }

    [TestMethod]
    public void BackgroundFileNamesAreValidNetworkNames()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(RepositoryFiles.AudioEffectsFolder,
                     CachedAudioEffectProvider.BackgroundFolderName)))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            Assert.AreEqual(name, CachedAudioEffectProvider.NormaliseBackgroundName(name),
                $"{file}: background names travel in ambient.abType and must be lower case letters/digits");
        }
    }

    [TestMethod]
    public void BackgroundNamesFromTheNetworkAreSanitised()
    {
        Assert.AreEqual("jet", CachedAudioEffectProvider.NormaliseBackgroundName("JET"));
        Assert.AreEqual("helicopter", CachedAudioEffectProvider.NormaliseBackgroundName(" Helicopter "));
        Assert.AreEqual("windowssystem32evil", CachedAudioEffectProvider.NormaliseBackgroundName(@"..\..\Windows\System32\evil"));
        Assert.AreEqual("", CachedAudioEffectProvider.NormaliseBackgroundName(null));
        Assert.AreEqual("", CachedAudioEffectProvider.NormaliseBackgroundName("   "));
        Assert.AreEqual("", CachedAudioEffectProvider.NormaliseBackgroundName("../.."));
        Assert.HasCount(32, CachedAudioEffectProvider.NormaliseBackgroundName(new string('a', 50)));
    }

    // the selectable start / end sounds that ship with the client, in the order of the settings lists
    private static readonly (string File, string Name)[] ShippedStartSounds =
    {
        ("RADIO_TRANS_START.wav", "Click"),
        ("RADIO_TRANS_START_ALTERNATE.wav", "Soft click"),
        ("RADIO_TRANS_START_CHIRP.wav", "Chirp"),
        ("RADIO_TRANS_START_KEY_UP_BEEP.wav", "Key-up beep"),
        ("FancyRelease.wav", "Fancy Release"),
        ("AlmostFancy.wav", "Almost Fancy")
    };

    private static readonly (string File, string Name)[] ShippedEndSounds =
    {
        ("RADIO_TRANS_END.wav", "Click"),
        ("RADIO_TRANS_END_ALTERNATE.wav", "Soft click"),
        ("RADIO_TRANS_END_ROGER_BEEP.wav", "Roger beep"),
        ("RADIO_TRANS_END_DOUBLE_BEEP.wav", "Double beep"),
        ("RADIO_TRANS_END_THREE_TONE_BEEP.wav", "Three-tone beep"),
        ("FancyRelease.wav", "Fancy Release"),
        ("AlmostFancy.wav", "Almost Fancy")
    };

    // generated by tools/generate-sounds.py
    private static readonly string[] GeneratedSounds =
    {
        "RADIO_TRANS_START_CHIRP.wav", "RADIO_TRANS_START_KEY_UP_BEEP.wav", "RADIO_TRANS_END_ROGER_BEEP.wav",
        "RADIO_TRANS_END_DOUBLE_BEEP.wav", "RADIO_TRANS_END_THREE_TONE_BEEP.wav"
    };

    // provided by the project owner, selectable in every slot (start and end); originals in tools/sound-sources
    private static readonly string[] OwnerSounds = { "FancyRelease.wav", "AlmostFancy.wav" };

    private static CachedAudioEffect LoadShipped(string fileName,
        CachedAudioEffect.AudioEffectTypes? type = null)
    {
        type ??= fileName.StartsWith("RADIO_TRANS_END", StringComparison.Ordinal)
            ? CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END
            : CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START;

        return new CachedAudioEffect(type.Value, fileName, Path.Combine(RepositoryFiles.AudioEffectsFolder, fileName));
    }

    private static double Decibels(double linear)
    {
        return 20.0 * Math.Log10(linear);
    }

    [TestMethod]
    public void ToneNamesAreFriendly()
    {
        Assert.AreEqual("Click", CachedAudioEffect.BuildDisplayName(
            CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START, "RADIO_TRANS_START.wav"));
        Assert.AreEqual("Soft click", CachedAudioEffect.BuildDisplayName(
            CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START, "RADIO_TRANS_START_ALTERNATE.wav"));
        Assert.AreEqual("Roger beep", CachedAudioEffect.BuildDisplayName(
            CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END, "radio_trans_end_ROGER_BEEP.wav"));
        Assert.AreEqual("Key-up beep", CachedAudioEffect.BuildDisplayName(
            CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START, "RADIO_TRANS_START_KEY_UP_BEEP.wav"));
        Assert.AreEqual("My Tone", CachedAudioEffect.BuildDisplayName(
            CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END, "RADIO_TRANS_END_MY_TONE.wav"),
            "own sound files are named after their file name");
        Assert.AreEqual("Jet", CachedAudioEffect.BuildDisplayName(
            CachedAudioEffect.AudioEffectTypes.BACKGROUND, "jet.wav"));

        foreach (var type in new[]
                 {
                     CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START, CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END
                 })
        {
            Assert.AreEqual("Fancy Release", CachedAudioEffect.BuildDisplayName(type, "FancyRelease.wav"));
            Assert.AreEqual("Almost Fancy", CachedAudioEffect.BuildDisplayName(type, "almostfancy.WAV"));
        }

        var effect = LoadShipped("RADIO_TRANS_END_ALTERNATE.wav");
        Assert.AreEqual("Soft click", effect.ToString(), "combo boxes show the friendly name");
        Assert.AreEqual("RADIO_TRANS_END_ALTERNATE.wav", effect.FileName, "the profile stores the file name");
    }

    [TestMethod]
    public void TheStartAndEndSoundsAreShippedInSettingsOrder()
    {
        // what the client loads from its AudioEffects folder
        var (start, end) = CachedAudioEffectProvider.LoadTones(RepositoryFiles.AudioEffectsFolder);

        foreach (var (sounds, loaded) in new[] { (ShippedStartSounds, start), (ShippedEndSounds, end) })
        {
            Assert.IsTrue(loaded.All(effect => effect.Loaded), string.Join(", ", loaded.Select(e => e.FileName)));
            CollectionAssert.AreEqual(sounds.Select(sound => sound.File).ToList(),
                loaded.Select(effect => effect.FileName).ToList());
            CollectionAssert.AreEqual(sounds.Select(sound => sound.Name).ToList(),
                loaded.Select(effect => effect.DisplayName).ToList());
        }

        Assert.IsTrue(start.All(effect => effect.AudioEffectType == CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START));
        Assert.IsTrue(end.All(effect => effect.AudioEffectType == CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END));

        // the order does not depend on the order of the files
        var shuffled = CachedAudioEffectProvider.SortTones(Enumerable.Reverse(start));
        CollectionAssert.AreEqual(start.Select(e => e.FileName).ToList(), shuffled.Select(e => e.FileName).ToList());

        // own files come after the shipped ones
        var own = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END, "RADIO_TRANS_END_AAA.wav",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "RADIO_TRANS_END_AAA.wav"));
        var withOwn = CachedAudioEffectProvider.SortTones(end.Append(own));
        Assert.AreSame(own, withOwn[^1]);
        Assert.AreEqual("RADIO_TRANS_END.wav", withOwn[0].FileName, "the default click stays first");

        // every sound in the folder is known: selectable sounds, effect types, UI sounds
        var known = ShippedStartSounds.Concat(ShippedEndSounds).Select(sound => sound.File)
            .Concat(Enum.GetNames<CachedAudioEffect.AudioEffectTypes>().Select(type => type + ".wav"))
            .Concat(UiSounds).ToList();
        foreach (var file in Directory.GetFiles(RepositoryFiles.AudioEffectsFolder, "*.wav"))
            CollectionAssert.Contains(known, Path.GetFileName(file));

        // a missing folder still gives one (blank) sound per list
        var (noStart, noEnd) = CachedAudioEffectProvider.LoadTones(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.HasCount(1, noStart);
        Assert.HasCount(1, noEnd);
    }

    [TestMethod]
    public void GeneratedSoundsAreLoudnessMatchedAndClickFree()
    {
        var click = LoadShipped("RADIO_TRANS_START.wav").AudioEffectFloat;
        var clickRms = Decibels(Math.Sqrt(click.Average(s => (double)s * s)));

        foreach (var name in GeneratedSounds)
        {
            var samples = LoadShipped(name).AudioEffectFloat;
            Assert.IsNotNull(samples, name);

            var peak = Decibels(samples.Max(s => Math.Abs((double)s)));
            var rms = Decibels(Math.Sqrt(samples.Average(s => (double)s * s)));
            var milliseconds = samples.Length * 1000.0 / 48000;

            Assert.IsTrue(Math.Abs(rms - clickRms) <= 3.0, $"{name}: RMS {rms:F1} dBFS, click {clickRms:F1} dBFS");
            Assert.IsTrue(peak is > -24.0 and < -12.0, $"{name}: peak {peak:F1} dBFS");
            Assert.IsTrue(milliseconds is >= 60 and <= 200, $"{name}: {milliseconds} ms");

            // faded in and out: no click at the start or the end
            Assert.IsTrue(Math.Abs(samples[0]) < 0.001f && Math.Abs(samples[^1]) < 0.001f, name);
        }

        // press sounds delay the received voice by their length (the receive start sound plays first) - keep them short
        Assert.IsTrue(LoadShipped("RADIO_TRANS_START_CHIRP.wav").AudioEffectFloat.Length <= 48000 * 150 / 1000);
        Assert.IsTrue(LoadShipped("RADIO_TRANS_START_KEY_UP_BEEP.wav").AudioEffectFloat.Length <= 48000 * 150 / 1000);
    }

    [TestMethod]
    public void TheOwnersSoundsAreShippedConvertedAndTheirSourcesAreNot()
    {
        var click = LoadShipped("RADIO_TRANS_START.wav").AudioEffectFloat;

        foreach (var name in OwnerSounds)
        {
            var samples = LoadShipped(name).AudioEffectFloat;
            Assert.IsNotNull(samples, $"{name} must be 16-bit PCM, 48000 Hz, mono");

            // 0.23 / 0.28 s - longer than the clicks; as a receive start sound it delays the received voice by that
            var seconds = samples.Length / 48000.0;
            Assert.IsTrue(seconds is > 0.2 and < 0.3, $"{name}: {seconds:F3} s");
            Assert.IsTrue(samples.Length > click.Length * 5, name);
            Assert.IsTrue(Math.Abs(samples[0]) < 0.01f && Math.Abs(samples[^1]) < 0.01f, $"{name}: faded");
        }

        // the owner's originals (stereo, 44.1 kHz) stay in the repository: outside the client project, so the client's
        // AudioEffects glob can't ship them, and they are not the files the client loads
        var sourcesFolder = Path.GetFullPath(Path.Combine(RepositoryFiles.ClientFolder, "..", "tools", "sound-sources"));
        Assert.IsFalse(sourcesFolder.StartsWith(Path.GetFullPath(RepositoryFiles.ClientFolder) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
        var project = File.ReadAllText(Path.Combine(RepositoryFiles.ClientFolder, "EasyRadioLink.Client.csproj"));
        Assert.IsFalse(project.Contains("sound-sources", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(project.Contains(@"..\tools", StringComparison.OrdinalIgnoreCase));

        if (!Directory.Exists(sourcesFolder)) return;

        foreach (var source in Directory.GetFiles(sourcesFolder))
        {
            var shipped = Path.Combine(RepositoryFiles.AudioEffectsFolder, Path.GetFileName(source));
            if (File.Exists(shipped))
                CollectionAssert.AreNotEqual(File.ReadAllBytes(source), File.ReadAllBytes(shipped),
                    $"{shipped} must be the converted file, not the original");
        }
    }

    [TestMethod]
    public void TheSoundSourcesAreDocumented()
    {
        var sources = File.ReadAllText(Path.Combine(RepositoryFiles.AudioEffectsFolder, "SOURCES.txt"));
        foreach (var file in ShippedStartSounds.Concat(ShippedEndSounds).Select(sound => sound.File))
            StringAssert.Contains(sources, file);
        StringAssert.Contains(sources, "project owner");

        Assert.IsTrue(File.Exists(Path.Combine(RepositoryFiles.ClientFolder, "..", "tools", "generate-sounds.py")),
            "the generated sounds are reproducible");
    }

    [TestMethod]
    public void MissingEffectFilesAreNotLoaded()
    {
        var effect = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.NATO_TONE, "NATO_TONE.wav",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "NATO_TONE.wav"));

        Assert.IsFalse(effect.Loaded);
        Assert.IsNull(effect.AudioEffectFloat);
    }

    [TestMethod]
    public void TheClientShipsNoRadioLayout()
    {
        // the band plan defines the single radio - there is no radio layout file any more
        Assert.IsFalse(File.Exists(Path.Combine(RepositoryFiles.ClientFolder, "radios.json")));
    }

    [TestMethod]
    public void AudioSettingsKeysAreStandalone()
    {
        foreach (var removed in new[]
                 {
                     "IntercomChannel", "MIDSRadioEffect", "HAVEQUICKTone", "HQToneVolume",
                     "IntercomTransmissionStartSelection", "IntercomTransmissionEndSelection", "AMCollisionVolume",
                     "AmbientCockpitNoiseEffect", "AmbientCockpitNoiseEffectVolume", "AmbientCockpitIntercomNoiseEffect",
                     "AllowServerEAMRadioPreset",
                     // single radio (1.1): no encryption, presets, radio switches, server layout, radios 2-10
                     "RadioEncryptionEffects", "AutoSelectPresetChannel", "RadioSwitchIsPTT",
                     "RadioSwitchIsPTTOnlyWhenValid", "ServerPresetSelection", "AllowServerRadioPreset",
                     "Radio2Channel", "Radio10Channel"
                 })
            Assert.IsFalse(Enum.IsDefined(typeof(ProfileSettingsKeys), removed), removed);

        var defaults = ProfileSettingsStore.DefaultSettingsProfileSettings;
        foreach (var key in Enum.GetNames<ProfileSettingsKeys>())
            Assert.IsTrue(defaults.ContainsKey(key), $"{key} has no default");

        Assert.AreEqual("true", defaults[nameof(ProfileSettingsKeys.RadioRxSquelchTail)]);
        Assert.AreEqual("", defaults[nameof(ProfileSettingsKeys.BackgroundSound)], "no background sound by default");
        Assert.AreEqual("true", defaults[nameof(ProfileSettingsKeys.BackgroundSoundEffect)]);
        // owner's choice: Fancy Release when a transmission starts, Almost Fancy when it ends (sending and receiving)
        Assert.AreEqual("FancyRelease.wav", defaults[nameof(ProfileSettingsKeys.RadioTransmissionStartSelection)]);
        Assert.AreEqual("AlmostFancy.wav", defaults[nameof(ProfileSettingsKeys.RadioTransmissionEndSelection)]);
        Assert.AreEqual("FancyRelease.wav", defaults[nameof(ProfileSettingsKeys.RadioRxStartSelection)]);
        Assert.AreEqual("AlmostFancy.wav", defaults[nameof(ProfileSettingsKeys.RadioRxEndSelection)]);
        foreach (var key in new[]
                 {
                     ProfileSettingsKeys.RadioTransmissionStartSelection, ProfileSettingsKeys.RadioTransmissionEndSelection,
                     ProfileSettingsKeys.RadioRxStartSelection, ProfileSettingsKeys.RadioRxEndSelection
                 })
            Assert.IsTrue(File.Exists(Path.Combine(RepositoryFiles.AudioEffectsFolder, defaults[key.ToString()])), key.ToString());

        // the radio sounds are on by default
        foreach (var key in new[]
                 {
                     ProfileSettingsKeys.RadioTxEffects_Start, ProfileSettingsKeys.RadioTxEffects_End,
                     ProfileSettingsKeys.RadioRxEffects_Start, ProfileSettingsKeys.RadioRxEffects_End
                 })
            Assert.AreEqual("true", defaults[key.ToString()], key.ToString());
    }

    [TestMethod]
    public void ReceiveSoundsStartWithTheTransmitSoundsOfTheProfile()
    {
        // a profile of an older version: the receive sounds were the transmit sounds
        var older = new Section("Client Settings")
        {
            new Setting(nameof(ProfileSettingsKeys.RadioTransmissionStartSelection), "RADIO_TRANS_START_ALTERNATE.wav"),
            new Setting(nameof(ProfileSettingsKeys.RadioTransmissionEndSelection), "RADIO_TRANS_END_ALTERNATE.wav")
        };
        Assert.AreEqual("RADIO_TRANS_START_ALTERNATE.wav",
            ProfileSettingsStore.GetDefaultValue(older, nameof(ProfileSettingsKeys.RadioRxStartSelection)));
        Assert.AreEqual("RADIO_TRANS_END_ALTERNATE.wav",
            ProfileSettingsStore.GetDefaultValue(older, nameof(ProfileSettingsKeys.RadioRxEndSelection)));

        // a new profile: the default sounds
        var empty = new Section("Client Settings");
        Assert.AreEqual("FancyRelease.wav",
            ProfileSettingsStore.GetDefaultValue(empty, nameof(ProfileSettingsKeys.RadioRxStartSelection)));
        Assert.AreEqual("AlmostFancy.wav",
            ProfileSettingsStore.GetDefaultValue(empty, nameof(ProfileSettingsKeys.RadioRxEndSelection)));

        // other settings keep their plain defaults
        Assert.AreEqual("FancyRelease.wav",
            ProfileSettingsStore.GetDefaultValue(older, nameof(ProfileSettingsKeys.RadioTransmissionStartSelection)));
        Assert.AreEqual("true", ProfileSettingsStore.GetDefaultValue(older, nameof(ProfileSettingsKeys.RadioRxSquelchTail)));
        Assert.IsNull(ProfileSettingsStore.GetDefaultValue(older, "NoSuchSetting"));
    }

    [TestMethod]
    public void TheSelectedSoundFallsBackToTheDefaultClick()
    {
        var effects = CachedAudioEffectProvider.SortTones(ShippedEndSounds.Select(sound => LoadShipped(sound.File)));

        Assert.AreEqual("RADIO_TRANS_END_ROGER_BEEP.wav",
            CachedAudioEffectProvider.FindEffect(effects, "radio_trans_end_roger_beep.WAV").FileName);
        Assert.AreEqual("RADIO_TRANS_END.wav", CachedAudioEffectProvider.FindEffect(effects, "removed.wav").FileName);
        Assert.AreEqual("RADIO_TRANS_END.wav", CachedAudioEffectProvider.FindEffect(effects, null).FileName);
        Assert.IsNull(CachedAudioEffectProvider.FindEffect(Array.Empty<CachedAudioEffect>(), "RADIO_TRANS_END.wav"));
    }
}
