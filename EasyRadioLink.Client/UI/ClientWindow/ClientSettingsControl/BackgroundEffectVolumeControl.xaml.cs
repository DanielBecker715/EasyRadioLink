using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace EasyRadioLink.Client.UI.ClientWindow.ClientSettingsControl;

public partial class BackgroundEffectVolumeControl : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty VolumeSliderDependencyProperty =
        DependencyProperty.Register("VolumeValue", typeof(float), typeof(BackgroundEffectVolumeControl),
            new FrameworkPropertyMetadata((float)0)
        );

    public static readonly DependencyProperty MaximumPercentageProperty =
        DependencyProperty.Register(
            nameof(MaximumPercentage),
            typeof(float),
            typeof(BackgroundEffectVolumeControl),
            new PropertyMetadata(200f, MaximumPercentageChanged));

    /// <summary>Distance of the ticks the slider snaps to, in percent (default 10).</summary>
    public static readonly DependencyProperty TickStepProperty =
        DependencyProperty.Register(
            nameof(TickStep),
            typeof(double),
            typeof(BackgroundEffectVolumeControl),
            new PropertyMetadata(10.0));

    public BackgroundEffectVolumeControl()
    {
        InitializeComponent();
    }

    public float VolumeValue
    {
        set => SetValue(VolumeSliderDependencyProperty, value);
        get => (float)GetValue(VolumeSliderDependencyProperty);
    }

    public float MaximumPercentage
    {
        get => (float)GetValue(MaximumPercentageProperty);
        set => SetValue(MaximumPercentageProperty, value);
    }

    public float HalfMaximumPercentage => MaximumPercentage / 2f;

    public double TickStep
    {
        get => (double)GetValue(TickStepProperty);
        set => SetValue(TickStepProperty, value);
    }

    private static void MaximumPercentageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (BackgroundEffectVolumeControl)d;
        control.OnPropertyChanged(nameof(HalfMaximumPercentage));
    }

    // INotifyPropertyChanged implementation
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}