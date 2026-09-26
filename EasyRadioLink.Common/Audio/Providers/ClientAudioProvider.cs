using System;
using System.Collections.Generic;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using NAudio.Wave;
using System.Numerics;
using System.Runtime.InteropServices;
using EasyRadioLink.Common.Audio.Utility;
using EasyRadioLink.Common.Helpers;

namespace EasyRadioLink.Common.Audio.Providers;

/// <summary>
///     Audio of ONE sender (or the local passthrough): Opus decode, per-radio jitter buffers, incoming AGC/denoise,
///     the sender's background sound and the sender's radio model (<see cref="ClientTransmissionPipelineProvider" />).
/// </summary>
public class ClientAudioProvider : AudioProvider
{
    private readonly Random _random = new();
    public static readonly int MaxSamples = Constants.OUTPUT_SAMPLE_RATE * 120 / 1000; // 120ms is the max opus frame size.

    //background sound progress per radio and sound
    private readonly Dictionary<string, int>[] backgroundEffectProgress;

    private readonly CachedAudioEffectProvider audioEffectProvider = CachedAudioEffectProvider.Instance;
    private readonly ClientTransmissionPipelineProvider pipeline;

    private readonly ProfileSettingsStore settingsStore = GlobalSettingsStore.Instance.ProfileSettingsStore;
    private bool backgroundSoundEffectEnabled = true;

    private float backgroundSoundEffectVolume = 1.0f;

    private readonly SpeexPreprocessorProvider preprocessProvider = new SpeexPreprocessorProvider();

    private double lastLoaded;

    //   private readonly WaveFileWriter waveWriter;
    /// <param name="localPassthrough">
    ///     true for the own voice (mic output device, recording of the own transmissions): the sender's radio model like
    ///     for received audio, but no voice distortion - nothing was received over a radio link.
    /// </param>
    public ClientAudioProvider(bool localPassthrough = false)
    {
        pipeline = new ClientTransmissionPipelineProvider { VoiceDistortionEnabled = !localPassthrough };

        var radios = Constants.MAX_RADIOS;
        JitterBufferProviderInterface =
                new JitterBufferProviderInterface[radios];

        for (var i = 0; i < radios; i++)
            JitterBufferProviderInterface[i] =
                new JitterBufferProviderInterface(new WaveFormat(Constants.OUTPUT_SAMPLE_RATE, 1));
        //    waveWriter = new NAudio.Wave.WaveFileWriter($@"C:\\temp\\output{RandomFloat()}.wav", new WaveFormat(Constants.OUTPUT_SAMPLE_RATE, 1));


        backgroundEffectProgress = new Dictionary<string, int>[radios];

        for (var i = 0; i < radios; i++) backgroundEffectProgress[i] = new Dictionary<string, int>();
    }

    private JitterBufferProviderInterface[] JitterBufferProviderInterface { get; }

    public override int AddClientAudioSamples(ClientAudio audio)
    {
        if (audio.ReceivedRadio < 0 || audio.ReceivedRadio >= JitterBufferProviderInterface.Length)
        {
            Logger.Warn($"Dropping audio for invalid radio {audio.ReceivedRadio}");
            return 0;
        }

        ReLoadSettings();
        //sort out volume
        //            var timer = new Stopwatch();
        //            timer.Start();

        var newTransmission = LikelyNewTransmission();

        using var pcmFloats = new PooledArray<float>(MaxSamples);
        var decodedLength = 0;

        if (audio.EncodedAudio == null)
        {
            // scrambled: an end-to-end encrypted frame whose key never arrived. There is nothing to decode - one frame
            // of silence that Read() turns into the scrambled radio effect (noise + the model's encryption effect).
            if (audio.Decryptable) return 0;

            decodedLength = Constants.OUTPUT_SEGMENT_FRAMES;
            pcmFloats.Array.AsSpan(0, decodedLength).Clear();
        }
        else
        {
            // Target buffer contains at least one frame.
            try
            {
                decodedLength = _decoder.DecodeFloat(audio.EncodedAudio, new Memory<float>(pcmFloats.Array, 0, pcmFloats.Length), newTransmission);
                if (decodedLength <= 0)
                {
                    Logger.Info("Failed to decode audio from Packet for client");
                    return 0;
                }
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Error decoding audio packet.");
                return 0;
            }
        }

        //convert the byte buffer to a wave buffer
        //   var waveBuffer = new WaveBuffer(tmp);

        // waveWriter.WriteSamples(tmp,0,tmp.Length);
        // decryptable = decrypted with its end-to-end transmission key; false = scrambled (the key never arrived)
        var decrytable = audio.Decryptable;

        // Clean frequencies of the server are always played without radio effects.
        var noAudioEffects =
            RadioEffectRules.PlayWithoutRadioEffects(audio.NoAudioEffects, audio.Frequency, SyncedServerSettings.Instance);

        LastUpdate = DateTime.Now.Ticks;

        // Give it a 'real' array for returning.
        // Most of the time, we shouldn't need 120ms of audio, typically more 20-40ms.
        var decodedAudio = new float[decodedLength];
        pcmFloats.Array.AsSpan(0, decodedLength).CopyTo(decodedAudio.AsSpan(0, decodedLength));

        //return and skip jitter buffer if its passthrough as its local mic
        var jitter = new JitterBufferAudio
        {
            Audio = decodedAudio,
            PacketNumber = audio.PacketNumber,
            Decryptable = decrytable,
            Modulation = (Modulation)audio.Modulation,
            ReceivedRadio = audio.ReceivedRadio,
            Volume = audio.Volume,
            Frequency = audio.Frequency,
            NoAudioEffects = noAudioEffects,
            Guid = audio.ClientGuid,
            OriginalClientGuid = audio.OriginalClientGuid,
            Encryption = audio.Encryption,
            // the sender may not be known yet (first packets) - no background sound then
            Ambient = audio.Ambient?.Copy(),
        };

        JitterBufferProviderInterface[audio.ReceivedRadio].AddSamples(jitter);
        return decodedLength;
    }

