using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLog;
using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;

namespace EasyRadioLink.Common.Audio.Providers
{
    /// <summary>
    ///     The "sound of the sender's radio": applies the sender's radio model (transmit chain), background static and
    ///     the FM tone to one received transmission (with the final clip), mixes that with the dry voice by the radio
    ///     effect strength, then applies the distance (the weak signal link between the radios,
    ///     <see cref="WeakSignalChannelProvider" />) and the receiving radio's volume. One instance per sender (plus one
    ///     for the local passthrough).
    /// </summary>
    // #TODO: ISampleProvider?
    internal class ClientTransmissionPipelineProvider
    {
        public ClientTransmissionPipelineProvider()
        {
            LoadTones();
            RefreshSettings();
        }

        public void Process(DeJitteredTransmission transmission, Span<float> audioOut)
        {
            RefreshSettings();
            if (transmission.NoAudioEffects)
            {
                // Clean frequency: no radio effects, but the receiving radio's volume still applies.
                ApplyVolume(audioOut, transmission.Volume);
                return;
            }

            // Copy to regular array for compatibility with ISampleProvider.
            var floatPool = ArrayPool<float>.Shared;
            var drySourceBuffer = floatPool.Rent(audioOut.Length);
            audioOut.CopyTo(drySourceBuffer);

            // Dry/original provider
            var dryProvider = new TransmissionProvider(drySourceBuffer, 0, audioOut.Length);

            // Wet/effected provider: must use a separate buffer to avoid double-reading
            var wetSourceBuffer = floatPool.Rent(audioOut.Length);
            audioOut.CopyTo(wetSourceBuffer);
            ISampleProvider wetProvider = new TransmissionProvider(wetSourceBuffer, 0, audioOut.Length);

            if (RadioEffectsRatio > 0f)
            {
                if (RadioEffectRules.IsCleanPath(transmission.Modulation))
                {
                    // DIGITAL: the sender's model (clean "digital" model by default), no static, no tone.
                    var digitalModel = GetRadioModel(transmission, RadioModelFactory.DigitalModelKey);
                    wetProvider = BuildRadioPipeline(wetProvider, digitalModel, transmission);
                }
                else
                {
                    var radioModel = GetRadioModel(transmission, RadioModelFactory.DefaultModelKey);
                    wetProvider = BuildRadioEffectsChain(wetProvider, radioModel, transmission);
                }
            }

            // Wet/dry mix, the distance (the weak signal link) and the receiving radio's volume.
            var mixer = BuildOutput(dryProvider, wetProvider, RadioEffectsRatio, WeakSignalFor(transmission),
                transmission.Volume);

            var mixerBuffer = floatPool.Rent(audioOut.Length);
            try
            {
                int samplesRead = mixer.Read(mixerBuffer, 0, audioOut.Length);
                mixerBuffer.AsSpan(0, samplesRead).CopyTo(audioOut);
            }
            catch(Exception e)
            {
                Logger.Error(e, "Failed to process client audio: ");
                // Something borked - reset for the next packet(s).
                TxRadioModels.Clear();
                _hfNoiseModel = null;
                _hfNoiseModelLoaded = false;
                _weakSignal = null;
            }
            finally
            {
                floatPool.Return(mixerBuffer);
            }

            floatPool.Return(drySourceBuffer);
            floatPool.Return(wetSourceBuffer);
        }

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        ///     The end of the receive chain: the dry voice and the sender's radio sound (<paramref name="wet" />) mixed by
        ///     the radio effect strength, then the distance of the transmission (the weak signal channel, null = none)
        ///     and the receiving radio's volume.
        ///     <para>
        ///         The distance is the link between the two radios, so it acts on everything that arrives: the sender's
        ///         radio (model, static, FM tone) AND the dry share of the mix. Inside the wet branch the clean dry voice
        ///         would mask it (at 30 % effect strength 70 % of the voice would stay clean and close). It is off at 0 %
        ///         effect strength (amount 0 = exact bypass, see <see cref="WeakSignalChannelProvider.AmountFromSettings" />)
        ///         and never reached on clean frequencies. While active its output stays within +-0.9, so neither path
        ///         needs an extra clip (the mix is clipped in RadioMixingProvider as before). The squelch tail and the
        ///         receive start / end sounds are added later (RadioMixingProvider) and stay clean.
        ///     </para>
        /// </summary>
        internal static ISampleProvider BuildOutput(ISampleProvider dry, ISampleProvider wet, float radioEffectsRatio,
            WeakSignalChannelProvider weakSignal, float volume)
        {
            var dryVolume = new VolumeSampleProvider(dry) { Volume = Math.Max(1.0f - radioEffectsRatio, 0.0f) };
            var wetVolume = new VolumeSampleProvider(wet) { Volume = radioEffectsRatio };
            ISampleProvider output = new MixingSampleProvider(new[] { dryVolume, wetVolume });

            if (weakSignal != null)
            {
                weakSignal.Source = output;
                output = weakSignal;
            }

            return new VolumeSampleProvider(output) { Volume = volume };
        }

