using System;
using System.Threading;
using System.Threading.Tasks;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using NLog;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Client.Radios;

/// <summary>
///     Owns the radio state of one connection (created and started by the main window view model when the connection
///     is established, stopped on disconnect).
///     <list type="bullet">
///         <item>
///             <see cref="Start" /> switches the radio on with the remembered frequency and volume
///             (<see cref="RadioStatePersistence" />, default 27.185 MHz), modulation and radio model from the
///             <see cref="BandPlan" />, and sets <see cref="PlayerRadioInfo.IsActive" /> - the radio is usable.
///         </item>
///         <item>
///             Every <see cref="LoopInterval" /> the network relevant state (radio, background sound, name, recording
///             permission) is compared with what was sent last; a change, <c>ClientStateSingleton.LastSent == 0</c> (dirty
///             flag) or <see cref="RadioUpdatePingInterval" /> without update publishes a
///             <see cref="UnitUpdateMessage" /> (FullUpdate) that <c>TCPClientHandler</c> sends as RADIO_UPDATE. Tuning
///             is therefore coalesced to at most one update per interval (5 per second).
///         </item>
///         <item>
///             A changed frequency / volume is remembered <see cref="SaveDelay" /> after the last change;
///             <see cref="Stop" /> remembers it as well, resets the radio and marks it inactive.
///         </item>
///     </list>
/// </summary>
public sealed class RadioStateSyncService
{
    public static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan RadioUpdatePingInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ClientStateSingleton _clientState = ClientStateSingleton.Instance;
    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;

    private readonly object _sync = new();

    private CancellationTokenSource _cts;
    private int _loopErrors;

    // what was sent last
    private bool _lastAllowRecord;
    private string _lastName;
    private PlayerRadioInfo _lastSent;

    // what was remembered last (radio-state.json), the tuning seen last and when it changed
    private double _savedFrequency = double.NaN;
    private float _savedVolume = float.NaN;
    private double _pendingFrequency = double.NaN;
    private float _pendingVolume = float.NaN;
    private long _tuningChangedAt;

    public void Start()
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_cts != null) return;

            _cts = new CancellationTokenSource();
            token = _cts.Token;
            _lastSent = null;

            Activate();
        }

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
            if (info.IsActive) SaveTuning(info.Radio, true);

            info.Reset();

            _lastSent = null;
            _clientState.LastSent = 0;
        }

        Logger.Info("Radio state sync stopped");
    }

    /// <summary>Switches the radio on with the remembered tuning. Called under <see cref="_sync" />.</summary>
    private void Activate()
    {
        var state = RadioStatePersistence.Load();

        var info = _clientState.PlayerRadioInfo;
        info.Reset();

        // slot 0 is reserved and slots 2..10 stay switched off
        info.radios[PlayerRadioInfo.RadioId] = Radio.Create(state.Frequency, state.Volume);

        _savedFrequency = _pendingFrequency = info.Radio.freq;
        _savedVolume = _pendingVolume = info.Radio.volume;
        _tuningChangedAt = 0;

        UpdateBackgroundSound(info);

        _lastSent = null;
        info.IsActive = true;

        // force an immediate RADIO_UPDATE
        _clientState.LastSent = 0;

        var band = info.Radio.Band;
        Logger.Info(
            $"Radio switched on: {RadioCalculator.FormatMHz(info.Radio.freq)} MHz ({band.Label}, {band.Modulation}, {band.Model})");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = Task.Delay(LoopInterval, token);

            try
            {
                await SendUpdateIfNeededAsync(token);

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
            if (token.IsCancellationRequested || !info.IsActive) return;

            UpdateBackgroundSound(info);
            SaveTuning(info.Radio, false);

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
                // one snapshot: the radio may be retuned meanwhile - send exactly what is remembered as sent
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
                        RadioInfo = _lastSent.ConvertToRadioBase()
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

    /// <summary>
    ///     Remembers frequency and volume once they have not changed for <see cref="SaveDelay" /> (immediately with
    ///     <paramref name="now" />), so tuning with the knob does not write the file for every step.
    /// </summary>
    private void SaveTuning(Radio radio, bool now)
    {
        if (radio == null || !radio.IsEnabled) return;

        var frequency = radio.freq;
        var volume = radio.volume;
        var ticks = DateTime.Now.Ticks;

        // the delay starts again with every change
        if (!SameTuning(frequency, volume, _pendingFrequency, _pendingVolume))
        {
            _pendingFrequency = frequency;
            _pendingVolume = volume;
            _tuningChangedAt = ticks;
        }

        if (SameTuning(frequency, volume, _savedFrequency, _savedVolume)) return;

        if (!now && TimeSpan.FromTicks(ticks - _tuningChangedAt) < SaveDelay) return;

        RadioStatePersistence.SaveTuning(frequency, volume);

        _savedFrequency = frequency;
        _savedVolume = volume;
    }

    private static bool SameTuning(double frequency, float volume, double otherFrequency, float otherVolume)
    {
        return Math.Abs(frequency - otherFrequency) < 0.5 && Math.Abs(volume - otherVolume) < 0.001f;
    }

    /// <summary>
    ///     Writes the profile's background sound (BackgroundSound / BackgroundSoundVolume) and "Boost my voice"
    ///     (VoiceBoost) into ambient.
    /// </summary>
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

        var voiceBoost = profile.GetClientSettingFloat(ProfileSettingsKeys.VoiceBoost);
        voiceBoost = float.IsFinite(voiceBoost) ? (float)Math.Round(Math.Clamp(voiceBoost, 0f, 1f), 2) : 0f;

        var ambient = info.ambient;
        if (ambient == null || ambient.abType != name || ambient.vol != volume || ambient.voiceBoost != voiceBoost)
            info.ambient = new Ambient { abType = name, vol = volume, voiceBoost = voiceBoost };
    }
}
