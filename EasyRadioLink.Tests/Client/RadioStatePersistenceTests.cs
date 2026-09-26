using System;
using System.IO;
using System.Text.Json;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Client;

/// <summary>radio-state.json: frequency, volume and step of the one radio; migration of the six-radio file of 1.0.</summary>
[TestClass]
public class RadioStatePersistenceTests
{
    // radio-state.json as EasyRadioLink 1.0 wrote it: newest entries first, one per radio (and per layout)
    private const string Version1File = """
        {
          "version": 1,
          "selected": 2,
          "simultaneousTransmission": false,
          "radios": [
            {
              "slot": 1,
              "name": "CB",
              "modulation": 0,
              "freq": 27185000,
              "model": "cb",
              "guardEnabled": true,
              "enc": false,
              "encKey": 1,
              "volume": 1,
              "channel": -1,
              "simul": false,
              "savedUtc": "2026-08-30T18:12:45.1234567Z"
            },
            {
              "slot": 2,
              "name": "PMR446",
              "modulation": 1,
              "freq": 446006250,
              "model": "walkie",
              "guardEnabled": true,
              "enc": true,
              "encKey": 17,
              "volume": 0.55,
              "channel": 1,
              "simul": true,
              "savedUtc": "2026-08-30T18:12:45.1234567Z"
            },
            {
              "slot": 2,
              "name": "Airband",
              "modulation": 0,
              "freq": 121500000,
              "model": "airband",
              "guardEnabled": true,
              "enc": false,
              "encKey": 1,
              "volume": 0.8,
              "channel": -1,
              "simul": false,
              "savedUtc": "2026-07-01T10:00:00Z"
            }
          ]
        }
        """;

    private string _directory;
    private string _previousConfigDirectory;

