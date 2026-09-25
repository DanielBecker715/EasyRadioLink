using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Client.UI.ClientWindow.RadioPanel.PresetChannels;
using EasyRadioLink.Common;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Client.Radios;

/// <summary>
///     Owns the radio state of one connection (created and started by the main window view model when the connection
///     is established, stopped on disconnect).
///     <list type="bullet">
///         <item>
///             Activation: on the first <see cref="ServerSettingsUpdatedMessage" /> (or after
///             <see cref="ActivationFallbackDelay" /> if none arrives) the radio layout is loaded
///             (<see cref="RadioDefinitionStore" />), the saved tuning is re-applied (<see cref="RadioStatePersistence" />),
///             preset channels are initialised and <see cref="PlayerRadioInfo.IsActive" /> is set - radios are usable.
///         </item>
///         <item>
///             Every <see cref="LoopInterval" /> the network relevant state (radios, background sound, name, recording
///             permission) is compared with what was sent last; a change, <c>ClientStateSingleton.LastSent == 0</c> (dirty
///             flag) or <see cref="RadioUpdatePingInterval" /> without update publishes a
///             <see cref="UnitUpdateMessage" /> (FullUpdate) that <c>TCPClientHandler</c> sends as RADIO_UPDATE.
///         </item>
///         <item>
///             Server rules are enforced continuously: encryption is switched off when the server disallows it or the
///             radio is not capable; a changed server radio layout is re-loaded.
///         </item>
///         <item><see cref="Stop" /> saves the tuning, resets the radios and marks them inactive.</item>
///     </list>
/// </summary>
public sealed class RadioStateSyncService : IHandle<ServerSettingsUpdatedMessage>
{
    public static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan RadioUpdatePingInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ActivationFallbackDelay = TimeSpan.FromSeconds(2);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ClientStateSingleton _clientState = ClientStateSingleton.Instance;
    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;
    private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;

    private readonly object _sync = new();

    private volatile bool _activated;
    private CancellationTokenSource _cts;
    private int _loopErrors;

    // what was sent last
    private bool _lastAllowRecord;
    private string _lastName;
    private PlayerRadioInfo _lastSent;

    private RadioLayout _layout;

    /// <summary>True once the radios are loaded for this connection.</summary>
    public bool IsActivated => _activated;

    /// <summary>Where the current radio layout came from (null before activation).</summary>
    public RadioLayoutSource? LayoutSource => _layout?.Source;

    public Task HandleAsync(ServerSettingsUpdatedMessage message, CancellationToken cancellationToken)
    {
        var cts = _cts;
        if (cts == null || cts.IsCancellationRequested) return Task.CompletedTask;

        try
        {
            if (!_activated)
            {
                Activate(cts.Token, false);
            }
            else if (IsLayoutOutdated())
            {
                Logger.Info("The server radio layout changed - reloading the radios");

                lock (_sync)
                {
                    if (!cts.IsCancellationRequested) RadioStatePersistence.Save(_clientState.PlayerRadioInfo);
                }

                Activate(cts.Token, true);
            }
            else
            {
                lock (_sync)
                {
                    EnforceServerRules(_clientState.PlayerRadioInfo);
                }

                // make sure a changed server setting is reflected immediately
                _clientState.LastSent = 0;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to apply the server settings to the radios");
        }

        return Task.CompletedTask;
    }

    public void Start()
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_cts != null) return;

