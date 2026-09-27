using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Caliburn.Micro;
using EasyRadioLink.Client.Audio.Managers;
using EasyRadioLink.Client.Properties;
using EasyRadioLink.Client.UI.ClientWindow.RadioPanel;
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
///     Settings tab: application settings (global.cfg) and the settings of the current profile (radio sounds, radio
///     effects, background sound, push-to-talk, stereo balance). The tab shows the basic settings (radio sounds,
///     general) and an "Advanced settings" section.
/// </summary>
public class ClientSettingsViewModel : PropertyChangedBaseClass
{
    // everything that belongs to the profile - re-read when another profile is selected
    private static readonly string[] ProfileProperties =
    {
        nameof(TxStartSound), nameof(TxEndSound), nameof(RxStartSound), nameof(RxEndSound),
        nameof(RadioRxSquelchTail), nameof(BackgroundRadioNoiseToggle), nameof(NATORadioToneToggle),
        nameof(BackgroundSound), nameof(HasBackgroundSound), nameof(BackgroundSoundVolume), nameof(VoiceBoost),
        nameof(RadioSoundEffectsRatio), nameof(VoiceDistortion), nameof(HasRadioEffects),
        nameof(RadioSoundEffectsClipping), nameof(PerRadioModelEffects), nameof(NoiseGainDB), nameof(HFNoiseGainDB),
        nameof(NATORadioToneVolume), nameof(AmbientEffectToggle), nameof(AmbientEffectVolume), nameof(RadioBalance),
        nameof(AllowRotaryIncrement), nameof(PTTReleaseDelay), nameof(PTTStartDelay),
        nameof(HasSeveralProfiles), nameof(ProfileNotice)
    };

    // application settings (global.cfg) - re-read as well, they may have been changed elsewhere
    private static readonly string[] GlobalProperties =
    {
        nameof(AutoOpenRadioPanel), nameof(ShowTransmitterName), nameof(MinimiseToTray), nameof(StartMinimised),
        nameof(PlayConnectionSounds), nameof(VOXEnabled),
        nameof(MicDenoise), nameof(IncomingAudioAGC), nameof(IncomingAudioAGCMaxDB), nameof(IncomingAudioAGCTarget),
        nameof(IncomingAudioDenoise),
        nameof(VOXMinimimumTXTime), nameof(VOXMode), nameof(VOXMinimumRMS),
        nameof(AllowTransmissionsRecording), nameof(RecordTransmissions), nameof(SelectedRecordingFormat),
        nameof(RecordingQuality), nameof(DisallowedAudioTone),
        nameof(ExpandInputDevices), nameof(AllowXInputController), nameof(RadioPanelTaskbarItem),
        nameof(RequireAdminToggle), nameof(AdvancedSettingsExpanded)
    };

    private readonly CachedAudioEffectProvider _effects = CachedAudioEffectProvider.Instance;
    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;
    private readonly Logger Logger = LogManager.GetCurrentClassLogger();


