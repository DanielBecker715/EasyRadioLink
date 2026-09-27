using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

namespace EasyRadioLink.Common.Tests.Audio;

/// <summary>The "Distance (weak signal)" option (<see cref="WeakSignalChannelProvider" />).</summary>
[TestClass]
public class WeakSignalChannelTests
{
    private const int Fs = 48000;

    // the receive pipeline works in 40 ms blocks
    private const int Block = 1920;

    // 20 ms analysis windows
    private const int Window = Fs / 50;

    private static readonly WeakSignalBand[] Bands = Enum.GetValues<WeakSignalBand>();

    private static readonly WeakSignalBand[] AnalogueBands =
        { WeakSignalBand.Hf, WeakSignalBand.VhfAm, WeakSignalBand.VhfFm };

    private static readonly float[] Amounts = { 0.35f, 0.7f, 1f };

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

    /// <summary>A steady vowel: harmonics of 125 Hz up to 4.5 kHz with fixed formants (about -14 dBFS RMS).</summary>
    private static float[] Vowel(double seconds)
    {
        var vowel = new float[(int)(seconds * Fs)];
        for (var i = 0; i < vowel.Length; i++)
        {
            double v = 0;
            for (var k = 1; k * 125 < 4500; k++)
            {
                var f = k * 125.0;
                double d1 = (f - 700) / 150, d2 = (f - 1200) / 200, d3 = (f - 2500) / 250;
                var weight = 0.1 + 1 / (1 + d1 * d1) + 0.7 / (1 + d2 * d2) + 0.4 / (1 + d3 * d3);
                v += weight * Math.Sin(2 * Math.PI * f * i / Fs + 0.3 * k * k);
            }

            vowel[i] = (float)(0.07 * v);
        }

        return vowel;
    }

    private static WeakSignalChannelProvider Channel(float[] input, WeakSignalBand band, float amount, int seed = 1234,
        bool noiseMuted = false)
    {
        return new WeakSignalChannelProvider(new ArraySource(input), band, seed, amount) { NoiseMuted = noiseMuted };
    }

    private static float[] Process(float[] input, WeakSignalBand band, float amount, int seed = 1234, int block = Block,
        bool noiseMuted = false)
    {
        return Render(Channel(input, band, amount, seed, noiseMuted), input.Length, block);
    }

    private static float[] Difference(float[] a, float[] b)
    {
        var difference = new float[a.Length];
        for (var i = 0; i < a.Length; i++) difference[i] = a[i] - b[i];

        return difference;
    }

    private static double RmsDb(float[] samples, int from = 0, int count = -1)
    {
        if (count < 0) count = samples.Length - from;
        double sum = 0;
        for (var i = from; i < from + count; i++) sum += (double)samples[i] * samples[i];

        return 10 * Math.Log10(Math.Max(sum / count, 1e-24));
    }

    /// <summary>Level in dB of every window of <paramref name="size" /> samples from <paramref name="from" /> on.</summary>
    private static List<double> WindowLevels(float[] samples, int size, int from = Fs / 2)
    {
        var levels = new List<double>();
        for (var start = from; start + size <= samples.Length; start += size) levels.Add(RmsDb(samples, start, size));

        return levels;
    }

