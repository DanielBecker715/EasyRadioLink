using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Settings;

/// <summary>The owner runs a German Windows: "27.405" must never become 27405 MHz.</summary>
[TestClass]
public class FrequencyParsingTests
{
    private CultureInfo _culture;
    private CultureInfo _uiCulture;

    [TestInitialize]
    public void UseGermanCulture()
    {
        _culture = CultureInfo.CurrentCulture;
        _uiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
    }

    [TestCleanup]
    public void RestoreCulture()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    [TestMethod]
    public void FrequencyListIsParsedWithInvariantCulture()
    {
        var frequencies = RadioCalculator.ParseFrequencyListMHz("27.405,446.19375");

        CollectionAssert.AreEqual(new List<double> { 27405000, 446193750 }, frequencies);
    }

    [TestMethod]
    public void FrequencyListIgnoresBlanksAndGarbage()
    {
        CollectionAssert.AreEqual(new List<double> { 27185000, 145500000 },
            RadioCalculator.ParseFrequencyListMHz(" 27.185 ; abc, ,145.5,-3,0,NaN,"));
        Assert.IsEmpty(RadioCalculator.ParseFrequencyListMHz(null));
        Assert.IsEmpty(RadioCalculator.ParseFrequencyListMHz(""));
        Assert.AreEqual("27.405,446.19375", RadioCalculator.NormaliseFrequencyListMHz(" 27.4050 ;446.19375 "));
    }

    [TestMethod]
    public void SingleFrequencyParsingAndFormatting()
    {
        Assert.IsTrue(RadioCalculator.TryParseMHz("27.405", out var dot));
        Assert.AreEqual(27405000d, dot);

        // convenience for a German keyboard: a single comma is a decimal separator
        Assert.IsTrue(RadioCalculator.TryParseMHz("27,405", out var comma));
        Assert.AreEqual(27405000d, comma);

        Assert.IsFalse(RadioCalculator.TryParseMHz("1,000,000", out _));
        Assert.IsFalse(RadioCalculator.TryParseMHz("", out _));
        Assert.IsFalse(RadioCalculator.TryParseMHz("-5", out _));

        Assert.AreEqual("27.185", RadioCalculator.FormatMHz(27185000));
        Assert.AreEqual("446.00625", RadioCalculator.FormatMHz(446006250));
        Assert.AreEqual("124.800", RadioCalculator.FormatMHz(124800000));
    }

    [TestMethod]
    public void SyncedServerSettingsParsesFrequencyListsInvariant()
    {
        var settings = new SyncedServerSettings();
        settings.Decode(new Dictionary<string, string>
        {
            { ServerSettingsKeys.TEST_FREQUENCIES.ToString(), "27.405" },
            { ServerSettingsKeys.CLEAN_FREQUENCIES.ToString(), "145.5,446.1" }
        });

        CollectionAssert.AreEqual(new List<double> { 27405000 }, new List<double>(settings.TestFrequencies));
        CollectionAssert.AreEqual(new List<double> { 145500000, 446100000 },
            new List<double>(settings.CleanFrequencies));

        Assert.IsTrue(settings.IsTestFrequency(27405000));
        Assert.IsTrue(settings.IsCleanFrequency(145500000 + 200));
        Assert.IsTrue(settings.IsCleanFrequency(446100000));
        Assert.IsFalse(settings.IsCleanFrequency(145525000));
        Assert.IsFalse(settings.IsCleanFrequency(27405000));
    }

    [TestMethod]
    public void SyncedServerSettingsUsesDefaultsUntilTheServerSendsValues()
    {
        var settings = new SyncedServerSettings();

        CollectionAssert.AreEqual(new List<double> { 27405000, 446193750 },
            new List<double>(settings.TestFrequencies));
        Assert.IsEmpty(settings.CleanFrequencies);
        Assert.IsTrue(settings.GetSettingAsBool(ServerSettingsKeys.ALLOW_RADIO_ENCRYPTION));
        Assert.IsTrue(settings.GetSettingAsBool(ServerSettingsKeys.SHOW_TUNED_COUNT));
        Assert.IsFalse(settings.GetSettingAsBool(ServerSettingsKeys.STRICT_RADIO_ENCRYPTION));
        Assert.IsEmpty(settings.ServerRadioPreset);
        Assert.AreEqual(2, settings.GetSettingAsInt(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION));
    }

    [TestMethod]
    public void InvalidBooleansFallBackToDefaults()
    {
        var settings = new SyncedServerSettings();
        settings.Decode(new Dictionary<string, string>
        {
            { ServerSettingsKeys.ALLOW_RADIO_ENCRYPTION.ToString(), "" },
            { ServerSettingsKeys.STRICT_RADIO_ENCRYPTION.ToString(), "True" },
            { ServerSettingsKeys.SHOW_TUNED_COUNT.ToString(), "garbage" }
        });

        Assert.IsTrue(settings.GetSettingAsBool(ServerSettingsKeys.ALLOW_RADIO_ENCRYPTION));
        Assert.IsTrue(settings.GetSettingAsBool(ServerSettingsKeys.STRICT_RADIO_ENCRYPTION));
        Assert.IsTrue(settings.GetSettingAsBool(ServerSettingsKeys.SHOW_TUNED_COUNT));
    }

    [TestMethod]
    public void ResetForgetsThePreviousServer()
    {
        var settings = new SyncedServerSettings();
        settings.Decode(new Dictionary<string, string>
        {
            { ServerSettingsKeys.CLEAN_FREQUENCIES.ToString(), "145.5" },
            { ServerSettingsKeys.STRICT_RADIO_ENCRYPTION.ToString(), "true" }
        });
        settings.ServerVersion = "1.0.0";

        settings.Reset();

        Assert.IsEmpty(settings.CleanFrequencies);
        Assert.IsFalse(settings.GetSettingAsBool(ServerSettingsKeys.STRICT_RADIO_ENCRYPTION));
        Assert.IsNull(settings.ServerVersion);
    }

    [TestMethod]
    public void ServerSettingsRoundTripUnderGermanCulture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "erl-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configFile = Path.Combine(directory, "server.cfg");
            var store = new ServerSettingsStore(configFile);
            store.SetGeneralSetting(ServerSettingsKeys.TEST_FREQUENCIES, "27.405,446.19375");
            store.SetGeneralSetting(ServerSettingsKeys.CLEAN_FREQUENCIES, "145.5");
            store.SetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION, "3");

            var reloaded = new ServerSettingsStore(configFile);
            var broadcast = reloaded.ToDictionary();

            Assert.AreEqual("27.405,446.19375", broadcast[ServerSettingsKeys.TEST_FREQUENCIES.ToString()]);
            Assert.AreEqual(3, reloaded.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION).IntValue);

            var client = new SyncedServerSettings();
            client.Decode(broadcast);
            CollectionAssert.AreEqual(new List<double> { 27405000, 446193750 },
                new List<double>(client.TestFrequencies));
            Assert.IsTrue(client.IsCleanFrequency(145500000));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }
}