    private string StateFile => Path.Combine(_directory, RadioStatePersistence.FileName);

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "erl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        // radio-state.json lives in the configuration directory
        _previousConfigDirectory = GlobalSettingsStore.Path;
        GlobalSettingsStore.Path = _directory;
    }

    [TestCleanup]
    public void Cleanup()
    {
        GlobalSettingsStore.Path = _previousConfigDirectory;

        try
        {
            Directory.Delete(_directory, true);
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private static void AssertState(RadioState state, double frequency, float volume, int step)
    {
        Assert.AreEqual(frequency, state.Frequency, "frequency");
        Assert.AreEqual(volume, state.Volume, 1e-6f, "volume");
        Assert.AreEqual(step, state.Step, "step");
    }

    private JsonElement ReadSavedFile()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(StateFile));
        return document.RootElement.Clone();
    }

    [TestMethod]
    public void UsesTheConfigurationDirectory()
    {
        Assert.AreEqual(StateFile, RadioStatePersistence.FilePath);
    }

    [TestMethod]
    public void WithoutAFileTheDefaultsAreUsed()
    {
        AssertState(RadioStatePersistence.Load(), BandPlan.DefaultFrequency, 1.0f, TuningSteps.Default);
        Assert.AreEqual(27_185_000d, RadioState.Default.Frequency);
        Assert.IsFalse(File.Exists(StateFile), "loading never writes the file");
    }

    [TestMethod]
    public void TuningAndStepAreRemembered()
    {
        RadioStatePersistence.SaveTuning(446_193_750, 0.25f);
        RadioStatePersistence.SaveStep(100_000);

        AssertState(RadioStatePersistence.Load(), 446_193_750, 0.25f, 100_000);

        // saving the tuning keeps the step and the other way round
        RadioStatePersistence.SaveTuning(121_500_000, 0.75f);
        AssertState(RadioStatePersistence.Load(), 121_500_000, 0.75f, 100_000);

        RadioStatePersistence.SaveStep(1_000_000);
        AssertState(RadioStatePersistence.Load(), 121_500_000, 0.75f, 1_000_000);

        var saved = ReadSavedFile();
        Assert.AreEqual(2, saved.GetProperty("version").GetInt32());
        Assert.AreEqual(121_500_000d, saved.GetProperty("frequency").GetDouble());
        Assert.IsFalse(File.Exists(StateFile + ".tmp"));
    }

    [TestMethod]
    public void Version1ContinuesWithTheSelectedRadio()
    {
        File.WriteAllText(StateFile, Version1File);

        // the newest entry of the selected slot (2): PMR446 channel 1 at 55 %
        AssertState(RadioStatePersistence.Load(), 446_006_250, 0.55f, TuningSteps.Default);

        // the radio gets modulation and sound from the band plan
        var state = RadioStatePersistence.Load();
        var radio = Radio.Create(state.Frequency, state.Volume);
        Assert.AreEqual(Modulation.FM, radio.modulation);
        Assert.AreEqual("walkie", radio.model);
        Assert.AreEqual("PMR", radio.Band.Label);

        // radio 1 selected: CB channel 19
        File.WriteAllText(StateFile, Version1File.Replace("\"selected\": 2", "\"selected\": 1"));
        AssertState(RadioStatePersistence.Load(), 27_185_000, 1.0f, TuningSteps.Default);
    }

    [TestMethod]
    public void Version1IsRewrittenAsVersion2OnTheNextSave()
    {
        File.WriteAllText(StateFile, Version1File);

        RadioStatePersistence.SaveStep(10_000);

        var saved = ReadSavedFile();
        Assert.AreEqual(2, saved.GetProperty("version").GetInt32());
        Assert.AreEqual(446_006_250d, saved.GetProperty("frequency").GetDouble());
        Assert.AreEqual(0.55f, saved.GetProperty("volume").GetSingle(), 1e-6f);
        Assert.AreEqual(10_000, saved.GetProperty("step").GetInt32());

        // the six radios of 1.0 are gone
        Assert.IsFalse(saved.TryGetProperty("radios", out _));
        Assert.IsFalse(saved.TryGetProperty("selected", out _));

        AssertState(RadioStatePersistence.Load(), 446_006_250, 0.55f, 10_000);
    }

    [TestMethod]
    public void Version1WithoutTheSelectedRadioUsesTheFirstEntry()
    {
        File.WriteAllText(StateFile, """
            {"version":1,"selected":5,"radios":[
              {"slot":1,"name":"CB","modulation":0,"freq":27205000,"model":"cb","volume":0.9},
              {"slot":2,"name":"PMR","modulation":1,"freq":446006250,"model":"walkie","volume":1}
            ]}
            """);

        AssertState(RadioStatePersistence.Load(), 27_205_000, 0.9f, TuningSteps.Default);
    }

    [TestMethod]
    public void Version1WithoutRadiosUsesTheDefaults()
    {
        File.WriteAllText(StateFile, """{"version":1,"selected":1,"simultaneousTransmission":false,"radios":[]}""");

        AssertState(RadioStatePersistence.Load(), BandPlan.DefaultFrequency, 1.0f, TuningSteps.Default);
    }

    [TestMethod]
    public void Version1OutsideTheTuningRangeIsClamped()
    {
        // 1.0 radios could go up to 3 GHz
        File.WriteAllText(StateFile, """
            {"version":1,"selected":1,"radios":[
              {"slot":1,"name":"SAT","modulation":1,"freq":2400000000,"model":"standard","volume":1}
            ]}
            """);

        AssertState(RadioStatePersistence.Load(), BandPlan.MaxFrequency, 1.0f, TuningSteps.Default);
    }

    [TestMethod]
    public void InvalidValuesAreReplacedOrClamped()
    {
        File.WriteAllText(StateFile, """{"version":2,"frequency":5000000000,"volume":7,"step":1234}""");
        AssertState(RadioStatePersistence.Load(), BandPlan.MaxFrequency, 1.0f, TuningSteps.Default);

        File.WriteAllText(StateFile, """{"version":2,"frequency":-27185000,"volume":-0.5,"step":0}""");
        AssertState(RadioStatePersistence.Load(), BandPlan.DefaultFrequency, 0f, TuningSteps.Default);

        File.WriteAllText(StateFile, """{"version":2,"frequency":500000,"volume":0.5,"step":10000000}""");
        AssertState(RadioStatePersistence.Load(), BandPlan.MinFrequency, 0.5f, 10_000_000);

        // missing values
        File.WriteAllText(StateFile, """{"version":2}""");
        AssertState(RadioStatePersistence.Load(), BandPlan.DefaultFrequency, 1.0f, TuningSteps.Default);

        // whole Hz
        File.WriteAllText(StateFile, """{"version":2,"frequency":27185000.4,"volume":1,"step":1000}""");
        AssertState(RadioStatePersistence.Load(), 27_185_000, 1.0f, TuningSteps.Default);
    }

    [TestMethod]
    public void TheFrequencyIsNormalised()
    {
        // next to a modulation edge the remembered frequency is rounded to the whole kHz
        File.WriteAllText(StateFile, """{"version":2,"frequency":107999600,"volume":1,"step":1000}""");
        AssertState(RadioStatePersistence.Load(), 108_000_000, 1.0f, TuningSteps.Default);

        RadioStatePersistence.SaveTuning(29_999_400, 1.0f);
        AssertState(RadioStatePersistence.Load(), 29_999_000, 1.0f, TuningSteps.Default);
    }

    [TestMethod]
    public void AnUnreadableFileIsIgnored()
    {
        File.WriteAllText(StateFile, "{ this is not json");
        AssertState(RadioStatePersistence.Load(), BandPlan.DefaultFrequency, 1.0f, TuningSteps.Default);

        File.WriteAllText(StateFile, """{"version":2,"frequency":"loud"}""");
        AssertState(RadioStatePersistence.Load(), BandPlan.DefaultFrequency, 1.0f, TuningSteps.Default);

        // and replaced by the next save
        RadioStatePersistence.SaveStep(10_000);
        AssertState(RadioStatePersistence.Load(), BandPlan.DefaultFrequency, 1.0f, 10_000);
    }
}
