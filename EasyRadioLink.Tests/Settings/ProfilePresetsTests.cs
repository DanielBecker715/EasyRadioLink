using System;
using System.IO;
using System.Linq;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Tests.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpConfig;

namespace EasyRadioLink.Common.Tests.Settings;

/// <summary>The ready-made profiles (<see cref="ProfilePresets" />) and the remembered active profile.</summary>
[TestClass]
public class ProfilePresetsTests
{
    private const string PttGuid = "6f1d2b60-d5a0-11cf-bfc7-444553540000";

    private string _directory;
    private string _previousPath;
    private string _previousProfiles;
    private string _previousPresets;
    private string _previousCurrent;

    private static GlobalSettingsStore Global => GlobalSettingsStore.Instance;

    [TestInitialize]
    public void Setup()
    {
        // never the real settings folder: every file goes to a temporary directory
        _directory = Path.Combine(Path.GetTempPath(), "erl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _previousPath = GlobalSettingsStore.Path;
        GlobalSettingsStore.Path = _directory;

        _previousProfiles = Global.GetClientSetting(GlobalSettingsKeys.SettingsProfiles).RawValue;
        _previousPresets = Global.GetClientSetting(GlobalSettingsKeys.ProfilePresetsCreated).RawValue;
        _previousCurrent = Global.GetClientSetting(GlobalSettingsKeys.CurrentProfile).RawValue;

        // a settings folder of an older version: only the default profile, with a push-to-talk binding
        Global.SetClientSetting(GlobalSettingsKeys.SettingsProfiles, new[] { "default" });
        Global.SetClientSetting(GlobalSettingsKeys.ProfilePresetsCreated, "");
        Global.SetClientSetting(GlobalSettingsKeys.CurrentProfile, "default");
        File.WriteAllText(Path.Combine(_directory, "default.cfg"),
            "[Client Settings]\nBackgroundSound=jet\nRadioEffectsRatio=1\n\n" +
            $"[Ptt]\nname=Mouse\nbutton=4\nvalue=1\nguid={PttGuid}\n");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Global.SetClientSetting(GlobalSettingsKeys.SettingsProfiles, _previousProfiles, true);
        Global.SetClientSetting(GlobalSettingsKeys.ProfilePresetsCreated, _previousPresets, true);
        Global.SetClientSetting(GlobalSettingsKeys.CurrentProfile, _previousCurrent, true);
        GlobalSettingsStore.Path = _previousPath;

        try
        {
            Directory.Delete(_directory, true);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    [TestMethod]
    public void HelicopterPresetHasTheChosenSettings()
    {
        var settings = ProfilePresets.Helicopter.Settings;
        Assert.AreEqual("Helicopter", ProfilePresets.Helicopter.Name);
        Assert.AreEqual("helicopter", settings[ProfileSettingsKeys.BackgroundSound]);
        Assert.AreEqual("0.5", settings[ProfileSettingsKeys.BackgroundSoundVolume]);
        Assert.AreEqual("0.3", settings[ProfileSettingsKeys.RadioEffectsRatio]);
        Assert.AreEqual("20", settings[ProfileSettingsKeys.VoiceDistortion]);

        // the sound exists
        Assert.IsTrue(File.Exists(Path.Combine(RepositoryFiles.AudioEffectsFolder,
            CachedAudioEffectProvider.BackgroundFolderName, "helicopter.wav")));

        // only known settings with valid values
        foreach (var (key, value) in settings)
            Assert.IsTrue(ProfileSettingsStore.DefaultSettingsProfileSettings.ContainsKey(key.ToString()), key.ToString());
    }

    [TestMethod]
    public void PresetTakesTheKeyBindingsButNotTheSettingsOfTheDefaultProfile()
    {
        var source = Configuration.LoadFromString(File.ReadAllText(Path.Combine(_directory, "default.cfg")));
        var preset = ProfilePresets.CreateProfile(ProfilePresets.Helicopter, source);

        var settings = preset[ProfileSettingsStore.ClientSettingsSection];
        Assert.AreEqual("helicopter", settings["BackgroundSound"].RawValue);
        Assert.AreEqual("0.3", settings["RadioEffectsRatio"].RawValue);
        Assert.AreEqual(ProfilePresets.Helicopter.Settings.Count, settings.SettingCount);

        Assert.AreEqual(4, preset["Ptt"]["button"].IntValue);
        Assert.AreEqual(PttGuid, preset["Ptt"]["guid"].RawValue);

        // a copy: changing the preset leaves the default profile alone
        preset["Ptt"]["button"].IntValue = 7;
        Assert.AreEqual(4, source["Ptt"]["button"].IntValue);
    }

    [TestMethod]
    public void PresetIsCreatedOnceWithPushToTalk()
    {
        var store = new ProfileSettingsStore(Global);
        Assert.IsTrue(store.ProfileNames.Contains("Helicopter"));
        Assert.IsTrue(File.Exists(Path.Combine(_directory, "Helicopter.cfg")));
        Assert.AreEqual("default", store.CurrentProfileName);

        store.CurrentProfileName = "Helicopter";
        Assert.AreEqual("helicopter", store.GetClientSettingString(ProfileSettingsKeys.BackgroundSound));
        Assert.AreEqual(0.5f, store.GetClientSettingFloat(ProfileSettingsKeys.BackgroundSoundVolume));
        Assert.AreEqual(0.3f, store.GetClientSettingFloat(ProfileSettingsKeys.RadioEffectsRatio));
        Assert.AreEqual(20f, store.GetClientSettingFloat(ProfileSettingsKeys.VoiceDistortion));
        Assert.AreEqual(0f, store.GetClientSettingFloat(ProfileSettingsKeys.VoiceBoost));
        Assert.IsTrue(store.GetCurrentInputProfile().ContainsKey(InputBinding.Ptt), "push-to-talk works at once");

        // the default profile keeps its own settings
        store.CurrentProfileName = "default";
        Assert.AreEqual("jet", store.GetClientSettingString(ProfileSettingsKeys.BackgroundSound));

        // deleted: it does not come back
        store.RemoveProfile("Helicopter");
        var again = new ProfileSettingsStore(Global);
        Assert.IsFalse(again.ProfileNames.Contains("Helicopter"));
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "Helicopter.cfg")));
    }

    [TestMethod]
    public void ExistingProfileWithThePresetNameIsKept()
    {
        Global.SetClientSetting(GlobalSettingsKeys.SettingsProfiles, new[] { "default", "helicopter" });
        File.WriteAllText(Path.Combine(_directory, "helicopter.cfg"), "[Client Settings]\nBackgroundSound=prop\n");

        var store = new ProfileSettingsStore(Global);
        CollectionAssert.AreEquivalent(new[] { "default", "helicopter" }, store.ProfileNames);

        store.CurrentProfileName = "helicopter";
        Assert.AreEqual("prop", store.GetClientSettingString(ProfileSettingsKeys.BackgroundSound));
    }

    [TestMethod]
    public void ActiveProfileIsRemembered()
    {
        var store = new ProfileSettingsStore(Global);
        store.CurrentProfileName = "Helicopter";

        Assert.AreEqual("Helicopter", new ProfileSettingsStore(Global).CurrentProfileName);

        // a remembered profile that no longer exists: back to default
        Global.SetClientSetting(GlobalSettingsKeys.CurrentProfile, "gone");
        Assert.AreEqual("default", new ProfileSettingsStore(Global).CurrentProfileName);
    }
}
