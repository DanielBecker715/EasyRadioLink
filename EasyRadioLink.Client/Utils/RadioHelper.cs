using System;
using System.Linq;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Client.Settings.RadioChannels;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;

namespace EasyRadioLink.Client.Utils;

/// <summary>
///     All user actions on the local radios (radio panel, hotkeys). Every method is a no-op while
///     <see cref="RadiosAvailable" /> is false. Network relevant changes mark the state dirty
///     (<c>ClientStateSingleton.LastSent = 0</c>) so the next sync tick sends a RADIO_UPDATE.
/// </summary>
public static class RadioHelper
{
    /// <summary>
    ///     THE availability predicate: connected and the radios are loaded for this connection.
    /// </summary>
    public static bool RadiosAvailable()
    {
        var clientState = ClientStateSingleton.Instance;
        return clientState.IsConnected && clientState.PlayerRadioInfo.IsActive;
    }

    /// <summary>The radio in slot <paramref name="radio" /> (0..10) or null if radios are not available.</summary>
    public static Radio GetRadio(int radio)
    {
        var playerRadioInfo = ClientStateSingleton.Instance.PlayerRadioInfo;

        if (RadiosAvailable() && radio >= 0 && radio < playerRadioInfo.radios.Length)
            return playerRadioInfo.radios[radio];

        return null;
    }

    /// <summary>A user radio (1..10) that is switched on, or null.</summary>
    private static Radio GetUserRadio(int radioId)
    {
        if (radioId < PlayerRadioInfo.FirstUserRadio) return null;

        var radio = GetRadio(radioId);
        return radio != null && radio.IsEnabled ? radio : null;
    }

    private static void MarkDirty()
    {
        //make radio data stale to force resync
        ClientStateSingleton.Instance.LastSent = 0;
    }

    public static void ToggleGuard(int radioId)
    {
        var radio = GetUserRadio(radioId);

        if (radio != null && radio.HasGuard)
        {
            radio.guardEnabled = !radio.guardEnabled;
            MarkDirty();
        }
    }

    public static void SetGuard(int radioId, bool enabled)
    {
        var radio = GetUserRadio(radioId);

        if (radio != null && radio.HasGuard && radio.guardEnabled != enabled)
        {
            radio.guardEnabled = enabled;
            MarkDirty();
        }
    }

    /// <summary>
    ///     Changes the frequency of a radio (clamped to its range) and clears the selected preset channel.
    /// </summary>
    /// <param name="requestedValue">frequency or frequency step</param>
    /// <param name="delta">true: <paramref name="requestedValue" /> is added to the current frequency</param>
    /// <param name="inMHz">true: <paramref name="requestedValue" /> is in MHz, otherwise Hz</param>
    /// <returns>false if the frequency had to be clamped</returns>
    public static bool UpdateRadioFrequency(double requestedValue, int radioId, bool delta = true, bool inMHz = true)
    {
        var inLimit = true;

        if (!double.IsFinite(requestedValue)) return false;

        var frequency = requestedValue;
        if (inMHz) frequency *= RadioCalculator.MHz;

        var radio = GetUserRadio(radioId);

        if (radio != null)
        {
            if (delta)
            {
                if (GlobalSettingsStore.Instance.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys
                        .RotaryStyleIncrement) && frequency != 0)
                {
                    // Easier to simply shift the decimal place value to the ones position for finding numeral at specific position
                    double adjustedFrequency = Math.Abs((int)Math.Round(radio.freq / frequency));

                    var deltaPosition =
                        adjustedFrequency % 10 -
                        adjustedFrequency % 1 /
                        1; // calculate the value of the position where the delta will be applied
                    double rollOverValue = frequency < 0 ? 0 : 9;
                    var futureValue = frequency + radio.freq; // used for checking 10Mhz increments

                    if (Math.Abs(frequency) <= 1000000)
                        frequency = deltaPosition == rollOverValue ? frequency * -9 : frequency;
                    else if (frequency < 0 && radio.freqMin > futureValue)
                        frequency = 0;
                    else if (futureValue > radio.freqMax) frequency = 0;
                }

                radio.freq = Math.Round(radio.freq + frequency);
            }
            else
            {
                radio.freq = Math.Round(frequency);
            }

            //make sure we're not over or under a limit
            if (radio.freq > radio.freqMax)
            {
                inLimit = false;
                radio.freq = radio.freqMax;
            }
            else if (radio.freq < radio.freqMin)
            {
                inLimit = false;
                radio.freq = radio.freqMin;
            }

            //set to no channel
            radio.channel = -1;

            MarkDirty();
        }

