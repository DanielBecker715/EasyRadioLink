using System;
using System.Collections.Generic;
using System.Linq;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

namespace EasyRadioLink.Common.Tests.Audio;

/// <summary>The "Voice distortion" option (<see cref="VoiceDistortionProvider" />).</summary>
[TestClass]
public class VoiceDistortionTests
{
    private const int Fs = 48000;

    // the receive pipeline works in 40 ms blocks
    private const int Block = 1920;

    private static readonly VoiceDistortionFlavour[] Flavours = Enum.GetValues<VoiceDistortionFlavour>();

    /// <summary>
    ///     Deterministic speech-like signal: a harmonic series (pitch 110-155 Hz) with moving formants, syllables at
    ///     3.5 Hz (every fifth one silent) and faint static. About -14 dBFS RMS at gain 1 - the level the radio models
    ///     deliver.
    /// </summary>
    private sealed class SpeechLike : ISampleProvider
    {
        private readonly double _gain;
        private long _n;
        private double _phase;
        private ulong _rng = 88172645463325252UL;

        public SpeechLike(double gain = 1)
        {
            _gain = gain;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++, _n++)
            {
                var t = _n / (double)Fs;
                var f0 = 130 + 20 * Math.Sin(2 * Math.PI * 0.6 * t) + 6 * Math.Sin(2 * Math.PI * 5.3 * t);
                _phase = (_phase + 2 * Math.PI * f0 / Fs) % (2 * Math.PI);
                var f1 = 550 + 200 * Math.Sin(2 * Math.PI * 1.1 * t);
                var f2 = 1500 + 400 * Math.Sin(2 * Math.PI * 0.8 * t + 1);

                double voice = 0;
                for (var k = 1; k * f0 <= 4500; k++)
                {
                    var f = k * f0;
                    double d1 = (f - f1) / 120, d2 = (f - f2) / 160, d3 = (f - 2600) / 220;
                    var weight = 0.05 + 1 / (1 + d1 * d1) + 0.6 / (1 + d2 * d2) + 0.3 / (1 + d3 * d3);
                    voice += weight * Math.Sin(k * _phase + 0.3 * k * k);
                }

                var envelope = (long)(t * 3.5) % 5 == 4 ? 0 : Math.Pow(0.5 - 0.5 * Math.Cos(2 * Math.PI * 3.5 * t), 0.7);

                _rng ^= _rng << 13;
                _rng ^= _rng >> 7;
                _rng ^= _rng << 17;
                var noise = ((_rng >> 11) * (1.0 / (1UL << 53)) * 2 - 1) * 0.003;

                buffer[offset + i] = (float)(_gain * (0.256 * voice * envelope + noise));
            }

