using System.Windows.Controls;

namespace EasyRadioLink.Client.UI.ClientWindow.RadioPanel.PresetChannels;

/// <summary>
///     Preset channel selector of one radio (channel tab of the radio panel). The DataContext is the radio's
///     <see cref="PresetChannelsViewModel" />.
/// </summary>
public partial class PresetChannelsView : UserControl
{
    public PresetChannelsView()
    {
        InitializeComponent();
    }
}