            _cts = new CancellationTokenSource();
            token = _cts.Token;
            _activated = false;
            _lastSent = null;
            _layout = null;
        }

        EventBus.Instance.SubscribeOnBackgroundThread(this);

        // normally the SYNC reply (server settings) arrives right after connecting - don't wait forever for it
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ActivationFallbackDelay, token);

                if (!_activated)
                {
                    Logger.Warn("No server settings received - activating the radios with the current settings");
                    Activate(token, false);
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to activate the radios");
            }
        }, token);

        _ = Task.Run(() => LoopAsync(token), token);

        Logger.Info("Radio state sync started");
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_cts == null) return;

            _cts.Cancel();
            _cts = null;

            var info = _clientState.PlayerRadioInfo;
            if (_activated && info.IsActive) RadioStatePersistence.Save(info);

            info.Reset();

            _activated = false;
            _lastSent = null;
            _layout = null;
            _clientState.LastSent = 0;
        }

        EventBus.Instance.Unsubscribe(this);

        Logger.Info("Radio state sync stopped");
    }

    /// <param name="reload">false: do nothing if the radios are already activated (first activation only)</param>
    private void Activate(CancellationToken token, bool reload)
    {
        lock (_sync)
        {
            if (token.IsCancellationRequested) return;
            if (_activated && !reload) return;

            var allowServerPreset =
                _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.AllowServerRadioPreset);

            var layout = RadioDefinitionStore.Load(_serverSettings, allowServerPreset);

            var info = _clientState.PlayerRadioInfo;
            info.Reset();

            for (var i = 0; i < info.radios.Length; i++)
                info.radios[i] = i < layout.Definitions.Count
                    ? Radio.FromDefinition(layout.Definitions[i])
                    : new Radio();

            // reserved slot - never usable
            info.radios[0] = new Radio();

            var restoredSlots = RadioStatePersistence.Apply(info);

            if (info.selected < PlayerRadioInfo.FirstUserRadio || info.selected >= info.radios.Length ||
                !info.radios[info.selected].IsEnabled)
                info.selected = FirstEnabledRadio(info);

            EnforceServerRules(info);
            UpdateBackgroundSound(info);

            _layout = layout;
            _lastSent = null;
            _activated = true;
            info.IsActive = true;

            InitPresetChannels(info, restoredSlots);

            // force an immediate RADIO_UPDATE
            _clientState.LastSent = 0;

            Logger.Info($"Radios activated from {layout.Source} ({layout.SourceDescription})");
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = Task.Delay(LoopInterval, token);

            try
            {
                if (_activated) await SendUpdateIfNeededAsync(token);

                _loopErrors = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // don't flood the log if something keeps failing
                if (_loopErrors++ < 5) Logger.Error(ex, "Radio state sync failed");
            }

            try
            {
                await delay;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendUpdateIfNeededAsync(CancellationToken token)
    {
        UnitUpdateMessage update = null;

        lock (_sync)
        {
            var info = _clientState.PlayerRadioInfo;
            if (token.IsCancellationRequested || !_activated || !info.IsActive) return;

            EnforceServerRules(info);
            UpdateBackgroundSound(info);

            var name = _clientState.EffectiveName;
            var allowRecord = _globalSettings.GetClientSettingBool(GlobalSettingsKeys.AllowRecording);

            var changed = _lastSent == null
                          || !_lastSent.Equals(info)
                          || !string.Equals(name, _lastName, StringComparison.Ordinal)
                          || allowRecord != _lastAllowRecord;

            var lastSent = _clientState.LastSent;
            var sinceLastSent = TimeSpan.FromTicks(DateTime.Now.Ticks - lastSent);

            if (changed || lastSent < 1 || sinceLastSent > RadioUpdatePingInterval)
            {
                _clientState.LastSent = DateTime.Now.Ticks;
                _lastSent = info.DeepClone();
                _lastName = name;
                _lastAllowRecord = allowRecord;

                update = new UnitUpdateMessage
                {
                    FullUpdate = true,
                    UnitUpdate = new ClientInfo
                    {
                        ClientGuid = _clientState.ShortGUID,
                        Name = name,
                        AllowRecord = allowRecord,
                        RadioInfo = info.ConvertToRadioBase()
                    }
                };
            }
        }

        if (update != null)
        {
            Logger.Debug("Publishing radio update");
            await EventBus.Instance.PublishOnCurrentThreadAsync(update, token);
        }
    }

    /// <summary>True if the server's radio layout should replace the current one (or stop being used).</summary>
    private bool IsLayoutOutdated()
    {
        var layout = _layout;
        if (layout == null) return false;

        var allowServerPreset =
            _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.AllowServerRadioPreset);
        var serverHasPreset = _serverSettings.ServerRadioPreset.Count == Constants.MAX_RADIOS;

        if (layout.Source == RadioLayoutSource.Server)
            return !allowServerPreset || !serverHasPreset ||
                   !string.Equals(layout.ServerPresetJson, _serverSettings.ServerRadioPresetJson,
                       StringComparison.Ordinal);

        return allowServerPreset && serverHasPreset;
    }

    /// <summary>Encryption only on capable radios and only if the server allows it; keys 1..252.</summary>
    private void EnforceServerRules(PlayerRadioInfo info)
    {
        var encryptionAllowed = _serverSettings.GetSettingAsBool(ServerSettingsKeys.ALLOW_RADIO_ENCRYPTION);

        foreach (var radio in info.radios)
        {
            if (radio == null) continue;

            if (radio.enc && (!encryptionAllowed || !radio.encCapable || !radio.IsEnabled)) radio.enc = false;

            if (radio.encKey < RadioDefinition.MinEncryptionKey || radio.encKey > RadioDefinition.MaxEncryptionKey)
                radio.encKey = Math.Clamp(radio.encKey, RadioDefinition.MinEncryptionKey,
                    RadioDefinition.MaxEncryptionKey);
        }
    }

    /// <summary>Writes the profile's background sound (BackgroundSound / BackgroundSoundVolume) into ambient.</summary>
    private void UpdateBackgroundSound(PlayerRadioInfo info)
    {
        var profile = _globalSettings.ProfileSettingsStore;

        var name = CachedAudioEffectProvider.NormaliseBackgroundName(
            profile.GetClientSettingString(ProfileSettingsKeys.BackgroundSound));

        if (name.Length > 0 && !CachedAudioEffectProvider.Instance.IsBackgroundSoundAvailable(name)) name = "";

        var volume = 0f;
        if (name.Length > 0)
        {
            volume = profile.GetClientSettingFloat(ProfileSettingsKeys.BackgroundSoundVolume);
            volume = float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : 0f;
            // avoid re-sending because of float noise
            volume = (float)Math.Round(volume, 2);
        }

        var ambient = info.ambient;
        if (ambient == null || ambient.abType != name || ambient.vol != volume)
            info.ambient = new Ambient { abType = name, vol = volume };
    }

    private void InitPresetChannels(PlayerRadioInfo info, HashSet<int> restoredSlots)
    {
        var autoSelect =
            _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.AutoSelectPresetChannel);

        var fixedChannels = _clientState.FixedChannels;

        for (var radioId = PlayerRadioInfo.FirstUserRadio; radioId < info.radios.Length; radioId++)
        {
            var channelIndex = radioId - 1;
            if (fixedChannels == null || channelIndex >= fixedChannels.Length) break;

            var channels = fixedChannels[channelIndex];
            var radio = info.radios[radioId];

            if (channels == null) continue;

            try
            {
                if (!radio.IsEnabled)
                {
                    channels.Clear();
                    continue;
                }

                channels.Max = radio.freqMax;
                channels.Min = radio.freqMin;
                channels.Reload();

                var preselected = radio.channel;
                radio.channel = -1;

                var count = channels.PresetChannels.Count;

                if (restoredSlots.Contains(radioId))
                {
                    // keep the restored frequency; re-select the channel the user had
                    if (preselected >= 1 && preselected <= count) SelectPresetChannel(channels, radio, preselected);
                }
                else if (autoSelect && count > 0)
                {
                    SelectPresetChannel(channels, radio, 1);
                }
                else if (preselected >= 1 && preselected <= count)
                {
                    SelectPresetChannel(channels, radio, preselected);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to load the preset channels of radio {radioId}");
            }
        }
    }

    private static void SelectPresetChannel(PresetChannelsViewModel channels, Radio radio, int channel)
    {
        var preset = channels.PresetChannels[channel - 1];
        if (preset?.Value is not double frequency || !double.IsFinite(frequency)) return;

        radio.freq = Math.Clamp(frequency, radio.freqMin, radio.freqMax);
        radio.channel = preset.Channel > 0 ? preset.Channel : channel;
        channels.SelectedPresetChannel = preset;
    }

    private static short FirstEnabledRadio(PlayerRadioInfo info)
    {
        for (var i = PlayerRadioInfo.FirstUserRadio; i < info.radios.Length; i++)
            if (info.radios[i].IsEnabled)
                return (short)i;

        return PlayerRadioInfo.FirstUserRadio;
    }
}
