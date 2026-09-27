using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Caliburn.Micro;
using EasyRadioLink.Client.Audio.Managers;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;

namespace EasyRadioLink.Client.UI.ClientWindow.RadioPanel;

/// <summary>
///     The radio: an always-on-top, borderless window with ONE radio.
///     <list type="bullet">
///         <item>
///             Display: frequency on a seven-segment LCD (<c>000.000</c> MHz, two small digits for the part below
///             1 kHz), band and modulation (from the <see cref="BandPlan" />), users on the frequency and the name of
///             the speaker (if the server allows it), BUSY / TX / RX, the tuning step. The digit the step changes is
///             underlined. BUSY (busy channel lockout, if the server has it on) is lit while another station uses the
///             frequency and flashes after a refused push-to-talk press (<see cref="BusyChannelLockout" />).
///         </item>
///         <item>
///             Tuning: the knob (drag in a circle, mouse wheel), the mouse wheel over the display, the ▲ / ▼ keys and the
///             arrow keys Up / Down; STEP (or Left / Right) chooses the step, 1 kHz .. 100 MHz. Double-click on the
///             display (or Enter) to type a frequency: Enter applies, Esc cancels. When the frequency is changed
///             elsewhere (hotkeys) the knob turns a notch with it.
///         </item>
///         <item>Offline (not connected): dashes on the display, NO LINK, all controls disabled.</item>
///         <item>
///             The content has a fixed natural size and is scaled uniformly with the window (grip in the corner).
///             Position and scale are saved in global.cfg (RadioX / RadioY / RadioScale), the tuning step in
///             radio-state.json. The size keys of 1.0 (RadioWidth / RadioHeight - the size of the six-radio panel) are
///             not read.
///         </item>
///         <item><see cref="ResetRadioPanelMessage" /> closes the window without saving (settings reset / off-screen check).</item>
///     </list>
///     All changes go through <see cref="RadioHelper" />; the window repaints every 80 ms.
/// </summary>
public partial class RadioPanelWindow : Window, IHandle<ResetRadioPanelMessage>
{
    /// <summary>Default position of the window (global.cfg RadioX / RadioY).</summary>
    public const double DefaultLeft = 300;

    public const double DefaultTop = 300;

    /// <summary>Size of the window at scale 1 (including the transparent shadow margin).</summary>
    public const double NaturalWidth = 500;

    public const double NaturalHeight = 238;

    private const double MinScale = 0.7;
    private const double MaxScale = 4.0;

    private const string OfflineFrequency = "---.---";

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // link LED while not connected: an unlit red
    private static readonly Brush OfflineLedBrush = FrozenBrush(Color.FromRgb(0x5A, 0x23, 0x20));

    private readonly ClientStateSingleton _clientState = ClientStateSingleton.Instance;
    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;
    private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;

    private readonly Brush _litBrush;
    private readonly Brush _dimBrush;
    private readonly Brush _txBrush;
    private readonly Brush _rxBrush;
    private readonly Brush _busyBrush;

    private readonly Effect _txGlow;
    private readonly Effect _rxGlow;
    private readonly Effect _busyGlow;

    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _entryErrorTimer;

    // set when the window is closed because of a settings reset - don't save the placement then
    private bool _resetting;

    // resize grip: pointer position (window coordinates) and window size when the drag started
    private bool _resizing;
    private Point _resizeStart;
    private Size _resizeStartSize;

    // current scale of the window (1 = natural size), saved as RadioScale
    private double _scale = 1.0;

    // tuning step in Hz (STEP)
    private int _step;

    // frequency shown by the last refresh (NaN while offline) - a different frequency on the next refresh was tuned
    // outside the window (hotkeys)
    private double _shownFrequency = double.NaN;

    // volume shown in the tooltip of the volume knob
    private int _volumeTooltipPercent = -1;