            return count;
        }
    }

    private sealed class ArraySource : ISampleProvider
    {
        private readonly float[] _data;
        private int _position;

        public ArraySource(float[] data)
        {
            _data = data;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = _position < _data.Length ? _data[_position++] : 0f;

            return count;
        }
    }

    private static float[] Render(ISampleProvider provider, int samples, int block = Block)
    {
        var output = new float[samples];
        var buffer = new float[block];
        for (var position = 0; position < samples; position += block)
        {
            var count = Math.Min(block, samples - position);
            Assert.AreEqual(count, provider.Read(buffer, 0, count));
            Array.Copy(buffer, 0, output, position, count);
        }

        return output;
    }

    private static float[] Speech(double seconds, double gain = 1)
    {
        return Render(new SpeechLike(gain), (int)(seconds * Fs));
    }

    private static float[] Tone(double seconds, double amplitude = 0.28, double frequency = 1000)
    {
        var tone = new float[(int)(seconds * Fs)];
        for (var i = 0; i < tone.Length; i++) tone[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Fs));

        return tone;
    }

    private static VoiceDistortionProvider Distortion(float[] input, VoiceDistortionFlavour flavour, float amount,
        int seed = 1234)
    {
        return new VoiceDistortionProvider(new ArraySource(input), flavour, seed, amount);
    }

    private static float[] Distort(float[] input, VoiceDistortionFlavour flavour, float amount, int seed = 1234,
        int block = Block)
    {
        return Render(Distortion(input, flavour, amount, seed), input.Length, block);
    }

    private static double RmsDb(float[] samples, int from = 0, int count = -1)
    {
        if (count < 0) count = samples.Length - from;
        double sum = 0;
        for (var i = from; i < from + count; i++) sum += (double)samples[i] * samples[i];

        return 10 * Math.Log10(Math.Max(sum / count, 1e-24));
    }

    /// <summary>Level in dB of every 50 ms window after the first second (the fading shows up here).</summary>
    private static List<double> WindowLevels(float[] samples)
    {
        var levels = new List<double>();
        for (var start = Fs; start + Fs / 20 <= samples.Length; start += Fs / 20)
            levels.Add(RmsDb(samples, start, Fs / 20));

        return levels;
    }

    [TestMethod]
    public void ZeroPercentIsAnExactBypass()
    {
        var input = Speech(3);
        foreach (var flavour in Flavours)
        {
            var distortion = Distortion(input, flavour, 0f);
            var output = Render(distortion, input.Length);

            CollectionAssert.AreEqual(input, output, $"{flavour}: 0 % must be bit-identical");
            Assert.IsTrue(distortion.IsBypassed);
        }

        // NaN and negative amounts are 0 %
        CollectionAssert.AreEqual(input, Distort(input, VoiceDistortionFlavour.Standard, float.NaN));
        CollectionAssert.AreEqual(input, Distort(input, VoiceDistortionFlavour.Standard, -1f));
    }

    [TestMethod]
    public void SwitchingOffFadesToAnExactBypassAndBackOnWithoutAJump()
    {
        var input = Speech(3);
        var distortion = Distortion(input, VoiceDistortionFlavour.Standard, 0.7f);
        var output = new float[input.Length];
        var buffer = new float[Block];
        for (var position = 0; position < input.Length; position += Block)
        {
            // off after 1 s, on again after 2 s (the settings change at block boundaries, like in the client)
            if (position == 25 * Block) distortion.Amount = 0f;
            if (position == 50 * Block) distortion.Amount = 0.35f;

            distortion.Read(buffer, 0, Block);
            Array.Copy(buffer, 0, output, position, Block);
        }

        // 20 ms fade, then the dry input sample for sample
        var bypassFrom = 25 * Block + Fs / 50 + 5;
        for (var i = bypassFrom; i < 50 * Block; i++) Assert.AreEqual(input[i], output[i], $"sample {i}");

        // no jump when it comes back: the step between samples stays in the range of the signal itself
        var maxStepInput = Enumerable.Range(1, input.Length - 1).Max(i => Math.Abs(input[i] - input[i - 1]));
        for (var i = 50 * Block; i < 50 * Block + Fs / 25; i++)
            Assert.IsLessThan(4 * maxStepInput + 0.1, Math.Abs(output[i] - output[i - 1]), $"jump at sample {i}");
    }

    [TestMethod]
    public void LevelStaysWithinThreeDecibels()
    {
        var input = Speech(10);
        var inputDb = RmsDb(input, Fs / 2);
        Assert.AreEqual(-14, inputDb, 1.5, "test signal at the level of the radio models");

        foreach (var flavour in Flavours)
        foreach (var amount in new[] { 0.35f, 0.7f, 1f })
        {
            var output = Distort(input, flavour, amount);
            var delta = RmsDb(output, Fs / 2) - inputDb;
            Assert.IsLessThan(3.0, Math.Abs(delta), $"{flavour} {amount:P0}: {delta:+0.0;-0.0} dB");
        }
    }

    [TestMethod]
    public void OutputIsAlwaysFinite()
    {
        var square = Enumerable.Range(0, Fs * 2).Select(i => i / 60 % 2 == 0 ? 1f : -1f).ToArray();
        var impulses = new float[Fs * 2];
        for (var i = 0; i < impulses.Length; i += 4801) impulses[i] = 1f;
        var tiny = Enumerable.Range(0, Fs * 2).Select(i => (float)(1e-30 * Math.Sin(i))).ToArray();
        var inputs = new Dictionary<string, float[]>
        {
            ["speech"] = Speech(4), ["silence"] = new float[Fs * 2], ["full-scale square"] = square,
            ["impulses"] = impulses, ["tiny"] = tiny, ["loud speech"] = Speech(4, 6)
        };

        foreach (var input in inputs)
        foreach (var flavour in Flavours)
        foreach (var amount in new[] { 0.01f, 0.35f, 0.7f, 1f })
        {
            var output = Distort(input.Value, flavour, amount);
            Assert.IsTrue(output.All(float.IsFinite), $"{input.Key}, {flavour} {amount:P0}: NaN / Infinity");
        }
    }

    [TestMethod]
    public void LoudInputIsNotClipped()
    {
        // about -7 dBFS RMS, peaks above full scale
        var loud = Speech(8, 2.2);
        Assert.IsGreaterThan(1f, loud.Max(Math.Abs));

        foreach (var flavour in Flavours)
        foreach (var amount in new[] { 0.35f, 0.7f, 1f })
        {
            var output = Distort(loud, flavour, amount);
            var clipped = 100.0 * output.Count(sample => Math.Abs(sample) >= 0.999f) / output.Length;
            Assert.IsLessThan(5.0, clipped, $"{flavour} {amount:P0} clips {clipped:0.0}%");
            Assert.IsLessThan(0.95f, output.Max(Math.Abs), $"{flavour} {amount:P0}: peak");
        }
    }

    [TestMethod]
    public void BlockBoundariesAreSeamless()
    {
        var input = Speech(6);
        foreach (var flavour in Flavours)
        foreach (var amount in new[] { 0f, 0.1f, 0.2f, 0.35f })
        {
            var distortion = Distortion(input, flavour, amount, 77);
            var blocks = Render(distortion, input.Length);

            // the state carries over: 40 ms blocks, odd blocks and one single block give the same samples
            CollectionAssert.AreEqual(Distort(input, flavour, amount, 77, input.Length), blocks, $"{flavour} {amount:P0}");
            CollectionAssert.AreEqual(Distort(input, flavour, amount, 77, 441), blocks, $"{flavour} {amount:P0}");

            // no discontinuity spikes: second difference at the block boundaries vs. inside the blocks
            double boundary = 0, interior = 0;
            for (var i = 2; i < blocks.Length; i++)
            {
                var d2 = Math.Abs(blocks[i] - 2 * blocks[i - 1] + blocks[i - 2]);
                if (i % Block <= 1) boundary = Math.Max(boundary, d2);
                else interior = Math.Max(interior, d2);
            }

            Assert.IsLessThanOrEqualTo(interior, boundary, $"{flavour} {amount:P0}: spike at a block boundary");

            // and no dropouts / breakups / crackle up to 35 %
            Assert.AreEqual(0, distortion.DropoutCount, $"{flavour} {amount:P0}");
            Assert.AreEqual(0, distortion.BreakupCount, $"{flavour} {amount:P0}");
            Assert.AreEqual(0, distortion.CrackleCount, $"{flavour} {amount:P0}");
        }
    }

    [TestMethod]
    public void SameSeedGivesTheSameOutput()
    {
        var input = Speech(4);
        foreach (var flavour in Flavours)
        {
            var first = Distort(input, flavour, 0.8f, 42);
            CollectionAssert.AreEqual(first, Distort(input, flavour, 0.8f, 42), flavour.ToString());
            CollectionAssert.AreNotEqual(first, Distort(input, flavour, 0.8f, 43), flavour.ToString());
        }
    }

    [TestMethod]
    public void DropoutsAndCrackleOnlyAboveFiftyPercent()
    {
        var input = Speech(20);
        foreach (var flavour in new[] { VoiceDistortionFlavour.Standard, VoiceDistortionFlavour.Hf })
        {
            var half = Distortion(input, flavour, 0.5f);
            Render(half, input.Length);
            Assert.AreEqual(0, half.DropoutCount, $"{flavour} 50 %");
            Assert.AreEqual(0, half.CrackleCount, $"{flavour} 50 %");

            var full = Distortion(input, flavour, 1f);
            Render(full, input.Length);
            Assert.IsGreaterThan(5, full.DropoutCount, $"{flavour} 100 %: dropouts");
            Assert.IsGreaterThan(50, full.CrackleCount, $"{flavour} 100 %: crackle");
            Assert.AreEqual(0, full.BreakupCount, $"{flavour}: no digital breakup");
        }
    }

    [TestMethod]
    public void DigitalHasNoFadingAndNoCrackle()
    {
        foreach (var amount in new[] { 0.35, 0.7, 1.0 })
        {
            var parameters = VoiceDistortionProvider.ParametersFor(amount, VoiceDistortionFlavour.Digital);
            Assert.AreEqual(0, parameters.FadingDepthDb);
            Assert.AreEqual(0, parameters.CrackleRate);
            Assert.AreEqual(0, parameters.DropoutRate);
        }

        Assert.IsGreaterThan(0.0, VoiceDistortionProvider.ParametersFor(1, VoiceDistortionFlavour.Digital).BreakupRate);

        var speech = Speech(20);
        var digital = Distortion(speech, VoiceDistortionFlavour.Digital, 1f);
        Render(digital, speech.Length);
        Assert.AreEqual(0, digital.CrackleCount);
        Assert.AreEqual(0, digital.DropoutCount);
        Assert.IsGreaterThan(10, digital.BreakupCount, "digital breakup instead");

        // a steady tone keeps its level on DIG (no fading), while the analogue bands fade
        var tone = Tone(12);
        var digitalLevels = WindowLevels(Distort(tone, VoiceDistortionFlavour.Digital, 0.35f));
        Assert.IsLessThan(0.5, digitalLevels.Max() - digitalLevels.Min(), "DIG must not fade");

        var standardLevels = WindowLevels(Distort(tone, VoiceDistortionFlavour.Standard, 0.35f));
        Assert.IsGreaterThan(2.0, standardLevels.Max() - standardLevels.Min(), "VHF/UHF fades");
    }

    [TestMethod]
    public void FadingDepthFollowsTheAmountAndTheBand()
    {
        Assert.AreEqual(2, VoiceDistortionProvider.ParametersFor(0.35, VoiceDistortionFlavour.Standard).FadingDepthDb, 1e-9);
        Assert.AreEqual(5, VoiceDistortionProvider.ParametersFor(0.7, VoiceDistortionFlavour.Standard).FadingDepthDb, 1e-9);
        Assert.AreEqual(8, VoiceDistortionProvider.ParametersFor(1, VoiceDistortionFlavour.Standard).FadingDepthDb, 1e-9);
        foreach (var amount in new[] { 0.2, 0.35, 0.7, 1 })
            Assert.AreEqual(1.5 * VoiceDistortionProvider.ParametersFor(amount, VoiceDistortionFlavour.Standard).FadingDepthDb,
                VoiceDistortionProvider.ParametersFor(amount, VoiceDistortionFlavour.Hf).FadingDepthDb, 1e-9);

        // measured on a steady tone: the swing grows with the amount, HF (QSB) swings more
        var tone = Tone(30);
        double Swing(VoiceDistortionFlavour flavour, float amount)
        {
            var levels = WindowLevels(Distort(tone, flavour, amount, 11)).OrderBy(level => level).ToList();
            // ignore the dropouts at 100 %: 2nd percentile to maximum
            return levels[^1] - levels[levels.Count / 50];
        }

        var standard35 = Swing(VoiceDistortionFlavour.Standard, 0.35f);
        var standard70 = Swing(VoiceDistortionFlavour.Standard, 0.7f);
        var standard100 = Swing(VoiceDistortionFlavour.Standard, 1f);
        Assert.IsTrue(standard35 is > 2 and < 5, $"35 %: +-2 dB, measured {standard35:0.0} dB peak to peak");
        Assert.IsTrue(standard70 is > 6 and < 11, $"70 %: +-5 dB, measured {standard70:0.0} dB peak to peak");
        Assert.IsTrue(standard100 is > 10 and < 17, $"100 %: +-8 dB, measured {standard100:0.0} dB peak to peak");
        Assert.IsGreaterThan(standard70 + 2, Swing(VoiceDistortionFlavour.Hf, 0.7f), "HF fades deeper");
    }

    [TestMethod]
    public void ParametersScaleSmoothlyWithTheAmount()
    {
        var off = VoiceDistortionProvider.ParametersFor(0, VoiceDistortionFlavour.Standard);
        Assert.AreEqual(200, off.HighPassHz, 1e-9);
        Assert.AreEqual(3800, off.LowPassHz, 1e-9);
        Assert.AreEqual(0, off.DriveDb, 1e-9);
        Assert.AreEqual(0, off.Bits, "no bit reduction");
        Assert.AreEqual(Fs, off.HoldRateHz, 1e-9);

        var full = VoiceDistortionProvider.ParametersFor(1, VoiceDistortionFlavour.Standard);
        Assert.AreEqual(500, full.HighPassHz, 1e-6);
        Assert.AreEqual(2300, full.LowPassHz, 1e-6);
        Assert.AreEqual(20, full.DriveDb, 1e-9);
        Assert.AreEqual(6, full.Bits, 1e-9);
        Assert.AreEqual(8000, full.HoldRateHz, 1e-6);

        // lo-fi is clearly there from 40 % on
        var forty = VoiceDistortionProvider.ParametersFor(0.4, VoiceDistortionFlavour.Standard);
        Assert.IsLessThan(9.0, forty.Bits);
        Assert.IsLessThan(18000.0, forty.HoldRateHz);

        // monotonic, no jumps
        var previous = off;
        for (var amount = 0.01; amount <= 1.0001; amount += 0.01)
        {
            var current = VoiceDistortionProvider.ParametersFor(amount, VoiceDistortionFlavour.Standard);
            Assert.IsGreaterThanOrEqualTo(previous.HighPassHz, current.HighPassHz);
            Assert.IsLessThanOrEqualTo(previous.LowPassHz, current.LowPassHz);
            Assert.IsGreaterThanOrEqualTo(previous.DriveDb, current.DriveDb);
            Assert.IsGreaterThanOrEqualTo(previous.FadingDepthDb, current.FadingDepthDb);
            Assert.IsLessThanOrEqualTo(previous.HoldRateHz, current.HoldRateHz);
            Assert.IsLessThan(40.0, current.HighPassHz - previous.HighPassHz + (previous.LowPassHz - current.LowPassHz));
            Assert.IsLessThan(3.0, current.DriveDb - previous.DriveDb);
            if (current.Amount <= 0.5)
            {
                Assert.AreEqual(0, current.CrackleRate);
                Assert.AreEqual(0, current.DropoutRate);
            }

            previous = current;
        }
    }

    [TestMethod]
    public void FlavourFollowsTheBandPlan()
    {
        Assert.AreEqual(VoiceDistortionFlavour.Hf, VoiceDistortionProvider.FlavourFor(1_500_000, Modulation.AM), "MW");
        Assert.AreEqual(VoiceDistortionFlavour.Hf, VoiceDistortionProvider.FlavourFor(7_100_000, Modulation.AM), "HF");
        Assert.AreEqual(VoiceDistortionFlavour.Hf, VoiceDistortionProvider.FlavourFor(27_185_000, Modulation.AM), "CB");
        Assert.AreEqual(VoiceDistortionFlavour.Hf, VoiceDistortionProvider.FlavourFor(29_999_000, Modulation.AM));
        Assert.AreEqual(VoiceDistortionFlavour.Standard, VoiceDistortionProvider.FlavourFor(30_000_000, Modulation.FM));
        Assert.AreEqual(VoiceDistortionFlavour.Standard, VoiceDistortionProvider.FlavourFor(124_800_000, Modulation.AM));
        Assert.AreEqual(VoiceDistortionFlavour.Standard, VoiceDistortionProvider.FlavourFor(446_193_750, Modulation.FM));
        Assert.AreEqual(VoiceDistortionFlavour.Digital, VoiceDistortionProvider.FlavourFor(950_000_000, Modulation.DIGITAL));
        Assert.AreEqual(VoiceDistortionFlavour.Digital, VoiceDistortionProvider.FlavourFor(950_000_000, Modulation.FM), "DIG band");
        Assert.AreEqual(VoiceDistortionFlavour.Digital, VoiceDistortionProvider.FlavourFor(145_000_000, Modulation.DIGITAL));

        // the microphone preview has only the radio model
        Assert.AreEqual(VoiceDistortionFlavour.Hf, VoiceDistortionProvider.FlavourForModel("cb"));
        Assert.AreEqual(VoiceDistortionFlavour.Hf, VoiceDistortionProvider.FlavourForModel("hf"));
        Assert.AreEqual(VoiceDistortionFlavour.Hf, VoiceDistortionProvider.FlavourForModel("vintage"));
        Assert.AreEqual(VoiceDistortionFlavour.Standard, VoiceDistortionProvider.FlavourForModel("walkie"));
        Assert.AreEqual(VoiceDistortionFlavour.Standard, VoiceDistortionProvider.FlavourForModel("tactical"));
        Assert.AreEqual(VoiceDistortionFlavour.Standard, VoiceDistortionProvider.FlavourForModel("airband"));
        Assert.AreEqual(VoiceDistortionFlavour.Standard, VoiceDistortionProvider.FlavourForModel("standard"));
        Assert.AreEqual(VoiceDistortionFlavour.Digital, VoiceDistortionProvider.FlavourForModel("digital"));
        Assert.AreEqual(VoiceDistortionFlavour.Standard, VoiceDistortionProvider.FlavourForModel("my-own-radio"));
        Assert.AreEqual(VoiceDistortionFlavour.Standard, VoiceDistortionProvider.FlavourForModel(null));
    }

    [TestMethod]
    public void AmountComesFromTheProfileSettings()
    {
        Assert.AreEqual(0.35f, VoiceDistortionProvider.AmountFromSettings(1f, 35f), 1e-6f);
        Assert.AreEqual(1f, VoiceDistortionProvider.AmountFromSettings(0.5f, 100f), 1e-6f);
        Assert.AreEqual(0f, VoiceDistortionProvider.AmountFromSettings(0f, 35f), "radio effect strength 0 = off");
        Assert.AreEqual(0f, VoiceDistortionProvider.AmountFromSettings(float.NaN, 35f));
        Assert.AreEqual(1f, VoiceDistortionProvider.AmountFromSettings(1f, 150f));
        Assert.AreEqual(0f, VoiceDistortionProvider.AmountFromSettings(1f, -5f));
        Assert.AreEqual(0f, VoiceDistortionProvider.AmountFromSettings(1f, float.NaN));
    }

    [TestMethod]
    public void DefaultsAreSquelchTailOffAndThirtyFivePercentDistortion()
    {
        var defaults = ProfileSettingsStore.DefaultSettingsProfileSettings;
        Assert.AreEqual("false", defaults[nameof(ProfileSettingsKeys.RadioRxSquelchTail)]);
        Assert.AreEqual("35", defaults[nameof(ProfileSettingsKeys.VoiceDistortion)]);
        Assert.AreEqual(VoiceDistortionProvider.DefaultPercent,
            float.Parse(defaults[nameof(ProfileSettingsKeys.VoiceDistortion)], System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(VoiceDistortionProvider.SampleRate, Constants.OUTPUT_SAMPLE_RATE,
            "the provider's constant sample rate must be the output rate");
    }

    [TestMethod]
    public void ReceivePipelineDistortsTheDryShareOfTheMixToo()
    {
        // radio effect strength 30 % (a common setting): the wet branch is silent here, so all that is heard is the
        // dry share - the distortion must reach it, or the clean dry voice masks the fading and the dropouts
        var tone = Tone(12);
        var silence = new float[tone.Length];

        ISampleProvider Output(VoiceDistortionProvider distortion, float volume = 1f)
        {
            return ClientTransmissionPipelineProvider.BuildOutput(new ArraySource(tone), new ArraySource(silence), 0.3f,
                distortion, volume);
        }

        // 0 % is exactly the plain mix (also with the receiving radio's volume)
        var plain = Render(Output(null), tone.Length);
        CollectionAssert.AreEqual(plain,
            Render(Output(new VoiceDistortionProvider(null, VoiceDistortionFlavour.Standard, 1, 0f)), tone.Length));
        CollectionAssert.AreEqual(Render(Output(null, 0.5f), tone.Length),
            Render(Output(new VoiceDistortionProvider(null, VoiceDistortionFlavour.Standard, 1, 0f), 0.5f), tone.Length));

        // 70 %: the dry share fades (+-5 dB), drops out to silence and keeps its loudness
        var distortion = new VoiceDistortionProvider(null, VoiceDistortionFlavour.Standard, 11, 0.7f);
        var output = Render(Output(distortion), tone.Length);

        var levels = WindowLevels(output).OrderBy(level => level).ToList();
        var swing = levels[^1] - levels[levels.Count / 50];
        Assert.IsGreaterThan(6.0, swing, $"the dry share must fade: {swing:0.0} dB peak to peak");

        Assert.IsGreaterThan(0, distortion.DropoutCount);
        var quietest = double.MaxValue;
        for (var start = Fs; start + Fs / 500 <= output.Length; start += Fs / 500)
            quietest = Math.Min(quietest, RmsDb(output, start, Fs / 500));
        Assert.IsLessThan(RmsDb(plain, Fs) - 40, quietest, "a dropout silences the voice, not only the wet 30 %");

        Assert.AreEqual(RmsDb(plain, Fs), RmsDb(output, Fs), 2.0, "same loudness");
    }

    [TestMethod]
    public void RadioModelsStayEquallyLoudWithTheDistortion()
    {
        // the chain of the receive pipeline: sender's radio model -> voice distortion -> final clip at +-1
        var factory = RadioModelFactory.FromFolders(RepositoryFiles.RadioModelsFolder);
        var models = new[] { "standard", "cb", "walkie", "airband", "tactical", "hf", "vintage", "digital" };

        foreach (var amount in new[] { 0.35f, 1f })
        {
            var levels = new Dictionary<string, double>();
            foreach (var model in models)
            {
                // the models are calibrated for microphone level speech (about -26 dBFS)
                var clean = Render(new RadioFilter(new SpeechLike(0.25), model, false, factory), Fs * 8);
                var distorted = Distort(clean, VoiceDistortionProvider.FlavourForModel(model), amount);
                for (var i = 0; i < distorted.Length; i++) distorted[i] = Math.Clamp(distorted[i], -1f, 1f);

                var delta = RmsDb(distorted, Fs) - RmsDb(clean, Fs);
                Assert.IsLessThan(3.0, Math.Abs(delta), $"{model} {amount:P0}: {delta:+0.0;-0.0} dB");

                var cleanClipped = clean.Count(sample => Math.Abs(sample) >= 0.999f);
                var clipped = distorted.Count(sample => Math.Abs(sample) >= 0.999f);
                Assert.IsLessThanOrEqualTo(cleanClipped, clipped, $"{model} {amount:P0}: adds clipping");

                levels[model] = RmsDb(distorted, Fs);
            }

            foreach (var level in levels)
                Assert.IsLessThan(4.0, Math.Abs(level.Value - levels["standard"]),
                    $"{level.Key} {amount:P0} is {level.Value - levels["standard"]:0.0} dB louder/quieter than standard");
        }
    }
}
