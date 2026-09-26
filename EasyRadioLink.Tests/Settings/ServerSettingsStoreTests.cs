using System;
using System.IO;
using System.Linq;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Settings;

[TestClass]
public class ServerSettingsStoreTests
{
    private string _directory;

    private string ConfigFile => Path.Combine(_directory, "server.cfg");

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "erl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (Exception)
        {
            // ignored
        }
    }

    [TestMethod]
    public void EmptyPasswordMeansOpenServer()
    {
        Assert.IsTrue(ServerSettingsStore.PasswordMatches("", null));
        Assert.IsTrue(ServerSettingsStore.PasswordMatches("", ""));
        Assert.IsTrue(ServerSettingsStore.PasswordMatches(null, "anything"));
        Assert.IsTrue(ServerSettingsStore.PasswordMatches("   ", "anything"));

        var store = new ServerSettingsStore(ConfigFile);
        Assert.IsFalse(store.IsPasswordProtected);
        Assert.IsTrue(store.CheckPassword(null));
        Assert.IsTrue(store.CheckPassword("whatever"));
    }

    [TestMethod]
    public void WrongPasswordIsRejected()
    {
        Assert.IsTrue(ServerSettingsStore.PasswordMatches("secret", "secret"));
        Assert.IsTrue(ServerSettingsStore.PasswordMatches("secret", " secret "));
        Assert.IsFalse(ServerSettingsStore.PasswordMatches("secret", "Secret"));
        Assert.IsFalse(ServerSettingsStore.PasswordMatches("secret", "secret2"));
        Assert.IsFalse(ServerSettingsStore.PasswordMatches("secret", ""));
        Assert.IsFalse(ServerSettingsStore.PasswordMatches("secret", null));

        var store = new ServerSettingsStore(ConfigFile);
        store.SetServerPassword("secret");

        Assert.IsTrue(store.IsPasswordProtected);
        Assert.IsTrue(store.CheckPassword("secret"));
        Assert.IsFalse(store.CheckPassword("wrong"));
        Assert.IsFalse(store.CheckPassword(null));

        store.SetServerPassword("");
        Assert.IsFalse(store.IsPasswordProtected);
        Assert.IsTrue(store.CheckPassword(null));
    }

    [TestMethod]
    public void PasswordWithSpecialCharactersSurvivesReload()
    {
        const string password = "p#ss;w=rd \"quoted\" [x] ümlaut";

        var store = new ServerSettingsStore(ConfigFile);
        store.SetServerPassword(password);

        var reloaded = new ServerSettingsStore(ConfigFile);
        Assert.AreEqual(password, reloaded.GetServerPassword());
        Assert.IsTrue(reloaded.CheckPassword(password));
    }

    [TestMethod]
    public void PasswordIsStoredInServerSectionAndNeverBroadcast()
    {
        var store = new ServerSettingsStore(ConfigFile);
        store.SetServerPassword("topsecret");

        var content = File.ReadAllText(ConfigFile);
        var serverSection = content.IndexOf("[" + ServerSettingsStore.SERVER_SECTION + "]", StringComparison.Ordinal);
        var passwordLine = content.IndexOf("SERVER_PASSWORD", StringComparison.Ordinal);
        Assert.IsTrue(serverSection >= 0, "[Server Settings] section missing");
        Assert.IsTrue(passwordLine > serverSection, "SERVER_PASSWORD must be in [Server Settings]");

        var broadcast = store.ToDictionary();
        Assert.IsFalse(broadcast.ContainsKey(ServerSettingsKeys.SERVER_PASSWORD.ToString()));
        Assert.IsFalse(broadcast.ContainsKey(ServerSettingsKeys.HTTP_SERVER_API_KEY.ToString()));
        Assert.IsFalse(broadcast.Values.Any(value => value != null && value.Contains("topsecret")));
    }

    [TestMethod]
    public void PrivateSettingsAreNeverBroadcastEvenIfMovedToGeneralSection()
    {
        File.WriteAllText(ConfigFile,
            "[General Settings]\nSERVER_PASSWORD = leaked\nHTTP_SERVER_API_KEY = key123\nSHOW_TUNED_COUNT = false\n" +
            "[Server Settings]\nSERVER_PORT = 5010\n");

        var store = new ServerSettingsStore(ConfigFile);
        var broadcast = store.ToDictionary();

        foreach (var key in DefaultServerSettings.PrivateKeys)
            Assert.IsFalse(broadcast.ContainsKey(key.ToString()), key.ToString());

        Assert.AreEqual("false", broadcast[ServerSettingsKeys.SHOW_TUNED_COUNT.ToString()]);
    }

    [TestMethod]
    public void BroadcastContainsAllClientSettingsWithDefaults()
    {
        var store = new ServerSettingsStore(ConfigFile);
        var broadcast = store.ToDictionary();

        foreach (var key in DefaultServerSettings.BroadcastKeys)
            Assert.IsTrue(broadcast.ContainsKey(key.ToString()), key.ToString());

        Assert.AreEqual("27.405,446.19375", broadcast[ServerSettingsKeys.TEST_FREQUENCIES.ToString()]);
        Assert.AreEqual("", broadcast[ServerSettingsKeys.CLEAN_FREQUENCIES.ToString()]);
        Assert.AreEqual("[]", broadcast[ServerSettingsKeys.SERVER_RADIO_PRESET.ToString()]);
        Assert.IsTrue(broadcast.ContainsKey(ServerSettingsKeys.SERVER_PRESETS.ToString()));
        Assert.AreEqual(5010, store.GetServerPort());
    }

    [TestMethod]
    public void SettingsDumpMasksSecrets()
    {
        var store = new ServerSettingsStore(ConfigFile);
        store.SetServerPassword("topsecret");
        store.SetServerSetting(ServerSettingsKeys.HTTP_SERVER_API_KEY, "apikey123");

        var dump = store.GetAllSettings();

        Assert.IsFalse(dump.Any(line => line.Contains("topsecret")));
        Assert.IsFalse(dump.Any(line => line.Contains("apikey123")));
        Assert.Contains("SERVER_PASSWORD = " + ServerSettingsStore.MASKED_VALUE, dump);
        Assert.Contains("HTTP_SERVER_API_KEY = " + ServerSettingsStore.MASKED_VALUE, dump);

        store.SetServerPassword("");
        Assert.Contains("SERVER_PASSWORD = ", store.GetAllSettings());
    }

    [TestMethod]
    public void ServerRadioLayoutIsLoadedValidatedAndBroadcast()
    {
        File.WriteAllText(Path.Combine(_directory, ServerSettingsStore.SERVER_RADIOS_FILE),
            """
            // user radios only - the reserved slot 0 is added automatically
            [
              { "name": "CB", "model": "cb", "modulation": 0, "freq": 27185000, "freqMin": 26965000, "freqMax": 27405000 },
              { "Name": "PMR", "Modulation": 1, "Freq": 446006250, "FreqMin": 446006250, "FreqMax": 446193750, },
            ]
            """);

        var store = new ServerSettingsStore(ConfigFile);
        store.SetGeneralSetting(ServerSettingsKeys.SERVER_RADIO_PRESET_ENABLED, true);

        var json = store.ToDictionary()[ServerSettingsKeys.SERVER_RADIO_PRESET.ToString()];
        var radios = RadioDefinition.ParseList(json);

        Assert.HasCount(Constants.MAX_RADIOS, radios);
        Assert.AreEqual(Modulation.DISABLED, radios[0].modulation);
        Assert.AreEqual("CB", radios[1].name);
        Assert.AreEqual("PMR", radios[2].name);
        Assert.AreEqual(Modulation.FM, radios[2].modulation);

        store.SetGeneralSetting(ServerSettingsKeys.SERVER_RADIO_PRESET_ENABLED, false);
        Assert.AreEqual("[]", store.ToDictionary()[ServerSettingsKeys.SERVER_RADIO_PRESET.ToString()]);
    }

    [TestMethod]
    public void UnreadableValuesFallBackToDefaults()
    {
        File.WriteAllText(ConfigFile,
            "[General Settings]\nSHOW_TUNED_COUNT = maybe\nTRANSMISSION_LOG_RETENTION = two\n" +
            "[Server Settings]\nSERVER_PORT = 5010x\nHTTP_SERVER_PORT = 70000\nUPNP_ENABLED = false\n");

        var store = new ServerSettingsStore(ConfigFile);

        Assert.AreEqual(5010, store.GetServerPort());
        Assert.AreEqual(8080, store.GetServerSetting(ServerSettingsKeys.HTTP_SERVER_PORT).IntValue);
        Assert.IsTrue(store.GetGeneralSetting(ServerSettingsKeys.SHOW_TUNED_COUNT).BoolValue);
        Assert.AreEqual(2, store.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION).IntValue);

        // valid values are kept
        Assert.IsFalse(store.GetServerSetting(ServerSettingsKeys.UPNP_ENABLED).BoolValue);
    }

    [TestMethod]
    public void OutOfRangePortSetAtRuntimeFallsBackToDefault()
    {
        var store = new ServerSettingsStore(ConfigFile);
        store.SetServerSetting(ServerSettingsKeys.SERVER_PORT, "70000");

        Assert.AreEqual(5010, store.GetServerPort());

        store.SetServerSetting(ServerSettingsKeys.SERVER_PORT, "6000");
        Assert.AreEqual(6000, store.GetServerPort());
    }

    [TestMethod]
    public void PortValidation()
    {
        Assert.IsTrue(ServerSettingsStore.IsValidPort(1));
        Assert.IsTrue(ServerSettingsStore.IsValidPort(5010));
        Assert.IsTrue(ServerSettingsStore.IsValidPort(65535));
        Assert.IsFalse(ServerSettingsStore.IsValidPort(0));
        Assert.IsFalse(ServerSettingsStore.IsValidPort(-1));
        Assert.IsFalse(ServerSettingsStore.IsValidPort(65536));
        Assert.IsFalse(ServerSettingsStore.IsValidPort(70000));
    }

    [TestMethod]
    public void DefaultConfigFileIsNextToTheExecutable()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "server.cfg");

        Assert.AreEqual(expected, ServerSettingsStore.ResolveConfigFilePath(null));
        Assert.AreEqual(expected, ServerSettingsStore.ResolveConfigFilePath("  "));
        Assert.AreEqual(expected, ServerSettingsStore.ResolveConfigFilePath("\"\""));
    }

    [TestMethod]
    public void ConfigFilePathIsResolvedOnce()
    {
        // relative: against the current directory at start-up
        Assert.AreEqual(Path.GetFullPath("my-server.cfg"), ServerSettingsStore.ResolveConfigFilePath("my-server.cfg"));

        // absolute (quotes from a shortcut are removed)
        Assert.AreEqual(ConfigFile, ServerSettingsStore.ResolveConfigFilePath("\"" + ConfigFile + "\""));
    }

    [TestMethod]
    public void ServerFilesLiveNextToTheConfigFile()
    {
        var store = new ServerSettingsStore(ConfigFile);

        Assert.AreEqual(ConfigFile, store.ConfigFilePath);
        Assert.AreEqual(_directory, store.ConfigDirectory);
        Assert.AreEqual(Path.Combine(_directory, ServerSettingsStore.SERVER_RADIOS_FILE), store.ServerRadiosFilePath);
        Assert.AreEqual(Path.Combine(_directory, "banned.txt"), store.GetDataFilePath("banned.txt"));
        Assert.IsNull(store.LastSaveError);
    }

    [TestMethod]
    public void ServerPresetsAreReadFromTheConfigFolder()
    {
        var presets = Path.Combine(_directory, "Presets");
        Directory.CreateDirectory(presets);
        File.WriteAllLines(Path.Combine(presets, "CB Radio.txt"), new[] { "Channel 19|27.185", "Channel 9|27,065" });

        var store = new ServerSettingsStore(ConfigFile);
        store.SetGeneralSetting(ServerSettingsKeys.SERVER_PRESETS_ENABLED, true);

        var json = store.ToDictionary()[ServerSettingsKeys.SERVER_PRESETS.ToString()];

        StringAssert.Contains(json, "cbradio");
        StringAssert.Contains(json, "Channel 19");
        StringAssert.Contains(json, "27.065");
    }

    [TestMethod]
    public void CorruptConfigIsBackedUpAndReset()
    {
        File.WriteAllText(ConfigFile, "[General Settings\nthis is = not [valid");

        var store = new ServerSettingsStore(ConfigFile);

        Assert.AreEqual(5010, store.GetServerPort());
        Assert.IsTrue(File.Exists(ConfigFile + ".bak"));
    }
}
