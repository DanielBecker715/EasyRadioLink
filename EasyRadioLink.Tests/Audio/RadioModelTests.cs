using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;

namespace EasyRadioLink.Common.Tests.Audio;

[TestClass]
public class RadioModelTests
{
    private static readonly string[] CuratedModels =
        { "standard", "cb", "walkie", "airband", "tactical", "hf", "vintage", "digital" };

    private string _tempFolder;

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempFolder != null && Directory.Exists(_tempFolder)) Directory.Delete(_tempFolder, true);
    }

    private string TempFolder()
    {
        _tempFolder ??= Path.Combine(Path.GetTempPath(), "erl-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);
        return _tempFolder;
    }

    private static RadioModelFactory ShippedModels()
    {
        return RadioModelFactory.FromFolders(RepositoryFiles.RadioModelsFolder);
    }

    /// <summary>Deterministic speech-band test signal (48 kHz mono float).</summary>
    private sealed class TestSignal : ISampleProvider
    {
        private long _n;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Constants.OUTPUT_SAMPLE_RATE, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++, _n++)
            {
                double value = 0;
                for (var k = 1; k <= 25; k++) value += Math.Sin(2 * Math.PI * 140 * k * _n / 48000.0 + k) / k;
                buffer[offset + i] = (float)(value * 0.08 * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 4 * _n / 48000.0)));
            }

            return count;
        }
    }

    private static float[] Render(ISampleProvider provider, int blocks = 25)
    {
        var output = new List<float>();
        var buffer = new float[Constants.OUTPUT_SEGMENT_FRAMES];
        for (var i = 0; i < blocks; i++)
        {
            var read = provider.Read(buffer, 0, buffer.Length);
            output.AddRange(buffer.Take(read));
        }

        return output.ToArray();
    }

    [TestMethod]
    public void EveryShippedModelFileLoads()
    {
        var files = Directory.GetFiles(RepositoryFiles.RadioModelsFolder, "*.json");
        Assert.IsNotEmpty(files);

        var factory = ShippedModels();

        Assert.IsEmpty(factory.LoadErrors, string.Join(Environment.NewLine, factory.LoadErrors));
        foreach (var file in files)
            Assert.IsTrue(factory.HasModel(Path.GetFileNameWithoutExtension(file)), file);

        // model files must be top level - subfolders are never loaded
        Assert.IsEmpty(Directory.GetDirectories(RepositoryFiles.RadioModelsFolder));
    }

    [TestMethod]
    public void ShippedFileNamesAreValidModelKeys()
    {
        foreach (var file in Directory.GetFiles(RepositoryFiles.RadioModelsFolder, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            Assert.AreEqual(name, RadioModelFactory.NormaliseModelKey(name),
                $"{name}: file names must be lower case letters/digits so RadioBase.Model can select them");
        }
    }

    [TestMethod]
    public void CuratedModelsAreAvailableAndHfNoiseIsInternal()
    {
        var factory = ShippedModels();
        var keys = factory.AvailableModels.Select(model => model.Key).ToList();

        CollectionAssert.AreEquivalent(CuratedModels, keys);
        Assert.AreEqual(RadioModelFactory.DefaultModelKey, keys[0], "standard is listed first");

        Assert.IsTrue(factory.HasModel(RadioModelFactory.HfNoiseModelKey));
        Assert.DoesNotContain(RadioModelFactory.HfNoiseModelKey, keys);

        foreach (var model in factory.AvailableModels)
        {
            Assert.AreNotEqual(model.Key, model.DisplayName, $"{model.Key} needs a displayName");
            Assert.IsFalse(string.IsNullOrWhiteSpace(model.Description), $"{model.Key} needs a description");
            Assert.IsFalse(model.IsCustom);
            Assert.AreEqual(model.DisplayName, model.ToString());
        }
    }

    [TestMethod]
    public void EveryModelProducesFiniteAudio()
    {
        var factory = ShippedModels();
        foreach (var key in CuratedModels.Append(RadioModelFactory.HfNoiseModelKey))
        foreach (var encrypted in new[] { false, true })
        {
            var filter = new RadioFilter(new TestSignal(), key, encrypted, factory);
            Assert.AreEqual(key, filter.ModelKey);

            var output = Render(filter);
            Assert.IsTrue(output.All(float.IsFinite), $"{key} (encrypted {encrypted}) produced NaN/Infinity");

            var rms = Math.Sqrt(output.Skip(output.Length / 2).Average(sample => (double)sample * sample));
            Assert.IsGreaterThan(0.001, rms, $"{key} (encrypted {encrypted}) is silent");
        }
    }

    [TestMethod]
    public void ModelsAreRoughlyEquallyLoud()
    {
        // switching the Sound of a radio must not make the other side 20 dB quieter
        var factory = ShippedModels();
        var levels = new Dictionary<string, double>();
        foreach (var key in CuratedModels)
        {
            var output = Render(new RadioFilter(new TestSignal(), key, false, factory), 50);
            var rms = Math.Sqrt(output.Skip(output.Length / 2).Average(sample => (double)sample * sample));
            levels[key] = 20 * Math.Log10(rms);
        }

        var standard = levels[RadioModelFactory.DefaultModelKey];
        foreach (var level in levels)
            Assert.IsLessThan(4.0, Math.Abs(level.Value - standard),
                $"{level.Key} is {level.Value - standard:0.0} dB louder/quieter than standard");
    }

    [TestMethod]
    public void LoudSpeechDoesNotClip()
    {
        // a loud talker (about 3x the normal test level) must not be turned into hard-clipped crackle
        var factory = ShippedModels();
        foreach (var key in CuratedModels)
        {
            var loud = new NAudio.Wave.SampleProviders.VolumeSampleProvider(new TestSignal()) { Volume = 3f };
            var output = Render(new RadioFilter(loud, key, false, factory), 50).Skip(Constants.OUTPUT_SEGMENT_FRAMES * 25).ToArray();
            var clipped = 100.0 * output.Count(sample => Math.Abs(sample) >= 0.999f) / output.Length;
            Assert.IsLessThan(10.0, clipped, $"{key} clips {clipped:0.0}% of the samples");
        }
    }

    [TestMethod]
    public void CompressorCompressesAboveTheThreshold()
    {
        // ratio 4: 10 dB more input above the threshold may only give 2.5 dB more output (it used to expand)
        static double OutputDb(double inputDb)
        {
            var compressor = new EasyRadioLink.Common.Audio.Dsp.SidechainCompressor(1, 50, 48000)
                { Threshold = -40, Ratio = 4, MakeUpGain = 0 };
            var amplitude = Math.Pow(10, inputDb / 20);
            double sum = 0;
            for (var n = 0; n < 48000; n++)
            {
                var x = amplitude * Math.Sin(2 * Math.PI * 1000 * n / 48000.0);
                var y = compressor.Process(x, x);
                if (n >= 24000) sum += y * y;
            }

            return 10 * Math.Log10(sum / 24000) + 3.01; // RMS -> peak dB of a sine
        }

        var quiet = OutputDb(-20);
        var loud = OutputDb(-10);
        Assert.IsLessThan(-20.0, quiet, "no gain above the threshold");
        Assert.IsLessThan(4.0, loud - quiet, $"10 dB more input gave {loud - quiet:0.0} dB more output");
        Assert.IsGreaterThan(1.0, loud - quiet);
    }

    [TestMethod]
    public void CodeDefaultsMatchTheShippedFiles()
    {
        // an empty folder only has the code-built "standard" and "digital" models
        var codeDefaults = RadioModelFactory.FromFolders(TempFolder());
        var shipped = ShippedModels();

        CollectionAssert.AreEquivalent(new[] { "standard", "digital" },
            codeDefaults.AvailableModels.Select(model => model.Key).ToList());

        foreach (var key in new[] { RadioModelFactory.DefaultModelKey, RadioModelFactory.DigitalModelKey })
        {
            var fromCode = Render(new RadioFilter(new TestSignal(), key, false, codeDefaults));
            var fromFile = Render(new RadioFilter(new TestSignal(), key, false, shipped));

            CollectionAssert.AreEqual(fromCode, fromFile, $"{key}.json must stay identical to DefaultRadioModels");
        }
    }

    [TestMethod]
    public void UnknownModelsFallBackToStandard()
    {
        var factory = ShippedModels();

        Assert.AreEqual("standard", factory.ResolveModelKey(null));
        Assert.AreEqual("standard", factory.ResolveModelKey(""));
        Assert.AreEqual("standard", factory.ResolveModelKey("arc210"));
        Assert.AreEqual("cb", factory.ResolveModelKey(" CB "));
        Assert.AreEqual("digital", factory.ResolveModelKey("unknown", RadioModelFactory.DigitalModelKey));
        Assert.IsNull(factory.GetModelInfo("unknown"));

        Assert.AreEqual("standard", new RadioFilter(new TestSignal(), "no-such-model", false, factory).ModelKey);
    }

    [TestMethod]
    public void ModelKeysAreNormalisedLikeRadioBaseModel()
    {
        foreach (var name in new[] { "cb", "CB", " Cb-Radio 2! ", "Äbc", "walkie_talkie", new string('x', 40), "" })
        {
            var radio = new RadioBase { Model = name };
            Assert.AreEqual(radio.Model, RadioModelFactory.NormaliseModelKey(name), name);
        }

        Assert.AreEqual("", RadioModelFactory.NormaliseModelKey(null));
    }

    [TestMethod]
    public void BrokenFilesAreReportedAndSkipped()
    {
        var folder = TempFolder();
        File.Copy(Path.Combine(RepositoryFiles.RadioModelsFolder, "cb.json"), Path.Combine(folder, "good.json"));
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{ \"version\": 1, ");
        File.WriteAllText(Path.Combine(folder, "missingrx.json"),
            "{ \"version\": 1, \"noiseGain\": -20, \"txEffect\": { \"$type\": \"gain\", \"gain\": 1 } }");
        File.WriteAllText(Path.Combine(folder, "badfilter.json"),
            "{ \"version\": 1, \"noiseGain\": -20, \"txEffect\": { \"$type\": \"gain\", \"gain\": 1 }, " +
            "\"rxEffect\": { \"$type\": \"filters\", \"filters\": [ { \"$type\": \"lowpass\", \"frequency\": 3000, \"slope\": 2 } ] } }");

        var factory = RadioModelFactory.FromFolders(folder);

        Assert.HasCount(3, factory.LoadErrors);
        Assert.IsTrue(factory.HasModel("good"));
        Assert.IsFalse(factory.HasModel("broken"));
        Assert.IsFalse(factory.HasModel("missingrx"));
        Assert.IsFalse(factory.HasModel("badfilter"));
    }

    [TestMethod]
    public void ReaderIsTolerant()
    {
        // "$type" after other properties, different casing, comments and trailing commas
        var folder = TempFolder();
        File.WriteAllText(Path.Combine(folder, "tolerant.json"), """
            {
              // a comment
              "Version": 1,
              "NoiseGain": -20,
              "txEffect": { "gain": 3, "$type": "gain", },
              "rxEffect": { "filters": [ { "frequency": 300, "$type": "highpass" } ], "$type": "filters" },
            }
            """);

        var factory = RadioModelFactory.FromFolders(folder);

        Assert.IsEmpty(factory.LoadErrors, string.Join(Environment.NewLine, factory.LoadErrors));
        Assert.AreEqual("tolerant", factory.GetModelInfo("tolerant").DisplayName, "display name falls back to the key");
    }

    [TestMethod]
    public void LaterFoldersOverrideEarlierOnes()
    {
        var folder = TempFolder();
        var custom = File.ReadAllText(Path.Combine(RepositoryFiles.RadioModelsFolder, "cb.json"))
            .Replace("\"displayName\": \"CB Radio\"", "\"displayName\": \"My CB\"");
        File.WriteAllText(Path.Combine(folder, "cb.json"), custom);

        var factory = RadioModelFactory.FromFolders(RepositoryFiles.RadioModelsFolder, folder);

        Assert.AreEqual("My CB", factory.GetModelInfo("cb").DisplayName);
        Assert.HasCount(CuratedModels.Length, factory.AvailableModels);
    }

    [TestMethod]
    public void MissingFoldersStillProvideTheDefaults()
    {
        var factory = RadioModelFactory.FromFolders(Path.Combine(TempFolder(), "does-not-exist"));

        Assert.IsEmpty(factory.LoadErrors);
        Assert.IsTrue(factory.HasModel(RadioModelFactory.DefaultModelKey));
        Assert.IsTrue(factory.HasModel(RadioModelFactory.DigitalModelKey));
        Assert.IsNotNull(factory.LoadTxOrDefaultRadio("anything"));
        Assert.IsNotNull(factory.LoadTxOrDefaultDigital("anything"));
        Assert.IsNotNull(factory.LoadRxOrDefault(null));
    }
}
