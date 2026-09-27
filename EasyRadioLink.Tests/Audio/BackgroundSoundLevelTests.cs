using System;
using System.IO;
using System.Linq;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Audio.Utility;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Audio;

/// <summary>The level of the sender's background sound (<see cref="BackgroundSoundLevel" />).</summary>
[TestClass]
public class BackgroundSoundLevelTests
{
    private const int Fs = 48000;
    private const int Block = 1920;

    private static CachedAudioEffect[] ShippedBackgrounds()
    {
        var folder = Path.Combine(RepositoryFiles.AudioEffectsFolder, CachedAudioEffectProvider.BackgroundFolderName);
        var effects = Directory.GetFiles(folder, "*.wav")
            .Select(file => new CachedAudioEffect(CachedAudioEffect.AudioEffectTypes.BACKGROUND,
                Path.GetFileName(file), file))
            .ToArray();
        Assert.IsNotEmpty(effects);

        return effects;
    }

    private static double Db(double linear)
    {
        return 20 * Math.Log10(linear);
    }

    [TestMethod]
    public void EveryShippedSoundIsEquallyLoudThroughARadio()
    {
        foreach (var effect in ShippedBackgrounds())
        foreach (var volume in new[] { 1f, 0.4f, 0.25f })
        {
            Assert.IsTrue(effect.Loaded, effect.FileName);
            var gainDb = Db(BackgroundSoundLevel.Gain(effect.RadioBandRMS, effect.RMS, volume));
            var target = BackgroundSoundLevel.FullVolumeRadioBandDbfs + Db(volume);

            // without their rumble none of the shipped sounds needs the bass limit
            Assert.AreEqual(target, effect.RadioBandRMS + gainDb, 0.01, $"{effect.FileName} at {volume:P0}");
            Assert.IsLessThan(target + BackgroundSoundLevel.MaxFullBandAboveDb, effect.RMS + gainDb,
                $"{effect.FileName} at {volume:P0}");
        }
    }

    [TestMethod]
    public void BassHeavySoundsAreLimited()
    {
        // radio band -30 dBFS, full band -10 dBFS: the radio band target (-24) would put the full band at -4 dBFS
        var gainDb = Db(BackgroundSoundLevel.Gain(-30, -10, 1f));
        Assert.AreEqual(BackgroundSoundLevel.FullVolumeRadioBandDbfs + BackgroundSoundLevel.MaxFullBandAboveDb,
            -10 + gainDb, 1e-4);
    }

