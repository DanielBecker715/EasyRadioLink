using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Recording;
using EasyRadioLink.Common.Audio.Utility;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings;
using NAudio.Wave;
using NLog;
using System;
using System.Buffers;
using System.Collections.Generic;

namespace EasyRadioLink.Common.Audio.Providers;

/// <summary>
///     Output of ONE local radio (index 0..10, slot 0 is reserved and never receives): start/end clicks, encryption
///     tones, squelch tail, the mix of all received transmissions (receive filter, FM capture) and the stereo balance.
/// </summary>
public class RadioMixingProvider : ISampleProvider
{
    private static readonly Logger logger = LogManager.GetCurrentClassLogger();

    // Stereo balance setting per radio index (slot 0 is reserved - centred).
    private static readonly ProfileSettingsKeys?[] BalanceKeys =
    {
        null,
        ProfileSettingsKeys.Radio1Channel,
        ProfileSettingsKeys.Radio2Channel,
        ProfileSettingsKeys.Radio3Channel,
        ProfileSettingsKeys.Radio4Channel,
        ProfileSettingsKeys.Radio5Channel,
        ProfileSettingsKeys.Radio6Channel,
        ProfileSettingsKeys.Radio7Channel,
        ProfileSettingsKeys.Radio8Channel,
        ProfileSettingsKeys.Radio9Channel,
        ProfileSettingsKeys.Radio10Channel
    };

    private readonly AudioRecordingManager _audioRecordingManager = AudioRecordingManager.Instance;

    private readonly CachedAudioEffectProvider _cachedAudioEffectsProvider;
    private readonly CircularFloatBuffer effectsBuffer;


    private readonly ClientEffectsPipeline pipeline = new();

    private readonly ProfileSettingsStore profileSettings =
        GlobalSettingsStore.Instance.ProfileSettingsStore;

    private readonly Random _random = new();

    private readonly int radioId;
    private readonly List<ClientAudioProvider> sources;

    private bool IsReceiving { get; set; } = false;

    // What the transmission that is being received sounds like - decides the squelch tail when it ends.
    private bool _rxHasSquelchTail;
    private Modulation _rxModulation = Modulation.DISABLED;
    private float _rxVolume;

    private float[] mixBuffer;
    private int availableInBuffer = 0;