        return inLimit;
    }

    public static bool SelectRadio(int radioId)
    {
        var radio = GetUserRadio(radioId);

        if (radio != null)
        {
            ClientStateSingleton.Instance.PlayerRadioInfo.selected = (short)radioId;
            return true;
        }

        return false;
    }

    public static void SelectNextRadio()
    {
        if (!RadiosAvailable()) return;

        var playerRadioInfo = ClientStateSingleton.Instance.PlayerRadioInfo;
        int currentRadio = playerRadioInfo.selected;

        if (currentRadio < PlayerRadioInfo.FirstUserRadio || currentRadio >= playerRadioInfo.radios.Length)
            currentRadio = 0;

        //find next radio
        for (var i = currentRadio + 1; i < playerRadioInfo.radios.Length; i++)
            if (SelectRadio(i))
                return;

        //wrap around up to the current radio
        for (var i = PlayerRadioInfo.FirstUserRadio; i < currentRadio; i++)
            if (SelectRadio(i))
                return;
    }

    public static void SelectPreviousRadio()
    {
        if (!RadiosAvailable()) return;

        var playerRadioInfo = ClientStateSingleton.Instance.PlayerRadioInfo;
        int currentRadio = playerRadioInfo.selected;

        if (currentRadio < PlayerRadioInfo.FirstUserRadio || currentRadio >= playerRadioInfo.radios.Length)
            currentRadio = playerRadioInfo.radios.Length;

        //find previous radio
        for (var i = currentRadio - 1; i >= PlayerRadioInfo.FirstUserRadio; i--)
            if (SelectRadio(i))
                return;

        //wrap around down to the current radio
        for (var i = playerRadioInfo.radios.Length - 1; i > currentRadio; i--)
            if (SelectRadio(i))
                return;
    }

    /// <summary>True if the radio may encrypt: it is encryption capable and the server allows encryption.</summary>
    public static bool IsEncryptionAllowed(Radio radio)
    {
        return radio != null
               && radio.IsEnabled
               && radio.encCapable
               && SyncedServerSettings.Instance.GetSettingAsBool(ServerSettingsKeys.ALLOW_RADIO_ENCRYPTION);
    }

    public static void ToggleEncryption(int radioId)
    {
        var radio = GetUserRadio(radioId);

        if (IsEncryptionAllowed(radio))
        {
            radio.enc = !radio.enc;
            MarkDirty();
        }
    }

    public static void SetEncryptionKey(int radioId, int encKey)
    {
        var radio = GetUserRadio(radioId);

        if (radio != null && radio.encCapable)
        {
            encKey = Math.Clamp(encKey, RadioDefinition.MinEncryptionKey, RadioDefinition.MaxEncryptionKey);

            if (radio.encKey != encKey)
            {
                radio.encKey = (byte)encKey;
                MarkDirty();
            }
        }
    }

    public static void IncreaseEncryptionKey(int radioId)
    {
        var currentRadio = GetUserRadio(radioId);

        if (currentRadio != null) SetEncryptionKey(radioId, currentRadio.encKey + 1);
    }

    public static void DecreaseEncryptionKey(int radioId)
    {
        var currentRadio = GetUserRadio(radioId);

        if (currentRadio != null) SetEncryptionKey(radioId, currentRadio.encKey - 1);
    }

    /// <summary>Sets the radio model (sound character, a <c>RadioModelInfo.Key</c>) of a radio; "" = default model.</summary>
    public static void SetRadioModel(int radioId, string modelKey)
    {
        var radio = GetUserRadio(radioId);

        modelKey = modelKey?.Trim() ?? "";

        if (radio != null && !string.Equals(radio.model, modelKey, StringComparison.Ordinal))
        {
            radio.model = modelKey;
            MarkDirty();
        }
    }

    public static void SelectRadioChannel(PresetChannel selectedPresetChannel, int radioId)
    {
        var radio = GetUserRadio(radioId);

        if (radio != null && selectedPresetChannel?.Value is double frequency && double.IsFinite(frequency))
        {
            radio.freq = Math.Clamp(frequency, radio.freqMin, radio.freqMax);
            radio.channel = selectedPresetChannel.Channel;

            MarkDirty();
        }
    }

    public static void RadioChannelUp(int radioId)
    {
        var currentRadio = GetUserRadio(radioId);
        var radioChannels = GetPresetChannels(radioId);

        if (currentRadio == null || radioChannels == null || radioChannels.PresetChannels.Count == 0) return;

        var count = radioChannels.PresetChannels.Count;
        var next = currentRadio.channel + 1;

        // no channel selected, or past the last one - start again at the first
        var preset = currentRadio.channel < 1 || next > count
            ? radioChannels.PresetChannels[0]
            : radioChannels.PresetChannels[next - 1];

        SelectRadioChannel(preset, radioId);
        radioChannels.SelectedPresetChannel = preset;
    }

    public static void RadioChannelDown(int radioId)
    {
        var currentRadio = GetUserRadio(radioId);
        var radioChannels = GetPresetChannels(radioId);

        if (currentRadio == null || radioChannels == null || radioChannels.PresetChannels.Count == 0) return;

        var count = radioChannels.PresetChannels.Count;
        var previous = currentRadio.channel - 1;

        // no channel selected, or before the first one - wrap to the last
        var preset = previous < 1 || previous > count
            ? radioChannels.PresetChannels.Last()
            : radioChannels.PresetChannels[previous - 1];

        SelectRadioChannel(preset, radioId);
        radioChannels.SelectedPresetChannel = preset;
    }

    private static UI.ClientWindow.RadioPanel.PresetChannels.PresetChannelsViewModel GetPresetChannels(
        int radioId)
    {
        var fixedChannels = ClientStateSingleton.Instance.FixedChannels;
        var index = radioId - PlayerRadioInfo.FirstUserRadio;

        if (fixedChannels == null || index < 0 || index >= fixedChannels.Length) return null;

        return fixedChannels[index];
    }

    /// <summary>Local receive volume 0..1 (not sent to the server).</summary>
    public static void SetRadioVolume(float volume, int radioId)
    {
        if (!float.IsFinite(volume)) return;

        volume = Math.Clamp(volume, 0f, 1f);

        var currentRadio = GetUserRadio(radioId);

        if (currentRadio != null) currentRadio.volume = volume;
    }

    public static void RadioVolumeUp(short radioId)
    {
        var currentRadio = GetUserRadio(radioId);

        if (currentRadio != null) currentRadio.volume = Math.Min(1.0f, currentRadio.volume + 0.1f);
    }

    public static void RadioVolumeDown(short radioId)
    {
        var currentRadio = GetUserRadio(radioId);

        if (currentRadio != null) currentRadio.volume = Math.Max(0f, currentRadio.volume - 0.1f);
    }

    public static void ToggleGlobalSimultaneousTransmission()
    {
        var playerRadioInfo = ClientStateSingleton.Instance.PlayerRadioInfo;
        SetGlobalSimultaneousTransmission(!playerRadioInfo.simultaneousTransmission);
    }

    public static void SetGlobalSimultaneousTransmission(bool enabled)
    {
        if (!RadiosAvailable()) return;

        var playerRadioInfo = ClientStateSingleton.Instance.PlayerRadioInfo;
        playerRadioInfo.simultaneousTransmission = enabled;

        if (!enabled)
            foreach (var radio in playerRadioInfo.radios)
                if (radio != null)
                    radio.simul = false;
    }

    public static void ToggleSimultaneous(int radioId)
    {
        var playerRadioInfo = ClientStateSingleton.Instance.PlayerRadioInfo;
        if (!playerRadioInfo.simultaneousTransmission) return;

        var radio = GetUserRadio(radioId);

        if (radio != null && !radio.rxOnly) radio.simul = !radio.simul;
    }
}