    [TestMethod]
    public void RumbleIsRemovedAndTheLoopStaysSeamless()
    {
        float[] Sine(double frequency, int samples)
        {
            var x = new float[samples];
            for (var i = 0; i < x.Length; i++) x[i] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / Fs));
            return x;
        }

        double RmsDb(float[] x, int from)
        {
            double sum = 0;
            for (var i = from; i < x.Length; i++) sum += (double)x[i] * x[i];
            return 10 * Math.Log10(sum / (x.Length - from));
        }

        var rumble = Sine(40, Fs * 2);
        BackgroundSoundLevel.RemoveRumble(rumble, Fs);
        Assert.IsLessThan(-9.03 - 15, RmsDb(rumble, Fs / 2), "40 Hz");

        // 1 kHz: whole periods, so the looped sound is seamless before and after the filter
        var tone = Sine(1000, Fs * 2);
        BackgroundSoundLevel.RemoveRumble(tone, Fs);
        Assert.AreEqual(-9.03, RmsDb(tone, 0), 0.2, "1 kHz");

        var largestStep = 0.0;
        for (var i = 1; i < tone.Length; i++) largestStep = Math.Max(largestStep, Math.Abs(tone[i] - tone[i - 1]));
        Assert.IsLessThanOrEqualTo(largestStep * 1.01, Math.Abs(tone[0] - tone[^1]), "loop point");
    }

    [TestMethod]
    public void VolumeLaw()
    {
        var full = BackgroundSoundLevel.Gain(-18, -14, 1f);
        Assert.AreEqual(-6.0, Db(full), 1e-4, "-24 dBFS in the radio band at 100 %");
        Assert.AreEqual(-7.96, Db(BackgroundSoundLevel.Gain(-18, -14, 0.4f) / full), 0.01, "40 %");
        Assert.AreEqual(-12.04, Db(BackgroundSoundLevel.Gain(-18, -14, 0.25f) / full), 0.01, "25 %");
        Assert.AreEqual(full, BackgroundSoundLevel.Gain(-18, -14, 5f), "clamped to 100 %");

        var previous = float.MaxValue;
        for (var volume = 1f; volume > 0f; volume -= 0.05f)
        {
            var gain = BackgroundSoundLevel.Gain(-18, -14, volume);
            Assert.IsLessThan(previous, gain);
            previous = gain;
        }
    }

    [TestMethod]
    public void InvalidOrSilentSoundsStayQuiet()
    {
        Assert.AreEqual(0f, BackgroundSoundLevel.Gain(-18, -14, 0f));
        Assert.AreEqual(0f, BackgroundSoundLevel.Gain(-18, -14, -1f));
        Assert.AreEqual(0f, BackgroundSoundLevel.Gain(-18, -14, float.NaN));
        Assert.AreEqual(0f, BackgroundSoundLevel.Gain(double.NegativeInfinity, -14, 1f));
        Assert.AreEqual(0f, BackgroundSoundLevel.Gain(-18, double.NaN, 1f));

        // an almost silent file is raised by at most MaxGainDb
        Assert.AreEqual(BackgroundSoundLevel.MaxGainDb, Db(BackgroundSoundLevel.Gain(-90, -85, 1f)), 1e-4);

        Assert.AreEqual(double.NegativeInfinity, BackgroundSoundLevel.RadioBandRms(new float[480], Fs));
        Assert.AreEqual(double.NegativeInfinity, BackgroundSoundLevel.RadioBandRms(Array.Empty<float>(), Fs));
    }

    [TestMethod]
    public void RadioBandRmsIgnoresBass()
    {
        float[] Sine(double frequency)
        {
            var x = new float[Fs * 2];
            for (var i = 0; i < x.Length; i++) x[i] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / Fs));
            return x;
        }

        // 0.5 amplitude sine = -9.03 dBFS RMS
        Assert.AreEqual(-9.03, BackgroundSoundLevel.RadioBandRms(Sine(1000), Fs), 0.5);
        Assert.IsLessThan(-30, BackgroundSoundLevel.RadioBandRms(Sine(40), Fs));
    }

    /// <summary>
    ///     The reported bug: on HF (3.000 MHz) with the static on and the distance at 35 %, a helicopter at 40 % was buried
    ///     in the static (about 1 dB above it). It must stay clearly audible in the speech pauses - on HF and CB, with and
    ///     without distance - through the real receive pipeline (sender's model, static, compressor, distance).
    /// </summary>
    [TestMethod]
    public void BackgroundStaysAudibleOverTheStaticAndTheDistance()
    {
        const string sender = "BackgroundLevelTestSndr";
        var helicopter = ShippedBackgrounds().Single(e => e.FileName == "helicopter.wav");
        var gain = BackgroundSoundLevel.Gain(helicopter.RadioBandRMS, helicopter.RMS, 0.4f);

        var (voice, pause) = VowelBursts(12);
        var mix = new float[voice.Length];
        for (var i = 0; i < mix.Length; i++)
            mix[i] = voice[i] + gain * helicopter.AudioEffectFloat[i % helicopter.AudioEffectFloat.Length];

        var previousPath = GlobalSettingsStore.Path;
        var directory = Path.Combine(Path.GetTempPath(), "erl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        GlobalSettingsStore.Path = directory;

        var profile = GlobalSettingsStore.Instance.ProfileSettingsStore;
        var keys = new[]
        {
            ProfileSettingsKeys.RadioEffectsRatio, ProfileSettingsKeys.VoiceDistortion,
            ProfileSettingsKeys.RadioBackgroundNoiseEffect, ProfileSettingsKeys.NATOTone
        };
        var saved = keys.ToDictionary(key => key, key => profile.GetClientSettingString(key));
        var clients = ConnectedClientsSingleton.Instance.Clients;

        try
        {
            profile.SetClientSettingFloat(ProfileSettingsKeys.RadioEffectsRatio, 1f);
            profile.SetClientSettingBool(ProfileSettingsKeys.RadioBackgroundNoiseEffect, true);
            profile.SetClientSettingBool(ProfileSettingsKeys.NATOTone, false);

            foreach (var (frequency, model) in new[] { (3000000d, "hf"), (27185000d, "cb") })
            {
                var info = new PlayerRadioInfoBase();
                info.radios[1] = new RadioBase { freq = frequency, modulation = Modulation.AM, Model = model };
                clients[sender] = new ClientInfo { ClientGuid = sender, Name = "sender", RadioInfo = info };

                foreach (var distance in new[] { 0f, 35f })
                {
                    profile.SetClientSettingFloat(ProfileSettingsKeys.VoiceDistortion, distance);

                    var withBackground = Receive(mix, frequency, sender);
                    var voiceOnly = Receive(voice, frequency, sender);
                    var overStatic = PauseDb(withBackground, pause) - PauseDb(voiceOnly, pause);

                    Assert.IsGreaterThanOrEqualTo(4.0, overStatic,
                        $"{model} {frequency / 1e6:0.000} MHz, distance {distance} %: background only " +
                        $"{overStatic:0.0} dB above the static in the pauses");
                }
            }
        }
        finally
        {
            clients.TryRemove(sender, out _);
            foreach (var (key, value) in saved) profile.SetClientSettingString(key, value);
            GlobalSettingsStore.Path = previousPath;
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }

    private static float[] Receive(float[] input, double frequency, string sender)
    {
        var pipeline = new ClientTransmissionPipelineProvider();
        var transmission = new DeJitteredTransmission
        {
            Frequency = frequency, Modulation = Modulation.AM, Volume = 1f, Decryptable = true, Guid = sender,
            ReceivedRadio = 1
        };

        var output = (float[])input.Clone();
        for (var position = 0; position < output.Length; position += Block)
            pipeline.Process(transmission, output.AsSpan(position, Math.Min(Block, output.Length - position)));

        return output;
    }

    /// <summary>
    ///     Vowels of 0.5 s at about -22 dBFS RMS (received speech after the receive AGC) with 0.7 s pauses;
    ///     <c>pause</c> marks the pause samples at least 150 ms away from a vowel (after the first 2 s).
    /// </summary>
    private static (float[] Voice, bool[] Pause) VowelBursts(double seconds)
    {
        var n = (int)(seconds * Fs);
        var voice = new float[n];
        var pause = new bool[n];
        int on = Fs / 2, off = Fs * 7 / 10, edge = Fs * 15 / 100, fade = Fs / 50;

        for (var i = 0; i < n; i++)
        {
            var t = i % (on + off);
            if (t < on)
            {
                var envelope = Math.Min(1.0, Math.Min(t, on - t) / (double)fade);
                double v = 0;
                for (var k = 1; k * 125 < 4500; k++)
                {
                    var f = k * 125.0;
                    double d1 = (f - 700) / 150, d2 = (f - 1200) / 200, d3 = (f - 2500) / 250;
                    var weight = 0.1 + 1 / (1 + d1 * d1) + 0.7 / (1 + d2 * d2) + 0.4 / (1 + d3 * d3);
                    v += weight * Math.Sin(2 * Math.PI * f * i / Fs + 0.3 * k * k);
                }

                voice[i] = (float)(0.028 * envelope * v);
            }
            else
            {
                pause[i] = i >= 2 * Fs && t >= on + edge && t < on + off - edge;
            }
        }

        return (voice, pause);
    }

    private static double PauseDb(float[] samples, bool[] pause)
    {
        double sum = 0;
        var count = 0;
        for (var i = 0; i < samples.Length; i++)
            if (pause[i])
            {
                sum += (double)samples[i] * samples[i];
                count++;
            }

        return 10 * Math.Log10(Math.Max(sum / Math.Max(count, 1), 1e-24));
    }
}
