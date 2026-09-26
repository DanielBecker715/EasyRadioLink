using System;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Common;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Audio.Utility;
using EasyRadioLink.Common.Settings;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLog;

namespace EasyRadioLink.Client.Audio.Managers;

/// <summary>
///     Plays a radio sound once on the selected speakers (the play buttons of the Settings tab) the way the radio
///     plays it: with the speaker volume and the radio balance. Works with and without a connection (shared mode next
///     to the radio output). A new preview replaces the one that is playing. Errors are logged, never thrown.
/// </summary>
public static class SoundEffectPreview
{
    private const int LatencyMilliseconds = 80;

    // silence after the sound, so the sound has left the output buffer before the output stops
    private const int TrailingSilenceMilliseconds = 250;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly object Lock = new();
    private static LowLatencyWasapiOut _output;

    /// <summary>Plays <paramref name="effect" /> once (nothing happens for a sound that is not loaded).</summary>
    public static void Play(CachedAudioEffect effect)
    {
        if (effect?.AudioEffectFloat == null || !effect.Loaded) return;

        Stop();

        LowLatencyWasapiOut output = null;
        try
        {
            var outputs = AudioOutputSingleton.Instance;
            var device = outputs.SelectedAudioOutput?.Value as MMDevice ??
                         LowLatencyWasapiOut.GetDefaultAudioEndpoint();

            output = new LowLatencyWasapiOut(device, AudioClientShareMode.Shared, true, LatencyMilliseconds,
                outputs.WindowsN);

            var settings = GlobalSettingsStore.Instance;
            var balance = settings.ProfileSettingsStore.GetClientSettingFloat(ProfileSettingsKeys.Radio1Channel);
            var speakerVolume = VolumeConversionHelper.ConvertVolumeSliderToScale(
                (float)settings.GetClientSetting(GlobalSettingsKeys.SpeakerBoost).DoubleValue);

            ISampleProvider provider = new StereoSamples(RenderStereo(effect.AudioEffectFloat, balance));
            provider = new VolumeSampleProvider(provider) { Volume = speakerVolume };
            if (output.OutputWaveFormat.Channels == 1) provider = provider.ToMono();

            output.Init(provider);
            output.PlaybackStopped += OnPlaybackStopped;

            lock (Lock)
            {
                _output = output;
            }

            output.Play();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Unable to play the sound {effect.FileName}");

            lock (Lock)
            {
                if (ReferenceEquals(_output, output)) _output = null;
            }

            DisposeQuietly(output);
        }
    }

    /// <summary>Stops the sound that is playing (if any).</summary>
    public static void Stop()
    {
        LowLatencyWasapiOut output;
        lock (Lock)
        {
            output = _output;
            _output = null;
        }

        if (output == null) return;

        output.PlaybackStopped -= OnPlaybackStopped;
        DisposeQuietly(output);
    }

    /// <summary>
    ///     The mono sound as interleaved stereo with the radio balance applied (as <see cref="RadioMixingProvider" />
    ///     does), followed by <see cref="TrailingSilenceMilliseconds" /> of silence.
    /// </summary>
    internal static float[] RenderStereo(float[] mono, float balance)
    {
        var silence = Constants.OUTPUT_SAMPLE_RATE * TrailingSilenceMilliseconds / 1000;
        var stereo = new float[(mono.Length + silence) * 2];

        RadioMixingProvider.CreateBalancedMix(mono, 0, mono.Length, stereo, 0,
            float.IsFinite(balance) ? balance : 0f);

        return stereo;
    }

    private static void OnPlaybackStopped(object sender, StoppedEventArgs e)
    {
        if (e.Exception != null) Logger.Warn(e.Exception, "The sound preview stopped with an error");

        var output = sender as LowLatencyWasapiOut;
        lock (Lock)
        {
            if (ReferenceEquals(_output, output)) _output = null;
        }

        DisposeQuietly(output);
    }

    private static void DisposeQuietly(IDisposable output)
    {
        try
        {
            output?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unable to close the sound preview output");
        }
    }

    /// <summary>Interleaved stereo samples at the output sample rate, played once.</summary>
    private sealed class StereoSamples : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public StereoSamples(float[] samples)
        {
            _samples = samples;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Constants.OUTPUT_SAMPLE_RATE, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, _samples.Length - _position);
            if (read <= 0) return 0;

            // element by element: NAudio may pass a byte buffer viewed as float[] (WaveBuffer), which Array.Copy
            // rejects
            for (var i = 0; i < read; i++) buffer[offset + i] = _samples[_position + i];

            _position += read;
            return read;
        }
    }
}
