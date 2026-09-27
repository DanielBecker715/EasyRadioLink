using System;
using System.Collections.Generic;
using EasyRadioLink.Common.Audio.Utility;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Common;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Opus.Core;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Settings;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NLog;
using Application = EasyRadioLink.Common.Audio.Opus.Application;
using EasyRadioLink.Client.Audio.Utility;

namespace EasyRadioLink.Client.Audio.Managers;

internal class AudioPreview
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly AudioInputSingleton _audioInputSingleton = AudioInputSingleton.Instance;
    private readonly AudioOutputSingleton _audioOutputSingleton = AudioOutputSingleton.Instance;

    private readonly Queue<short> _micInputQueue = new(Constants.MIC_SEGMENT_FRAMES * 3);

    private readonly object lockob = new();
    private BufferedWaveProvider _buffBufferedWaveProvider;
    private OpusDecoder _decoder;
    private OpusEncoder _encoder;
    private BufferedWaveProvider _playBuffer;
    private EventDrivenResampler _resampler;

    private float _speakerBoost = 1.0f;

    private SpeexProcessor _speex;
    //private readonly CircularBuffer _circularBuffer = new CircularBuffer();

    private VolumeSampleProviderWithPeak _volumeSampleProvider;

    private WasapiCapture _wasapiCapture;

    private WaveFileWriter _waveFile;
    private LowLatencyWasapiOut _waveOut;

    private bool windowsN;

    public float MicBoost { get; set; } = 1.0f;

    public bool IsPreviewing => _waveOut != null;

    public float SpeakerBoost
    {
        get => _speakerBoost;
        set
        {
            _speakerBoost = value;
            if (_volumeSampleProvider != null) _volumeSampleProvider.Volume = value;
        }
    }

    public float MicMax { get; set; } = -100;
    public float SpeakerMax { get; set; } = -100;

    /// <summary>Radio model the preview is played through (a RadioModelInfo.Key).</summary>
    public string ModelKey { get; private set; } = RadioModelFactory.DefaultModelKey;

    /// <summary>
    ///     Plays the microphone through the Opus codec, the given radio model and the distance (weak signal) setting of
    ///     the profile on the speakers (mic test).
    /// </summary>
    /// <param name="modelKey">radio model to hear yourself through, e.g. "cb" or "walkie"; default "standard"</param>
    /// <returns>false if an audio device could not be opened (an error dialog was shown, nothing is left running)</returns>
    public bool StartPreview(bool windowsN, string modelKey = RadioModelFactory.DefaultModelKey)
    {
        this.windowsN = windowsN;
        try
        {
            MMDevice speakers = null;
            if (_audioOutputSingleton.SelectedAudioOutput.Value == null)
                speakers = LowLatencyWasapiOut.GetDefaultAudioEndpoint();
            else
                speakers = (MMDevice)_audioOutputSingleton.SelectedAudioOutput.Value;

            _waveOut = new LowLatencyWasapiOut(speakers, AudioClientShareMode.Shared, true, 80, windowsN);

            _buffBufferedWaveProvider =
                new BufferedWaveProvider(new WaveFormat(Constants.OUTPUT_SAMPLE_RATE, 16, 1));
            _buffBufferedWaveProvider.ReadFully = true;
            _buffBufferedWaveProvider.DiscardOnBufferOverflow = true;

            var filter = new RadioFilter(_buffBufferedWaveProvider.ToSampleProvider(),
                string.IsNullOrWhiteSpace(modelKey) ? RadioModelFactory.DefaultModelKey : modelKey);
            ModelKey = filter.ModelKey;

            // the distance (weak signal) of the profile after the model, like on a received voice - follows the
            // setting while the preview is running, so it can be tuned while listening
            var weakSignal = new LiveWeakSignal(new WeakSignalChannelProvider(filter,
                WeakSignalChannelProvider.BandForModel(ModelKey), Environment.TickCount,
                LiveWeakSignal.CurrentAmount()));

            //add final volume boost to all mixed audio
            _volumeSampleProvider = new VolumeSampleProviderWithPeak(weakSignal,
                peak => { SpeakerMax = (float)VolumeConversionHelper.ConvertFloatToDB(peak); });
            _volumeSampleProvider.Volume = SpeakerBoost;

            if (speakers.AudioClient.MixFormat.Channels == 1)
            {
                if (_volumeSampleProvider.WaveFormat.Channels == 2)
                    _waveOut.Init(_volumeSampleProvider.ToMono());
                else
                    //already mono
                    _waveOut.Init(_volumeSampleProvider);
            }
            else
            {
                if (_volumeSampleProvider.WaveFormat.Channels == 1)
                    _waveOut.Init(_volumeSampleProvider.ToStereo());
                else
                    //already stereo
                    _waveOut.Init(_volumeSampleProvider);
            }

            _waveOut.Play();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error starting audio output: " + ex.Message);

            StopEncoding();
            AudioManager.ShowOutputError("Problem initialising the audio output!");

            return false;
        }

        try
        {
            _speex = new SpeexProcessor(Constants.MIC_SEGMENT_FRAMES, Constants.MIC_SAMPLE_RATE);
            //opus
            _encoder = OpusEncoder.Create(Constants.MIC_SAMPLE_RATE, 1, Application.Voip);
            _encoder.ForwardErrorCorrection = false;
            _decoder = OpusDecoder.Create(Constants.OUTPUT_SAMPLE_RATE, 1);
            _decoder.ForwardErrorCorrection = false;
            _decoder.MaxDataBytes = Constants.OUTPUT_SAMPLE_RATE * 4;

            var device = (MMDevice)_audioInputSingleton.SelectedAudioInput.Value;

            if (device == null) device = WasapiCapture.GetDefaultCaptureDevice();

            if (!WineDetector.IsRunningUnderWine())
            {
                try
                {
                    device.AudioEndpointVolume.Mute = false;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to forcibly unmute: " + ex.Message);
                }
            }
            

            _wasapiCapture = new WasapiCapture(device, true);
            _wasapiCapture.ShareMode = AudioClientShareMode.Shared;
            _wasapiCapture.DataAvailable += WasapiCaptureOnDataAvailable;
            _wasapiCapture.RecordingStopped += WasapiCaptureOnRecordingStopped;

            //debug wave file
            //      _waveFile = new WaveFileWriter(@"C:\Temp\Test-Preview.wav", new WaveFormat(AudioManager.INPUT_SAMPLE_RATE, 16, 1));

            _wasapiCapture.StartRecording();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error starting audio input: " + ex.Message);

            StopEncoding();
            AudioManager.ShowInputError("Problem initialising the audio input!");

            return false;
        }

        return true;
    }

    private void WasapiCaptureOnRecordingStopped(object sender, StoppedEventArgs e)
    {
        Logger.Error("Recording Stopped");
    }

    //Stopwatch _stopwatch = new Stopwatch();

    private void WasapiCaptureOnDataAvailable(object sender, WaveInEventArgs e)
    {
        if (_resampler == null)
            _resampler = new EventDrivenResampler(windowsN, _wasapiCapture.WaveFormat,
                new WaveFormat(Constants.MIC_SAMPLE_RATE, 16, 1));

        if (e.BytesRecorded > 0)
        {
            //Logger.Info($"Time: {_stopwatch.ElapsedMilliseconds} - Bytes: {e.BytesRecorded}");
            var resampledPCM16Bit = _resampler.Resample(e.Buffer, e.BytesRecorded);

            // Logger.Info($"Time: {_stopwatch.ElapsedMilliseconds} - Bytes: {resampledPCM16Bit.Length}");


            //fill sound buffer

            short[] pcmShort = null;

            for (var i = 0; i < resampledPCM16Bit.Length; i++) _micInputQueue.Enqueue(resampledPCM16Bit[i]);

            //read out the queue
            while (pcmShort != null || _micInputQueue.Count >= Constants.MIC_SEGMENT_FRAMES)
            {
                //null sound buffer so read from the queue
                if (pcmShort == null)
                {
                    pcmShort = new short[Constants.MIC_SEGMENT_FRAMES];

                    for (var i = 0; i < Constants.MIC_SEGMENT_FRAMES; i++)
                        pcmShort[i] = _micInputQueue.Dequeue();
                }

                try
                {
                    //process with Speex
                    _speex.Process(new ArraySegment<short>(pcmShort));

                    var rms = VolumeConversionHelper.CalculateRMS(pcmShort);

                    //convert to dB
                    MicMax = (float)rms;

                    var pcmBytes = new byte[pcmShort.Length * 2];
                    Buffer.BlockCopy(pcmShort, 0, pcmBytes, 0, pcmBytes.Length);

                    //                 _buffBufferedWaveProvider.AddSamples(pcmBytes, 0, pcmBytes.Length);
                    //encode as opus bytes
                    int len;
                    //need to get framing right for opus -
                    var buff = _encoder.Encode(pcmBytes, pcmBytes.Length, out len);

                    if (buff != null && len > 0)
                    {
                        //create copy with small buffer
                        var encoded = new byte[len];

                        Buffer.BlockCopy(buff, 0, encoded, 0, len);

                        var decodedLength = 0;
                        //now decode
                        var decodedBytes = _decoder.Decode(encoded, len, out decodedLength);

                        _buffBufferedWaveProvider.AddSamples(decodedBytes, 0, decodedLength);

                        //            Logger.Info($"Time: {_stopwatch.ElapsedMilliseconds} - Added samples");
                    }
                    else
                    {
                        Logger.Error(
                            $"Invalid Bytes for Encoding - {e.BytesRecorded} should be {Constants.MIC_SEGMENT_FRAMES} ");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error encoding Opus! " + ex.Message);
                }

                pcmShort = null;
            }
        }

        //   _stopwatch.Restart();
    }

    /// <summary>
    ///     The preview's <see cref="WeakSignalChannelProvider" />, kept on the profile setting (checked every 100 ms of
    ///     audio, on the audio thread).
    /// </summary>
    private sealed class LiveWeakSignal : ISampleProvider
    {
        private static readonly int RefreshSamples = Constants.OUTPUT_SAMPLE_RATE / 10; // 100 ms

        private readonly WeakSignalChannelProvider _weakSignal;
        private int _untilRefresh;

        public LiveWeakSignal(WeakSignalChannelProvider weakSignal)
        {
            _weakSignal = weakSignal;
            _untilRefresh = RefreshSamples;
        }

        public WaveFormat WaveFormat => _weakSignal.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            _untilRefresh -= count;
            if (_untilRefresh <= 0)
            {
                _untilRefresh = RefreshSamples;
                _weakSignal.Amount = CurrentAmount();
            }

            return _weakSignal.Read(buffer, offset, count);
        }

        /// <summary>Distance (weak signal) of the current profile (0..1); 0 when the radio effect strength is 0.</summary>
        public static float CurrentAmount()
        {
            try
            {
                var profile = GlobalSettingsStore.Instance.ProfileSettingsStore;
                return WeakSignalChannelProvider.AmountFromSettings(
                    profile.GetClientSettingFloat(ProfileSettingsKeys.RadioEffectsRatio),
                    profile.GetClientSettingFloat(ProfileSettingsKeys.VoiceDistortion));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Unable to read the distance (weak signal) setting");
                return 0f;
            }
        }
    }

    public void StopEncoding()
    {
        lock (lockob)
        {
            _wasapiCapture?.StopRecording();
            _wasapiCapture?.Dispose();
            _wasapiCapture = null;

            _resampler?.Dispose(true);
            _resampler = null;

            _waveOut?.Dispose();
            _waveOut = null;

            _playBuffer?.ClearBuffer();
            _playBuffer = null;

            _encoder?.Dispose();
            _encoder = null;

            _decoder?.Dispose();
            _decoder = null;

            _playBuffer?.ClearBuffer();
            _playBuffer = null;

            _speex?.Dispose();
            _speex = null;

            _waveFile?.Flush();
            _waveFile?.Dispose();
            _waveFile = null;

            SpeakerMax = -100;
            MicMax = -100;
        }
    }
}