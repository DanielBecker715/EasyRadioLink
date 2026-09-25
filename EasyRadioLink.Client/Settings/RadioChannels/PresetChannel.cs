namespace EasyRadioLink.Client.Settings.RadioChannels;

public class PresetChannel
{
    public string Text { get; set; }

    // frequency in Hz (double)
    public object Value { get; set; }

    // 1-based channel number
    public int Channel { get; set; }

    public override string ToString()
    {
        return Text;
    }
}