        private static void ApplyVolume(Span<float> audio, float volume)
        {
            if (volume == 1.0f) return;

            for (var i = 0; i < audio.Length; i++) audio[i] *= volume;
        }

        private ISampleProvider BuildRadioEffectsChain(ISampleProvider voiceProvider, TxRadioModel radioModel, DeJitteredTransmission transmission)
        {
            if (ClippingEnabled)
            {
                voiceProvider = new ClippingProvider(voiceProvider, RadioFilter.CLIPPING_MIN, RadioFilter.CLIPPING_MAX);
            }

            if (BackgroundNoiseEffect)
            {
                voiceProvider = BuildBackgroundNoiseEffect(voiceProvider, radioModel, transmission);
            }
            else
            {
                // The FM tone has its own setting - keep it when the static is switched off.
                var tone = GetToneProvider(transmission.Modulation);
                if (tone != null && tone.Active)
                {
                    var toneMixer = new MixingSampleProvider(voiceProvider.WaveFormat);
                    toneMixer.AddMixerInput(tone);
                    toneMixer.AddMixerInput(voiceProvider);
                    voiceProvider = toneMixer;
                }
            }

            // Only apply radio pipeline if effect amount > 0
            if (RadioEffectsRatio > 0f)
            {
                voiceProvider = BuildRadioPipeline(voiceProvider, radioModel, transmission);
            }

            // (the distance follows after the dry / wet mix, see BuildOutput)
            voiceProvider = new ClippingProvider(voiceProvider, -1, 1);

            return voiceProvider;
        }

        /// <summary>
        ///     The distance (weak signal channel, see <see cref="WeakSignalChannelProvider" />) of the current
        ///     transmission, set to the current amount; null for the local passthrough. One instance per transmission: a
        ///     pause longer than the jitter buffer, another frequency or another modulation starts a new one (a new
        ///     random point of the fading).
        /// </summary>
        private WeakSignalChannelProvider WeakSignalFor(DeJitteredTransmission transmission)
        {
            if (!VoiceDistortionEnabled) return null;

            var now = Stopwatch.GetTimestamp();
            var newTransmission = _weakSignal == null
                                  || Stopwatch.GetElapsedTime(_weakSignalLastUse, now) > JitterBufferProviderInterface.JITTER_MS
                                  || _weakSignalFrequency != transmission.Frequency
                                  || _weakSignalModulation != transmission.Modulation;
            _weakSignalLastUse = now;

            if (newTransmission)
            {
                // nothing to do while it is off - but it must exist, so the slider can be turned up mid-transmission
                _weakSignal = new WeakSignalChannelProvider(null,
                    WeakSignalChannelProvider.BandFor(transmission.Frequency, transmission.Modulation),
                    _weakSignalSeeds.Next(), VoiceDistortionAmount);
                _weakSignalFrequency = transmission.Frequency;
                _weakSignalModulation = transmission.Modulation;
            }

            _weakSignal.Amount = VoiceDistortionAmount;
            return _weakSignal;
        }

