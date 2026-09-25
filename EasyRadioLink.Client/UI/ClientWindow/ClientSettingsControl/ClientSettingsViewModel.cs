using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Caliburn.Micro;
using EasyRadioLink.Client.Properties;
using EasyRadioLink.Client.UI.ClientWindow.RadioPanel;
using EasyRadioLink.Client.UI.ClientWindow.RadioPanel.PresetChannels;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Audio.Recording;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Client.UI.ClientWindow.ClientSettingsControl;

/// <summary>
///     Settings tab: application settings (global.cfg) and the settings of the current profile (radio effects,
///     background sound, push-to-talk, audio channels).
/// </summary>
public class ClientSettingsViewModel : PropertyChangedBaseClass
{
    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;
    private readonly Logger Logger = LogManager.GetCurrentClassLogger();


    public ClientSettingsViewModel()
    {
        ResetOverlayCommand = new DelegateCommand(() =>
        {
            // an open radio panel closes without saving, so it can't overwrite the defaults below
            EventBus.Instance.PublishOnUIThreadAsync(new ResetRadioPanelMessage());

            RadioPanelWindow.ResetSavedPlacement(_globalSettings);
        });

        CreateProfileCommand = new DelegateCommand(() =>
        {
            var inputProfileWindow = new InputProfileWindow.InputProfileWindow(name =>
            {
                if (name.Trim().Length > 0)
                {
                    _globalSettings.ProfileSettingsStore.AddNewProfile(name);

                    NotifyPropertyChanged(nameof(AvailableProfiles));
                    ReloadSettings();
                }
            });
            inputProfileWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            inputProfileWindow.Owner = Application.Current.MainWindow;
            inputProfileWindow.ShowDialog();
        });

        CopyProfileCommand = new DelegateCommand(() =>
        {
            var current = _globalSettings.ProfileSettingsStore.CurrentProfileName;
            var inputProfileWindow = new InputProfileWindow.InputProfileWindow(name =>
            {
                if (name.Trim().Length > 0)
                {
                    _globalSettings.ProfileSettingsStore.CopyProfile(current, name);
                    NotifyPropertyChanged(nameof(AvailableProfiles));
                    ReloadSettings();
                }
            });
            inputProfileWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            inputProfileWindow.Owner = Application.Current.MainWindow;
            inputProfileWindow.ShowDialog();
        });

        RenameProfileCommand = new DelegateCommand(() =>
        {
            var current = _globalSettings.ProfileSettingsStore.CurrentProfileName;
            if (current.Equals("default"))
            {
                MessageBox.Show(Application.Current.MainWindow,
                    Resources.MsgBoxErrorRenameText,
                    Resources.MsgBoxError,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            else
            {
                var oldName = current;
                var inputProfileWindow = new InputProfileWindow.InputProfileWindow(name =>
                {
                    if (name.Trim().Length > 0)
                    {
                        _globalSettings.ProfileSettingsStore.RenameProfile(oldName, name);
                        SelectedProfile = _globalSettings.ProfileSettingsStore.CurrentProfileName;
                        NotifyPropertyChanged(nameof(AvailableProfiles));
                        ReloadSettings();
                    }
                }, true, oldName);
                inputProfileWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                inputProfileWindow.Owner = Application.Current.MainWindow;
                inputProfileWindow.ShowDialog();
            }
        });

        DeleteProfileCommand = new DelegateCommand(() =>
        {
            var current = _globalSettings.ProfileSettingsStore.CurrentProfileName;

            if (current.Equals("default"))
            {
                MessageBox.Show(Application.Current.MainWindow,
                    Resources.MsgBoxErrorInputText,
                    Resources.MsgBoxError,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            else
            {
                var result = MessageBox.Show(Application.Current.MainWindow,
                    string.Format(Resources.MsgBoxConfirmDeleteText, current),
                    Resources.MsgBoxConfirm,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _globalSettings.ProfileSettingsStore.RemoveProfile(current);
                    SelectedProfile = _globalSettings.ProfileSettingsStore.CurrentProfileName;
                    NotifyPropertyChanged(nameof(AvailableProfiles));
                    ReloadSettings();
                }
            }
        });

    }

    public ICommand ResetOverlayCommand { get; set; }

    public ICommand CreateProfileCommand { get; set; }
    public ICommand CopyProfileCommand { get; set; }
    public ICommand RenameProfileCommand { get; set; }
    public ICommand DeleteProfileCommand { get; set; }

    /**
         * Global Settings
         */

    public bool ExpandInputDevices
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.ExpandControls);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.ExpandControls, value);
            NotifyPropertyChanged();
            MessageBox.Show(
                Resources.MsgBoxRestartExpandText,
                Resources.MsgBoxRestart, MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public bool RadioOverlayTaskbarItem
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.RadioOverlayTaskbarHide);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.RadioOverlayTaskbarHide, value);
            NotifyPropertyChanged();
        }
    }

    public bool ShowTransmitterName
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.ShowTransmitterName);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.ShowTransmitterName, value);
            NotifyPropertyChanged();
        }
    }

    public bool DisallowedAudioTone
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.DisallowedAudioTone);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.DisallowedAudioTone, value);
            NotifyPropertyChanged();
        }
    }


    /// <summary>
    ///     Choices for <see cref="SelectedServerPresetConfiguration" />: Key = <see cref="ServerPresetConfiguration" />
    ///     name (stored in the profile), Value = display text.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ServerPresetConfigurations { get; } =
    [
        new(nameof(ServerPresetConfiguration.USE_CLIENT_AND_SERVER_IF_SET), Resources.PresetsUseClientAndServer),
        new(nameof(ServerPresetConfiguration.USE_SERVER_ONLY_IF_SET), Resources.PresetsUseServerIfSet),
        new(nameof(ServerPresetConfiguration.USE_CLIENT_ONLY), Resources.PresetsUseClientOnly)
    ];

    public string SelectedServerPresetConfiguration
    {
        set
        {
            GlobalSettingsStore.Instance.ProfileSettingsStore.SetClientSettingString(
                ProfileSettingsKeys.ServerPresetSelection, value);
            NotifyPropertyChanged();
            EventBus.Instance.PublishOnUIThreadAsync(new ServerSettingsPresetsSettingChangedMessage());
        }
        get =>
            GlobalSettingsStore.Instance.ProfileSettingsStore.GetClientSettingString(ProfileSettingsKeys
                .ServerPresetSelection);
    }

    /// <summary>Use the server's radio layout if the server provides one (applied on the next connect).</summary>
    public bool ServerRadioPresetEnabled
    {
        get => GlobalSettingsStore.Instance.ProfileSettingsStore.GetClientSettingBool(
            ProfileSettingsKeys.AllowServerRadioPreset);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.AllowServerRadioPreset,
                value);
            NotifyPropertyChanged();
        }
    }

    /// <summary>Open the radio panel automatically after connecting.</summary>
    public bool AutoOpenRadioPanel
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.AutoOpenRadioPanel);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.AutoOpenRadioPanel, value);
            NotifyPropertyChanged();
        }
    }
    
    
    public bool VOXEnabled
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.VOX);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.VOX, value);
            NotifyPropertyChanged();
        }
    }

    public int VOXMinimimumTXTime
    {
        get => _globalSettings.GetClientSettingInt(GlobalSettingsKeys.VOXMinimumTime);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.VOXMinimumTime, value);
            NotifyPropertyChanged();
        }
    }


    public int VOXMode
    {
        get => _globalSettings.GetClientSettingInt(GlobalSettingsKeys.VOXMode);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.VOXMode, value);
            NotifyPropertyChanged();
        }
    }

    public double VOXMinimumRMS
    {
        get => _globalSettings.GetClientSettingDouble(GlobalSettingsKeys.VOXMinimumDB);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.VOXMinimumDB, value);
            NotifyPropertyChanged();
        }
    }

    public bool AllowTransmissionsRecording
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.AllowRecording);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.AllowRecording, value);
            NotifyPropertyChanged();
        }
    }

    public bool RecordTransmissions
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.RecordAudio);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.RecordAudio, value);
            NotifyPropertyChanged();
        }
    }

    public int RecordingQuality
    {
        get => int.Parse(_globalSettings.GetClientSetting(GlobalSettingsKeys.RecordingQuality).StringValue.TrimStart('V'));
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.RecordingQuality, $"V{value}");
            NotifyPropertyChanged();
        }
    }

    public bool SingleFileMixdown
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.SingleFileMixdown);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.SingleFileMixdown, value);
            NotifyPropertyChanged();
        }
    }

    public IReadOnlyList<string> RecordingFormats => AudioRecordingManager.Instance.AvailableFormats;

    public string SelectedRecordingFormat
    {
        get => _globalSettings.GetClientSetting(GlobalSettingsKeys.RecordingFormat).StringValue;
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.RecordingFormat, value);
            NotifyPropertyChanged();
        }
    }

    public bool RequireAdminToggle
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.RequireAdmin);
        set
        {
            if (value)
            {
                var choice = MessageBox.Show(Application.Current.MainWindow,
                Resources.MsgBoxAdminText,
                Resources.MsgBoxAdmin, MessageBoxButton.YesNo, MessageBoxImage.Stop);

                if (choice != MessageBoxResult.Yes)
                {
                    value = false;
                }
            }
            _globalSettings.SetClientSetting(GlobalSettingsKeys.RequireAdmin, value);
            NotifyPropertyChanged();
        }
    }

    public bool AllowXInputController
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.AllowXInputController);
        set
        {
            MessageBox.Show(
                Resources.MsgBoxRestartXInputText,
                Resources.MsgBoxRestart, MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _globalSettings.SetClientSetting(GlobalSettingsKeys.AllowXInputController, value);
            NotifyPropertyChanged();
        }
    }

    public bool MinimiseToTray
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.MinimiseToTray);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.MinimiseToTray, value);
            NotifyPropertyChanged();
        }
    }

    public bool StartMinimised
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.StartMinimised);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.StartMinimised, value);
            NotifyPropertyChanged();
        }
    }


    public bool MicDenoise
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.Denoise);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.Denoise, value);
            NotifyPropertyChanged();
        }
    }

    #region Automatic Gain Control (Incoming Audio)
    public bool IncomingAudioAGC
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.IncomingAudioAGC);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.IncomingAudioAGC, value);
            NotifyPropertyChanged();
        }
    }

    public int IncomingAudioAGCMaxDB
    {
        get => _globalSettings.GetClientSettingInt(GlobalSettingsKeys.IncomingAudioAGCLevelMax);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.IncomingAudioAGCLevelMax, value);
            NotifyPropertyChanged();
        }
    }

    public int IncomingAudioAGCTarget
    {
        get => _globalSettings.GetClientSettingInt(GlobalSettingsKeys.IncomingAudioAGCTarget);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.IncomingAudioAGCTarget, value);
            NotifyPropertyChanged();
        }
    }

    public bool IncomingAudioDenoise
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.IncomingAudioDenoise);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.IncomingAudioDenoise, value);
            NotifyPropertyChanged();
        }
    }