    public RadioPanelWindow()
    {
        InitializeComponent();

        _litBrush = (Brush)FindResource("LcdLitBrush");
        _dimBrush = (Brush)FindResource("LcdDimBrush");
        _txBrush = (Brush)FindResource("LcdTxBrush");
        _rxBrush = (Brush)FindResource("LcdRxBrush");
        _busyBrush = (Brush)FindResource("LcdBusyBrush");

        _txGlow = Glow(((SolidColorBrush)_txBrush).Color);
        _rxGlow = Glow(((SolidColorBrush)_rxBrush).Color);
        _busyGlow = Glow(((SolidColorBrush)_busyBrush).Color);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioX).DoubleValue;
        Top = _globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioY).DoubleValue;
        ApplyScale(SavedScale());

        _step = RadioStatePersistence.Load().Step;

        //allows click and drag anywhere on the housing
        ContainerPanel.MouseLeftButtonDown += ContainerPanel_MouseLeftButtonDown;
        PreviewKeyDown += Window_PreviewKeyDown;

        // no control takes the keyboard focus: the window itself gets it, so the arrow keys tune
        Activated += (_, _) =>
        {
            if (!IsKeyboardFocusWithin) Focus();
        };

        _entryErrorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _entryErrorTimer.Tick += (_, _) =>
        {
            _entryErrorTimer.Stop();
            EntryPanel.BorderBrush = _litBrush;
        };

        Refresh(null, null);

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _updateTimer.Tick += Refresh;
        _updateTimer.Start();

        EventBus.Instance.SubscribeOnUIThread(this);
    }

    public Task HandleAsync(ResetRadioPanelMessage message, CancellationToken cancellationToken)
    {
        // settings reset / off-screen check: close without saving so the reset position is kept
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _resetting = true;
            Close();
        });

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Writes the default placement of the window (position, natural size). An open window must be closed with
    ///     <see cref="ResetRadioPanelMessage" /> so it does not save its own placement afterwards.
    /// </summary>
    public static void ResetSavedPlacement(GlobalSettingsStore settings)
    {
        settings.SetPositionSetting(GlobalSettingsKeys.RadioX, DefaultLeft);
        settings.SetPositionSetting(GlobalSettingsKeys.RadioY, DefaultTop);
        settings.SetPositionSetting(GlobalSettingsKeys.RadioScale, 1);
    }

    /// <summary>
    ///     The saved scale of the window (global.cfg RadioScale, 1 = natural size); 1 if it is missing or invalid. Not
    ///     yet limited to <see cref="MinScale" /> .. <see cref="MaxScale" /> and the monitor.
    /// </summary>
    public static double ReadSavedScale(GlobalSettingsStore settings)
    {
        var scale = settings.GetPositionSetting(GlobalSettingsKeys.RadioScale).DoubleValue;

        return double.IsFinite(scale) && scale > 0 ? scale : 1.0;
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Effect Glow(Color color)
    {
        var glow = new DropShadowEffect { Color = color, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 };
        glow.Freeze();
        return glow;
    }

    #region Display

    private void Refresh(object sender, EventArgs e)
    {
        var connected = _clientState.IsConnected;
        LinkLed.Fill = connected ? _rxBrush : OfflineLedBrush;
        LinkLedGlow.Opacity = connected ? 0.8 : 0;

        var radio = RadioHelper.GetRadio();

        if (radio == null)
            ShowOffline(connected);
        else
            ShowRadio(radio);

        StepText.Text = string.Format(CultureInfo.InvariantCulture, Properties.Resources.RadioStepFormat,
            TuningSteps.Label(_step));
    }

    private void ShowOffline(bool connected)
    {
        if (EntryPanel.Visibility == Visibility.Visible) CloseEntry();

        _shownFrequency = double.NaN;

        // the backlight is dimmed while the radio is off
        Backlight.Opacity = 0.35;

        FrequencyDisplay.Text = OfflineFrequency;
        FrequencyDisplay.LitBrush = _dimBrush;
        FrequencyDisplay.GlowRadius = 0;
        FrequencyDisplay.MarkedDigit = -1;
        SubKiloHertzDisplay.Text = "";

        BandChip.Visibility = Visibility.Hidden;
        ModulationText.Visibility = Visibility.Hidden;
        UsersPanel.Visibility = Visibility.Collapsed;
        UnitText.Foreground = _dimBrush;
        StepText.Visibility = Visibility.Hidden;

        SetIndicator(BusyText, false, _busyBrush, _busyGlow);
        SetIndicator(TxText, false, _txBrush, _txGlow);
        SetIndicator(RxText, false, _rxBrush, _rxGlow);

        StatusText.Foreground = _dimBrush;
        StatusText.Text = connected ? Properties.Resources.RadioStatusLoading : Properties.Resources.RadioStatusNoLink;

        SetControlsEnabled(false);
    }

    private void ShowRadio(Radio radio)
    {
        var frequency = radio.freq;
        var band = BandPlan.GetBand(frequency);

        // tuned outside the window (hotkeys): turn the knob a notch with it (the window's own tuning has updated
        // _shownFrequency already, see RefreshAfterTuning)
        if (!double.IsNaN(_shownFrequency) && Math.Abs(frequency - _shownFrequency) >= 0.5)
            TuningKnob.Nudge(Math.Sign(frequency - _shownFrequency));

        _shownFrequency = frequency;

        Backlight.Opacity = 1;

        FrequencyDisplay.Text = FormatFrequency(frequency, out var subKiloHertz);
        FrequencyDisplay.LitBrush = _litBrush;
        FrequencyDisplay.GlowRadius = 12;
        FrequencyDisplay.MarkedDigit = TuningSteps.DigitIndex(_step);

        // 446.19375 MHz: "75" after the kHz digits; blank (unlit segments only) on the kHz grid
        SubKiloHertzDisplay.Text = subKiloHertz > 0
            ? (subKiloHertz / 10).ToString("00", CultureInfo.InvariantCulture)
            : "";

        BandText.Text = band.Label;
        BandChip.Visibility = Visibility.Visible;
        ModulationText.Text = radio.modulation switch
        {
            Modulation.AM => Properties.Resources.OverlayAM,
            Modulation.FM => Properties.Resources.OverlayFM,
            Modulation.DIGITAL => Properties.Resources.OverlayDIG,
            _ => ""
        };
        ModulationText.Visibility = Visibility.Visible;
        UnitText.Foreground = _litBrush;
        StepText.Visibility = Visibility.Visible;

        // users on the frequency - only if the server allows it
        if (ClientStateSingleton.ShowTunedCount)
        {
            UsersText.Text = _clientState.ClientsOnFreq(frequency, radio.modulation)
                .ToString(CultureInfo.InvariantCulture);
            UsersPanel.Visibility = Visibility.Visible;
        }
        else
        {
            UsersPanel.Visibility = Visibility.Collapsed;
        }

        var sending = _clientState.RadioSendingState;
        var transmitting = sending.IsSending && sending.SendingOn == PlayerRadioInfo.RadioId;

        var receiveState = _clientState.RadioReceivingState[PlayerRadioInfo.RadioId];
        var receiving = receiveState != null && receiveState.IsReceiving;

        // one speaker per frequency: only when the server has the busy channel lockout on
        var busy = _serverSettings.BusyChannelLockout &&
                   _clientState.BusyChannel.IsIndicatorLit(frequency, radio.modulation,
                       BusyChannelLockout.NowMilliseconds);

        SetIndicator(BusyText, busy, _busyBrush, _busyGlow);
        SetIndicator(TxText, transmitting, _txBrush, _txGlow);
        SetIndicator(RxText, receiving, _rxBrush, _rxGlow);

        // the name of the speaker (empty unless the server and the user allow it)
        StatusText.Foreground = _litBrush;
        StatusText.Text = receiving && !string.IsNullOrWhiteSpace(receiveState.SentBy) ? receiveState.SentBy : "";

        if (!VolumeKnob.IsMouseCaptured && Math.Abs(VolumeKnob.Value - radio.volume) > 0.001)
            VolumeKnob.Value = radio.volume;

        var percent = (int)Math.Round(radio.volume * 100);
        if (percent != _volumeTooltipPercent)
        {
            _volumeTooltipPercent = percent;
            VolumeKnob.ToolTip = string.Format(CultureInfo.InvariantCulture, Properties.Resources.ToolTipRadioVolume,
                percent);
        }

        SetControlsEnabled(true);
    }

    /// <summary>"027.185" (MHz with three decimals) plus the rest below 1 kHz in Hz (0..999).</summary>
    internal static string FormatFrequency(double frequencyHz, out int subKiloHertz)
    {
        var hertz = (long)Math.Round(Math.Max(0, frequencyHz));
        var kiloHertz = hertz / 1000;
        subKiloHertz = (int)(hertz % 1000);

        return string.Format(CultureInfo.InvariantCulture, "{0:000}.{1:000}", kiloHertz / 1000, kiloHertz % 1000);
    }

    private static void SetIndicator(System.Windows.Controls.TextBlock indicator, bool on, Brush onBrush,
        Effect glow)
    {
        var brush = on ? onBrush : null;
        if (brush == null)
        {
            indicator.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
            indicator.Effect = null;
        }
        else if (!ReferenceEquals(indicator.Foreground, brush))
        {
            indicator.Foreground = brush;
            indicator.Effect = glow;
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        if (StepUpButton.IsEnabled == enabled && TuningKnob.IsEnabled == enabled) return;

        StepUpButton.IsEnabled = enabled;
        StepDownButton.IsEnabled = enabled;
        StepButton.IsEnabled = enabled;
        TuningKnob.IsEnabled = enabled;
        VolumeKnob.IsEnabled = enabled;
    }

    #endregion

    #region Tuning

    /// <summary>Tunes by <paramref name="steps" /> tuning steps (negative = down).</summary>
    private void Tune(int steps)
    {
        if (steps == 0 || RadioHelper.GetRadio() == null) return;

        // one step at a time: with rotary style steps every step rolls its digit over on its own
        var direction = Math.Sign(steps);
        for (var i = 0; i < Math.Abs(steps); i++) RadioHelper.StepFrequency(direction * (double)_step);

        RefreshAfterTuning();
    }

    /// <summary>
    ///     Repaints after the window itself changed the frequency: the knob has turned already (or does not turn for a
    ///     typed frequency), so the change must not count as tuned elsewhere.
    /// </summary>
    private void RefreshAfterTuning()
    {
        var radio = RadioHelper.GetRadio();
        _shownFrequency = radio?.freq ?? double.NaN;

        Refresh(null, null);
    }

    private void TuningKnob_Tuned(object sender, int detents)
    {
        Tune(detents);
    }

    private void StepUpButton_Click(object sender, RoutedEventArgs e)
    {
        TuningKnob.Nudge(1);
        Tune(1);
    }

    private void StepDownButton_Click(object sender, RoutedEventArgs e)
    {
        TuningKnob.Nudge(-1);
        Tune(-1);
    }

    private void StepButton_Click(object sender, RoutedEventArgs e)
    {
        SetStep(TuningSteps.Next(_step));
    }

    /// <summary>Selects the next larger (<paramref name="larger" />) or smaller step, without wrapping.</summary>
    private void ShiftStep(bool larger)
    {
        var index = -1;
        for (var i = 0; i < TuningSteps.All.Count; i++)
            if (TuningSteps.All[i] == _step)
                index = i;

        index = Math.Clamp(index + (larger ? 1 : -1), 0, TuningSteps.All.Count - 1);
        SetStep(TuningSteps.All[index]);
    }

    private void SetStep(int step)
    {
        if (!TuningSteps.IsValid(step) || step == _step) return;

        _step = step;
        RadioStatePersistence.SaveStep(step);
        Refresh(null, null);
    }

    private void Display_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // typing a frequency, or offline (the knob is disabled and must not turn)
        if (EntryPanel.Visibility == Visibility.Visible || RadioHelper.GetRadio() == null) return;

        e.Handled = true;

        var detents = Controls.TuningKnob.WheelDetents(e.Delta);
        TuningKnob.Nudge(detents);
        Tune(detents);
    }

    private void VolumeKnob_ValueChangedByUser(object sender, EventArgs e)
    {
        RadioHelper.SetRadioVolume((float)VolumeKnob.Value);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // the direct entry box handles its own keys
        if (EntryPanel.Visibility == Visibility.Visible || RadioHelper.GetRadio() == null) return;

        switch (e.Key)
        {
            case Key.Up:
                TuningKnob.Nudge(1);
                Tune(1);
                e.Handled = true;
                break;
            case Key.Down:
                TuningKnob.Nudge(-1);
                Tune(-1);
                e.Handled = true;
                break;
            case Key.Left:
                ShiftStep(true);
                e.Handled = true;
                break;
            case Key.Right:
                ShiftStep(false);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenEntry();
                e.Handled = true;
                break;
        }
    }

    #endregion

    #region Direct entry

    private void Display_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // a single click drags the window (ContainerPanel), a double-click types a frequency
        if (e.ClickCount != 2) return;

        e.Handled = true;
        OpenEntry();
    }

    private void OpenEntry()
    {
        var radio = RadioHelper.GetRadio();
        if (radio == null) return;

        EntryBox.Text = RadioCalculator.FormatMHz(radio.freq);
        EntryPanel.BorderBrush = _litBrush;
        EntryPanel.Visibility = Visibility.Visible;
        FrequencyPanel.Visibility = Visibility.Hidden;

        Activate();

        // after the layout pass the box can take the focus
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (EntryPanel.Visibility != Visibility.Visible) return;

            EntryBox.Focus();
            Keyboard.Focus(EntryBox);
            EntryBox.SelectAll();
        }));
    }

    private void CloseEntry()
    {
        _entryErrorTimer.Stop();
        EntryPanel.Visibility = Visibility.Collapsed;
        EntryPanel.BorderBrush = _litBrush;
        FrequencyPanel.Visibility = Visibility.Visible;

        // keep the keyboard on the window (arrow keys tune)
        if (IsActive) Focus();
    }

    private void EntryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ApplyEntry();
                break;
            case Key.Escape:
                e.Handled = true;
                CloseEntry();
                break;
        }
    }

    private void EntryBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // clicked elsewhere: cancel
        if (EntryPanel.Visibility == Visibility.Visible) CloseEntry();
    }

    /// <summary>
    ///     Applies the typed frequency in MHz. Invariant culture ("446.19375") and the German decimal comma
    ///     ("446,19375") are accepted; out of range values are clamped to 1.000 - 999.999 MHz and the frequency is
    ///     normalised (<see cref="BandPlan.Normalise" />, by <see cref="RadioHelper.SetFrequency" />).
    /// </summary>
    private void ApplyEntry()
    {
        if (!TryParseEntry(EntryBox.Text, out var frequencyHz))
        {
            // invalid: flash the frame and let the user correct it
            EntryPanel.BorderBrush = _txBrush;
            _entryErrorTimer.Stop();
            _entryErrorTimer.Start();
            EntryBox.SelectAll();
            return;
        }

        RadioHelper.SetFrequency(frequencyHz);
        CloseEntry();
        RefreshAfterTuning();
    }

    /// <summary>
    ///     The typed frequency (MHz, see <see cref="RadioCalculator.TryParseMHz" />) in Hz, rounded to 10 Hz - the
    ///     resolution of the display and of the entry box (<see cref="RadioCalculator.FormatMHz" />, 5 decimals), so
    ///     Enter on an unchanged entry never moves the radio.
    /// </summary>
    internal static bool TryParseEntry(string text, out double frequencyHz)
    {
        if (!RadioCalculator.TryParseMHz(text, out frequencyHz)) return false;

        frequencyHz = Math.Round(frequencyHz / 10, MidpointRounding.AwayFromZero) * 10;
        return true;
    }

    #endregion

    #region Placement

    /// <summary>The saved scale (RadioScale), limited to the monitor the window is on.</summary>
    private double SavedScale()
    {
        return LimitScale(ReadSavedScale(_globalSettings));
    }

    /// <summary>Clamps a scale to <see cref="MinScale" />..<see cref="MaxScale" /> and to the monitor's working area.</summary>
    private double LimitScale(double scale)
    {
        if (!double.IsFinite(scale)) return 1.0;

        var max = MaxScale;
        try
        {
            var area = ScreenHelper.WorkingAreaAt(Left, Top);
            if (area.Width > 0 && area.Height > 0)
                max = Math.Min(max, Math.Min(area.Width / NaturalWidth, area.Height / NaturalHeight));
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unable to check the size of the radio window against the monitor");
        }

        return Math.Clamp(scale, MinScale, Math.Max(MinScale, max));
    }

    private void ApplyScale(double scale)
    {
        _scale = scale;
        Width = NaturalWidth * scale;
        Height = NaturalHeight * scale;
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (!ResizeGrip.CaptureMouse()) return;

        _resizing = true;
        _resizeStart = e.GetPosition(this);
        _resizeStartSize = new Size(ActualWidth, ActualHeight);
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;

        // the window grows to the right / bottom, so positions relative to the window stay comparable
        var position = e.GetPosition(this);
        var scaleX = (_resizeStartSize.Width + position.X - _resizeStart.X) / NaturalWidth;
        var scaleY = (_resizeStartSize.Height + position.Y - _resizeStart.Y) / NaturalHeight;

        ApplyScale(LimitScale(Math.Max(scaleX, scaleY)));
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;

        e.Handled = true;
        ResizeGrip.ReleaseMouseCapture();
    }

    private void ResizeGrip_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _resizing = false;
    }

    private void ContainerPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // the mouse button was released already
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_resetting)
        {
            _globalSettings.SetPositionSetting(GlobalSettingsKeys.RadioX, Left);
            _globalSettings.SetPositionSetting(GlobalSettingsKeys.RadioY, Top);
            _globalSettings.SetPositionSetting(GlobalSettingsKeys.RadioScale, Math.Round(_scale, 3));
        }

        base.OnClosing(e);

        _updateTimer.Stop();
        _entryErrorTimer.Stop();

        EventBus.Instance.Unsubscribe(this);
    }

    private void Button_Minimise(object sender, RoutedEventArgs e)
    {
        // Minimising a window without a taskbar icon leaves a small part of the window at the bottom of the screen,
        // so the window is closed instead (like the toggle).
        if (_globalSettings.GetClientSettingBool(GlobalSettingsKeys.RadioPanelTaskbarHide))
            Close();
        else
            WindowState = WindowState.Minimized;
    }

    private void Button_Close(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion
}