    //high throughput - cache these settings for 3 seconds
    private void ReLoadSettings()
    {
        var now = DateTime.Now.Ticks;
        if (now - lastLoaded > 30000000)
        {
            lastLoaded = now;
            backgroundSoundEffectEnabled =
                settingsStore.GetClientSettingBool(ProfileSettingsKeys.BackgroundSoundEffect);
            backgroundSoundEffectVolume =
                VolumeConversionHelper.ConvertRadioVolumeSlider(
                    settingsStore.GetClientSettingFloat(ProfileSettingsKeys.BackgroundSoundEffectVolume), true);

            var globalStore = GlobalSettingsStore.Instance;

            var agc = globalStore.GetClientSettingBool(GlobalSettingsKeys.IncomingAudioAGC);
            var agcTarget = globalStore.GetClientSetting(GlobalSettingsKeys.IncomingAudioAGCTarget).IntValue;
            var agcDecrement = globalStore.GetClientSetting(GlobalSettingsKeys.IncomingAudioAGCDecrement).IntValue;
            var agcMaxGain = globalStore.GetClientSetting(GlobalSettingsKeys.IncomingAudioAGCLevelMax).IntValue;

            var denoise = globalStore.GetClientSettingBool(GlobalSettingsKeys.IncomingAudioDenoise);
            var denoiseAttenuation = globalStore.GetClientSetting(GlobalSettingsKeys.IncomingAudioDenoiseAttenuation).IntValue;

            //From https://github.com/mumble-voip/mumble/blob/a189969521081565b8bda93d253670370778d471/src/mumble/Settings.cpp
            //and  https://github.com/mumble-voip/mumble/blob/3ffd9ad3ed18176774d8e1c64a96dffe0de69655/src/mumble/AudioInput.cpp#L605

            if (agc != preprocessProvider.Preprocessor.AutomaticGainControl) preprocessProvider.Preprocessor.AutomaticGainControl = agc;
            if (agcTarget != preprocessProvider.Preprocessor.AutomaticGainControlTarget) preprocessProvider.Preprocessor.AutomaticGainControlTarget = agcTarget;
            if (agcDecrement != preprocessProvider.Preprocessor.AutomaticGainControlDecrement) preprocessProvider.Preprocessor.AutomaticGainControlDecrement = agcDecrement;
            if (agcMaxGain != preprocessProvider.Preprocessor.AutomaticGainControlMaxGain) preprocessProvider.Preprocessor.AutomaticGainControlMaxGain = agcMaxGain;

            if (denoise != preprocessProvider.Preprocessor.Denoise) preprocessProvider.Preprocessor.Denoise = denoise;
            if (denoiseAttenuation != preprocessProvider.Preprocessor.DenoiseAttenuation) preprocessProvider.Preprocessor.DenoiseAttenuation = denoiseAttenuation;
        }
    }