#endregion Automatic Gain Control (Incoming Audio)

    public bool PlayConnectionSounds
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.PlayConnectionSounds);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.PlayConnectionSounds, value);
            NotifyPropertyChanged();
        }
    }

    /**
         * Profile Settings
         */

    public bool RadioSwitchIsPTT
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.RadioSwitchIsPTT);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.RadioSwitchIsPTT, value);
            NotifyPropertyChanged();
        }
    }

    public bool AutoSelectChannel
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(
            ProfileSettingsKeys.AutoSelectPresetChannel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.AutoSelectPresetChannel,
                value);
            NotifyPropertyChanged();
        }
    }

    public float PTTReleaseDelay
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.PTTReleaseDelay);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.PTTReleaseDelay, value);
            NotifyPropertyChanged();
        }
    }

    public float PTTStartDelay
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.PTTStartDelay);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.PTTStartDelay, value);
            NotifyPropertyChanged();
        }
    }

    public bool RadioRxStart
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.RadioRxEffects_Start);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.RadioRxEffects_Start,
                value);
            NotifyPropertyChanged();
        }
    }

    public bool RadioRxEnd
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.RadioRxEffects_End);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.RadioRxEffects_End,
                value);
            NotifyPropertyChanged();
        }
    }

    public bool RadioTxStart
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.RadioTxEffects_Start);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.RadioTxEffects_Start,
                value);
            NotifyPropertyChanged();
        }
    }

    public bool RadioTxEnd
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.RadioTxEffects_End);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.RadioTxEffects_End,
                value);
            NotifyPropertyChanged();
        }
    }

    public bool AllowRotaryIncrement
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(
            ProfileSettingsKeys.RotaryStyleIncrement);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.RotaryStyleIncrement,
                value);
            NotifyPropertyChanged();
        }
    }

    public bool RadioEncryptionEffectsToggle
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(
            ProfileSettingsKeys.RadioEncryptionEffects);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.RadioEncryptionEffects,
                value);
            NotifyPropertyChanged();
        }
    }


    /// <summary>Listener: play the background sounds of other users.</summary>
    public bool AmbientEffectToggle
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(
            ProfileSettingsKeys.BackgroundSoundEffect);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.BackgroundSoundEffect,
                value);
            NotifyPropertyChanged();
        }
    }


    public float AmbientEffectVolume
    {
        get => (float)((_globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys
                            .BackgroundSoundEffectVolume)
                        / double.Parse(
                            ProfileSettingsStore.DefaultSettingsProfileSettings[
                                ProfileSettingsKeys.BackgroundSoundEffectVolume.ToString()],
                            CultureInfo.InvariantCulture)) * 100.0f);
        set
        {
            var orig = double.Parse(
                ProfileSettingsStore.DefaultSettingsProfileSettings[
                    ProfileSettingsKeys.BackgroundSoundEffectVolume.ToString()], CultureInfo.InvariantCulture);

            var vol = orig * (value / 100.0f);

            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(
                ProfileSettingsKeys.BackgroundSoundEffectVolume, (float)vol);
            NotifyPropertyChanged();
        }
    }

    /// <summary>Short noise burst when a received AM/FM transmission ends ("CB feel").</summary>
    public bool RadioRxSquelchTail
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.RadioRxSquelchTail);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.RadioRxSquelchTail, value);
            NotifyPropertyChanged();
        }
    }

    /// <summary>
    ///     Choices for <see cref="BackgroundSound" />: Key = sound name ("" = none), Value = display text.
    ///     Bind with <c>DisplayMemberPath="Value" SelectedValuePath="Key" SelectedValue="{Binding BackgroundSound}"</c>.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> BackgroundSoundOptions
    {
        get
        {
            var options = new List<KeyValuePair<string, string>> { new("", "None") };
            foreach (var sound in CachedAudioEffectProvider.Instance.AvailableBackgroundSounds)
                options.Add(new KeyValuePair<string, string>(sound,
                    sound.Length > 0 ? char.ToUpperInvariant(sound[0]) + sound.Substring(1) : sound));

            return options;
        }
    }

    /// <summary>Sender: background sound mixed into your own transmissions ("" = none, "jet", "prop", ...).</summary>
    public string BackgroundSound
    {
        get => CachedAudioEffectProvider.NormaliseBackgroundName(
            _globalSettings.ProfileSettingsStore.GetClientSettingString(ProfileSettingsKeys.BackgroundSound));
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingString(ProfileSettingsKeys.BackgroundSound,
                CachedAudioEffectProvider.NormaliseBackgroundName(value));
            NotifyPropertyChanged();
        }
    }

    /// <summary>Sender: volume of the own background sound in percent (0..100).</summary>
    public float BackgroundSoundVolume
    {
        get => Math.Clamp(
            _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.BackgroundSoundVolume),
            0f, 1f) * 100f;
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.BackgroundSoundVolume,
                Math.Clamp(value, 0f, 100f) / 100f);
            NotifyPropertyChanged();
        }
    }