        private ISampleProvider BuildBackgroundNoiseEffect(ISampleProvider voiceProvider, TxRadioModel radioModel, DeJitteredTransmission transmission)
        {
            // HF noise (very grainy/rain sounding) at or below the cutoff (CB included) vs white noise above.
            var isHFNoise = RadioEffectRules.IsHfNoise(transmission.Frequency);
            var backgroundEffectsProvider = new MixingSampleProvider(voiceProvider.WaveFormat);
            // Noise, initial power depends on frequency band.
            // HF very susceptible (higher base), V/UHF not as much.
            // We can do a rough estimation applying a log-based rule.
            // Rough figures for attenuation:
            // 1-30 (HF): 0-17 dB
            // 30-100: 17-23 dB
            // 100-200 (VHF): 23-26 dB
            // 200-400 (UHF): 26-29 dB
            var noiseGainDB = RadioEffectRules.FrequencyNoiseGainDb(transmission.Frequency);

            // Apply user defined noise attenuation/gain
            noiseGainDB += isHFNoise ? HFNoiseGainOffsetDB : NoiseGainOffsetDB;
            // Apply radio model noise attenuation/gain.
            noiseGainDB += radioModel.NoiseGain;

            var noiseGeneratorGainDB = !isHFNoise ? noiseGainDB : 0f;

            // #TODO: noise type should be part of the radio model really.
            // Tube/HF noise (red/pink) vs transistor (white/AGWN)
            ISampleProvider noiseProvider = null;
            if (!isHFNoise)
            {
                noiseProvider = new VolumeSampleProvider(new GaussianWhiteNoise())
                {
                    Volume = (float)Decibels.DecibelsToLinear(noiseGeneratorGainDB),
                };
            }
            else
            {
                noiseProvider = new SignalGenerator(voiceProvider.WaveFormat.SampleRate, voiceProvider.WaveFormat.Channels)
                {
                    Type = SignalGeneratorType.Pink,
                    Gain = (float)Decibels.DecibelsToLinear(noiseGeneratorGainDB),
                };
            }

            noiseProvider = new FiltersProvider(noiseProvider)
            {
                Filters = new Dsp.IFilter[] { Dsp.FirstOrderFilter.LowPass(voiceProvider.WaveFormat.SampleRate, 800) },
            };

            if (isHFNoise)
            {
                // Shape the HF static with the internal "hfnoise" model (own instance - never shared with the
                // voice chain, which could be using a model with the same key).
                var hfNoise = GetHfNoiseModel();
                if (hfNoise != null)
                {
                    noiseProvider = BuildRadioPipeline(noiseProvider, hfNoise, new DeJitteredTransmission
                    {
                        Frequency = transmission.Frequency,
                        Modulation = transmission.Modulation,
                        Encryption = transmission.Encryption,
                    });
                }

                noiseProvider = new VolumeSampleProvider(noiseProvider)
                {
                    Volume = (float)Decibels.DecibelsToLinear(noiseGainDB)
                };
            }

            backgroundEffectsProvider.AddMixerInput(noiseProvider);

            var tone = GetToneProvider(transmission.Modulation);
            if (tone != null && tone.Active)
            {
                backgroundEffectsProvider.AddMixerInput(tone);
            }

#if false // Mains hum @ 400Hz (aviation standard)
                fxMixer.AddMixerInput(new SignalGenerator(voiceProvider.WaveFormat.SampleRate, 1)
                {
                    Type = SignalGeneratorType.SawTooth,
                    Frequency = 400,
                    Gain = (float)Decibels.DecibelsToLinear(-60),
                });
#endif
            backgroundEffectsProvider.AddMixerInput(voiceProvider);
            return backgroundEffectsProvider;
        }

        private ISampleProvider BuildRadioPipeline(ISampleProvider voiceProvider, TxRadioModel radioModel, DeJitteredTransmission details)
        {
            radioModel.TxSource.Source = voiceProvider;
            // Scrambled audio (an end-to-end encrypted frame whose transmission key never arrived, Encryption = 1) gets
            // the model's encryption effect on top of the garbling done by ClientAudioProvider.
            var encryptionEffects = details.Encryption > 0;
            if (encryptionEffects && radioModel.EncryptionProvider != null)
            {
                voiceProvider = radioModel.EncryptionProvider;
            }
            else
            {
                voiceProvider = radioModel.TxEffectProvider;
            }

            return voiceProvider;
        }

        private VolumeCachedEffectProvider GetToneProvider(Modulation modulation)
        {
            return RadioEffectRules.HasFmTone(modulation)
                ? ToneProviders[CachedAudioEffect.AudioEffectTypes.NATO_TONE]
                : null;
        }

        private TxRadioModel GetHfNoiseModel()
        {
            if (!_hfNoiseModelLoaded)
            {
                _hfNoiseModel = RadioModelFactory.Instance.LoadTxRadio(RadioModelFactory.HfNoiseModelKey);
                _hfNoiseModelLoaded = true;
            }

            return _hfNoiseModel;
        }

