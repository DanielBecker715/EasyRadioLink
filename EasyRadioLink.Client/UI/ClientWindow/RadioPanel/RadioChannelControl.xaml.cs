using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Client.UI.ClientWindow.RadioPanel.PresetChannels;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;
using NLog;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace EasyRadioLink.Client.UI.ClientWindow.RadioPanel;

/// <summary>
///     One radio (1..10) of the radio panel: frequency (step buttons or typed, invariant culture), mode / guard /
///     channel / key / users-on-frequency display, TX/RX indicator with the transmitter's name, volume, guard
///     receiver ("G"), simultaneous transmission ("ST"), radio sound (model), encryption tab (encryption capable radios
///     only) and preset channel tab. All changes go through <see cref="RadioHelper" />; the panel repaints every 80 ms.
/// </summary>
public partial class RadioChannelControl : UserControl
{
    private const int MaxSimultaneousTransmissions = 3;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // frozen brushes - the panel repaints every 80 ms
    private static readonly SolidColorBrush IdleTextBrush = Freeze("#00FF00");
    private static readonly SolidColorBrush TransmittingBrush = Freeze("#96FF6D");
    private static readonly SolidColorBrush SimulTransmittingBrush = Freeze("#4F86FF");
    private static readonly SolidColorBrush RedBrush = Freeze(Colors.Red);
    private static readonly SolidColorBrush GreenBrush = Freeze(Colors.Green);
    private static readonly SolidColorBrush DarkBlueBrush = Freeze(Colors.DarkBlue);
    private static readonly SolidColorBrush OrangeBrush = Freeze(Colors.Orange);
    private static readonly SolidColorBrush WhiteBrush = Freeze(Colors.White);

    private readonly ClientStateSingleton _clientStateSingleton = ClientStateSingleton.Instance;

    // the user drags the volume slider - don't overwrite it from the radio state
    private bool _dragging;

    // last guard frequency shown in the "G" tooltip
    private double _guardTooltipFrequency = -1;

    private int _radioId;

    // the combo box / slider are changed by the repaint, not by the user
    private bool _updatingModel;
    private bool _updatingVolume;

    public RadioChannelControl()
    {
        DataContext = this; // ChannelViewModel binding of the channel tab

        InitializeComponent();

        RadioFrequency.MaxLines = 1;
        // e.g. "446.00625"
        RadioFrequency.MaxLength = 10;

        RadioFrequency.LostFocus += RadioFrequencyOnLostFocus;
        RadioFrequency.KeyDown += RadioFrequencyOnKeyDown;
        RadioFrequency.GotFocus += RadioFrequencyOnGotFocus;

        try
        {
            // Key = what radio.model stores, DisplayName is shown
            RadioModel.ItemsSource = RadioModelFactory.Instance.AvailableModels;
            RadioModel.SelectedValuePath = nameof(RadioModelInfo.Key);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unable to load the radio models for the radio panel");
        }
    }

    /// <summary>Preset channels of this radio (DataContext of the channel tab).</summary>
    public PresetChannelsViewModel ChannelViewModel { get; set; }

    /// <summary>Radio slot 1..10 shown by this control.</summary>
    public int RadioId
    {
        get => _radioId;
        set
        {
            _radioId = value;
            UpdateBinding();
        }
    }