    //  private readonly WaveFileWriter waveWriter;
    public RadioMixingProvider(WaveFormat waveFormat, int radioId)
    {
        if (waveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("Mixer wave format must be IEEE float");

        this.radioId = radioId;
        sources = new List<ClientAudioProvider>();
        WaveFormat = waveFormat;

        //5 seconds worth of buffer
        effectsBuffer = new CircularFloatBuffer(WaveFormat.SampleRate * 5);
        _cachedAudioEffectsProvider = CachedAudioEffectProvider.Instance;

        //   waveWriter = new NAudio.Wave.WaveFileWriter($@"C:\\temp\\output{Guid.NewGuid()}.wav", new WaveFormat(AudioManager.OUTPUT_SAMPLE_RATE, 2));
    }

    /// <summary>
    ///     The output WaveFormat of this sample provider
    /// </summary>
    public WaveFormat WaveFormat { get; }

    /// <summary>
    ///     Reads samples from this sample provider
    /// </summary>
    /// <param name="buffer">Sample buffer</param>
    /// <param name="offset">Offset into sample buffer</param>
    /// <param name="count">Number of samples required</param>
    /// <returns>Number of samples read</returns>
    public int Read(float[] buffer, int offset, int count)
    {
        var floatPool = ArrayPool<float>.Shared;
        // Accumulate in local mono buffer before switching to stereo.
        //ask for count/2 as the source is MONO but the request for this is STEREO
        var monoBufferLength = count / 2; // Array pool can give us an array that is larger.
        var monoBuffer = floatPool.Rent(monoBufferLength);

        Array.Clear(monoBuffer, 0, monoBufferLength);

        // Read effects.
        var monoOffset = ReadEffects(monoBuffer, 0, monoBufferLength);

        // Read any available audio that we have queued up.
        monoOffset += ReadMixBuffer(monoBuffer, monoOffset, monoBufferLength - monoOffset);

        // Are we starved? Rehydrate.
        if (monoOffset < monoBufferLength)
        {
            List<TransmissionSegment> segments = null;

            // Update sources by queueing incoming audio.
            var encryptionTone = false;
            var longestSegmentLength = 0;
            lock (sources)
            {
                if (sources.Count > 0)
                {
                    segments = new(sources.Count);
                    var index = sources.Count - 1;
                    var desired = monoBufferLength - monoOffset;
                    while (index >= 0)
                    {
                        var source = sources[index];

                        // #TODO: Should run TX effect chain per ClientAudioProvider, then mixdown.
                        // Read from the source, which should dejitter + transform the audio.
                        // Then have this radio run its mixer.
                        try
                        {
                            var segment = source.Read(radioId, desired);
                            if (segment != null)
                            {
                                segments.Add(segment);
                                longestSegmentLength = Math.Max(longestSegmentLength, segment.Audio.Length);
                                if (segment.Decryptable && segment.HasEncryption)
                                {
                                    encryptionTone = true;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            logger.Error(e, "Error reading from source: ");
                        }


                        index--;
                    }
                }

            }

            var hasIncomingAudio = segments?.Count > 0;

            //copy to the recording service - as we have everything we need to know about the audio
            //at this point
            if (hasIncomingAudio)
            {
                _audioRecordingManager.AppendClientAudio(radioId, segments);
                UpdateReceivedTransmission(segments);
            }

            // #FIXME: Should copy into mixBuffer, and use that throughout as our primary mixdown here.
            monoOffset += HandleStartEndTones(hasIncomingAudio || availableInBuffer > 0, encryptionTone, monoBuffer, monoOffset, monoBufferLength - monoOffset);

            // Queue new audio (if any).
            if (hasIncomingAudio)
            {
                try
                {
                    // Need to be able to hold whatever is left over + incoming audio.
                    var totalSize = availableInBuffer + longestSegmentLength;
                    if (mixBuffer == null || mixBuffer.Length < totalSize)
                    {
                        Array.Resize(ref mixBuffer, totalSize);
                    }

                    var targetSpan = mixBuffer.AsSpan(availableInBuffer, longestSegmentLength);
                    targetSpan.Clear();


                    // #TODO: pass receiving radio model name.
                    pipeline.ProcessSegments(targetSpan, segments, null);

                    //now clip all mixing
                    AudioManipulationHelper.ClipArray(targetSpan);
                    availableInBuffer += targetSpan.Length;

                    // Drain newly queued audio.
                    monoOffset += ReadMixBuffer(monoBuffer, monoOffset, monoBufferLength - monoOffset);
                }
                catch (Exception e)
                {
                    logger.Error(e, "Error mixing segments");
                }
            }
        }

        // Did we consume everything? Make sure we don't keep a buffer too big.
        if (mixBuffer != null && availableInBuffer == 0)
        {
            if (mixBuffer.Length > Constants.OUTPUT_SEGMENT_FRAMES)
            {
                Array.Resize(ref mixBuffer, Constants.OUTPUT_SEGMENT_FRAMES);
            }
        }


        if (monoOffset > 0)
        {
             // We have available data, make it stereo and copy to target.
            SeparateAudio(monoBuffer, 0, monoOffset, buffer, offset, radioId);
        }

        floatPool.Return(monoBuffer);
        return monoOffset * 2; // double because of mono -> stereo.
    }

    /// <summary>
    ///     Adds a new mixer input
    /// </summary>
    /// <param name="mixerInput">Mixer input</param>
    public void AddMixerInput(ClientAudioProvider mixerInput)
    {
        // we'll just call the lock around add since we are protecting against an AddMixerInput at
        // the same time as a Read, rather than two AddMixerInput calls at the same time
        lock (sources)
        {
            sources.Add(mixerInput);
        }
    }

    public void RemoveMixerInput(ClientAudioProvider mixerInput)
    {
        lock (sources)
        {
            sources.Remove(mixerInput);
        }
    }

    /// <summary>
    ///     Removes all mixer inputs
    /// </summary>
    public void RemoveAllMixerInputs()
    {
        lock (sources)
        {
            sources.Clear();
        }
    }

    // Remember what is being received so the right squelch tail can be played when it ends.
    private void UpdateReceivedTransmission(List<TransmissionSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (RadioEffectRules.HasSquelchTail(segment.Modulation, segment.NoAudioEffects))
            {
                _rxHasSquelchTail = true;
                _rxModulation = segment.Modulation;
            }

            if (float.IsFinite(segment.Volume)) _rxVolume = Math.Max(_rxVolume, segment.Volume);
        }
    }

    private void ResetReceivedTransmission()
    {
        _rxHasSquelchTail = false;
        _rxModulation = Modulation.DISABLED;
        _rxVolume = 0f;
    }

    private int HandleStartEndTones(bool isActive, bool encryption, float[] buffer, int offset, int count)
    {
        if (isActive ^ IsReceiving)
        {
            // State change.
            if (isActive)
            {
                // Start
                effectsBuffer.Reset(); // In case we were playing the end tone, cut it short.
                PlaySoundEffectStartReceive(encryption);
                IsReceiving = true;
            }
            else
            {
                IsReceiving = false;
                // end.
                //TODO not sure about simultaneous
                PlaySoundEffectEndReceive();
                PlaySquelchTail();
                ResetReceivedTransmission();
            }
        }

        return ReadEffects(buffer, offset, count);
    }

    private int ReadEffects(float[] buffer, int offset, int count)
    {
        //read
        var outputSamples = Math.Min(effectsBuffer.Count, count);
        if (outputSamples > 0)
        {
            outputSamples = effectsBuffer.Read(buffer, offset, outputSamples);
        }

        return outputSamples;
    }
    private int ReadMixBuffer(float[] buffer, int offset, int count)
    {
        // Drain current.
        var samplesRead = Math.Min(count, availableInBuffer);
        if (samplesRead > 0)
        {
            Array.Copy(mixBuffer, 0, buffer, offset, samplesRead);
            availableInBuffer -= samplesRead;
            // Shift elements so that the data available is always at the beginning.
            Array.Copy(mixBuffer, samplesRead, mixBuffer, 0, availableInBuffer);
        }

        return samplesRead;
    }

    private void WriteEffect(CachedAudioEffect effect)
    {
        if (effect != null && effect.Loaded)
            effectsBuffer.Write(effect.AudioEffectFloat, 0, effect.AudioEffectFloat.Length);
    }

    private void PlaySoundEffectEndReceive()
    {
        if (!profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioRxEffects_End)) return;

        WriteEffect(_cachedAudioEffectsProvider.SelectedRadioTransmissionEndEffect);
    }

    /// <summary>Queues the receive start effect: the encryption tone for decryptable encrypted audio, else the selected click.</summary>
    public void PlaySoundEffectStartReceive(bool encrypted)
    {
        if (!profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioRxEffects_Start)) return;

        if (encrypted &&
            profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioEncryptionEffects))
        {
            WriteEffect(_cachedAudioEffectsProvider.EncryptionReceiveTone);
        }
        else
        {
            WriteEffect(_cachedAudioEffectsProvider.SelectedRadioTransmissionStartEffect);
        }
    }

    /// <summary>
    ///     The short fading noise burst when a received AM/FM transmission ends ("CB feel").
    ///     Not for DIGITAL, clean frequencies or when <see cref="ProfileSettingsKeys.RadioRxSquelchTail" /> is off.
    /// </summary>
    private void PlaySquelchTail()
    {
        if (!_rxHasSquelchTail || _rxVolume <= 0f) return;

        if (!profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioRxSquelchTail)) return;

        var effect = _rxModulation == Modulation.FM
            ? _cachedAudioEffectsProvider.SquelchTailFM
            : _cachedAudioEffectsProvider.SquelchTailAM;

        if (effect == null || !effect.Loaded) return;

        var noise = effect.AudioEffectFloat;
        var length = Math.Min(SquelchTail.Samples, noise.Length);

        // start somewhere random in the noise so every tail sounds a little different
        var startOffset = noise.Length > length ? _random.Next(0, noise.Length - length) : 0;

        var floatPool = ArrayPool<float>.Shared;
        var tail = floatPool.Rent(length);
        try
        {
            var written = SquelchTail.Render(noise, startOffset, tail.AsSpan(0, length), _rxVolume);
            if (written > 0) effectsBuffer.Write(tail, 0, written);
        }
        finally
        {
            floatPool.Return(tail);
        }
    }

    /// <summary>Queues the transmit start effect on this radio: the encryption tone when encrypted, else the selected click.</summary>
    public void PlaySoundEffectStartTransmit(bool encrypted)
    {
        if (!profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioTxEffects_Start)) return;

        if (encrypted && profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioEncryptionEffects))
        {
            WriteEffect(_cachedAudioEffectsProvider.EncryptionTransmitTone);
        }
        else
        {
            WriteEffect(_cachedAudioEffectsProvider.SelectedRadioTransmissionStartEffect);
        }
    }

    /// <summary>Queues the transmit end effect (selected click) on this radio.</summary>
    public void PlaySoundEffectEndTransmit()
    {
        if (!profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioTxEffects_End)) return;

        WriteEffect(_cachedAudioEffectsProvider.SelectedRadioTransmissionEndEffect);
    }


    public void SeparateAudio(float[] srcFloat, int srcOffset, int srcCount, float[] dstFloat, int dstOffset,
        int radioId)
    {
        var settingType = radioId >= 0 && radioId < BalanceKeys.Length ? BalanceKeys[radioId] : null;

        float balance = 0f;
        if (settingType.HasValue)
        {
            try
            {
                balance = profileSettings.GetClientSettingFloat(settingType.Value);
            }
            catch (Exception)
            {
                //ignore
            }
        }

        CreateBalancedMix(srcFloat, srcOffset, srcCount, dstFloat, dstOffset, balance);
    }

    public static void CreateBalancedMix(float[] srcFloat, int srcOffset, int srcCount, float[] dstFloat,
        int dstOffset, float balance)
    {
        balance = Math.Clamp(balance, -1f, 1f);
        var left = (1.0f - balance) / 2.0f;
        var right = 1.0f - left;

        //temp set of mono floats
        for (var i = 0; i < srcCount; ++i)
        {
            dstFloat[dstOffset + 2 * i] = srcFloat[srcOffset + i] * left;
            dstFloat[dstOffset + 2 * i + 1] = srcFloat[srcOffset + i] * right;
        }
    }
}
