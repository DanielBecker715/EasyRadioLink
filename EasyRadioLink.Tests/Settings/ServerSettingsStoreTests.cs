using System;
using System.IO;
using System.Linq;
using EasyRadioLink.Common.Network.Singletons;
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
        Assert.AreEqual(5010, store.GetServerPort());
    }

    [TestMethod]
    public void OnlyTheSettingsOfTheSingleRadioServerExist()
    {
        // 1.1: one radio per user - no encryption, server channel preset or server radio layout settings
        CollectionAssert.AreEquivalent(new[]
        {
            "SERVER_PORT", "SERVER_IP", "UPNP_ENABLED", "SERVER_PASSWORD",
            "IRL_RADIO_TX", "IRL_RADIO_RX_INTERFERENCE", "TEST_FREQUENCIES", "CLEAN_FREQUENCIES",
            "SHOW_TUNED_COUNT", "SHOW_TRANSMITTER_NAME",
            "CLIENT_EXPORT_ENABLED", "CLIENT_EXPORT_FILE_PATH", "TRANSMISSION_LOG_ENABLED", "TRANSMISSION_LOG_RETENTION",
            "HTTP_SERVER_ENABLED", "HTTP_SERVER_PORT", "HTTP_SERVER_API_KEY", "HTTP_SERVER_ADDRESS"
        }, Enum.GetNames<ServerSettingsKeys>());

        // every setting is either sent to the clients or kept on the server - exactly one of the two
        CollectionAssert.AreEquivalent(Enum.GetValues<ServerSettingsKeys>(),
            DefaultServerSettings.BroadcastKeys.Concat(DefaultServerSettings.PrivateKeys).ToArray());

        foreach (var key in Enum.GetValues<ServerSettingsKeys>())
            Assert.IsTrue(DefaultServerSettings.Defaults.ContainsKey(key.ToString()), $"{key} has no default");
    }

    [TestMethod]
    public void SettingsOfOlderVersionsAreIgnored()
    {
        // an older server.cfg may contain settings of features that no longer exist
        File.WriteAllText(ConfigFile,
            "[General Settings]\nOLD_FEATURE_ENABLED = true\nOLD_FEATURE_LEVEL = maybe\nSHOW_TUNED_COUNT = false\n" +
            "[Server Settings]\nSERVER_PORT = 6000\n");

        var store = new ServerSettingsStore(ConfigFile);

        Assert.AreEqual(6000, store.GetServerPort());
        Assert.IsFalse(store.GetGeneralSetting(ServerSettingsKeys.SHOW_TUNED_COUNT).BoolValue);
        Assert.IsNull(store.LastSaveError);

        // the client only reads the settings it knows
        var client = new SyncedServerSettings();
        client.Decode(store.ToDictionary(), false);
        Assert.IsFalse(client.GetSettingAsBool(ServerSettingsKeys.SHOW_TUNED_COUNT));
        CollectionAssert.AreEqual(new[] { 27405000d, 446193750d }, client.TestFrequencies.ToArray());
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
        Assert.AreEqual(Path.Combine(_directory, "banned.txt"), store.GetDataFilePath("banned.txt"));
        Assert.IsNull(store.LastSaveError);
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