    public ClientSettingsViewModel()
    {
        StartSoundOptions = RadioSoundChoice.BuildOptions(_effects.RadioTransmissionStart, Resources.RadioSoundOff);
        EndSoundOptions = RadioSoundChoice.BuildOptions(_effects.RadioTransmissionEnd, Resources.RadioSoundOff);

        PlaySoundCommand = new DelegateCommand(PlaySound,
            parameter => parameter is string fileName && fileName.Length > 0);

        ResetRadioPanelCommand = new DelegateCommand(() =>
        {
            // an open radio window closes without saving, so it can't overwrite the defaults below
            EventBus.Instance.PublishOnUIThreadAsync(new ResetRadioPanelMessage());

            RadioPanelWindow.ResetSavedPlacement(_globalSettings);
        });

        CreateProfileCommand = new DelegateCommand(() =>
        {
            var inputProfileWindow = new InputProfileWindow.InputProfileWindow(name =>
            {
                name = name.Trim();
                if (name.Length > 0 && !ProfileExists(name))
                {
                    _globalSettings.ProfileSettingsStore.AddNewProfile(name);

                    // the list first, so the drop down contains the new profile when it is selected
                    NotifyPropertyChanged(nameof(AvailableProfiles));
                    SelectedProfile = name;
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
                name = name.Trim();
                if (name.Length > 0 && !ProfileExists(name))
                {
                    _globalSettings.ProfileSettingsStore.CopyProfile(current, name);

                    // continue with the copy (the drop down and the settings below show it)
                    NotifyPropertyChanged(nameof(AvailableProfiles));
                    SelectedProfile = name;
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
                    name = name.Trim();

                    // unchanged (the dialog starts with the old name), or only the case changed (same profile file)
                    if (name.Length == 0 || string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase)) return;

                    if (!ProfileExists(name))
                    {
                        _globalSettings.ProfileSettingsStore.RenameProfile(oldName, name);

                        // stay on the renamed profile
                        NotifyPropertyChanged(nameof(AvailableProfiles));
                        SelectedProfile = name;
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

    public ICommand ResetRadioPanelCommand { get; set; }

    public ICommand CreateProfileCommand { get; set; }
    public ICommand CopyProfileCommand { get; set; }
    public ICommand RenameProfileCommand { get; set; }
    public ICommand DeleteProfileCommand { get; set; }

    /// <summary>Plays the sound whose file name is the command parameter once on the speakers (play buttons).</summary>
    public ICommand PlaySoundCommand { get; }

    /// <summary>The "Advanced settings" section is expanded (remembered in global.cfg).</summary>
    public bool AdvancedSettingsExpanded
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.SettingsAdvancedExpanded);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.SettingsAdvancedExpanded, value);
            NotifyPropertyChanged();
        }
    }

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

    public bool RadioPanelTaskbarItem
    {
        get => _globalSettings.GetClientSettingBool(GlobalSettingsKeys.RadioPanelTaskbarHide);
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.RadioPanelTaskbarHide, value);
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


    /// <summary>Open the radio window automatically after connecting.</summary>
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

    #region Radio sounds

    /// <summary>"Off" and the start sounds (RADIO_TRANS_START*.wav): push-to-talk pressed / someone starts talking.</summary>
    public IReadOnlyList<RadioSoundOption> StartSoundOptions { get; }

    /// <summary>"Off" and the end sounds (RADIO_TRANS_END*.wav): push-to-talk released / someone stops talking.</summary>
    public IReadOnlyList<RadioSoundOption> EndSoundOptions { get; }

    /// <summary>When I press push-to-talk: RadioTxEffects_Start + RadioTransmissionStartSelection.</summary>
    public string TxStartSound
    {
        get => GetSound(ProfileSettingsKeys.RadioTxEffects_Start, ProfileSettingsKeys.RadioTransmissionStartSelection,
            _effects.RadioTransmissionStart);
        set => SetSound(value, ProfileSettingsKeys.RadioTxEffects_Start,
            ProfileSettingsKeys.RadioTransmissionStartSelection, _effects.RadioTransmissionStart);
    }

    /// <summary>When I release push-to-talk: RadioTxEffects_End + RadioTransmissionEndSelection.</summary>
    public string TxEndSound
    {
        get => GetSound(ProfileSettingsKeys.RadioTxEffects_End, ProfileSettingsKeys.RadioTransmissionEndSelection,
            _effects.RadioTransmissionEnd);
        set => SetSound(value, ProfileSettingsKeys.RadioTxEffects_End,
            ProfileSettingsKeys.RadioTransmissionEndSelection, _effects.RadioTransmissionEnd);
    }

    /// <summary>When someone starts talking: RadioRxEffects_Start + RadioRxStartSelection.</summary>
    public string RxStartSound
    {
        get => GetSound(ProfileSettingsKeys.RadioRxEffects_Start, ProfileSettingsKeys.RadioRxStartSelection,
            _effects.RadioTransmissionStart);
        set => SetSound(value, ProfileSettingsKeys.RadioRxEffects_Start, ProfileSettingsKeys.RadioRxStartSelection,
            _effects.RadioTransmissionStart);
    }

    /// <summary>When someone stops talking: RadioRxEffects_End + RadioRxEndSelection.</summary>
    public string RxEndSound
    {
        get => GetSound(ProfileSettingsKeys.RadioRxEffects_End, ProfileSettingsKeys.RadioRxEndSelection,
            _effects.RadioTransmissionEnd);
        set => SetSound(value, ProfileSettingsKeys.RadioRxEffects_End, ProfileSettingsKeys.RadioRxEndSelection,
            _effects.RadioTransmissionEnd);
    }

    private string GetSound(ProfileSettingsKeys enabledKey, ProfileSettingsKeys selectionKey,
        IReadOnlyList<CachedAudioEffect> effects)
    {
        var profile = _globalSettings.ProfileSettingsStore;

        return RadioSoundChoice.FromSettings(profile.GetClientSettingBool(enabledKey),
            profile.GetClientSettingString(selectionKey), effects);
    }

    private void SetSound(string choice, ProfileSettingsKeys enabledKey, ProfileSettingsKeys selectionKey,
        IReadOnlyList<CachedAudioEffect> effects, [CallerMemberName] string propertyName = "")
    {
        if (RadioSoundChoice.TryApply(choice, effects, out var enabled, out var fileName))
        {
            var profile = _globalSettings.ProfileSettingsStore;

            if (fileName != null) profile.SetClientSettingString(selectionKey, fileName);
            profile.SetClientSettingBool(enabledKey, enabled);
        }

        NotifyPropertyChanged(propertyName);
    }

    private void PlaySound(object parameter)
    {
        if (parameter is not string fileName || fileName.Length == 0) return;

        var effect = _effects.RadioTransmissionStart.Concat(_effects.RadioTransmissionEnd)
            .FirstOrDefault(sound => string.Equals(sound.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (effect == null)
        {
            Logger.Warn($"The sound {fileName} is not available");
            return;
        }

        SoundEffectPreview.Play(effect);
    }

    #endregion Radio sounds

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
            var options = new List<KeyValuePair<string, string>> { new("", Resources.BackgroundSoundNone) };
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
            NotifyPropertyChanged(nameof(HasBackgroundSound));
        }
    }

    /// <summary>A background sound is selected (enables its volume).</summary>
    public bool HasBackgroundSound => BackgroundSound.Length > 0;

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
    /// <summary>
    ///     Sender: "Boost my voice" in percent (0 = normal, 100 = +10 dB), applied by everybody who hears you (see
    ///     <see cref="EasyRadioLink.Common.Audio.Utility.VoiceBoost" />).
    /// </summary>
    public float VoiceBoost
    {
        get
        {
            var value = _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.VoiceBoost);
            return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) * 100f : 0f;
        }
        set
        {
            var percent = float.IsFinite(value) ? (float)Math.Round(Math.Clamp(value, 0f, 100f)) : 0f;
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.VoiceBoost, percent / 100f);
            NotifyPropertyChanged();
        }
    }

