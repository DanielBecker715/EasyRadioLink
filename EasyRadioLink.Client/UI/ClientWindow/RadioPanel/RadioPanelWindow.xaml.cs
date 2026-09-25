using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Caliburn.Micro;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;

namespace EasyRadioLink.Client.UI.ClientWindow.RadioPanel;

/// <summary>
///     The radio panel: an always-on-top window with the user's radios 1..10.
///     <list type="bullet">
///         <item>Only switched-on radios are shown (disabled slots are hidden) and the window fits them: up to five in a row,
///             more in two rows. While radios are not available (not connected) a short hint is shown instead.</item>
///         <item>The content has a fixed natural size and is scaled uniformly with the window (resize grip, the aspect
///             ratio is kept). Position, size and opacity are saved in global.cfg (RadioX/RadioY/RadioWidth/RadioHeight/RadioOpacity).</item>
///         <item><see cref="ResetRadioPanelMessage" /> closes the panel without saving (settings reset / off-screen check).</item>
///     </list>
/// </summary>
public partial class RadioPanelWindow : Window, IHandle<ResetRadioPanelMessage>
{
    /// <summary>Default position of the panel (global.cfg RadioX / RadioY).</summary>
    public const double DefaultLeft = 300;

    public const double DefaultTop = 300;

    // radios per row
    private const int MaxColumns = 5;

    // largest scale restored from a saved size
    private const double MaxRestoredScale = 4.0;