    private static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.OrderBy(value => value).ToList();
        return sorted[(int)Math.Clamp(fraction * sorted.Count, 0, sorted.Count - 1)];
    }

    private static double StandardDeviation(IReadOnlyCollection<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / values.Count);
    }

    /// <summary>
    ///     Impulsiveness of a noise signal in 20 ms windows: median and largest kurtosis (Gaussian: 3) and the largest
    ///     crest factor (peak / RMS; Gaussian noise over 960 samples: about 3.5-4.5, a crackle impulse: far more).
    /// </summary>
    private static (double MedianKurtosis, double MaxKurtosis, double MaxCrest) Impulsiveness(float[] noise)
    {
        var kurtosis = new List<double>();
        var crest = 0.0;
        for (var start = Fs / 2; start + Window <= noise.Length; start += Window)
        {
            double m2 = 0, m4 = 0, peak = 0;
            for (var i = start; i < start + Window; i++)
            {
                double x = noise[i];
                m2 += x * x;
                m4 += x * x * x * x;
                peak = Math.Max(peak, Math.Abs(x));
            }

            m2 /= Window;
            m4 /= Window;
            if (m2 < 1e-20) continue;

            kurtosis.Add(m4 / (m2 * m2));
            crest = Math.Max(crest, peak / Math.Sqrt(m2));
        }

        return (Percentile(kurtosis, 0.5), kurtosis.Max(), crest);
    }

    /// <summary>
    ///     Click detector: sample-to-sample jumps (second difference) larger than 8 times their local RMS (+-5 ms).
    ///     Filtered noise and band-limited voice stay below 5; a step, an impulse or a zipper step of the waveform
    ///     stands far out.
    /// </summary>
    private static int CountClicks(float[] samples)
    {
        var jump = new double[samples.Length];
        for (var i = 2; i < samples.Length; i++) jump[i] = samples[i] - 2.0 * samples[i - 1] + samples[i - 2];

        var half = Fs / 200;
        var prefix = new double[samples.Length + 1];
        for (var i = 0; i < samples.Length; i++) prefix[i + 1] = prefix[i] + jump[i] * jump[i];

        var clicks = 0;
        for (var i = Math.Max(half, 2); i < samples.Length - half; i++)
        {
            var rms = Math.Sqrt((prefix[i + half] - prefix[i - half]) / (2 * half));
            if (rms > 1e-9 && Math.Abs(jump[i]) > 8 * rms) clicks++;
        }

        return clicks;
    }

    /// <summary>
    ///     How much the notch pattern moves: for every harmonic of the <see cref="Vowel" /> between 500 and 2500 Hz, the
    ///     variance (dB^2) over 50 ms windows of its level relative to the mean of all of them (flat level changes do not
    ///     count), averaged over the harmonics.
    /// </summary>
    private static double SpectralVariance(float[] samples)
    {
        const int size = Fs / 20;
        var harmonics = Enumerable.Range(4, 17).ToArray(); // 500 .. 2500 Hz
        var series = harmonics.Select(_ => new List<double>()).ToArray();
        var levels = new double[harmonics.Length];
        for (var start = Fs; start + size <= samples.Length; start += size)
        {
            for (var h = 0; h < harmonics.Length; h++)
            {
                // Goertzel
                var coefficient = 2 * Math.Cos(2 * Math.PI * 125 * harmonics[h] / Fs);
                double s1 = 0, s2 = 0;
                for (var i = start; i < start + size; i++)
                {
                    var s0 = samples[i] + coefficient * s1 - s2;
                    s2 = s1;
                    s1 = s0;
                }

                levels[h] = 10 * Math.Log10(s1 * s1 + s2 * s2 - coefficient * s1 * s2 + 1e-20);
            }

            var mean = levels.Average();
            for (var h = 0; h < harmonics.Length; h++) series[h].Add(levels[h] - mean);
        }

        return series.Average(s => Math.Pow(StandardDeviation(s), 2));
    }

    [TestMethod]
    public void ZeroPercentIsAnExactBypass()
    {
        var input = Speech(3);
        foreach (var band in Bands)
        {
            var channel = Channel(input, band, 0f);
            var output = Render(channel, input.Length);

            CollectionAssert.AreEqual(input, output, $"{band}: 0 % must be bit-identical");
            Assert.IsTrue(channel.IsBypassed);
        }

        // NaN and negative amounts are 0 %
        CollectionAssert.AreEqual(input, Process(input, WeakSignalBand.Hf, float.NaN));
        CollectionAssert.AreEqual(input, Process(input, WeakSignalBand.VhfFm, -1f));
    }

    [TestMethod]
    public void SwitchingOffFadesToAnExactBypassAndBackOnWithoutAJump()
    {
        var input = Speech(3);
        var channel = Channel(input, WeakSignalBand.Hf, 0.7f);
        var output = new float[input.Length];
        var buffer = new float[Block];
        for (var position = 0; position < input.Length; position += Block)
        {
            // off after 1 s, on again after 2 s (the settings change at block boundaries, like in the client)
            if (position == 25 * Block) channel.Amount = 0f;
            if (position == 50 * Block) channel.Amount = 0.35f;

            channel.Read(buffer, 0, Block);
            Array.Copy(buffer, 0, output, position, Block);
        }

        // 20 ms fade, then the dry input sample for sample
        var bypassFrom = 25 * Block + Fs / 50 + 5;
        for (var i = bypassFrom; i < 50 * Block; i++) Assert.AreEqual(input[i], output[i], $"sample {i}");

        // no click when it goes off or comes back
        Assert.AreEqual(0, CountClicks(output));
    }

    [TestMethod]
    public void LoudnessStaysWithinTwoDecibels()
    {
        var input = Speech(10);
        var inputDb = RmsDb(input, Fs / 2);
        Assert.AreEqual(-14, inputDb, 1.5, "test signal at the level of the radio models");

        foreach (var band in Bands)
        foreach (var amount in Amounts)
        {
            var delta = RmsDb(Process(input, band, amount), Fs / 2) - inputDb;
            Assert.IsLessThan(2.0, Math.Abs(delta), $"{band} {amount:P0}: {delta:+0.0;-0.0} dB");
        }
    }

    [TestMethod]
    public void NothingClipsAndPeaksStayBelowNinetyPercent()
    {
        // speech at the level of the radio models and far too loud speech (about -7 dBFS RMS, peaks above full scale)
        var loud = Speech(6, 2.2);
        Assert.IsGreaterThan(1f, loud.Max(Math.Abs));

        foreach (var input in new[] { Speech(6), loud })
        foreach (var band in Bands)
        foreach (var amount in Amounts)
        {
            var output = Process(input, band, amount);
            Assert.AreEqual(0, output.Count(sample => Math.Abs(sample) >= 0.999f), $"{band} {amount:P0}: clipping");
            Assert.IsLessThanOrEqualTo(0.9f, output.Max(Math.Abs), $"{band} {amount:P0}: peak");
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
            ["speech"] = Speech(3), ["silence"] = new float[Fs * 2], ["full-scale square"] = square,
            ["impulses"] = impulses, ["tiny"] = tiny, ["loud speech"] = Speech(3, 6)
        };

        foreach (var input in inputs)
        foreach (var band in Bands)
        foreach (var amount in new[] { 0.01f, 0.35f, 0.7f, 1f })
        {
            var output = Process(input.Value, band, amount);
            Assert.IsTrue(output.All(float.IsFinite), $"{input.Key}, {band} {amount:P0}: NaN / Infinity");
            Assert.IsLessThanOrEqualTo(0.9f, output.Max(Math.Abs), $"{input.Key}, {band} {amount:P0}: peak");
        }
    }

    [TestMethod]
    public void OutputDoesNotDependOnTheBlockSize()
    {
        var input = Speech(4);
        foreach (var band in Bands)
        foreach (var amount in new[] { 0.1f, 0.35f, 1f })
        {
            // 40 ms blocks (the receive pipeline), 10 ms blocks, odd blocks and one single block: the same samples
            var blocks = Process(input, band, amount, 77);
            CollectionAssert.AreEqual(blocks, Process(input, band, amount, 77, Fs / 100), $"{band} {amount:P0} 10 ms");
            CollectionAssert.AreEqual(blocks, Process(input, band, amount, 77, 441), $"{band} {amount:P0} 441");
            CollectionAssert.AreEqual(blocks, Process(input, band, amount, 77, input.Length), $"{band} {amount:P0} single");
        }
    }

    [TestMethod]
    public void SameSeedGivesTheSameOutput()
    {
        var input = Speech(4);
        foreach (var band in Bands)
        {
            var first = Process(input, band, 0.8f, 42);
            CollectionAssert.AreEqual(first, Process(input, band, 0.8f, 42), band.ToString());
            if (band != WeakSignalBand.Digital) // digital has no random parts
                CollectionAssert.AreNotEqual(first, Process(input, band, 0.8f, 43), band.ToString());
        }
    }

    [TestMethod]
    public void NoiseIsFilteredGaussianNotImpulsive()
    {
        var speech = Speech(8);
        foreach (var band in AnalogueBands)
        foreach (var amount in Amounts)
        {
            // the added noise: the output minus the same processing without the noise
            var noise = Difference(Process(speech, band, amount), Process(speech, band, amount, noiseMuted: true));
            Assert.IsGreaterThan(-60.0, RmsDb(noise, Fs / 2), $"{band} {amount:P0}: there is noise");

            var (median, max, crest) = Impulsiveness(noise);
            var name = $"{band} {amount:P0}: kurtosis {median:0.00} (max {max:0.00}), crest {crest:0.00}";
            Assert.IsTrue(median is > 2.5 and < 3.5, name);
            Assert.IsLessThan(6.5, max, name);
            Assert.IsLessThan(6.5, crest, name);
            Assert.AreEqual(0, CountClicks(noise), name);
        }

        // the check itself catches crackle: a few impulses of 10 x the noise level
        var crackle = Difference(Process(speech, WeakSignalBand.Hf, 1f),
            Process(speech, WeakSignalBand.Hf, 1f, noiseMuted: true));
        var level = (float)Math.Sqrt(crackle.Skip(Fs).Average(x => (double)x * x));
        for (var i = Fs; i < crackle.Length; i += Fs) crackle[i] += 10 * level;
        Assert.IsGreaterThan(6.5, Impulsiveness(crackle).MaxCrest, "a crackle impulse must be detected");
    }

    [TestMethod]
    public void NoClicks()
    {
        var speech = Speech(6);
        var vowel = Vowel(6);
        foreach (var band in Bands)
        foreach (var amount in Amounts)
        {
            Assert.AreEqual(0, CountClicks(Process(speech, band, amount)), $"{band} {amount:P0} speech");
            Assert.AreEqual(0, CountClicks(Process(vowel, band, amount)), $"{band} {amount:P0} vowel");
        }

        // moving the slider while a transmission plays: the processing glides, no clicks
        var targets = new[] { 1f, 0.5f, 0.05f, 0.8f, 0.2f };
        foreach (var band in Bands)
        {
            var channel = Channel(speech, band, 0.35f);
            var output = new float[speech.Length];
            var buffer = new float[Block];
            for (var position = 0; position < speech.Length; position += Block)
            {
                if (position / Block % 12 == 11) channel.Amount = targets[position / Block / 12 % targets.Length];
                channel.Read(buffer, 0, Block);
                Array.Copy(buffer, 0, output, position, Block);
            }

            Assert.AreEqual(0, CountClicks(output), $"{band}: slider moved while playing");
        }

        // the detector finds a small step (half the local level) in such an output
        var stepped = Process(speech, WeakSignalBand.Hf, 0.35f);
        var at = 2 * Fs + 1234;
        var local = (float)Math.Sqrt(stepped.Skip(at - 240).Take(480).Average(x => (double)x * x));
        for (var i = at; i < at + 480; i++) stepped[i] += 0.5f * local;
        Assert.IsGreaterThanOrEqualTo(1, CountClicks(stepped), "a step must be detected");
    }

    [TestMethod]
    public void NoGapsOnASteadyVowel()
    {
        var vowel = Vowel(10);
        foreach (var band in Bands)
        foreach (var amount in Amounts)
        {
            // 20 ms windows never more than 20 dB below the median: fades and FM surges are filled, never silent
            var levels = WindowLevels(Process(vowel, band, amount), Window);
            var median = Percentile(levels, 0.5);
            var deepest = levels.Min() - median;
            Assert.IsGreaterThan(-20.0, deepest, $"{band} {amount:P0}: {deepest:0.0} dB");
        }
    }

    [TestMethod]
    public void MultipathMovesNotchesThroughTheVoice()
    {
        var vowel = Vowel(12);
        var still = SpectralVariance(vowel);
        var strongest = new Dictionary<WeakSignalBand, double>();
        foreach (var band in AnalogueBands)
        {
            // without the noise, so only the multipath is measured
            var previous = still;
            foreach (var amount in Amounts)
            {
                var variance = SpectralVariance(Process(vowel, band, amount, noiseMuted: true));
                Assert.IsGreaterThan(previous * 1.05, variance,
                    $"{band} {amount:P0}: {variance:0.00} dB^2 must be more than {previous:0.00} dB^2");
                previous = variance;
            }

            strongest[band] = previous;
        }

        // the swirl is strongest below 30 MHz, the FM limiter keeps FM smoothest
        Assert.IsGreaterThan(strongest[WeakSignalBand.VhfAm], strongest[WeakSignalBand.Hf]);
        Assert.IsGreaterThan(strongest[WeakSignalBand.VhfFm], strongest[WeakSignalBand.VhfAm]);
        Assert.IsGreaterThan(8.0, strongest[WeakSignalBand.Hf], "HF at 100 %: a strong swirl");
    }

    [TestMethod]
    public void FmKeepsTheVoiceSteadierThanAm()
    {
        var vowel = Vowel(12);
        foreach (var amount in new[] { 0.7f, 1f })
        {
            // the same channel (same seed) - AM fades with the signal, the FM limiter holds the voice
            var am = StandardDeviation(WindowLevels(Process(vowel, WeakSignalBand.VhfAm, amount, noiseMuted: true), Fs / 20));
            var fm = StandardDeviation(WindowLevels(Process(vowel, WeakSignalBand.VhfFm, amount, noiseMuted: true), Fs / 20));
            Assert.IsLessThan(0.6 * am, fm, $"{amount:P0}: FM {fm:0.00} dB vs AM {am:0.00} dB");

            // instead, the FM hiss surges up in the fades (FM threshold), more than the AM hiss breathes
            var speech = Speech(8);
            double Surges(WeakSignalBand band)
            {
                var noise = Difference(Process(speech, band, amount), Process(speech, band, amount, noiseMuted: true));
                var levels = WindowLevels(noise, Window);
                return Percentile(levels, 0.95) - Percentile(levels, 0.5);
            }

            var fmSurges = Surges(WeakSignalBand.VhfFm);
            var amSurges = Surges(WeakSignalBand.VhfAm);
            Assert.IsGreaterThan(5.0, fmSurges, $"{amount:P0}: FM hiss surges {fmSurges:0.0} dB");
            Assert.IsGreaterThan(amSurges + 1, fmSurges, $"{amount:P0}: FM {fmSurges:0.0} dB vs AM {amSurges:0.0} dB");
        }
    }

    [TestMethod]
    public void VoiceToNoiseFollowsTheAmount()
    {
        // over active speech (50 ms windows within 10 dB of the loudest), median of three transmissions:
        // about 24 dB at 35 %, 14 dB at 70 %, 7 dB at 100 % (HF 1 dB noisier)
        var speech = Speech(12);
        var inputLevels = WindowLevels(speech, Fs / 20, 0);
        var loudest = Percentile(inputLevels, 0.95);
        var targets = new Dictionary<float, double> { [0.35f] = 24, [0.7f] = 14, [1f] = 7 };

        foreach (var band in AnalogueBands)
        foreach (var amount in Amounts)
        {
            var ratios = new List<double>();
            foreach (var seed in new[] { 11, 22, 33 })
            {
                var output = Process(speech, band, amount, seed);
                var voice = Process(speech, band, amount, seed, noiseMuted: true);
                var noise = Difference(output, voice);
                for (var w = 20; w < inputLevels.Count; w++)
                    if (inputLevels[w] > loudest - 10)
                        ratios.Add(RmsDb(voice, w * Fs / 20, Fs / 20) - RmsDb(noise, w * Fs / 20, Fs / 20));
            }

            var median = Percentile(ratios, 0.5);
            var target = targets[amount] - (band == WeakSignalBand.Hf ? 1 : 0);
            Assert.AreEqual(target, median, 3.0, $"{band} {amount:P0}: {median:0.0} dB");
        }
    }

    [TestMethod]
    public void DigitalHasNoNoiseNoFadingNoWhistle()
    {
        foreach (var amount in Amounts)
        {
            var parameters = WeakSignalChannelProvider.ParametersFor(amount, WeakSignalBand.Digital);
            Assert.AreEqual(double.PositiveInfinity, parameters.SnrDb);
            Assert.AreEqual(double.PositiveInfinity, parameters.RiceKDb);
            Assert.AreEqual(0, parameters.EchoGain);
            Assert.AreEqual(double.NegativeInfinity, parameters.HeterodyneDb);
            Assert.IsGreaterThan(0.5, parameters.CodecMix, "the CVSD coding is clearly there from 35 %");
        }

        Assert.AreEqual(1, WeakSignalChannelProvider.ParametersFor(1, WeakSignalBand.Digital).CodecMix, 1e-9);

        // silence stays silence (the analogue bands hiss)
        var silence = new float[Fs * 3];
        foreach (var amount in Amounts)
        {
            var digital = Process(silence, WeakSignalBand.Digital, amount);
            Assert.IsLessThan(-70.0, RmsDb(digital, Fs / 2), $"DIG {amount:P0}: no noise");
            Assert.IsGreaterThan(-60.0, RmsDb(Process(silence, WeakSignalBand.VhfFm, amount), Fs / 2), $"FM {amount:P0}");
        }

        // nothing random: muting the noise changes nothing, another seed changes nothing
        var speech = Speech(4);
        var coded = Process(speech, WeakSignalBand.Digital, 1f);
        CollectionAssert.AreEqual(coded, Process(speech, WeakSignalBand.Digital, 1f, noiseMuted: true));
        CollectionAssert.AreEqual(coded, Process(speech, WeakSignalBand.Digital, 1f, 999));

        // a steady tone keeps its level (no fading), while the analogue bands fade
        var tone = Tone(10);
        foreach (var amount in Amounts)
        {
            var levels = WindowLevels(Process(tone, WeakSignalBand.Digital, amount), Fs / 20, Fs);
            Assert.IsLessThan(1.0, levels.Max() - levels.Min(), $"DIG {amount:P0} must not fade");
        }

        var fading = WindowLevels(Process(tone, WeakSignalBand.VhfAm, 0.35f), Fs / 20, Fs);
        Assert.IsGreaterThan(2.0, fading.Max() - fading.Min(), "VHF AM fades");

        // instead the CVSD coding: its error (against the same processing without it) grows with the amount
        var previous = double.NegativeInfinity;
        foreach (var amount in Amounts)
        {
            var output = Process(speech, WeakSignalBand.Digital, amount);
            var plain = Render(new WeakSignalChannelProvider(new ArraySource(speech), WeakSignalBand.Digital, 1234, amount)
                { CodecMuted = true }, speech.Length);
            var error = RmsDb(Difference(output, plain), Fs / 2) - RmsDb(plain, Fs / 2);
            Assert.IsGreaterThan(previous + 2, error, $"DIG {amount:P0}: coding error {error:0.0} dB");
            previous = error;
        }

        Assert.IsGreaterThan(-9.0, previous, "100 %: gritty secure voice");
    }

    [TestMethod]
    public void HeterodyneWhistleOnlyBelowThirtyMegahertzAboveHalf()
    {
        foreach (var amount in new[] { 0.0, 0.35, 0.5 })
            Assert.AreEqual(double.NegativeInfinity,
                WeakSignalChannelProvider.ParametersFor(amount, WeakSignalBand.Hf).HeterodyneDb, $"{amount:P0}");

        // peak amplitude relative to the tracked speech level; -34 dB keeps the whistle's rms below -32 dB relative
        // to the active voice
        var seventy = WeakSignalChannelProvider.ParametersFor(0.7, WeakSignalBand.Hf).HeterodyneDb;
        Assert.IsTrue(seventy is > -47 and < -34, $"70 %: {seventy:0.0} dB");
        Assert.AreEqual(-34, WeakSignalChannelProvider.ParametersFor(1, WeakSignalBand.Hf).HeterodyneDb, 1e-9);

        foreach (var band in new[] { WeakSignalBand.VhfAm, WeakSignalBand.VhfFm, WeakSignalBand.Digital })
            Assert.AreEqual(double.NegativeInfinity, WeakSignalChannelProvider.ParametersFor(1, band).HeterodyneDb);
    }

    [TestMethod]
    public void ParametersScaleSmoothlyWithTheAmount()
    {
        foreach (var band in Bands)
        {
            var off = WeakSignalChannelProvider.ParametersFor(0, band);
            Assert.AreEqual(150, off.HighPassHz, 1e-9);
            Assert.AreEqual(4200, off.LowPassHz, 1e-9);
            Assert.AreEqual(0, off.PeakDb, 1e-9);
            Assert.AreEqual(0, off.DriveDb, 1e-9);
            Assert.AreEqual(0, off.EchoGain, 1e-9);

            var full = WeakSignalChannelProvider.ParametersFor(1, band);
            Assert.AreEqual(450, full.HighPassHz, 1e-6);
            Assert.AreEqual(2600, full.LowPassHz, 1e-6);
            Assert.AreEqual(8, full.PeakDb, 1e-9);
            Assert.AreEqual(12, full.DriveDb, 1e-9);
            Assert.IsTrue(full.PeakHz is >= 1200 and <= 1800, band.ToString());

            var previous = off;
            for (var amount = 0.01; amount <= 1.0001; amount += 0.01)
            {
                var current = WeakSignalChannelProvider.ParametersFor(amount, band);
                var name = $"{band} {amount:P0}";
                Assert.IsGreaterThanOrEqualTo(previous.HighPassHz, current.HighPassHz, name);
                Assert.IsLessThanOrEqualTo(previous.LowPassHz, current.LowPassHz, name);
                Assert.IsGreaterThanOrEqualTo(previous.PeakDb, current.PeakDb, name);
                Assert.IsGreaterThanOrEqualTo(previous.DriveDb, current.DriveDb, name);
                Assert.IsLessThan(40.0, current.HighPassHz - previous.HighPassHz + (previous.LowPassHz - current.LowPassHz), name);
                Assert.IsLessThan(1.5, current.DriveDb - previous.DriveDb, name);

                if (band == WeakSignalBand.Digital)
                {
                    Assert.IsGreaterThanOrEqualTo(previous.CodecMix, current.CodecMix, name);
                }
                else
                {
                    Assert.IsLessThanOrEqualTo(previous.SnrDb, current.SnrDb, name);
                    Assert.IsLessThanOrEqualTo(previous.RiceKDb, current.RiceKDb, name);
                    Assert.IsGreaterThanOrEqualTo(previous.EchoGain, current.EchoGain, name);
                    Assert.IsGreaterThanOrEqualTo(previous.EchoDopplerHz, current.EchoDopplerHz, name);
                    Assert.IsGreaterThanOrEqualTo(previous.FadeRateHz, current.FadeRateHz, name);
                    Assert.IsLessThan(0.02,
                        Math.Pow(10, -current.SnrDb / 20) - Math.Pow(10, -previous.SnrDb / 20), name + ": noise jumps");
                    Assert.AreEqual(0, current.CodecMix, name);
                }

                previous = current;
            }
        }

        // the SNR at the nominal signal before the band offsets: 24 dB at 35 %, 13.5 dB at 70 %, 7 dB at 100 %
        Assert.AreEqual(26.4, WeakSignalChannelProvider.ParametersFor(0.35, WeakSignalBand.VhfAm).SnrDb, 0.1);
        Assert.AreEqual(10.2, WeakSignalChannelProvider.ParametersFor(1, WeakSignalBand.VhfAm).SnrDb, 0.1);

        // HF fades slowly and deeply, VHF / UHF flutters faster and shallower
        var hf = WeakSignalChannelProvider.ParametersFor(1, WeakSignalBand.Hf);
        var vhf = WeakSignalChannelProvider.ParametersFor(1, WeakSignalBand.VhfFm);
        Assert.IsTrue(hf.FadeRateHz is >= 0.05 and <= 0.5, $"HF fading {hf.FadeRateHz} Hz");
        Assert.IsTrue(vhf.FadeRateHz is >= 2 and <= 8, $"VHF flutter {vhf.FadeRateHz} Hz");
        Assert.IsLessThan(vhf.RiceKDb, hf.RiceKDb);
        Assert.IsGreaterThan(vhf.EchoGain, hf.EchoGain);

        // AM above 30 MHz: the flutter is too fast for the AGC and reaches the voice directly - it stays a shallow
        // warble (stronger steady component, higher floor than HF and than the FM fading), never a dropout
        foreach (var amount in new[] { 0.7, 1.0 })
        {
            var am = WeakSignalChannelProvider.ParametersFor(amount, WeakSignalBand.VhfAm);
            var hfAt = WeakSignalChannelProvider.ParametersFor(amount, WeakSignalBand.Hf);
            var fmAt = WeakSignalChannelProvider.ParametersFor(amount, WeakSignalBand.VhfFm);
            Assert.IsGreaterThan(hfAt.RiceKDb + 5, am.RiceKDb, $"{amount:P0}");
            Assert.IsGreaterThan(fmAt.RiceKDb, am.RiceKDb, $"{amount:P0}");
            Assert.IsGreaterThan(hfAt.FadeFloorDb + 5, am.FadeFloorDb, $"{amount:P0}");
        }
        Assert.IsGreaterThan(0.0, WeakSignalChannelProvider.ParametersFor(1, WeakSignalBand.VhfAm).AgcReleaseMs);
        Assert.AreEqual(double.PositiveInfinity, WeakSignalChannelProvider.ParametersFor(1, WeakSignalBand.VhfAm).FmThresholdDb);
        Assert.IsTrue(double.IsFinite(vhf.FmThresholdDb), "FM threshold");
    }

    [TestMethod]
    public void BandFollowsTheBandPlan()
    {
        Assert.AreEqual(WeakSignalBand.Hf, WeakSignalChannelProvider.BandFor(1_500_000, Modulation.AM), "MW");
        Assert.AreEqual(WeakSignalBand.Hf, WeakSignalChannelProvider.BandFor(7_100_000, Modulation.AM), "HF");
        Assert.AreEqual(WeakSignalBand.Hf, WeakSignalChannelProvider.BandFor(27_185_000, Modulation.AM), "CB");
        Assert.AreEqual(WeakSignalBand.Hf, WeakSignalChannelProvider.BandFor(29_999_000, Modulation.AM));
        Assert.AreEqual(WeakSignalBand.VhfFm, WeakSignalChannelProvider.BandFor(30_000_000, Modulation.FM), "VHF");
        Assert.AreEqual(WeakSignalBand.VhfFm, WeakSignalChannelProvider.BandFor(100_000_000, Modulation.FM), "FM");
        Assert.AreEqual(WeakSignalBand.VhfAm, WeakSignalChannelProvider.BandFor(124_800_000, Modulation.AM), "AIR");
        Assert.AreEqual(WeakSignalBand.VhfAm, WeakSignalChannelProvider.BandFor(251_000_000, Modulation.AM), "UHF AM");
        Assert.AreEqual(WeakSignalBand.VhfFm, WeakSignalChannelProvider.BandFor(446_193_750, Modulation.FM), "PMR");
        Assert.AreEqual(WeakSignalBand.VhfFm, WeakSignalChannelProvider.BandFor(460_000_000, Modulation.FM), "UHF FM");
        Assert.AreEqual(WeakSignalBand.Digital, WeakSignalChannelProvider.BandFor(950_000_000, Modulation.DIGITAL));
        Assert.AreEqual(WeakSignalBand.Digital, WeakSignalChannelProvider.BandFor(950_000_000, Modulation.FM), "DIG band");
        Assert.AreEqual(WeakSignalBand.Digital, WeakSignalChannelProvider.BandFor(145_000_000, Modulation.DIGITAL));

        // the modulation of the transmission decides AM / FM above 30 MHz, the band plan's if it is neither
        Assert.AreEqual(WeakSignalBand.VhfAm, WeakSignalChannelProvider.BandFor(145_000_000, Modulation.AM));
        Assert.AreEqual(WeakSignalBand.VhfFm, WeakSignalChannelProvider.BandFor(145_000_000, Modulation.DISABLED));
        Assert.AreEqual(WeakSignalBand.VhfAm, WeakSignalChannelProvider.BandFor(124_800_000, Modulation.DISABLED));

        // the microphone preview has only the radio model
        Assert.AreEqual(WeakSignalBand.Hf, WeakSignalChannelProvider.BandForModel("cb"));
        Assert.AreEqual(WeakSignalBand.Hf, WeakSignalChannelProvider.BandForModel("hf"));
        Assert.AreEqual(WeakSignalBand.Hf, WeakSignalChannelProvider.BandForModel("vintage"));
        Assert.AreEqual(WeakSignalBand.VhfFm, WeakSignalChannelProvider.BandForModel("walkie"));
        Assert.AreEqual(WeakSignalBand.VhfFm, WeakSignalChannelProvider.BandForModel("tactical"));
        Assert.AreEqual(WeakSignalBand.VhfAm, WeakSignalChannelProvider.BandForModel("airband"));
        Assert.AreEqual(WeakSignalBand.VhfFm, WeakSignalChannelProvider.BandForModel("standard"));
        Assert.AreEqual(WeakSignalBand.Digital, WeakSignalChannelProvider.BandForModel("digital"));
        Assert.AreEqual(WeakSignalBand.VhfFm, WeakSignalChannelProvider.BandForModel("my-own-radio"));
        Assert.AreEqual(WeakSignalBand.VhfFm, WeakSignalChannelProvider.BandForModel(null));
    }

    [TestMethod]
    public void AmountComesFromTheProfileSettings()
    {
        Assert.AreEqual(0.35f, WeakSignalChannelProvider.AmountFromSettings(1f, 35f), 1e-6f);
        Assert.AreEqual(1f, WeakSignalChannelProvider.AmountFromSettings(0.5f, 100f), 1e-6f);
        Assert.AreEqual(0f, WeakSignalChannelProvider.AmountFromSettings(0f, 35f), "radio effect strength 0 = off");
        Assert.AreEqual(0f, WeakSignalChannelProvider.AmountFromSettings(float.NaN, 35f));
        Assert.AreEqual(1f, WeakSignalChannelProvider.AmountFromSettings(1f, 150f));
        Assert.AreEqual(0f, WeakSignalChannelProvider.AmountFromSettings(1f, -5f));
        Assert.AreEqual(0f, WeakSignalChannelProvider.AmountFromSettings(1f, float.NaN));
    }

    [TestMethod]
    public void DefaultsAreSquelchTailOffAndThirtyFivePercentDistance()
    {
        var defaults = ProfileSettingsStore.DefaultSettingsProfileSettings;
        Assert.AreEqual("false", defaults[nameof(ProfileSettingsKeys.RadioRxSquelchTail)]);
        Assert.AreEqual("35", defaults[nameof(ProfileSettingsKeys.VoiceDistortion)]);
        Assert.AreEqual(WeakSignalChannelProvider.DefaultPercent,
            float.Parse(defaults[nameof(ProfileSettingsKeys.VoiceDistortion)], CultureInfo.InvariantCulture));
        Assert.AreEqual(WeakSignalChannelProvider.SampleRate, Constants.OUTPUT_SAMPLE_RATE,
            "the provider's constant sample rate must be the output rate");
    }

    [TestMethod]
    public void ReceivePipelineAppliesTheDistanceToTheDryShareToo()
    {
        // radio effect strength 30 % (a common setting): the wet branch is silent here, so all that is heard is the
        // dry share - the distance must reach it, or the clean dry voice masks the fading and the noise
        var tone = Tone(10);
        var silence = new float[tone.Length];

        ISampleProvider Output(WeakSignalChannelProvider weakSignal, float volume = 1f)
        {
            return ClientTransmissionPipelineProvider.BuildOutput(new ArraySource(tone), new ArraySource(silence), 0.3f,
                weakSignal, volume);
        }

        // 0 % is exactly the plain mix (also with the receiving radio's volume)
        var plain = Render(Output(null), tone.Length);
        CollectionAssert.AreEqual(plain,
            Render(Output(new WeakSignalChannelProvider(null, WeakSignalBand.VhfAm, 1, 0f)), tone.Length));
        CollectionAssert.AreEqual(Render(Output(null, 0.5f), tone.Length),
            Render(Output(new WeakSignalChannelProvider(null, WeakSignalBand.VhfAm, 1, 0f), 0.5f), tone.Length));

        // 70 %: the dry share flutters, gets noise and keeps its loudness
        var output = Render(Output(new WeakSignalChannelProvider(null, WeakSignalBand.VhfAm, 11, 0.7f)), tone.Length);
        var voice = Render(Output(new WeakSignalChannelProvider(null, WeakSignalBand.VhfAm, 11, 0.7f) { NoiseMuted = true }),
            tone.Length);

        var levels = WindowLevels(voice, Fs / 20, Fs);
        Assert.IsGreaterThan(4.0, levels.Max() - levels.Min(), "the dry share must fade");
        Assert.IsGreaterThan(-30.0, RmsDb(Difference(output, voice), Fs) - RmsDb(plain, Fs), "noise");
        Assert.AreEqual(RmsDb(plain, Fs), RmsDb(output, Fs), 2.0, "same loudness");
    }

    [TestMethod]
    public void RadioModelsStayEquallyLoudWithTheDistance()
    {
        // the chain of the receive pipeline: sender's radio model -> distance -> final clip at +-1
        var factory = RadioModelFactory.FromFolders(RepositoryFiles.RadioModelsFolder);
        var models = new[] { "standard", "cb", "walkie", "airband", "tactical", "hf", "vintage", "digital" };

        foreach (var amount in new[] { 0.35f, 1f })
        {
            var levels = new Dictionary<string, double>();
            foreach (var model in models)
            {
                // the models are calibrated for microphone level speech (about -26 dBFS)
                var clean = Render(new RadioFilter(new SpeechLike(0.25), model, false, factory), Fs * 8);
                var distant = Process(clean, WeakSignalChannelProvider.BandForModel(model), amount);
                for (var i = 0; i < distant.Length; i++) distant[i] = Math.Clamp(distant[i], -1f, 1f);

                var delta = RmsDb(distant, Fs) - RmsDb(clean, Fs);
                Assert.IsLessThan(2.0, Math.Abs(delta), $"{model} {amount:P0}: {delta:+0.0;-0.0} dB");
                Assert.AreEqual(0, distant.Count(sample => Math.Abs(sample) >= 0.999f), $"{model} {amount:P0}: clipping");

                levels[model] = RmsDb(distant, Fs);
            }

            foreach (var level in levels)
                Assert.IsLessThan(4.0, Math.Abs(level.Value - levels["standard"]),
                    $"{level.Key} {amount:P0} is {level.Value - levels["standard"]:0.0} dB louder/quieter than standard");
        }
    }

    [TestMethod]
    public void ReadingAllocatesNothing()
    {
        var input = Speech(3);
        foreach (var band in Bands)
        {
            var channel = Channel(input, band, 1f);
            var buffer = new float[Block];
            channel.Read(buffer, 0, Block); // warm up

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var position = Block; position < input.Length; position += Block) channel.Read(buffer, 0, Block);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated, $"{band}: bytes allocated on the audio path");
        }
    }
}