    /// <summary>
    ///     Mixes the sender's background sound (jet, prop, helicopter, ...) into the voice, BEFORE the sender's radio
    ///     model, so it sounds like it was picked up by the sender's microphone.
    ///     <paramref name="ambient" /> comes from the network: only whitelisted sounds from AudioEffects\Background are
    ///     used and the volume is clamped to 0..1.
    /// </summary>
    // #TODO: Move to dedicated audio provider.
    private void AddBackgroundAudio(int receiveRadio, Ambient ambient, Span<float> pcmAudio)
    {
        if (!backgroundSoundEffectEnabled || ambient == null || pcmAudio.IsEmpty)
            return;

        var effect = audioEffectProvider.GetBackgroundEffect(ambient.abType);

        if (effect == null || !effect.Loaded || effect.AudioEffectFloat.Length == 0) return;

        var senderVolume = float.IsFinite(ambient.vol) ? Math.Clamp(ambient.vol, 0f, 1f) : 0f;
        if (senderVolume <= 0f) return;

        var vol = (float) VolumeConversionHelper.DecibelsToLinear(VolumeConversionHelper.GetTargetdB(effect.RMS, senderVolume));

        var effectVolume = vol * backgroundSoundEffectVolume;
        if (effectVolume <= 0f || !float.IsFinite(effectVolume)) return;

        var backgroundEffectProg = backgroundEffectProgress[receiveRadio];
        var backgroundName = CachedAudioEffectProvider.NormaliseBackgroundName(ambient.abType);

        var effectAudio = effect.AudioEffectFloat;
        var effectLength = effectAudio.Length;
        var progress = backgroundEffectProg.GetValueOrDefault(backgroundName, 0);
        if (progress < 0 || progress >= effectLength) progress = 0;

        var vectorSize = Vector<float>.Count;
        var v_effectVolume = new Vector<float>(effectVolume);
        var v_min = new Vector<float>(-1.0f);
        var v_max = new Vector<float>(1.0f);

        ref float pcmAudioPtr = ref MemoryMarshal.GetReference(pcmAudio);
        ref float effectPtr = ref MemoryMarshal.GetArrayDataReference(effectAudio);

        var i = 0;
        while (i < pcmAudio.Length)
        {
            // Vector path only when a whole vector fits in BOTH the output and the rest of the looped effect.
            if (i + vectorSize <= pcmAudio.Length && progress + vectorSize <= effectLength)
            {
                var v_samples = Vector.LoadUnsafe(ref pcmAudioPtr, (nuint)i);
                var v_effect = Vector.LoadUnsafe(ref effectPtr, (nuint)progress);

                var v_mixed = v_samples + v_effect * v_effectVolume;

                var v_tooLow = Vector.LessThan(v_mixed, v_min);
                var v_tooHigh = Vector.GreaterThan(v_mixed, v_max);
                var v_outOfAudioBounds = Vector.BitwiseOr(v_tooLow, v_tooHigh);

                // If out of bounds, pick original audio , else pick v_mixed (with effect)
                var v_final = Vector.ConditionalSelect(v_outOfAudioBounds, v_samples, v_mixed);

                v_final.StoreUnsafe(ref pcmAudioPtr, (nuint)i);

                i += vectorSize;
                progress += vectorSize;
            }
            else
            {
                var mixed = pcmAudio[i] + effectAudio[progress] * effectVolume;

                if (mixed is > -1f and < 1f)
                {
                    pcmAudio[i] = mixed;
                }

                i++;
                progress++;
            }

            if (progress >= effectLength) progress = 0;
        }

        backgroundEffectProg[backgroundName] = progress;
    }

    private void AddEncryptionFailureEffect(Span<float> pcmAudio)
    {
        for (var i = 0; i < pcmAudio.Length; i++) pcmAudio[i] = RandomFloat();
    }


    private float RandomFloat()
    {
        //random float at max volume at eights
        var f = _random.Next(-32768 / 8, 32768 / 8) / (float)32768;
        f = Math.Clamp(f, -1f, 1f);

        return f;
    }

    public TransmissionSegment Read(int radioId, int desired)
    {
        if (desired == 0)
            return null;

        var transmission = JitterBufferProviderInterface[radioId].Read(desired);
        if (transmission.PCMAudioLength == 0)
            return null;

        preprocessProvider.Read(transmission.PCMMonoAudio, 0, transmission.PCMAudioLength);
        var segment = new TransmissionSegment(transmission);

        var segmentAudio = segment.Audio.AsSpan();
        if (transmission.Decryptable)
        {
            // clean frequencies get no background sound either
            if (!transmission.NoAudioEffects)
                AddBackgroundAudio(transmission.ReceivedRadio, transmission.Ambient, segmentAudio);
        }
        else
        {
            AddEncryptionFailureEffect(segmentAudio);
        }

        pipeline.Process(transmission, segmentAudio);

        JitterBufferProviderInterface[radioId].Dispose(ref transmission);
        return segment;
    }
}