    private static readonly SolidColorBrush WhiteBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush OrangeBrush = Freeze(new SolidColorBrush(Colors.Orange));
    private static readonly SolidColorBrush ConnectedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0xC8, 0x3C)));
    private static readonly SolidColorBrush DisconnectedBrush = Freeze(new SolidColorBrush(Colors.Red));

    private readonly ClientStateSingleton _clientStateSingleton = ClientStateSingleton.Instance;

    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;

    private readonly RadioChannelControl[] _radioControls;

    private readonly DispatcherTimer _updateTimer;

    // width / height of the content at scale 1
    private double _aspectRatio = 1;

    // the window size is being changed by the code, not by the user
    private bool _adjustingSize;

    // bit n = radio n shown, -1 = not computed yet, 0 = no radios (hint shown)
    private int _layoutSignature = -1;

    private Size _naturalSize = Size.Empty;

    // set when the window is closed because of a settings reset - don't save the position then
    private bool _resetting;

    // the saved size was applied (to a layout with radios) - only then the size is saved again
    private bool _sizeRestored;

    public RadioPanelWindow()
    {
        //load opacity before the intialising as the slider changed
        //method fires after initialisation
        var opacity = _globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioOpacity).DoubleValue;

        InitializeComponent();

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioX).DoubleValue;
        Top = _globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioY).DoubleValue;

        Opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, WindowOpacitySlider.Minimum, 1.0) : 1.0;
        WindowOpacitySlider.Value = Opacity;

        _radioControls = [radio1, radio2, radio3, radio4, radio5, radio6, radio7, radio8, radio9, radio10];

        //allows click and drag anywhere on the window
        ContainerPanel.MouseLeftButtonDown += ContainerPanel_MouseLeftButtonDown;

        RadioRefresh(null, null);

        //init radio refresh
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _updateTimer.Tick += RadioRefresh;
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
    ///     Writes the default placement of the panel (position, natural size, full opacity). An open panel must be
    ///     closed with <see cref="ResetRadioPanelMessage" /> so it does not save its own placement afterwards.
    /// </summary>
    public static void ResetSavedPlacement(GlobalSettingsStore settings)
    {
        settings.SetPositionSetting(GlobalSettingsKeys.RadioX, DefaultLeft);
        settings.SetPositionSetting(GlobalSettingsKeys.RadioY, DefaultTop);
        // 0 = natural size (scale 1)
        settings.SetPositionSetting(GlobalSettingsKeys.RadioWidth, 0);
        settings.SetPositionSetting(GlobalSettingsKeys.RadioHeight, 0);
        settings.SetPositionSetting(GlobalSettingsKeys.RadioOpacity, 1.0);
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void RadioRefresh(object sender, EventArgs eventArgs)
    {
        UpdateRadioLayout();

        foreach (var radio in _radioControls)
        {
            if (radio.Visibility != Visibility.Visible) continue;

            radio.RepaintRadioStatus();
            radio.RepaintRadioReceive();
        }

        ConnectionIndicator.Fill = _clientStateSingleton.IsConnected ? ConnectedBrush : DisconnectedBrush;

        HandleGlobalSimultaneousTransmissionButton();
    }

    #region Layout and size

    /// <summary>Shows the switched-on radios (or the hint) and fits the window when that set changes.</summary>
    private void UpdateRadioLayout()
    {
        var signature = 0;

        if (RadioHelper.RadiosAvailable())
        {
            var radios = _clientStateSingleton.PlayerRadioInfo.radios;

            for (var i = 0; i < _radioControls.Length; i++)
            {
                var radioId = _radioControls[i].RadioId;
                if (radioId > 0 && radioId < radios.Length && radios[radioId] != null && radios[radioId].IsEnabled)
                    signature |= 1 << radioId;
            }
        }

        if (signature == _layoutSignature) return;

        var previousScale = CurrentScale();
        _layoutSignature = signature;

        var visibleCount = 0;
        foreach (var control in _radioControls)
        {
            var visible = (signature & (1 << control.RadioId)) != 0;
            control.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) visibleCount++;
        }

        if (visibleCount == 0)
        {
            RadioGrid.Visibility = Visibility.Collapsed;
            NoRadiosPanel.Visibility = Visibility.Visible;
        }
        else
        {
            // up to five radios in one row, more in two rows
            var rows = visibleCount <= MaxColumns ? 1 : 2;
            var columns = (int)Math.Ceiling(visibleCount / (double)rows);

            RadioGrid.Rows = rows;
            RadioGrid.Columns = columns;
            RadioGrid.Visibility = Visibility.Visible;
            NoRadiosPanel.Visibility = Visibility.Collapsed;
        }

        // natural size of the content - the parents cache their measure, so invalidate the chain first
        for (var element = (UIElement)RadioGrid; element != null; element = VisualTreeHelper.GetParent(element) as UIElement)
        {
            element.InvalidateMeasure();
            if (ReferenceEquals(element, ContainerPanel)) break;
        }

        ContainerPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var natural = ContainerPanel.DesiredSize;

        if (natural.Width < 1 || natural.Height < 1) return;

        double scale;
        if (!_sizeRestored && visibleCount > 0)
        {
            // first real layout: use the size saved last time
            scale = SavedScale(natural);
            _sizeRestored = true;
        }
        else
        {
            scale = previousScale;
        }

        ApplyNaturalSize(natural, scale);
    }

    /// <summary>Current scale of the content (1 = natural size).</summary>
    private double CurrentScale()
    {
        if (_naturalSize.IsEmpty || _naturalSize.Width < 1) return 1.0;

        var width = double.IsFinite(Width) ? Width : ActualWidth;
        if (!double.IsFinite(width) || width <= 0) return 1.0;

        return Math.Max(1.0, width / _naturalSize.Width);
    }

    /// <summary>
    ///     Scale that fits the saved panel size (RadioWidth / RadioHeight) to <paramref name="natural" />. The smaller
    ///     of both ratios is used, so a layout with a different number of radios is never scaled up beyond the saved
    ///     size.
    /// </summary>
    private double SavedScale(Size natural)
    {
        var savedWidth = _globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioWidth).DoubleValue;
        var savedHeight = _globalSettings.GetPositionSetting(GlobalSettingsKeys.RadioHeight).DoubleValue;

        if (!double.IsFinite(savedWidth) || !double.IsFinite(savedHeight) || savedWidth <= 0 || savedHeight <= 0)
            return 1.0;

        var scale = Math.Min(savedWidth / natural.Width, savedHeight / natural.Height);

        return double.IsFinite(scale) ? Math.Clamp(scale, 1.0, MaxRestoredScale) : 1.0;
    }

    private void ApplyNaturalSize(Size natural, double scale)
    {
        _naturalSize = natural;
        _aspectRatio = natural.Width / natural.Height;

        _adjustingSize = true;
        try
        {
            // the minimum is the natural size (scale 1)
            MinWidth = 0;
            MinHeight = 0;
            Width = natural.Width * scale;
            Height = natural.Height * scale;
            MinWidth = natural.Width;
            MinHeight = natural.Height;
        }
        finally
        {
            _adjustingSize = false;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (_adjustingSize || _naturalSize.IsEmpty) return;

        // the user resizes with the grip: keep the aspect ratio of the content
        _adjustingSize = true;
        try
        {
            if (sizeInfo.WidthChanged)
                Height = sizeInfo.NewSize.Width / _aspectRatio;
            else
                Width = sizeInfo.NewSize.Height * _aspectRatio;
        }
        finally
        {
            _adjustingSize = false;
        }
    }

    #endregion

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
            _globalSettings.SetPositionSetting(GlobalSettingsKeys.RadioOpacity, Opacity);

            // the size of the hint (no radios yet) says nothing about the size the user wants
            if (_sizeRestored)
            {
                _globalSettings.SetPositionSetting(GlobalSettingsKeys.RadioWidth, Width);
                _globalSettings.SetPositionSetting(GlobalSettingsKeys.RadioHeight, Height);
            }
        }

        base.OnClosing(e);

        _updateTimer.Stop();

        EventBus.Instance.Unsubscribe(this);
    }

    private void Button_Minimise(object sender, RoutedEventArgs e)
    {
        // Minimising a window without a taskbar icon leaves a small part of the window at the bottom of the screen,
        // so the panel is closed instead (like the toggle).
        if (_globalSettings.GetClientSettingBool(GlobalSettingsKeys.RadioOverlayTaskbarHide))
            Close();
        else
            WindowState = WindowState.Minimized;
    }

    private void Button_Close(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void WindowOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Opacity = e.NewValue;
    }

    private void HandleGlobalSimultaneousTransmissionButton()
    {
        var playerRadioInfo = _clientStateSingleton.PlayerRadioInfo;

        if (!RadioHelper.RadiosAvailable())
        {
            ToggleGlobalSimultaneousTransmissionButton.IsEnabled = false;
            ToggleGlobalSimultaneousTransmissionButton.Foreground = WhiteBrush;
            ToggleGlobalSimultaneousTransmissionButton.Content = Properties.Resources.OverlaySimulTransOFF;
            return;
        }

        ToggleGlobalSimultaneousTransmissionButton.IsEnabled = true;

        ToggleGlobalSimultaneousTransmissionButton.Content =
            playerRadioInfo.simultaneousTransmission
                ? Properties.Resources.OverlaySimulTransON
                : Properties.Resources.OverlaySimulTransOFF;
        ToggleGlobalSimultaneousTransmissionButton.Foreground =
            playerRadioInfo.simultaneousTransmission ? OrangeBrush : WhiteBrush;

        if (!playerRadioInfo.simultaneousTransmission)
            foreach (var radio in _radioControls)
                radio.ToggleSimultaneousTransmissionButton.Foreground = WhiteBrush;
    }

    private void ToggleGlobalSimultaneousTransmissionButton_Click(object sender, RoutedEventArgs e)
    {
        RadioHelper.ToggleGlobalSimultaneousTransmission();
    }
}
