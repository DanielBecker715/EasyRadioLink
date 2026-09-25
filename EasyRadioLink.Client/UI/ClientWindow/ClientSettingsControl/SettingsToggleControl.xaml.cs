using System.Windows;
using System.Windows.Controls;

namespace EasyRadioLink.Client.UI.ClientWindow.ClientSettingsControl;

/// <summary>
///     On/Off switch of a settings row (MahApps ToggleSwitch) with a bindable <see cref="ToggleValue" />.
/// </summary>
public partial class SettingsToggleControl : UserControl
{
    // "ToggleValue" must match the property name
    public static readonly DependencyProperty ToggleDependencyProperty =
        DependencyProperty.Register("ToggleValue", typeof(bool), typeof(SettingsToggleControl),
            new FrameworkPropertyMetadata(false)
        );

    public SettingsToggleControl()
    {
        InitializeComponent();
    }

    public bool ToggleValue
    {
        set => SetValue(ToggleDependencyProperty, value);
        get => (bool)GetValue(ToggleDependencyProperty);
    }
}
