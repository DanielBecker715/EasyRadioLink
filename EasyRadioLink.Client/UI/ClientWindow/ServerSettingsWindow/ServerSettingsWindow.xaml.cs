using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings.Setting;
using MahApps.Metro.Controls;
using NLog;

namespace EasyRadioLink.Client.UI.ClientWindow.ServerSettingsWindow;

/// <summary>
///     "Server Info": the settings of the connected server that matter for the user (read only, refreshed every
///     second from <see cref="SyncedServerSettings" />).
/// </summary>
public partial class ServerSettingsWindow : MetroWindow
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;
    private readonly DispatcherTimer _updateTimer;

    public ServerSettingsWindow()
    {
        InitializeComponent();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _updateTimer.Tick += UpdateUI;
        _updateTimer.Start();

        UpdateUI(null, null);
    }

    private static string OnOff(bool value)
    {
        return value ? Properties.Resources.ValueON : Properties.Resources.ValueOFF;
    }

    /// <summary>"27.405, 446.19375 MHz" or "None".</summary>
    private static string FrequencyList(IReadOnlyList<double> frequencies)
    {
        if (frequencies == null || frequencies.Count == 0) return Properties.Resources.ValueNone;

        return string.Join(", ", frequencies.Select(RadioCalculator.FormatMHz)) + " MHz";
    }

    private void UpdateUI(object sender, EventArgs e)
    {
        var settings = _serverSettings;

        try
        {
            RealRadio.Text = OnOff(settings.GetSettingAsBool(ServerSettingsKeys.IRL_RADIO_TX));
            RadioRXInterference.Text = OnOff(settings.GetSettingAsBool(ServerSettingsKeys.IRL_RADIO_RX_INTERFERENCE));
            TunedClientCount.Text = OnOff(settings.GetSettingAsBool(ServerSettingsKeys.SHOW_TUNED_COUNT));
            ShowTransmitterName.Text = OnOff(settings.GetSettingAsBool(ServerSettingsKeys.SHOW_TRANSMITTER_NAME));

            TestFrequencies.Text = FrequencyList(settings.TestFrequencies);
            CleanFrequencies.Text = FrequencyList(settings.CleanFrequencies);

            ServerVersion.Text = string.IsNullOrWhiteSpace(settings.ServerVersion)
                ? Properties.Resources.ValueUnknown
                : settings.ServerVersion;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unable to show the server settings");
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        _updateTimer.Stop();
    }
}
