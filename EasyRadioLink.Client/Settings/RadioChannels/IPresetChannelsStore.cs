using System.Collections.Generic;

namespace EasyRadioLink.Client.Settings.RadioChannels;

public interface IPresetChannelsStore
{
    /// <summary>Preset channels of a radio (<c>&lt;normalised radio name&gt;.txt</c> in the presets folder).</summary>
    IEnumerable<PresetChannel> LoadFromStore(string radioName);

    /// <summary>Creates an empty preset file for the radio (or returns the existing one); null on error.</summary>
    string CreatePresetFile(string radioName);
}
