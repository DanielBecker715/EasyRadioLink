using System;
using System.IO;
using System.Linq;
using EasyRadioLink.Client.Audio.Managers;
using EasyRadioLink.Client.UI.ClientWindow.ClientSettingsControl;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Tests.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>
///     The radio sound drop-downs of the Settings tab ("Off", "Click", "Roger beep", ...) and the two profile settings
///     behind each of them (on/off key and selection key), and the play button's rendering.
/// </summary>
[TestClass]
public class RadioSoundChoiceTests
{
    // the lists of the four drop-downs: start sounds (press PTT / someone starts talking), end sounds (release PTT /
    // someone stops talking), as the client loads them
    private static CachedAudioEffect[] StartSounds()
    {
        return CachedAudioEffectProvider.LoadTones(RepositoryFiles.AudioEffectsFolder).Start.ToArray();
    }

    private static CachedAudioEffect[] EndSounds()
    {
        return CachedAudioEffectProvider.LoadTones(RepositoryFiles.AudioEffectsFolder).End.ToArray();
    }

    [TestMethod]
    public void TheOptionsAreOffAndTheSounds()
    {
        var options = RadioSoundChoice.BuildOptions(EndSounds(), "Off");

        Assert.AreEqual(new RadioSoundOption(RadioSoundChoice.Off, "Off"), options[0]);
        CollectionAssert.AreEqual(
            new[]
            {
                "Off", "Click", "Soft click", "Roger beep", "Double beep", "Three-tone beep", "Fancy Release",
                "Almost Fancy"
            },
            options.Select(option => option.Name).ToArray());
        Assert.AreEqual("RADIO_TRANS_END_ROGER_BEEP.wav", options[3].Value, "the value is the stored file name");

        CollectionAssert.AreEqual(
            new[] { "Off", "Click", "Soft click", "Chirp", "Key-up beep", "Fancy Release", "Almost Fancy" },
            RadioSoundChoice.BuildOptions(StartSounds(), "Off").Select(option => option.Name).ToArray());
    }

    [TestMethod]
    public void TheOwnersSoundsWorkInEverySlot()
    {
        foreach (var sounds in new[] { StartSounds(), EndSounds() })
        foreach (var file in new[] { "FancyRelease.wav", "AlmostFancy.wav" })
        {
            Assert.IsTrue(RadioSoundChoice.TryApply(file, sounds, out var enabled, out var fileName), file);
            Assert.IsTrue(enabled);
            Assert.AreEqual(file, fileName);
            Assert.AreEqual(file, RadioSoundChoice.FromSettings(true, fileName, sounds));

            var effect = CachedAudioEffectProvider.FindEffect(sounds, file);
            Assert.IsTrue(effect.Loaded && effect.AudioEffectFloat.Length > 0, "the play button and the radio play it");
        }
    }

    [TestMethod]
    public void TheSettingsShowOffOrTheSoundThatPlays()
    {
        var sounds = EndSounds();

        Assert.AreEqual(RadioSoundChoice.Off,
            RadioSoundChoice.FromSettings(false, "RADIO_TRANS_END_ROGER_BEEP.wav", sounds), "switched off");
        Assert.AreEqual("RADIO_TRANS_END_ROGER_BEEP.wav",
            RadioSoundChoice.FromSettings(true, "RADIO_TRANS_END_ROGER_BEEP.wav", sounds));
        Assert.AreEqual("RADIO_TRANS_END_ROGER_BEEP.wav",
            RadioSoundChoice.FromSettings(true, "radio_trans_end_roger_beep.WAV", sounds), "file names ignore the case");
        Assert.AreEqual("RADIO_TRANS_END.wav", RadioSoundChoice.FromSettings(true, "removed-own-sound.wav", sounds),
            "a sound that is not available any more plays (and shows) the default click");
        Assert.AreEqual(RadioSoundChoice.Off,
            RadioSoundChoice.FromSettings(true, "RADIO_TRANS_END.wav", Array.Empty<CachedAudioEffect>()));
    }

    [TestMethod]
    public void ChoosingASoundWritesBothSettings()
    {
        var sounds = EndSounds();

        Assert.IsTrue(RadioSoundChoice.TryApply("RADIO_TRANS_END_DOUBLE_BEEP.wav", sounds, out var enabled,
            out var fileName));
        Assert.IsTrue(enabled);
        Assert.AreEqual("RADIO_TRANS_END_DOUBLE_BEEP.wav", fileName);

        Assert.IsTrue(RadioSoundChoice.TryApply("radio_trans_end_double_beep.wav", sounds, out enabled, out fileName));
        Assert.AreEqual("RADIO_TRANS_END_DOUBLE_BEEP.wav", fileName, "stored with the file's own spelling");

        // "Off" switches the sound off and keeps the stored sound
        Assert.IsTrue(RadioSoundChoice.TryApply(RadioSoundChoice.Off, sounds, out enabled, out fileName));
        Assert.IsFalse(enabled);
        Assert.IsNull(fileName);
        Assert.IsTrue(RadioSoundChoice.TryApply(null, sounds, out enabled, out fileName));
        Assert.IsFalse(enabled);

        // anything else writes nothing
        Assert.IsFalse(RadioSoundChoice.TryApply("RADIO_TRANS_START_CHIRP.wav", sounds, out _, out _),
            "a start sound is not an end sound");
        Assert.IsFalse(RadioSoundChoice.TryApply(@"..\..\evil.wav", sounds, out _, out _));
    }

    [TestMethod]
    public void EveryOptionRoundTrips()
    {
        var sounds = EndSounds();
        var stored = "RADIO_TRANS_END_THREE_TONE_BEEP.wav";

        foreach (var option in RadioSoundChoice.BuildOptions(sounds, "Off"))
        {
            Assert.IsTrue(RadioSoundChoice.TryApply(option.Value, sounds, out var enabled, out var fileName),
                option.Name);
            stored = fileName ?? stored;

            Assert.AreEqual(option.Value, RadioSoundChoice.FromSettings(enabled, stored, sounds), option.Name);
        }
    }

    [TestMethod]
    public void ThePreviewPlaysTheSoundWithTheRadioBalance()
    {
        var mono = new[] { 1f, -0.5f, 0.25f };

        var centred = SoundEffectPreview.RenderStereo(mono, 0f);
        Assert.HasCount((3 + 48000 / 4) * 2, centred, "stereo, followed by 250 ms of silence");
        CollectionAssert.AreEqual(new[] { 0.5f, 0.5f, -0.25f, -0.25f, 0.125f, 0.125f }, centred.Take(6).ToArray(),
            "centred like the radio (RadioMixingProvider.CreateBalancedMix)");
        Assert.IsTrue(centred.Skip(6).All(sample => sample == 0f));

        var left = SoundEffectPreview.RenderStereo(mono, -1f);
        Assert.AreEqual(1f, left[0]);
        Assert.AreEqual(0f, left[1], "radio on the left: nothing on the right");

        Assert.AreEqual(0.5f, SoundEffectPreview.RenderStereo(mono, float.NaN)[1], "an invalid balance is centred");
    }
}
