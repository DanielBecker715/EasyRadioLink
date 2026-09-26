using System;
using System.IO;
using System.Linq;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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

    [TestMethod]
    public void ToneNamesAreFriendly()
    {
        Assert.AreEqual("Default", CachedAudioEffect.BuildDisplayName(
            CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START, "RADIO_TRANS_START.wav"));
        Assert.AreEqual("Alternate", CachedAudioEffect.BuildDisplayName(
            CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_START, "RADIO_TRANS_START_ALTERNATE.wav"));
        Assert.AreEqual("Roger Beep", CachedAudioEffect.BuildDisplayName(
            CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END, "radio_trans_end_ROGER_BEEP.wav"));
        Assert.AreEqual("Jet", CachedAudioEffect.BuildDisplayName(
            CachedAudioEffect.AudioEffectTypes.BACKGROUND, "jet.wav"));

        var file = Path.Combine(RepositoryFiles.AudioEffectsFolder, "RADIO_TRANS_END_ALTERNATE.wav");
        var effect = new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.RADIO_TRANS_END,
            "RADIO_TRANS_END_ALTERNATE.wav", file);
        Assert.AreEqual("Alternate", effect.ToString(), "combo boxes show the friendly name");
        Assert.AreEqual("RADIO_TRANS_END_ALTERNATE.wav", effect.FileName, "the profile stores the file name");
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
        Assert.AreEqual("RADIO_TRANS_START.wav", defaults[nameof(ProfileSettingsKeys.RadioTransmissionStartSelection)]);
        Assert.AreEqual("RADIO_TRANS_END.wav", defaults[nameof(ProfileSettingsKeys.RadioTransmissionEndSelection)]);
        Assert.IsTrue(File.Exists(Path.Combine(RepositoryFiles.AudioEffectsFolder,
            defaults[nameof(ProfileSettingsKeys.RadioTransmissionStartSelection)])));
        Assert.IsTrue(File.Exists(Path.Combine(RepositoryFiles.AudioEffectsFolder,
            defaults[nameof(ProfileSettingsKeys.RadioTransmissionEndSelection)])));
    }
}