    /// <summary>Radio effect strength in percent: 0 = clean voice, 100 = full radio effect (dry/wet ratio).</summary>
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
            NotifyPropertyChanged(nameof(HasRadioEffects));
        }
    }

    /// <summary>Radio effects are on (strength above 0) - the distance (weak signal) only applies then.</summary>
    public bool HasRadioEffects => RadioSoundEffectsRatio > 0f;

    /// <summary>
    ///     Distance (weak signal) in percent: 0 = right next to you, 100 = very far away. Acts on received voices (and the
    ///     Audio Preview): multipath fading that swirls through the voice, static, a narrower and harsher voice.
    /// </summary>
    public float VoiceDistortion
    {
        get
        {
            var value = _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.VoiceDistortion);
            return float.IsFinite(value) ? Math.Clamp(value, 0f, 100f) : WeakSignalChannelProvider.DefaultPercent;
        }
        set
        {
            var percent = float.IsFinite(value) ? (float)Math.Round(Math.Clamp(value, 0f, 100f)) : 0f;
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.VoiceDistortion, percent);
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


    /// <summary>Stereo balance of the radio (-1 left .. +1 right; profile key Radio1Channel).</summary>
    public float RadioBalance
    {
        get => _globalSettings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio1Channel);
        set
        {
            _globalSettings.ProfileSettingsStore.SetClientSettingFloat(ProfileSettingsKeys.Radio1Channel, value);
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

    /// <summary>More than the default profile exists (the radio sounds show which profile they belong to).</summary>
    public bool HasSeveralProfiles => _globalSettings.ProfileSettingsStore.ProfileNames.Count > 1;

    /// <summary>"Saved in the profile ..." above the radio sounds.</summary>
    public string ProfileNotice => string.Format(Resources.RadioSoundsProfileNotice, SelectedProfile);


    /// <summary>
    ///     True (and an error is shown) if a profile with this name exists already. Profiles are files, so the case
    ///     is ignored - a new profile would overwrite the existing one.
    /// </summary>
    private bool ProfileExists(string name)
    {
        if (!_globalSettings.ProfileSettingsStore.ProfileNames.Any(profile =>
                string.Equals(profile, name, StringComparison.OrdinalIgnoreCase)))
            return false;

        MessageBox.Show(Application.Current.MainWindow,
            string.Format(Resources.MsgBoxProfileExistsText, name),
            Resources.MsgBoxError,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        return true;
    }

    private void ReloadSettings()
    {
        // not AvailableProfiles / SelectedProfile: this runs while the profile drop-down changes its selection
        foreach (var property in GlobalProperties) NotifyPropertyChanged(property);
        foreach (var property in ProfileProperties) NotifyPropertyChanged(property);

        //TODO send message to tell input to reload!
        //TODO pick up in inputhandler that settings have changed?
        EventBus.Instance.PublishOnUIThreadAsync(new ProfileChangedMessage());
    }
}