    private static SolidColorBrush Freeze(string color)
    {
        return Freeze((Color)ColorConverter.ConvertFromString(color));
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    //updates the binding so the changes are picked up for the linked preset channels
    private void UpdateBinding()
    {
        var fixedChannels = _clientStateSingleton.FixedChannels;
        var index = _radioId - PlayerRadioInfo.FirstUserRadio;

        ChannelViewModel = index >= 0 && index < fixedChannels.Length ? fixedChannels[index] : null;

        PresetChannelsView.GetBindingExpression(DataContextProperty)?.UpdateTarget();
    }

    /// <summary>The radio of this control, or null if radios are not available.</summary>
    private Radio CurrentRadio()
    {
        var playerRadioInfo = _clientStateSingleton.PlayerRadioInfo;

        if (!RadioHelper.RadiosAvailable() || RadioId < PlayerRadioInfo.FirstUserRadio ||
            RadioId >= playerRadioInfo.radios.Length)
            return null;

        return playerRadioInfo.radios[RadioId];
    }

    #region Frequency

    private void RadioFrequencyOnGotFocus(object sender, RoutedEventArgs routedEventArgs)
    {
        var radio = CurrentRadio();

        if (radio == null || !radio.IsEnabled) ClearFrequencyFocus();
    }

    private void RadioFrequencyOnKeyDown(object sender, KeyEventArgs keyEventArgs)
    {
        if (keyEventArgs.Key == Key.Enter)
        {
            ClearFrequencyFocus();
        }
        else if (keyEventArgs.Key == Key.Escape)
        {
            // discard the typed text
            var radio = CurrentRadio();
            if (radio != null) RadioFrequency.Text = RadioCalculator.FormatMHz(radio.freq);
            ClearFrequencyFocus();
        }
    }

    private void ClearFrequencyFocus()
    {
        //remove focus to somewhere else, then clear altogether
        RadioVolume.Focus();
        Keyboard.ClearFocus();
    }

    private void RadioFrequencyOnLostFocus(object sender, RoutedEventArgs routedEventArgs)
    {
        var radio = CurrentRadio();
        if (radio == null) return;

        // unchanged text - keep the exact frequency (the text may be rounded)
        if (RadioFrequency.Text.Trim() == RadioCalculator.FormatMHz(radio.freq)) return;

        // Invariant culture: "123.45" is always 123.45 MHz, also with a German Windows ("123,45" is accepted too)
        if (RadioCalculator.TryParseMHz(RadioFrequency.Text, out var frequencyHz))
            RadioHelper.UpdateRadioFrequency(frequencyHz, RadioId, false, false);
        else
            RadioFrequency.Text = RadioCalculator.FormatMHz(radio.freq);
    }

    private void Up0001_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.UpdateRadioFrequency(0.001, RadioId);
    }