        /// <summary>
        ///     The sender's radio model: the model of the sender's radio on this frequency (if per-radio models are
        ///     enabled), otherwise / if unknown <paramref name="fallbackKey" />.
        /// </summary>
        private TxRadioModel GetRadioModel(DeJitteredTransmission transmission, string fallbackKey)
        {
            var guid = transmission.Guid;
            if (guid == null)
            {
                guid = transmission.OriginalClientGuid;
            }

            var candidateModel = string.Empty;
            if (PerRadioModelEffect && guid != null && ConnectedClientsSingleton.Instance.Clients.TryGetValue(guid, out var sender))
            {
                if (sender != null && sender.RadioInfo != null && sender.RadioInfo.radios != null)
                {
                    // Try to find which radio the transmission is coming from.
                    // "best match": if the sender has several radios on the same frequency and modulation the
                    // first one wins.
                    var candidate = Array.Find(sender.RadioInfo.radios, radio => radio != null && radio.modulation == transmission.Modulation && RadioBase.FreqCloseEnough(transmission.Frequency, radio.freq));

                    if (candidate != null)
                    {
                        candidateModel = candidate.Model;
                    }
                }
            }

            // Resolve unknown names to the fallback BEFORE caching so the cache only holds existing models.
            var key = RadioModelFactory.Instance.ResolveModelKey(candidateModel, fallbackKey);

            if (!TxRadioModels.TryGetValue(key, out var radioModel))
            {
                radioModel = fallbackKey == RadioModelFactory.DigitalModelKey
                    ? RadioModelFactory.Instance.LoadTxOrDefaultDigital(key)
                    : RadioModelFactory.Instance.LoadTxOrDefaultRadio(key);
                TxRadioModels[key] = radioModel;
            }

            return radioModel;
        }

        private void RefreshSettings()
        {
            long now = DateTime.Now.Ticks;

            if (TimeSpan.FromTicks(now - LastRefresh).TotalSeconds <= 3)
                return;

            //3 seconds since last refresh
            LastRefresh = now;

            var profileSettings = GlobalSettingsStore.Instance.ProfileSettingsStore;
            PerRadioModelEffect = profileSettings.GetClientSettingBool(ProfileSettingsKeys.PerRadioModelEffects);

            // Use RadioEffectsAmount as float (0..1), clamp for safety
            RadioEffectsRatio = Math.Clamp(profileSettings.GetClientSettingFloat(ProfileSettingsKeys.RadioEffectsRatio), 0f, 1f);

            clippingEnabled = profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioEffectsClipping);
            BackgroundNoiseEffect = profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioBackgroundNoiseEffect);

            NoiseGainOffsetDB = profileSettings.GetClientSettingFloat(ProfileSettingsKeys.NoiseGainDB);
            HFNoiseGainOffsetDB = profileSettings.GetClientSettingFloat(ProfileSettingsKeys.HFNoiseGainDB);

            ToneProviders[CachedAudioEffect.AudioEffectTypes.NATO_TONE].Enabled = profileSettings.GetClientSettingBool(ProfileSettingsKeys.NATOTone);
            ToneProviders[CachedAudioEffect.AudioEffectTypes.NATO_TONE].Volume = profileSettings.GetClientSettingFloat(ProfileSettingsKeys.NATOToneVolume);

            VoiceDistortionAmount = WeakSignalChannelProvider.AmountFromSettings(RadioEffectsRatio,
                profileSettings.GetClientSettingFloat(ProfileSettingsKeys.VoiceDistortion));
        }

        private long LastRefresh { get; set; }

        /// <summary>False for the local passthrough (own voice): no distance (weak signal) effect.</summary>
        public bool VoiceDistortionEnabled { get; init; } = true;

        // distance (0..1, profile setting VoiceDistortion) and the weak signal channel of the current transmission
        private float VoiceDistortionAmount { get; set; }
        private WeakSignalChannelProvider _weakSignal;
        private long _weakSignalLastUse;
        private double _weakSignalFrequency;
        private Modulation _weakSignalModulation;
        private readonly Random _weakSignalSeeds = new();

        private bool PerRadioModelEffect { get; set; }
        private float RadioEffectsRatio { get; set; } = 1.0f;
        private bool BackgroundNoiseEffect { get; set; }

        private bool clippingEnabled = false;
        private bool ClippingEnabled => RadioEffectsRatio > 0f && clippingEnabled;
        private float NoiseGainOffsetDB { get; set; } = 0f;
        private float HFNoiseGainOffsetDB { get; set; } = 0f;

        private IDictionary<string, TxRadioModel> TxRadioModels { get; } = new Dictionary<string, TxRadioModel>();
        private TxRadioModel _hfNoiseModel;
        private bool _hfNoiseModelLoaded;
        private IReadOnlyDictionary<CachedAudioEffect.AudioEffectTypes, VolumeCachedEffectProvider> ToneProviders { get; set; }

        private void LoadTones()
        {
            var loadedTones = new Dictionary<CachedAudioEffect.AudioEffectTypes, VolumeCachedEffectProvider>();

            var effectProvider = CachedAudioEffectProvider.Instance;
            loadedTones.Add(CachedAudioEffect.AudioEffectTypes.NATO_TONE, new VolumeCachedEffectProvider(new CachedEffectProvider(effectProvider.NATOTone)));

            ToneProviders = loadedTones.ToFrozenDictionary();
        }
    }
}