/***
 *
 */
    public List<CachedAudioEffect> RadioTransmissionStart =>
        CachedAudioEffectProvider.Instance.RadioTransmissionStart;

    public CachedAudioEffect SelectedRadioTransmissionStartEffect
    {
        set
        {
            GlobalSettingsStore.Instance.ProfileSettingsStore.SetClientSettingString(
                ProfileSettingsKeys.RadioTransmissionStartSelection, value.FileName);
            NotifyPropertyChanged();
        }
        get => CachedAudioEffectProvider.Instance.SelectedRadioTransmissionStartEffect;
    }

    public List<CachedAudioEffect> RadioTransmissionEnd => CachedAudioEffectProvider.Instance.RadioTransmissionEnd;

    public CachedAudioEffect SelectedRadioTransmissionEndEffect
    {
        set
        {
            GlobalSettingsStore.Instance.ProfileSettingsStore.SetClientSettingString(
                ProfileSettingsKeys.RadioTransmissionEndSelection, value.FileName);
            NotifyPropertyChanged();
        }
        get => CachedAudioEffectProvider.Instance.SelectedRadioTransmissionEndEffect;
    }
/***
 *
 */

/***
 *
 */
    // Add the new float property for the slider (0-100%)
    public float RadioSoundEffectsRatio
    {
        get
        {
            float value = _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.RadioEffectsRatio);
            value = Math.Clamp(value, 0f, 1f);
            return value * 100f; // 0.0–1.0 → 0–100%
        }
        set
        {
            float clamped = Math.Clamp(value, 0f, 100f) / 100f; // 0–100% → 0.0–1.0
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.RadioEffectsRatio, clamped);
            NotifyPropertyChanged();
        }
    }

    public bool RadioSoundEffectsClipping
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.RadioEffectsClipping);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.RadioEffectsClipping,
                value);
            NotifyPropertyChanged();
        }
    }

    public bool NATORadioToneToggle
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.NATOTone);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.NATOTone, value);
            NotifyPropertyChanged();
        }
    }

    public double NATORadioToneVolume
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.NATOToneVolume)
            / double.Parse(
                ProfileSettingsStore.DefaultSettingsProfileSettings[ProfileSettingsKeys.NATOToneVolume.ToString()],
                CultureInfo.InvariantCulture) * 100.0f;
        set
        {
            var orig = double.Parse(
                ProfileSettingsStore.DefaultSettingsProfileSettings[ProfileSettingsKeys.NATOToneVolume.ToString()],
                CultureInfo.InvariantCulture);
            var vol = orig * (value / 100.0f);

            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.NATOToneVolume,
                (float)vol);
            NotifyPropertyChanged();
        }
    }

    public bool BackgroundRadioNoiseToggle
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys
            .RadioBackgroundNoiseEffect);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(
                ProfileSettingsKeys.RadioBackgroundNoiseEffect, value);
            NotifyPropertyChanged();
        }
    }

    public bool PerRadioModelEffects
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.PerRadioModelEffects);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingBool(ProfileSettingsKeys.PerRadioModelEffects, value);
            NotifyPropertyChanged();
        }
    }

    public float NoiseGainDB
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.NoiseGainDB);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.NoiseGainDB, value);
            NotifyPropertyChanged();
        }
    }

    public float HFNoiseGainDB
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.HFNoiseGainDB);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.HFNoiseGainDB, value);
            NotifyPropertyChanged();
        }
    }


    /**
         * Radio Audio Balance
         */

    public float RadioChannel1
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio1Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio1Channel, value);
            NotifyPropertyChanged();
        }
    }

    public float RadioChannel2
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio2Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio2Channel, value);
            NotifyPropertyChanged();
        }
    }

    public float RadioChannel3
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio3Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio3Channel, value);
            NotifyPropertyChanged();
        }
    }

    public float RadioChannel4
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio4Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio4Channel, value);
            NotifyPropertyChanged();
        }
    }

    public float RadioChannel5
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio5Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio5Channel, value);
            NotifyPropertyChanged();
        }
    }

    public float RadioChannel6
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio6Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio6Channel, value);
            NotifyPropertyChanged();
        }
    }

    public float RadioChannel7
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio7Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio7Channel, value);
            NotifyPropertyChanged();
        }
    }

    public float RadioChannel8
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio8Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio8Channel, value);
            NotifyPropertyChanged();
        }
    }

    public float RadioChannel9
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio9Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio9Channel, value);
            NotifyPropertyChanged();
        }
    }

    public float RadioChannel10
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio10Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio10Channel, value);
            NotifyPropertyChanged();
        }
    }

    public string SelectedProfile
    {
        set
        {
            if (value != null)
            {
                _globalSettings.ProfileSettingsStore.CurrentProfileName = value;
                ReloadSettings();
            }

            NotifyPropertyChanged();
        }
        get => _globalSettings.ProfileSettingsStore.CurrentProfileName;
    }

    public List<string> AvailableProfiles
    {
        set
        {
            //do nothing
        }
        get => _globalSettings.ProfileSettingsStore.ProfileNames;
    }



    private void ReloadSettings()
    {
        //ResetOverlayCommand
        NotifyPropertyChanged(nameof(RadioOverlayTaskbarItem));

        NotifyPropertyChanged(nameof(MinimiseToTray));
        NotifyPropertyChanged(nameof(StartMinimised));
        NotifyPropertyChanged(nameof(ShowTransmitterName));
        
        NotifyPropertyChanged(nameof(MicDenoise));

        NotifyPropertyChanged(nameof(VOXEnabled));
        NotifyPropertyChanged(nameof(VOXMinimimumTXTime));
        NotifyPropertyChanged(nameof(VOXMode));
        NotifyPropertyChanged(nameof(VOXMinimumRMS));

        NotifyPropertyChanged(nameof(AllowTransmissionsRecording));
        NotifyPropertyChanged(nameof(RecordTransmissions));
        NotifyPropertyChanged(nameof(SingleFileMixdown));
        NotifyPropertyChanged(nameof(RecordingQuality));

        NotifyPropertyChanged(nameof(RequireAdminToggle));
        NotifyPropertyChanged(nameof(ExpandInputDevices));
        NotifyPropertyChanged(nameof(AllowXInputController));
        NotifyPropertyChanged(nameof(PlayConnectionSounds));
        //TODO handle Profile list??

        NotifyPropertyChanged(nameof(RadioSwitchIsPTT));
        NotifyPropertyChanged(nameof(AutoSelectChannel));
        NotifyPropertyChanged(nameof(AllowRotaryIncrement));
        NotifyPropertyChanged(nameof(PTTReleaseDelay));
        NotifyPropertyChanged(nameof(PTTStartDelay));
        NotifyPropertyChanged(nameof(RadioRxStart));
        NotifyPropertyChanged(nameof(RadioRxEnd));
        NotifyPropertyChanged(nameof(RadioTxStart));
        NotifyPropertyChanged(nameof(RadioTxEnd));
        NotifyPropertyChanged(nameof(SelectedRadioTransmissionStartEffect));
        NotifyPropertyChanged(nameof(SelectedRadioTransmissionEndEffect));
        NotifyPropertyChanged(nameof(RadioEncryptionEffectsToggle));
        NotifyPropertyChanged(nameof(RadioSoundEffectsRatio));
        NotifyPropertyChanged(nameof(RadioSoundEffectsClipping));
        NotifyPropertyChanged(nameof(NATORadioToneToggle));
        NotifyPropertyChanged(nameof(NATORadioToneVolume));
        NotifyPropertyChanged(nameof(BackgroundRadioNoiseToggle));
        NotifyPropertyChanged(nameof(NoiseGainDB));
        NotifyPropertyChanged(nameof(HFNoiseGainDB));

        NotifyPropertyChanged(nameof(AmbientEffectToggle));
        NotifyPropertyChanged(nameof(AmbientEffectVolume));
        NotifyPropertyChanged(nameof(RadioRxSquelchTail));
        NotifyPropertyChanged(nameof(BackgroundSound));
        NotifyPropertyChanged(nameof(BackgroundSoundVolume));
        NotifyPropertyChanged(nameof(ServerRadioPresetEnabled));
        NotifyPropertyChanged(nameof(SelectedServerPresetConfiguration));

        NotifyPropertyChanged(nameof(RadioChannel1));
        NotifyPropertyChanged(nameof(RadioChannel2));
        NotifyPropertyChanged(nameof(RadioChannel3));
        NotifyPropertyChanged(nameof(RadioChannel4));
        NotifyPropertyChanged(nameof(RadioChannel5));
        NotifyPropertyChanged(nameof(RadioChannel6));
        NotifyPropertyChanged(nameof(RadioChannel7));
        NotifyPropertyChanged(nameof(RadioChannel8));
        NotifyPropertyChanged(nameof(RadioChannel9));
        NotifyPropertyChanged(nameof(RadioChannel10));
        
        NotifyPropertyChanged(nameof(ServerPresetConfigurations));

        //TODO send message to tell input to reload!
        //TODO pick up in inputhandler that settings have changed?
        EventBus.Instance.PublishOnUIThreadAsync(new ProfileChangedMessage());
    }
}