    private void Up001_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.UpdateRadioFrequency(0.01, RadioId);
    }

    private void Up01_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.UpdateRadioFrequency(0.1, RadioId);
    }

    private void Up1_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.UpdateRadioFrequency(1, RadioId);
    }

    private void Up10_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.UpdateRadioFrequency(10, RadioId);
    }

    private void Down10_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.UpdateRadioFrequency(-10, RadioId);
    }

    private void Down1_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.UpdateRadioFrequency(-1, RadioId);
    }

    private void Down01_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.UpdateRadioFrequency(-0.1, RadioId);
    }

    private void Down001_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.UpdateRadioFrequency(-0.01, RadioId);
    }

    private void Down0001_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.UpdateRadioFrequency(-0.001, RadioId);
    }

    #endregion

    #region Selection, guard, volume, simultaneous transmission, sound

    private void RadioSelectSwitch(object sender, RoutedEventArgs e)
    {
        RadioHelper.SelectRadio(RadioId);
    }

    private void RadioFrequencyText_Click(object sender, MouseButtonEventArgs e)
    {
        RadioHelper.SelectRadio(RadioId);
    }

    private void RadioFrequencyText_RightClick(object sender, MouseButtonEventArgs e)
    {
        RadioHelper.ToggleGuard(RadioId);
    }

    private void GuardButton_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.ToggleGuard(RadioId);
    }

    private void RadioVolume_DragStarted(object sender, RoutedEventArgs e)
    {
        _dragging = true;
    }

    private void RadioVolume_DragCompleted(object sender, RoutedEventArgs e)
    {
        RadioHelper.SetRadioVolume((float)RadioVolume.Value / 100.0f, RadioId);

        _dragging = false;
    }

    private void RadioVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolume) return;

        // live while dragging, and for clicks on the track
        RadioHelper.SetRadioVolume((float)e.NewValue / 100.0f, RadioId);
    }

    private void ToggleSimultaneousTransmissionButton_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.ToggleSimultaneous(RadioId);
    }

    private void RadioModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingModel) return;

        if (RadioModel.SelectedValue is string modelKey) RadioHelper.SetRadioModel(RadioId, modelKey);
    }

    #endregion

    #region Repaint

    private void ToggleButtons(bool enable)
    {
        var visibility = enable ? Visibility.Visible : Visibility.Hidden;

        foreach (var button in new[] { Up10, Up1, Up01, Up001, Up0001, Down10, Down1, Down01, Down001, Down0001 })
        {
            button.Visibility = visibility;
            button.IsEnabled = enable;
        }

        RadioVolume.IsEnabled = enable;
        RadioModel.IsEnabled = enable;

        if (!enable)
        {
            ToggleSimultaneousTransmissionButton.IsEnabled = false;
            ToggleSimultaneousTransmissionButton.Foreground = WhiteBrush;
            GuardButton.Visibility = Visibility.Hidden;

            ChannelTab.Visibility = Visibility.Collapsed;
            return;
        }

        PresetChannelsView.IsEnabled = true;
        ChannelTab.Visibility = Visibility.Visible;

        var playerRadioInfo = _clientStateSingleton.PlayerRadioInfo;
        var currentRadio = playerRadioInfo.radios[RadioId];

        if (!currentRadio.rxOnly && playerRadioInfo.simultaneousTransmission)
        {
            var simulTransmission = 0;
            for (var i = PlayerRadioInfo.FirstUserRadio; i < playerRadioInfo.radios.Length; i++)
                if (i != RadioId && playerRadioInfo.radios[i].simul)
                    simulTransmission++;

            if (simulTransmission < MaxSimultaneousTransmissions || currentRadio.simul)
            {
                ToggleSimultaneousTransmissionButton.IsEnabled = true;
            }
            else
            {
                ToggleSimultaneousTransmissionButton.IsEnabled = false;
                ToggleSimultaneousTransmissionButton.Foreground = WhiteBrush;
            }
        }
        else
        {
            ToggleSimultaneousTransmissionButton.IsEnabled = false;
            ToggleSimultaneousTransmissionButton.Foreground = WhiteBrush;
        }
    }

    /// <summary>Repaints everything except the receive state (<see cref="RepaintRadioReceive" />).</summary>
    internal void RepaintRadioStatus()
    {
        var playerRadioInfo = _clientStateSingleton.PlayerRadioInfo;
        var currentRadio = CurrentRadio();

        SetupEncryption(currentRadio);
        HandleSimultaneousStatus(currentRadio);

        if (currentRadio == null || !currentRadio.IsEnabled)
        {
            RadioActive.Fill = RedBrush;
            RadioLabel.Text = Properties.Resources.OverlayNoRadio;
            if (!RadioFrequency.IsFocused) RadioFrequency.Text = Properties.Resources.ValueUnknown;

            RadioMetaData.Text = "";

            ToggleButtons(false);

            //reset dragging just incase
            _dragging = false;
        }
        else
        {
            var transmitting = _clientStateSingleton.RadioSendingState;

            if (transmitting.IsSending)
            {
                if (transmitting.SendingOn == RadioId)
                    RadioActive.Fill = TransmittingBrush;
                else if (currentRadio.simul)
                    RadioActive.Fill = SimulTransmittingBrush;
                else if (RadioId == playerRadioInfo.selected)
                    RadioActive.Fill = GreenBrush;
                else
                    RadioActive.Fill = OrangeBrush;
            }
            else
            {
                if (RadioId == playerRadioInfo.selected)
                    RadioActive.Fill = GreenBrush;
                else if (currentRadio.simul)
                    RadioActive.Fill = DarkBlueBrush;
                else
                    RadioActive.Fill = OrangeBrush;
            }

            if (!RadioFrequency.IsFocused)
                //make number UK / US style with decimals not commas!
                RadioFrequency.Text = RadioCalculator.FormatMHz(currentRadio.freq);

            RadioMetaData.Text = BuildMetaData(currentRadio);
            RadioLabel.Text = currentRadio.name;
            RadioLabel.ToolTip = currentRadio.name;

            ToggleButtons(true);
            RepaintGuard(currentRadio);
            RepaintModel(currentRadio);

            if (!_dragging)
            {
                var volume = Math.Clamp(currentRadio.volume * 100.0, 0, 100);
                if (Math.Abs(RadioVolume.Value - volume) > 0.01)
                {
                    _updatingVolume = true;
                    RadioVolume.Value = volume;
                    _updatingVolume = false;
                }
            }

            RadioVolume.ToolTip = string.Format(CultureInfo.InvariantCulture, Properties.Resources.ToolTipRadioVolume,
                (int)Math.Round(RadioVolume.Value));
        }

        var item = TabControl.SelectedItem as TabItem;

        if (item?.Visibility != Visibility.Visible) TabControl.SelectedIndex = 0;
    }

    /// <summary>"AM G C3 E7 RX 👤2": mode, guard on, preset channel, encryption key, receive only, users tuned in.</summary>
    private string BuildMetaData(Radio radio)
    {
        var metaData = radio.modulation switch
        {
            Modulation.AM => Properties.Resources.OverlayAM,
            Modulation.FM => Properties.Resources.OverlayFM,
            Modulation.DIGITAL => Properties.Resources.OverlayDIG,
            _ => ""
        };

        if (radio.secFreq > 0) metaData += " " + Properties.Resources.OverlayGuard;

        if (radio.channel > -1) metaData += " C" + radio.channel;
        if (radio.enc && radio.encKey > 0)
            metaData += " E" + radio.encKey; // ENCRYPTED

        if (radio.rxOnly) metaData += " RX";

        var count = _clientStateSingleton.ClientsOnFreq(radio.freq, radio.modulation);

        if (count > 0) metaData += " 👤" + count;

        return metaData;
    }

    private void RepaintGuard(Radio radio)
    {
        if (!radio.HasGuard)
        {
            GuardButton.Visibility = Visibility.Hidden;
            return;
        }

        GuardButton.Visibility = Visibility.Visible;
        GuardButton.IsEnabled = true;
        GuardButton.Foreground = radio.guardEnabled ? OrangeBrush : WhiteBrush;

        if (Math.Abs(_guardTooltipFrequency - radio.guardFreq) > 0.5)
        {
            _guardTooltipFrequency = radio.guardFreq;
            GuardButton.ToolTip = string.Format(Properties.Resources.ToolTipGuardFrequency,
                RadioCalculator.FormatMHz(radio.guardFreq));
        }
    }

    private void RepaintModel(Radio radio)
    {
        if (RadioModel.IsDropDownOpen || RadioModel.ItemsSource == null) return;

        string modelKey;
        try
        {
            // no model configured: the listeners use "standard" ("digital" for digital radios)
            modelKey = RadioModelFactory.Instance.ResolveModelKey(radio.model,
                radio.modulation == Modulation.DIGITAL
                    ? RadioModelFactory.DigitalModelKey
                    : RadioModelFactory.DefaultModelKey);
        }
        catch (Exception)
        {
            return;
        }

        if (Equals(RadioModel.SelectedValue, modelKey)) return;

        _updatingModel = true;
        try
        {
            RadioModel.SelectedValue = modelKey;
        }
        finally
        {
            _updatingModel = false;
        }
    }

    private void HandleSimultaneousStatus(Radio currentRadio)
    {
        var playerRadioInfo = _clientStateSingleton.PlayerRadioInfo;
        if (!playerRadioInfo.simultaneousTransmission) return;

        if (currentRadio != null)
            ToggleSimultaneousTransmissionButton.Foreground = currentRadio.simul ? OrangeBrush : WhiteBrush;
    }

    private void SetupEncryption(Radio currentRadio)
    {
        // the tab is only shown for radios that can encrypt; the server may still forbid encryption
        if (currentRadio == null || !currentRadio.IsEnabled || !currentRadio.encCapable)
        {
            EncryptionKeySpinner.IsEnabled = false;
            EncryptionButton.IsEnabled = false;
            EncryptionButton.Content = Properties.Resources.BtnEnable;
            EncryptionTab.Visibility = Visibility.Collapsed;
            return;
        }

        EncryptionTab.Visibility = Visibility.Visible;

        if (!EncryptionKeySpinner.IsFocused && !EncryptionKeySpinner.IsKeyboardFocusWithin)
            EncryptionKeySpinner.Value = currentRadio.encKey;

        if (RadioHelper.IsEncryptionAllowed(currentRadio))
        {
            EncryptionKeySpinner.IsEnabled = true;
            EncryptionButton.IsEnabled = true;
            EncryptionButton.Content = currentRadio.enc
                ? Properties.Resources.BtnDisable
                : Properties.Resources.BtnEnable;
            EncryptionStatus.Text = currentRadio.enc
                ? string.Format(Properties.Resources.OverlayEncryptionOn, currentRadio.encKey)
                : Properties.Resources.OverlayEncryptionOff;
        }
        else
        {
            EncryptionKeySpinner.IsEnabled = false;
            EncryptionButton.IsEnabled = false;
            EncryptionButton.Content = Properties.Resources.BtnEnable;
            EncryptionStatus.Text = Properties.Resources.OverlayEncryptionNotAllowed;
        }
    }

    /// <summary>Receive indicator: the display turns white (red on the guard frequency) and shows who is talking.</summary>
    internal void RepaintRadioReceive()
    {
        TransmitterName.Visibility = Visibility.Collapsed;
        RadioFrequency.Visibility = Visibility.Visible;
        RadioMetaData.Visibility = Visibility.Visible;

        var receiveState = RadioId >= 0 && RadioId < _clientStateSingleton.RadioReceivingState.Length
            ? _clientStateSingleton.RadioReceivingState[RadioId]
            : null;

        //check if current
        if (receiveState == null || !receiveState.IsReceiving || CurrentRadio() == null)
        {
            RadioFrequency.Foreground = IdleTextBrush;
            RadioMetaData.Foreground = IdleTextBrush;
            return;
        }

        if (!string.IsNullOrEmpty(receiveState.SentBy) && !RadioFrequency.IsFocused)
        {
            TransmitterName.Text = receiveState.SentBy;

            TransmitterName.Visibility = Visibility.Visible;
            RadioFrequency.Visibility = Visibility.Hidden;
            RadioMetaData.Visibility = Visibility.Hidden;
        }

        // received on the guard / secondary frequency
        var brush = receiveState.IsSecondary ? RedBrush : WhiteBrush;
        TransmitterName.Foreground = brush;
        RadioFrequency.Foreground = brush;
        RadioMetaData.Foreground = brush;
    }

    #endregion

    #region Encryption

    private void Encryption_ButtonClick(object sender, RoutedEventArgs e)
    {
        RadioHelper.ToggleEncryption(RadioId);

        var currentRadio = CurrentRadio();
        if (currentRadio != null)
            EncryptionButton.Content = currentRadio.enc
                ? Properties.Resources.BtnDisable
                : Properties.Resources.BtnEnable;
    }

    private void EncryptionKeySpinner_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (EncryptionKeySpinner?.Value != null)
            RadioHelper.SetEncryptionKey(RadioId, EncryptionKeySpinner.Value.Value);
    }

    #endregion
}
